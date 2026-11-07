// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Voxel;

/// <summary>Inclusive local block bounds of a finite grid.</summary>
public readonly struct GridBounds : IEquatable<GridBounds>
{
    /// <summary>Initializes bounds.</summary>
    /// <exception cref="ArgumentException">The minimum is after the maximum.</exception>
    public GridBounds(BlockCoordinate minimum, BlockCoordinate maximum)
    {
        if (minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z) throw new ArgumentException("The minimum bound must not be after the maximum bound.");
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the minimum block coordinate.</summary>
    public BlockCoordinate Minimum { get; }

    /// <summary>Gets the maximum block coordinate.</summary>
    public BlockCoordinate Maximum { get; }

    /// <summary>Tells whether a block is inside the bounds.</summary>
    [HotPath]
    public bool Contains(BlockCoordinate coordinate) => coordinate.X >= Minimum.X && coordinate.X <= Maximum.X && coordinate.Y >= Minimum.Y && coordinate.Y <= Maximum.Y && coordinate.Z >= Minimum.Z && coordinate.Z <= Maximum.Z;

    /// <inheritdoc/>
    public bool Equals(GridBounds other) => Minimum == other.Minimum && Maximum == other.Maximum;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GridBounds other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Minimum, Maximum);
}

/// <summary>Options used to create a voxel grid entity.</summary>
public sealed class GridOptions
{
    /// <summary>Gets or initializes the world transform.</summary>
    public WorldTransform Transform { get; init; } = WorldTransform.Identity;

    /// <summary>Gets or initializes optional finite local block bounds.</summary>
    public GridBounds? Bounds { get; init; }

    /// <summary>Gets or initializes whether this is the terrain grid of its dimension.</summary>
    public bool IsTerrain { get; init; }

    /// <summary>Gets or initializes where the chunks live; <see langword="null"/> gives <see cref="GridResidency.Tickets"/> to a terrain grid and <see cref="GridResidency.Kept"/> to another grid.</summary>
    public GridResidency? Residency { get; init; }
}

/// <summary>Identifies a voxel grid storage instance.</summary>
public readonly struct GridHandle : IEquatable<GridHandle>
{
    internal GridHandle(int index, int generation) => (Index, Generation) = (index, generation);

    /// <summary>Gets the slot index.</summary>
    public int Index { get; }

    /// <summary>Gets the slot generation.</summary>
    public int Generation { get; }

    /// <inheritdoc/>
    public bool Equals(GridHandle other) => Index == other.Index && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GridHandle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <summary>Compares two handles.</summary>
    public static bool operator ==(GridHandle left, GridHandle right) => left.Equals(right);

    /// <summary>Compares two handles.</summary>
    public static bool operator !=(GridHandle left, GridHandle right) => !left.Equals(right);
}

/// <summary>The unmanaged ECS component that points to a grid storage.</summary>
public struct GridComponent
{
    /// <summary>Gets or sets the storage handle.</summary>
    public GridHandle Handle;
}

/// <summary>A voxel grid: an ECS entity with a separate chunk storage.</summary>
public sealed class Grid : IDisposable
{
    private readonly EntityWorld _world;
    private readonly ComponentType<WorldTransform> _transformComponent;
    private readonly GridSpatialIndex _index;
    private bool _disposed;

    internal Grid(ModuleContext owner, EntityWorld world, EntityId entity, GridHandle handle, ComponentType<WorldTransform> transformComponent, GridOptions options, Func<ChunkCoordinate, JobData> chunkDataFactory, GridSpatialIndex index)
    {
        OwnerModuleId = owner.Module.Id;
        _world = world;
        Entity = entity;
        Handle = handle;
        _transformComponent = transformComponent;
        _index = index;
        Storage = new GridStorage(this, options.Bounds, chunkDataFactory);
        Bounds = options.Bounds;
        IsTerrain = options.IsTerrain;
        Residency = options.Residency ?? (options.IsTerrain ? GridResidency.Tickets : GridResidency.Kept);
        UpdateSpatialIndex();
    }

