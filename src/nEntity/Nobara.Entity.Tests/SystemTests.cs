// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;
using Nobara.Core.Profiling;
using Nobara.Entity.Tests.Support;
using Nobara.Game;
using Xunit.Abstractions;
using F = Nobara.Formats;

namespace Nobara.Entity.Tests;

/// <summary>How entity systems run: once per world, with declared accesses, command buffers, jobs, and no allocation.</summary>
public sealed class SystemTests(ITestOutputHelper output) : IDisposable
{
    private readonly EntityHarness _harness = new();
    private readonly List<string> _journal = [];
    private FarmComponents _farm = null!;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void A_system_runs_once_per_world_in_the_order_of_the_dimensions_and_skips_frozen_ones()
    {
        _harness.Dimensions.Clear();
        _harness.Dimensions.AddRange([new DimensionId(2), new DimensionId(1)]);
        Farm(p => p.Worlds.AddSystem(p.Context, "example.farm.growth", new ActionSystem((world, _) => _journal.Add($"{world.Dimension.Value}@{p.Time.Tick}")), new EntitySystemOptions
        {
            Query = new QueryDescription { All = [_farm.Position, _farm.Velocity] },
            Reads = [_farm.Velocity],
            Writes = [_farm.Position],
        }));
        using Engine engine = _harness.StartSession(Side.Server);
        Probe owner = _harness.Owner;

        owner.Time.SetFrozen(new DimensionId(2), true);
        _harness.Tick(engine);

        Assert.Equal(["1@1", "2@1", "1@2"], _journal);
        TickSystemInfo system = Assert.Single(owner.Schedule.GetOrder().Single(s => s.Name == TickStages.Entities).Systems);
        Assert.Equal("example.farm.growth", system.Name);
        Assert.Equal(["component example.farm.velocity", "entity structure"], system.Reads);
        Assert.Equal(["component example.farm.position"], system.Writes);
    }

    [Fact]
    public void A_system_moves_the_entities_of_its_query()
    {
        Farm(p => p.Worlds.AddSystem(p.Context, "example.farm.move", new MoveSystem(_farm), MoveOptions()));
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId moving = world.Create(world.GetArchetype(_farm.Position, _farm.Velocity));
        world.Set(moving, _farm.Velocity, new Velocity { Value = new Vector3(3, 0, 0) });
        EntityId still = world.Create(world.GetArchetype(_farm.Position));

        _harness.Tick(engine, 2);

        Assert.Equal(2 * 3f / 30, world.Read(moving, _farm.Position).Value.X, 5);
        Assert.Equal(Vector3.Zero, world.Read(still, _farm.Position).Value);
    }

    [Fact]
    public void In_development_mode_a_system_that_writes_a_component_it_only_reads_is_stopped()
    {
        _harness.DevelopmentMode = true;
        Farm(
            p => p.Worlds.AddSystem(p.Context, "example.farm.sneaky", new ActionSystem((world, _) =>
            {
                foreach (ChunkView block in world.Query(new QueryDescription { All = [_farm.Velocity] }))
                {
                    block.Write(_farm.Velocity);
                }
            }), new EntitySystemOptions { Reads = [_farm.Velocity] }),
            F.Policy.Log);
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        world.Create(world.GetArchetype(_farm.Velocity));

        // The check throws in the storage of the entity module, so the error is charged to it.
        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Tick(engine));

