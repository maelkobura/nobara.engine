// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Game;

namespace Nobara.Entity;

/// <summary>
/// The code of an entity system: it runs once per tick in each world, in
/// the order of the dimensions, skipping frozen dimensions.
/// </summary>
/// <remarks>
/// <see cref="Run"/> is hot path code, checked by the engine analyzers in
/// every implementation. It runs on the loop thread; work spread over
/// processors goes through <see cref="EntitySystemRun.RunChunks{TJob}(in TJob)"/>.
/// </remarks>
public interface IEntitySystem
{
    /// <summary>Runs the system on one world for one tick.</summary>
    /// <param name="run">The world, the tick, the query of the system, and its command buffer.</param>
    [HotPath]
    void Run(in EntitySystemRun run);
}

/// <summary>
/// Work on one block of entities, which an entity system runs over the
/// blocks of a query, on worker threads when the blocks are many. Implement
/// it on a <see langword="struct"/>.
/// </summary>
/// <remarks>
/// Each batch of blocks runs on its own copy of the struct, so the job keeps
/// no result in its fields. <see cref="Execute"/> is hot path code.
/// </remarks>
public interface IChunkJob
{
    /// <summary>Runs the job on one block.</summary>
    /// <param name="chunk">The block.</param>
    [HotPath]
    void Execute(in ChunkView chunk);
}

/// <summary>How an entity system is ordered, what it touches, and how it spreads its work.</summary>
public sealed class EntitySystemOptions
{
    /// <summary>Gets the stage of the system: <see cref="TickStages.Entities"/> by default.</summary>
    public string Stage { get; init; } = TickStages.Entities;

    /// <summary>Gets the systems that run before this one, by qualified name; a name no system has is ignored.</summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>Gets the systems that run after this one, by qualified name; a name no system has is ignored.</summary>
    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>Gets the entities the system goes through: <see cref="EntitySystemRun.Query"/> in each world.</summary>
    public QueryDescription Query { get; init; } = new();

    /// <summary>Gets the components the system only reads.</summary>
    public IReadOnlyList<ComponentType> Reads { get; init; } = [];

    /// <summary>Gets the components the system writes, and may read.</summary>
    public IReadOnlyList<ComponentType> Writes { get; init; } = [];

    /// <summary>Gets other data the system only reads, such as data of another module.</summary>
    public IReadOnlyList<JobData> ReadsData { get; init; } = [];

    /// <summary>Gets other data the system writes, and may read.</summary>
    public IReadOnlyList<JobData> WritesData { get; init; } = [];

    /// <summary>
    /// Gets whether the system changes the structure of the worlds through its
    /// command buffer: creates or destroys entities, adds or removes
    /// components, changes parents. In development mode, applying commands of
    /// a system that does not declare it throws.
    /// </summary>
    public bool ChangesStructure { get; init; }

    /// <summary>
    /// Gets the number of blocks from which <see cref="EntitySystemRun.RunChunks{TJob}(in TJob)"/>
    /// spreads the blocks over worker threads; below it, it runs them on the
    /// loop thread, which costs less for a small amount of work. 0 always
    /// spreads them. The default, 128 blocks, is a starting point to tune with
    /// the profiler.
    /// </summary>
    public int ParallelThreshold { get; init; } = 128;

    /// <summary>Gets the sides where the system runs: <see cref="Side.Both"/> by default.</summary>
    public Side Side { get; init; } = Side.Both;
}

/// <summary>A registered entity system. Disposing it removes the system from the next tick on.</summary>
public sealed class EntitySystemHandle : IDisposable
{
    private readonly TickSystemHandle _tick;

    internal EntitySystemHandle(TickSystemHandle tick)
    {
        _tick = tick;
    }

    /// <summary>Gets the qualified name of the system, which is also its name among the tick systems.</summary>
    public string Name => _tick.Name;

    /// <summary>Gets whether the handle was disposed.</summary>
    public bool IsRevoked => _tick.IsRevoked;

    /// <summary>Removes the system from the next tick on.</summary>
    public void Dispose() => _tick.Dispose();
}

/// <summary>What an entity system gets for one world and one tick.</summary>
[HotPath]
public readonly ref struct EntitySystemRun
{
    private readonly EntitySystemRunner _runner;
    private readonly EntitySystemRunner.WorldState _state;

    internal EntitySystemRun(EntitySystemRunner runner, EntitySystemRunner.WorldState state, in TickTime time)
    {
        _runner = runner;
        _state = state;
        Time = time;
    }

    /// <summary>Gets the world the system runs on. Its structure is locked: record structural changes in <see cref="Commands"/>.</summary>
    public EntityWorld World => _state.World;

    /// <summary>Gets the dimension of the world.</summary>
    public DimensionId Dimension => _state.World.Dimension;

    /// <summary>Gets the tick that runs.</summary>
    public TickTime Time { get; }

    /// <summary>Gets the entities of the world that match the query of the system (<see cref="EntitySystemOptions.Query"/>).</summary>
    public EntityQuery Query => _state.Query;

    /// <summary>Gets the command buffer of the system on this world, applied once <see cref="IEntitySystem.Run"/> returns.</summary>
    public EntityCommandBuffer Commands => _state.Commands;

    /// <summary>
    /// Gets the change version of the previous run of the system on this
    /// world, or 0 for its first run: a value changed since then is newer than
    /// it (<see cref="ChunkView.HasChangedSince(ComponentType, uint)"/>).
    /// </summary>
    public uint LastRunVersion => _state.LastRunVersion;

    /// <summary>
    /// Runs a job on every block of the query of the system, and returns once
    /// all are done. From <see cref="EntitySystemOptions.ParallelThreshold"/>
    /// blocks on, the blocks are spread over worker threads, with the accesses
    /// the system declares; below it, they run on the loop thread.
    /// </summary>
    /// <typeparam name="TJob">The type of the job.</typeparam>
    /// <param name="job">The job; each batch of blocks runs on a copy.</param>
    [HotPath]
    public void RunChunks<TJob>(in TJob job)
        where TJob : struct, IChunkJob =>
        _runner.RunChunks(_state, _state.Query, job);

    /// <summary>Runs a job on every block of another query of the same world, as <see cref="RunChunks{TJob}(in TJob)"/> does.</summary>
    /// <typeparam name="TJob">The type of the job.</typeparam>
    /// <param name="query">The query, of <see cref="World"/>.</param>
    /// <param name="job">The job; each batch of blocks runs on a copy.</param>
    /// <exception cref="ArgumentException">The query belongs to another world.</exception>
    [HotPath]
    public void RunChunks<TJob>(EntityQuery query, in TJob job)
        where TJob : struct, IChunkJob
    {
        if (query.World != _state.World)
        {
            throw new ArgumentException("The query belongs to another world.", nameof(query));
        }

        _runner.RunChunks(_state, query, job);
    }
}