    /// <summary>Gets the module that created this grid.</summary>
    public string OwnerModuleId { get; }

    /// <summary>Gets the dimension containing the grid.</summary>
    public DimensionId Dimension
    {
        [HotPath]
        get => _world.Dimension;
    }

    /// <summary>Gets the ECS entity of the grid.</summary>
    public EntityId Entity { get; }

    /// <summary>Gets the stable storage handle.</summary>
    public GridHandle Handle { get; }

    /// <summary>Gets the neutral identifier used by block positions.</summary>
    public ulong Identifier
    {
        [HotPath]
        get => (uint)Handle.Index;
    }

    /// <summary>Gets whether this is the terrain grid.</summary>
    public bool IsTerrain { get; }

    /// <summary>Gets the optional local bounds.</summary>
    public GridBounds? Bounds { get; }

    /// <summary>Gets where the chunks live: loaded by tickets, or kept while the grid exists.</summary>
    public GridResidency Residency { get; }

    /// <summary>Gets whether the grid is destroyed.</summary>
    public bool IsDestroyed
    {
        [HotPath]
        get => _disposed;
    }

    /// <summary>Gets the chunk storage.</summary>
    public GridStorage Storage { get; }

    /// <summary>Gets the world transform stored on the grid entity.</summary>
    public WorldTransform Transform
    {
        get => _world.Read(Entity, _transformComponent);
    }

    /// <summary>Updates the transform of this grid and its spatial index.</summary>
    /// <param name="transform">The new transform.</param>
    public void UpdateTransform(in WorldTransform transform)
    {
        RequireAlive();
        _world.Set(Entity, _transformComponent, transform);
        UpdateSpatialIndex();
    }

    /// <summary>Reads a block state in grid-local coordinates.</summary>
    [HotPath]
    public bool TryGetBlock(BlockCoordinate coordinate, out uint state) => Storage.TryGetState(coordinate, out state);

    /// <summary>Reads the state, returning air when the containing chunk is absent.</summary>
    [HotPath]
    public uint GetBlock(BlockCoordinate coordinate) => Storage.GetState(coordinate);

    /// <summary>Releases the grid storage. The owner normally calls <see cref="IVoxelWorlds.Destroy"/> instead.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Storage.Dispose();
    }

    internal void UpdateSpatialIndex() => _index.Update(this);

    internal void RequireAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Chunk storage belonging to one grid.</summary>
public sealed class GridStorage : IDisposable
{
    private readonly Dictionary<ChunkCoordinate, Chunk> _chunks = [];
    private readonly GridBounds? _bounds;
    private readonly Func<ChunkCoordinate, JobData> _chunkDataFactory;
    private readonly Grid _grid;
    private bool _disposed;

    internal GridStorage(Grid grid, GridBounds? bounds, Func<ChunkCoordinate, JobData> chunkDataFactory) => (_grid, _bounds, _chunkDataFactory) = (grid, bounds, chunkDataFactory);

    /// <summary>Called on the loop thread when a chunk becomes resident, unless it is being filled by a provider.</summary>
    internal IChunkResidencyObserver? Observer { get; set; }

    /// <summary>Gets the number of resident chunks.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>Gets a resident chunk without creating it.</summary>
    [HotPath]
    public bool TryGetChunk(ChunkCoordinate coordinate, out Chunk? chunk) => _chunks.TryGetValue(coordinate, out chunk);

    /// <summary>Gets or creates a resident chunk.</summary>
    /// <param name="coordinate">The chunk coordinate.</param>
    /// <returns>The resident chunk.</returns>
    public Chunk GetOrCreateChunk(ChunkCoordinate coordinate)
    {
        RequireAlive();
        return _chunks.TryGetValue(coordinate, out Chunk? chunk) ? chunk : AddChunk(coordinate);
    }

