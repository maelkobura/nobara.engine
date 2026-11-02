// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Profiling;
using Nobara.Game;

namespace Nobara.Entity;

/// <summary>
/// The tick system that runs an entity system: once per world, in the order
/// of the dimensions, skipping frozen dimensions. nGame sees one system, and
/// knows nothing of the worlds.
/// </summary>
internal sealed unsafe class EntitySystemRunner : ITickSystem
{
    private readonly EntityWorlds _worlds;
    private readonly IGameTime _time;
    private readonly IEntitySystem _system;
    private readonly QueryDescription _query;
    private readonly JobData[] _reads;
    private readonly JobData[] _writes;
    private readonly int _threshold;

    public EntitySystemRunner(EntityWorlds worlds, IGameTime time, ModuleContext module, string name, IEntitySystem system, EntitySystemOptions options, JobData[] reads, JobData[] writes, int slot)
    {
        _worlds = worlds;
        _time = time;
        Module = module;
        Name = name;
        _system = system;
        _query = options.Query;
        _reads = reads;
        _writes = writes;
        _threshold = options.ParallelThreshold;
        Slot = slot;
    }

    public ModuleContext Module { get; }

    /// <summary>Gets the data the system only reads, as nGame and the jobs of the system declare it.</summary>
    public JobData[] Reads => _reads;

    /// <summary>Gets the data the system writes.</summary>
    public JobData[] Writes => _writes;

    public string Name { get; }

    /// <summary>Gets the index of the state of the system in each world.</summary>
    public int Slot { get; }

    /// <summary>Creates the state of the system in a new world: its query, command buffer, and profiler marker.</summary>
    public WorldState Attach(EntityWorld world) => new(
        world,
        world.Query(_query),
        new EntityCommandBuffer(world),
        Module.Profiler.CreateMarker("Tick system " + Name + " in " + world.Dimension));

    [HotPath]
    public void Run(in TickTime time)
    {
        EntityWorld[] worlds = _worlds.Sorted;
        int count = _worlds.SortedCount;
        string moduleId = Module.Module.Id;
        _worlds.Running++;
        try
        {
            for (int i = 0; i < count; i++)
            {
                EntityWorld world = worlds[i];
                DimensionId dimension = world.Dimension;
                if (!_time.HasDimension(dimension) || _time.IsFrozen(dimension))
                {
                    continue;
                }

                if (RunOn(world.SystemStates[Slot], time) is { } failure)
                {
                    // The policy of the module applies; a disabled module runs no more.
                    Module.ReportFault(failure);
                    if (!Module.IsModuleActive(moduleId))
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            _worlds.Running--;
        }
    }

    /// <summary>Runs a job over the blocks of a query: on the loop thread below the threshold, on worker threads from it.</summary>
    [HotPath]
    public void RunChunks<TJob>(WorldState state, EntityQuery query, in TJob job)
        where TJob : struct, IChunkJob
    {
        query.Update();
        ref NativeList<BlockRef> blocks = ref state.Blocks;
        blocks.Clear();
        List<EntityArchetype> matches = query.Matches;
        for (int a = 0; a < matches.Count; a++)
        {
            EntityArchetype archetype = matches[a];
            NativeList<EntityArchetype.Block> archetypeBlocks = archetype.Blocks;
            for (int b = 0; b < archetypeBlocks.Count; b++)
            {
                blocks.Add(new BlockRef { Archetype = archetype.Index, Data = archetypeBlocks[b].Data, Count = archetypeBlocks[b].Count });
            }
        }

        int count = blocks.Count;
        if (count == 0)
        {
            return;
        }

        ModuleJobs jobs = Module.Jobs;
        if (count < _threshold || count == 1)
        {
            TJob local = job;
            List<EntityArchetype> archetypes = state.World.ArchetypeList;
            for (int i = 0; i < count; i++)
            {
                BlockRef block = blocks[i];
                local.Execute(new ChunkView(archetypes[block.Archetype], block.Data, block.Count));
            }

            return;
        }

        var runner = new ChunkJob<TJob> { Job = job, World = state.World, Blocks = blocks.Pointer };
        int batch = Math.Max(1, count / (jobs.WorkerCount * 4));
        JobHandle handle = jobs.ScheduleFor(runner, count, batch, new JobAccess(_reads, _writes));
        handle.Complete();
    }

    [HotPath]
    private Exception? RunOn(WorldState state, in TickTime time)
    {
        EntityWorld world = state.World;
        ProfilerScope scope = state.Marker.Begin();
        Exception? failure = null;
        world.Lock();
        try
        {
            _system.Run(new EntitySystemRun(this, state, time));
        }
        catch (Exception thrown) when (thrown is not EngineStopException)
        {
            failure = thrown;
        }
        finally
        {
            world.Unlock();
        }

        try
        {
            if (failure is null)
            {
                state.Commands.Playback();
            }
            else
            {
                state.Commands.Clear();
            }
        }
        catch (Exception thrown) when (thrown is not EngineStopException)
        {
            failure = thrown;
        }
        finally
        {
            // The writes that follow, by code outside the system, carry a newer version: the next run sees them.
            state.LastRunVersion = world.ChangeVersion;
            world.AdvanceChangeVersion();
            scope.Dispose();
        }

        return failure;
    }

    /// <summary>A block of a query, collected on the loop thread for the jobs.</summary>
    internal struct BlockRef
    {
        public int Archetype;
        public byte* Data;
        public int Count;
    }

    /// <summary>The state of an entity system in one world.</summary>
    internal sealed class WorldState(EntityWorld world, EntityQuery query, EntityCommandBuffer commands, ProfilerMarker marker) : IDisposable
    {
        private NativeList<BlockRef> _blocks = new(16);

        public EntityWorld World { get; } = world;

        public EntityQuery Query { get; } = query;

        public EntityCommandBuffer Commands { get; } = commands;

        public ProfilerMarker Marker { get; } = marker;

        public uint LastRunVersion { get; set; }

        public ref NativeList<BlockRef> Blocks
        {
            [HotPath]
            get => ref _blocks;
        }

        public void Dispose()
        {
            Commands.Dispose();
            _blocks.Dispose();
        }
    }

    /// <summary>The job that runs an <see cref="IChunkJob"/> over the blocks collected before, one index per block.</summary>
    private struct ChunkJob<TJob> : IJobFor
        where TJob : struct, IChunkJob
    {
        public TJob Job;
        public EntityWorld World;
        public BlockRef* Blocks;

        public void Execute(int index)
        {
            BlockRef block = Blocks[index];
            Job.Execute(new ChunkView(World.ArchetypeList[block.Archetype], block.Data, block.Count));
        }
    }
}
