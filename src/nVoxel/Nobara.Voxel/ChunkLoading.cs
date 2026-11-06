// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Voxel;

/// <summary>What a ticket asks of the chunks it holds. A chunk takes the highest level of its tickets.</summary>
public enum TicketLevel
{
    /// <summary>The chunk is loaded: it can be read and changed, but its blocks do not tick.</summary>
    Loaded,

    /// <summary>The chunk is loaded and its blocks tick once every <see cref="VoxelSettings.ReducedTickInterval"/> ticks.</summary>
    ReducedActive,

    /// <summary>The chunk is loaded and its blocks tick at every tick.</summary>
    Active,
}

/// <summary>Where the chunks of a grid live while the grid exists.</summary>
public enum GridResidency
{
    /// <summary>A chunk is loaded while a ticket holds it, produced by the chunk providers, and unloaded after a delay without a ticket. The default of terrain grids.</summary>
    Tickets,

    /// <summary>Every chunk that has been written stays in memory until the grid is destroyed; an absent chunk is air. The default of other grids.</summary>
    Kept,
}

/// <summary>Where a chunk is in its loading.</summary>
public enum ChunkLoadState
{
    /// <summary>The chunk is not in memory and nothing asked for it; queries report it as not loaded.</summary>
    NotLoaded,

    /// <summary>A ticket holds the chunk and a provider is producing it; queries report it as not loaded.</summary>
    Loading,

    /// <summary>The chunk is in memory.</summary>
    Loaded,

    /// <summary>The provider of the chunk failed; nVoxel asks again once no ticket holds the chunk and a new ticket does.</summary>
    Failed,
}

/// <summary>The loading state of a chunk and the level its tickets give it.</summary>
/// <param name="State">Where the chunk is in its loading.</param>
/// <param name="Level">The highest level of its tickets, or <see langword="null"/> when no ticket holds it.</param>
public readonly record struct ChunkStatus(ChunkLoadState State, TicketLevel? Level)
{
    /// <summary>Gets whether the chunk is in memory.</summary>
    public bool IsLoaded => State == ChunkLoadState.Loaded;

    /// <summary>Gets whether the blocks of the chunk tick, at full or reduced frequency.</summary>
    public bool IsActive => State == ChunkLoadState.Loaded && Level is TicketLevel.Active or TicketLevel.ReducedActive;
}

/// <summary>A cylindrical area of chunks around a center chunk: a horizontal radius and a vertical half-height, in chunks.</summary>
/// <param name="Radius">The horizontal radius: a chunk is inside when the squared offsets along X and Z add up to at most the squared radius.</param>
/// <param name="Height">The vertical half-height: a chunk is inside when its offset along Y is at most this.</param>
public readonly record struct ChunkArea(int Radius, int Height)
{
    /// <summary>Tells whether an offset in chunks from the center is inside the area.</summary>
    /// <param name="x">The offset along X.</param>
    /// <param name="y">The offset along Y.</param>
    /// <param name="z">The offset along Z.</param>
    /// <returns><see langword="true"/> when the chunk at that offset is inside.</returns>
    [HotPath]
    public bool Contains(int x, int y, int z) => ((long)x * x) + ((long)z * z) <= (long)Radius * Radius && Math.Abs((long)y) <= Height;

    /// <summary>Gets the number of chunks inside the area.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            for (int x = -Radius; x <= Radius; x++)
            {
                for (int z = -Radius; z <= Radius; z++)
                {
                    if (Contains(x, 0, z)) count++;
                }
            }

            return count * ((2 * Height) + 1);
        }
    }
}