    internal IEnumerable<KeyValuePair<ChunkCoordinate, Chunk>> ResidentChunks
    {
        get
        {
            RequireAlive();
            return _chunks;
        }
    }

    internal bool Contains(BlockCoordinate coordinate)
    {
        RequireInBounds(coordinate);
        return true;
    }

    /// <summary>Reads a state, reporting whether its chunk is resident.</summary>
    [HotPath]
    public bool TryGetState(BlockCoordinate coordinate, out uint state)
    {
        RequireInBounds(coordinate);
        ChunkCoordinate chunkCoordinate = VoxelCoordinates.ToChunk(coordinate);
        if (_chunks.TryGetValue(chunkCoordinate, out Chunk? chunk))
        {
            state = chunk.GetState(VoxelCoordinates.ToIndex(coordinate));
            return true;
        }

        state = 0;
        return false;
    }

    /// <summary>Reads a state and returns air for a non-resident chunk.</summary>
    [HotPath]
    public uint GetState(BlockCoordinate coordinate) => TryGetState(coordinate, out uint state) ? state : 0;

    /// <summary>Tells whether a chunk coordinate has cells inside the bounds of the grid.</summary>
    [HotPath]
    public bool Overlaps(ChunkCoordinate coordinate)
    {
        if (_bounds is not { } bounds) return true;
        long minX = (long)coordinate.X * VoxelCoordinates.ChunkSize;
        long minY = (long)coordinate.Y * VoxelCoordinates.ChunkSize;
        long minZ = (long)coordinate.Z * VoxelCoordinates.ChunkSize;
        return minX <= bounds.Maximum.X && minX + VoxelCoordinates.ChunkSize > bounds.Minimum.X
            && minY <= bounds.Maximum.Y && minY + VoxelCoordinates.ChunkSize > bounds.Minimum.Y
            && minZ <= bounds.Maximum.Z && minZ + VoxelCoordinates.ChunkSize > bounds.Minimum.Z;
    }

    /// <summary>Copies a resident chunk with requested neighbor borders.</summary>
    public ChunkSnapshot CopyFrozen(ChunkCoordinate coordinate, params ChunkCoordinate[] borders)
    {
        RequireAlive();
        if (!_chunks.TryGetValue(coordinate, out Chunk? chunk)) throw new KeyNotFoundException($"Chunk {coordinate} is not resident.");
        var neighbors = new List<Chunk>(borders.Length);
        foreach (ChunkCoordinate border in borders) if (_chunks.TryGetValue(border, out Chunk? neighbor)) neighbors.Add(neighbor);
        return chunk.CopyFrozen([.. neighbors]);
    }

    /// <summary>Releases every resident chunk.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Chunk chunk in _chunks.Values) chunk.Dispose();
        _chunks.Clear();
        GC.SuppressFinalize(this);
    }

    private Chunk AddChunk(ChunkCoordinate coordinate)
    {
        Chunk chunk = AddChunkSilently(coordinate);
        Observer?.OnResident(_grid, chunk);
        return chunk;
    }

    /// <summary>Adds an empty chunk without telling the observer, for a provider result that tells it once filled.</summary>
    internal Chunk AddChunkSilently(ChunkCoordinate coordinate)
    {
        RequireAlive();
        Chunk chunk = new(coordinate, _chunkDataFactory(coordinate));
        _chunks.Add(coordinate, chunk);
        return chunk;
    }

    /// <summary>Removes and releases a resident chunk.</summary>
    internal bool RemoveChunk(ChunkCoordinate coordinate)
    {
        RequireAlive();
        if (!_chunks.Remove(coordinate, out Chunk? chunk)) return false;
        chunk.Dispose();
        return true;
    }

    [HotPath]
    private void RequireInBounds(BlockCoordinate coordinate)
    {
        if (_bounds is { } bounds && !bounds.Contains(coordinate)) throw new ArgumentOutOfRangeException(nameof(coordinate), $"Block {coordinate} is outside the grid bounds.");
    }

    private void RequireAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
}