        Assert.Equal("nobara.entity", stop.ModuleId);
        LogEntry error = _harness.Log.Entries.First(e => e.Exception is JobAccessException);
        Assert.Equal(
            "The tick system 'example.farm.sneaky' of module 'example.farm' writes 'component example.farm.velocity', which it does not declare as written. Declare it in its accesses.",
            error.Exception!.Message);
    }

    [Fact]
    public void In_development_mode_a_job_of_a_system_that_writes_an_undeclared_component_is_stopped()
    {
        _harness.DevelopmentMode = true;
        Farm(
            p => p.Worlds.AddSystem(p.Context, "example.farm.sneaky", new ParallelSystem<SneakyJob>(new SneakyJob { Velocity = _farm.Velocity }), new EntitySystemOptions
            {
                Query = new QueryDescription { All = [_farm.Position, _farm.Velocity] },
                Writes = [_farm.Position],
                ParallelThreshold = 0,
            }),
            F.Policy.Log);
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        world.Create(world.GetArchetype(_farm.Position, _farm.Velocity), new EntityId[10_000]);

        // The job fails; its error reaches the next synchronization point.
        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Tick(engine, 2));

        Assert.Equal("nobara.entity", stop.ModuleId);
        LogEntry error = _harness.Log.Entries.First(e => e.Exception is JobFailedException);
        JobAccessException access = Assert.IsType<JobAccessException>(error.Exception!.InnerException);
        Assert.Contains("of module 'example.farm' writes 'component example.farm.velocity'", access.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_structure_is_locked_while_a_system_runs_and_its_commands_apply_after()
    {
        Exception? direct = null;
        int burningDuringRun = -1;
        _harness.DevelopmentMode = true;
        Farm(p => p.Worlds.AddSystem(p.Context, "example.farm.spread", new CommandSystem(run =>
        {
            if (run.Query.Count() == 0)
            {
                return;
            }

            direct = Record.Exception(() => run.World.Create());
            foreach (ChunkView block in run.Query)
            {
                foreach (EntityId entity in block.Entities)
                {
                    run.Commands.Add(entity, _farm.Burning, new Burning { Remaining = 5 });
                }
            }

            run.Commands.Create(run.World.GetArchetype(_farm.Burning));
            burningDuringRun = run.World.Query(new QueryDescription { All = [_farm.Burning] }).Count();
        }), new EntitySystemOptions
        {
            Query = new QueryDescription { All = [_farm.Health], None = [_farm.Burning] },
            ChangesStructure = true,
        }));
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        world.Create(world.GetArchetype(_farm.Health), new EntityId[3]);

        _harness.Tick(engine);

        Assert.IsType<InvalidOperationException>(direct);
        Assert.Equal(0, burningDuringRun);
        Assert.False(world.IsLocked);
        Assert.Equal(4, world.Query(new QueryDescription { All = [_farm.Burning] }).Count());
        Assert.Equal(4, world.EntityCount);
    }

    [Fact]
    public void In_development_mode_commands_of_a_system_that_does_not_declare_structural_changes_are_stopped()
    {
        _harness.DevelopmentMode = true;
        Farm(
            p => p.Worlds.AddSystem(p.Context, "example.farm.spawn", new CommandSystem(run => run.Commands.Create(run.World.GetArchetype(_farm.Health)))),
            F.Policy.Log);
        using Engine engine = _harness.Start();

        // The first frame starts the session, then runs the first tick.
        Assert.Throws<EngineStopException>(() => _harness.Tick(engine));

        LogEntry error = _harness.Log.Entries.First(e => e.Exception is JobAccessException);
        Assert.Equal(
            "The tick system 'example.farm.spawn' of module 'example.farm' writes 'entity structure', which it does not declare as written. Declare it in its accesses.",
            error.Exception!.Message);
    }

    [Fact]
    public void An_error_in_one_world_follows_the_policy_of_the_module_and_the_other_worlds_still_run()
    {
        _harness.Dimensions.Add(new DimensionId(1));
        Farm(
            p => p.Worlds.AddSystem(p.Context, "example.farm.fragile", new ActionSystem((world, _) =>
            {
                _journal.Add(world.Dimension.ToString());
                if (world.Dimension == DimensionId.Default)
                {
                    throw new InvalidOperationException("broken");
                }
            })),
            F.Policy.Log);

        using Engine engine = _harness.StartSession(Side.Server);

        Assert.Equal(["dimension 0", "dimension 1"], _journal);
        LogEntry error = Assert.Single(_harness.Log.Entries, e => e.Exception is InvalidOperationException { Message: "broken" });
        Assert.Equal("example.farm", error.Source);
    }

    [Fact]
    public void The_profiler_measures_each_system_in_each_world()
    {
        _harness.Profiling = true;
        _harness.Dimensions.Add(new DimensionId(3));
        Farm(p => p.Worlds.AddSystem(p.Context, "example.farm.growth", new ActionSystem((_, _) => { })));
        using Engine engine = _harness.StartSession(Side.Server);

        _harness.Tick(engine, 2);

        IReadOnlyList<ProfileEntry> entries = engine.Profiler.Snapshot();
        Assert.Equal(3, Assert.Single(entries, e => e.Name == "Tick system example.farm.growth").Calls);
        foreach (string name in new[] { "Tick system example.farm.growth in dimension 0", "Tick system example.farm.growth in dimension 3" })
        {
            ProfileEntry world = Assert.Single(entries, e => e.Name == name);
            Assert.Equal("example.farm", world.Module);
            Assert.Equal(3, world.Calls);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 128)]
    public void A_system_goes_through_100_000_entities_without_allocating(bool developmentMode, int parallelThreshold)
    {
        const int count = 100_000;
        const int ticks = 300;
        _harness.DevelopmentMode = developmentMode;
        _harness.Profiling = true;
        Farm(p => p.Worlds.AddSystem(p.Context, "example.farm.move", new MoveSystem(_farm), MoveOptions(parallelThreshold)));
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        var entities = new EntityId[count];
        world.Create(world.GetArchetype(_farm.Position, _farm.Velocity), entities);
        foreach (EntityId entity in entities)
        {
            world.Set(entity, _farm.Velocity, new Velocity { Value = Vector3.One });
        }

        _harness.Tick(engine, 50);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long beforeAll = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        _harness.Tick(engine, ticks);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long allocatedAll = GC.GetTotalAllocatedBytes(precise: true) - beforeAll;

        Assert.Equal(0, allocated);
        Assert.Equal((ticks + 50) / 30f, world.Read(entities[^1], _farm.Position).Value.X, 2);
        ProfileEntry system = Assert.Single(engine.Profiler.Snapshot(), e => e.Name == "Tick system example.farm.move in dimension 0");
        output.WriteLine(
            $"{count} entities in {world.GetArchetype(_farm.Position, _farm.Velocity).BlockCount} blocks, {Environment.ProcessorCount} processors, development mode {developmentMode}, parallel threshold {parallelThreshold}: " +
            $"{system}; whole frame {elapsed.TotalMicroseconds / ticks:F0} µs; {allocatedAll} bytes allocated on all threads in {ticks} ticks.");
    }

    private EntitySystemOptions MoveOptions(int parallelThreshold = 128) => new()
    {
        Query = new QueryDescription { All = [_farm.Position, _farm.Velocity] },
        Reads = [_farm.Velocity],
        Writes = [_farm.Position],
        ParallelThreshold = parallelThreshold,
    };

    private void Farm(Action<Probe> initialize, F.Policy? policy = null) =>
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            _farm = new FarmComponents(p);
            initialize(p);
        }, policy);

    /// <summary>Moves entities by their velocity, over the blocks of its query.</summary>
    private sealed class MoveSystem(FarmComponents farm) : IEntitySystem
    {
        public void Run(in EntitySystemRun run) => run.RunChunks(new MoveJob
        {
            Position = farm.Position,
            Velocity = farm.Velocity,
            Delta = (float)run.Time.Delta.TotalSeconds,
        });
    }

    private struct MoveJob : IChunkJob
    {
        public ComponentType<Position> Position;
        public ComponentType<Velocity> Velocity;
        public float Delta;

        public readonly void Execute(in ChunkView chunk)
        {
            Span<Position> positions = chunk.Write(Position);
            ReadOnlySpan<Velocity> velocities = chunk.Read(Velocity);
            for (int i = 0; i < chunk.Count; i++)
            {
                positions[i].Value += velocities[i].Value * Delta;
            }
        }
    }

    private struct SneakyJob : IChunkJob
    {
        public ComponentType<Velocity> Velocity;

        public readonly void Execute(in ChunkView chunk) => chunk.Write(Velocity);
    }

    private sealed class ParallelSystem<TJob>(TJob job) : IEntitySystem
        where TJob : struct, IChunkJob
    {
        public void Run(in EntitySystemRun run) => run.RunChunks(job);
    }

    private sealed class CommandSystem(Action<SystemRunParts> action) : IEntitySystem
    {
        public void Run(in EntitySystemRun run) => action(new SystemRunParts(run.World, run.Query, run.Commands));
    }

    /// <summary>The parts of an <see cref="EntitySystemRun"/> that a lambda can hold.</summary>
    private sealed record SystemRunParts(EntityWorld World, EntityQuery Query, EntityCommandBuffer Commands);
}