/// <summary>What a chunk provider is asked to produce.</summary>
public readonly struct ChunkRequest
{
    internal ChunkRequest(DimensionId dimension, ulong grid, bool isTerrain, GridBounds? bounds, ChunkCoordinate chunk)
    {
        Dimension = dimension;
        Grid = grid;
        IsTerrain = isTerrain;
        Bounds = bounds;
        Chunk = chunk;
    }

    /// <summary>Gets the dimension of the grid.</summary>
    public DimensionId Dimension { get; }

    /// <summary>Gets the identifier of the grid, as <see cref="Voxel.Grid.Identifier"/> gives it.</summary>
    public ulong Grid { get; }

    /// <summary>Gets whether the grid is the terrain of its dimension.</summary>
    public bool IsTerrain { get; }

    /// <summary>Gets the bounds of the grid, if it has some.</summary>
    public GridBounds? Bounds { get; }

    /// <summary>Gets the chunk to produce.</summary>
    public ChunkCoordinate Chunk { get; }
}

/// <summary>
/// A source of chunks: a save, a generator, or the network. nVoxel asks the
/// providers in order for a chunk that a ticket holds and that is not in
/// memory; the first one that accepts it produces it.
/// </summary>
/// <remarks>
/// <see cref="Accepts"/> runs on the loop thread of the context;
/// <see cref="Produce"/> runs in a background job, on a worker thread, and must
/// read nothing that the loop thread changes. nVoxel applies the result on the
/// loop thread, at the <c>World</c> stage, as a technical batch whose cause is
/// <see cref="Nobara.Blocks.BlockCause.Generation"/>.
/// </remarks>
public interface IChunkProvider
{
    /// <summary>Tells whether this provider produces a chunk; the next provider is asked when it does not.</summary>
    /// <param name="request">The chunk asked for.</param>
    /// <returns><see langword="true"/> to produce it.</returns>
    bool Accepts(in ChunkRequest request);

    /// <summary>Writes the block states of an accepted chunk.</summary>
    /// <param name="request">The chunk asked for.</param>
    /// <param name="states">The 32,768 states, by local index (<see cref="VoxelCoordinates.ToIndex(LocalBlockCoordinate)"/>), all air when the call starts.</param>
    /// <remarks>The method runs in a job: the analyzers check it as hot path code, so it does not allocate.</remarks>
    [HotPath]
    void Produce(in ChunkRequest request, Span<uint> states);
}

/// <summary>Why a chunk entered or left memory.</summary>
public enum ChunkResidencyKind
{
    /// <summary>The chunk is now in memory, with its blocks.</summary>
    Loaded,

    /// <summary>The chunk is about to leave memory: it can still be read during the call.</summary>
    Unloading,
}

/// <summary>A chunk that entered or left memory.</summary>
/// <param name="Kind">Whether it entered or leaves memory.</param>
/// <param name="Grid">Its grid.</param>
/// <param name="Chunk">Its coordinate.</param>
public readonly record struct ChunkResidencyChange(ChunkResidencyKind Kind, Grid Grid, ChunkCoordinate Chunk);

/// <summary>A revocable registration of nVoxel loading: a ticket, a provider, or a subscription.</summary>
public abstract class LoadingRegistration : IDisposable
{
    private protected LoadingRegistration(string ownerId) => OwnerId = ownerId;

    /// <summary>Gets the module that made the registration.</summary>
    public string OwnerId { get; }

    /// <summary>Gets whether the registration still has an effect: it is not disposed, and its grid and session still exist.</summary>
    public bool IsActive => !IsDisposed && !IsDetached;

    internal bool IsDisposed { get; private set; }

    internal bool IsDetached { get; set; }

    /// <summary>Removes the registration. Disposing twice does nothing.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        OnDispose();
        GC.SuppressFinalize(this);
    }

    private protected abstract void OnDispose();
}

/// <summary>A ticket that holds one chunk at a level.</summary>
public sealed class ChunkTicket : LoadingRegistration
{
    private readonly ChunkLoader _loader;

    internal ChunkTicket(ChunkLoader loader, string ownerId, Grid grid, ChunkCoordinate chunk, TicketLevel level)
        : base(ownerId)
    {
        _loader = loader;
        Grid = grid;
        Chunk = chunk;
        Level = level;
    }

    /// <summary>Gets the grid.</summary>
    public Grid Grid { get; }

    /// <summary>Gets the chunk held.</summary>
    public ChunkCoordinate Chunk { get; }

    /// <summary>Gets the level it gives the chunk.</summary>
    public TicketLevel Level { get; }

    private protected override void OnDispose() => _loader.Release(this);
}

/// <summary>A ticket that holds the chunks of a cylindrical area around a position that can move.</summary>
public sealed class AreaTicket : LoadingRegistration
{
    private readonly ChunkLoader _loader;

    internal AreaTicket(ChunkLoader loader, string ownerId, Grid grid, ChunkArea area, TicketLevel level)
        : base(ownerId)
    {
        _loader = loader;
        Grid = grid;
        Area = area;
        Level = level;
    }

    /// <summary>Gets the grid.</summary>
    public Grid Grid { get; }

    /// <summary>Gets the area around the center chunk.</summary>
    public ChunkArea Area { get; }

    /// <summary>Gets the level it gives the chunks of the area.</summary>
    public TicketLevel Level { get; }

    /// <summary>Gets the chunk of the grid that contains the center.</summary>
    public ChunkCoordinate Center { get; internal set; }

    /// <summary>Moves the area to a new position in the world; the chunks change when the center enters another chunk.</summary>
    /// <param name="position">The new center, converted to the frame of the grid with its current transform.</param>
    /// <exception cref="ObjectDisposedException">The ticket is disposed.</exception>
    public void Move(in WorldPosition position)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _loader.Move(this, position);
    }

    private protected override void OnDispose() => _loader.Release(this);
}

/// <summary>
/// The tickets of a viewer, such as the local player before the network: the
/// view area at <see cref="TicketLevel.Loaded"/> and the simulation area at
/// <see cref="TicketLevel.Active"/>, around one position.
/// </summary>
public sealed class ViewerTicket : IDisposable
{
    internal ViewerTicket(AreaTicket view, AreaTicket simulation) => (View, Simulation) = (view, simulation);

    /// <summary>Gets the ticket of the view area.</summary>
    public AreaTicket View { get; }

    /// <summary>Gets the ticket of the simulation area.</summary>
    public AreaTicket Simulation { get; }

    /// <summary>Moves both areas.</summary>
    /// <param name="position">The new position of the viewer in the world.</param>
    public void Move(in WorldPosition position)
    {
        View.Move(position);
        Simulation.Move(position);
    }

    /// <summary>Removes both tickets.</summary>
    public void Dispose()
    {
        View.Dispose();
        Simulation.Dispose();
    }
}

/// <summary>The registration of a chunk provider.</summary>
public sealed class ChunkProviderHandle : LoadingRegistration
{
    private readonly ChunkLoader _loader;

    internal ChunkProviderHandle(ChunkLoader loader, ModuleContext owner, IChunkProvider provider, int priority, int sequence)
        : base(owner.Module.Id)
    {
        _loader = loader;
        Owner = owner;
        Provider = provider;
        Priority = priority;
        Sequence = sequence;
    }

    /// <summary>Gets the provider.</summary>
    public IChunkProvider Provider { get; }

    /// <summary>Gets the priority: higher is asked first.</summary>
    public int Priority { get; }

    internal ModuleContext Owner { get; }

    internal int Sequence { get; }

    private protected override void OnDispose() => _loader.Release(this);
}

/// <summary>A subscription to chunks entering and leaving memory.</summary>
public sealed class ChunkResidencySubscription : LoadingRegistration
{
    private readonly ChunkLoader _loader;

    internal ChunkResidencySubscription(ChunkLoader loader, ModuleContext owner, Action<ChunkResidencyChange> handler)
        : base(owner.Module.Id)
    {
        _loader = loader;
        Owner = owner;
        Handler = handler;
    }

    internal ModuleContext Owner { get; }

    internal Action<ChunkResidencyChange> Handler { get; }

    private protected override void OnDispose() => _loader.Release(this);
}

/// <summary>
/// The loading of chunks of one context: tickets that hold chunks, the chain
/// of chunk providers, and the chunks that enter and leave memory. Resolve it
/// with <c>context.Resolve&lt;IChunkLoading&gt;()</c>.
/// </summary>
/// <remarks>
/// Use it on the loop thread of the context, during a world session.
/// Tickets apply to grids whose residency is <see cref="GridResidency.Tickets"/>;
/// on other grids they only give a level, for block ticks. A chunk that no
/// ticket holds is unloaded <see cref="VoxelSettings.UnloadDelay"/> ticks
/// later. Providers are asked at the <c>World</c> stage, nearest chunks of the
/// highest level first.
/// </remarks>
public interface IChunkLoading
{
    /// <summary>Gets the view area of the configuration (<see cref="VoxelSettings.ViewRadius"/>, <see cref="VoxelSettings.ViewHeight"/>).</summary>
    ChunkArea ViewArea { get; }

    /// <summary>Gets the simulation area of the configuration (<see cref="VoxelSettings.SimulationRadius"/>, <see cref="VoxelSettings.SimulationHeight"/>).</summary>
    ChunkArea SimulationArea { get; }

    /// <summary>Holds one chunk.</summary>
    /// <param name="owner">The module that owns the ticket and disposes it before the session ends.</param>
    /// <param name="grid">The grid.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="level">The level.</param>
    /// <returns>The ticket.</returns>
    /// <exception cref="InvalidOperationException">No world session is active or starting.</exception>
    /// <exception cref="ArgumentException">The grid belongs to another context or is destroyed.</exception>
    ChunkTicket AddTicket(ModuleContext owner, Grid grid, ChunkCoordinate chunk, TicketLevel level);

    /// <summary>Holds the chunks of an area around a position.</summary>
    /// <param name="owner">The module that owns the ticket and disposes it before the session ends.</param>
    /// <param name="grid">The grid.</param>
    /// <param name="center">The center in the world.</param>
    /// <param name="area">The area, in chunks.</param>
    /// <param name="level">The level.</param>
    /// <returns>The ticket.</returns>
    /// <exception cref="InvalidOperationException">No world session is active or starting.</exception>
    /// <exception cref="ArgumentException">The grid belongs to another context or is destroyed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius or the height is negative.</exception>
    AreaTicket AddAreaTicket(ModuleContext owner, Grid grid, in WorldPosition center, ChunkArea area, TicketLevel level);

    /// <summary>Holds the view area and the simulation area of the configuration around a viewer.</summary>
    /// <param name="owner">The module that owns the tickets.</param>
    /// <param name="grid">The grid, usually the terrain.</param>
    /// <param name="center">The position of the viewer.</param>
    /// <returns>The tickets.</returns>
    ViewerTicket AddViewer(ModuleContext owner, Grid grid, in WorldPosition center);

    /// <summary>Adds a chunk provider to the chain.</summary>
    /// <param name="owner">The module of the provider, to which its failures are reported.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="priority">Higher is asked first; at equal priority, providers follow the load order of their modules, then the order of registration.</param>
    /// <returns>The handle that removes the provider; a chunk it is producing is then dropped.</returns>
    ChunkProviderHandle AddProvider(ModuleContext owner, IChunkProvider provider, int priority = 0);

    /// <summary>Subscribes to chunks entering and leaving memory, in every grid of the context.</summary>
    /// <param name="owner">The module that subscribes.</param>
    /// <param name="handler">Called on the loop thread when a chunk is loaded, and before it is unloaded.</param>
    /// <returns>The subscription.</returns>
    ChunkResidencySubscription SubscribeResidency(ModuleContext owner, Action<ChunkResidencyChange> handler);

    /// <summary>Gets the loading state and level of a chunk.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="chunk">The chunk.</param>
    /// <returns>The status.</returns>
    ChunkStatus GetStatus(Grid grid, ChunkCoordinate chunk);

    /// <summary>Gets the number of chunks in memory in every grid of the context.</summary>
    int LoadedChunks { get; }

    /// <summary>Gets the number of chunks that providers are producing.</summary>
    int PendingChunks { get; }
}
