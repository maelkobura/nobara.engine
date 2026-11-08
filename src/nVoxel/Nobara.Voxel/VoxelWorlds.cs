// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Jobs;
using System.Numerics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Voxel;

/// <summary>Per-context implementation of the voxel world service.</summary>
internal sealed class VoxelWorlds : IVoxelWorlds, IDisposable
{
    private readonly ModuleContext _module;
    private IEntityWorlds? _entities;
    private readonly Dictionary<DimensionId, DimensionDefinition> _dimensions = [];
    private readonly Dictionary<GridHandle, Grid> _grids = [];
    private readonly Dictionary<string, EntityArchetype> _archetypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _countedOwners = new(StringComparer.Ordinal);
    private readonly GridSpatialIndex _spatialIndex = new();
    private readonly List<Grid> _gridView = [];
    private readonly JobData _chunkAccess;
    private IGameTime? _time;
    private IWorldSessions? _sessions;
    private int _nextHandle;
    private bool _disposed;
    private VoxelBatches? _batches;
    private ChunkLoader? _loader;

    public VoxelWorlds(ModuleContext module)
    {
        _module = module;
        _chunkAccess = module.Jobs.CreateData("voxel chunk storage");
        _dimensions.Add(DimensionId.Default, new DimensionDefinition(0, null));
    }

    public IReadOnlyList<DimensionId> Dimensions => _dimensions.Keys.OrderBy(d => d.Value).ToArray();

    public IReadOnlyList<Grid> Grids => _gridView;

    public GridSpatialIndex SpatialIndex => _spatialIndex;

    internal JobData ChunkAccess => _chunkAccess;

    /// <summary>Gets the declared dimensions, enumerated without allocation.</summary>
    internal Dictionary<DimensionId, DimensionDefinition>.KeyCollection DimensionKeys
    {
        [HotPath]
        get => _dimensions.Keys;
    }

    internal void AttachLoader(ChunkLoader loader) => _loader = loader;

    internal JobData EntityStructure => _entities!.Structure;

    public ComponentType<GridComponent> GridComponent { get; private set; }

    public void Initialize(IEntityWorlds entities, IGameTime time, IWorldSessions sessions)
    {
        _entities = entities;
        GridComponent = entities.RegisterComponent<GridComponent>(_module, "nobara.voxel.grid");
        _time = time;
        _sessions = sessions;
        foreach ((DimensionId dimension, DimensionDefinition definition) in _dimensions)
        {
            if (dimension != DimensionId.Default && !time.HasDimension(dimension)) time.AddDimension(dimension, definition.Clock);
        }

        sessions.SubscribeStarting(_module, _ =>
        {
            DisposeLeftovers();
            foreach (DimensionId dimension in _dimensions.Keys.OrderBy(d => d.Value))
            {
                if (!time.HasDimension(dimension)) time.AddDimension(dimension, _dimensions[dimension].Clock);
                if (!_entities!.TryGet(dimension, out EntityWorld? existingWorld)) _entities.Create(_module, dimension);
                if (!TryGetTerrain(dimension, out Grid? existingTerrain)) CreateGrid(_module, dimension, new GridOptions { IsTerrain = true, Bounds = _dimensions[dimension].TerrainBounds });
            }
        });
        sessions.SubscribeEnded(_module, _ =>
        {
            foreach (Grid grid in _grids.Values.Where(grid => grid.OwnerModuleId == _module.Module.Id).ToArray()) DestroyCore(grid);
            foreach (DimensionId dimension in _dimensions.Keys.OrderByDescending(d => d.Value))
            {
                if (_entities!.TryGet(dimension, out EntityWorld? existingWorld)) _entities.Destroy(_module, dimension);
            }

            _archetypes.Clear();
        });
    }

    public void AddDimension(DimensionId dimension, ulong clock = 0, GridBounds? terrainBounds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessions is { State: not SessionState.None }) throw new InvalidOperationException("Dimensions are declared outside world sessions.");
        if (_dimensions.ContainsKey(dimension)) throw new ArgumentException($"The {dimension} is declared already.", nameof(dimension));
        _dimensions.Add(dimension, new DimensionDefinition(clock, terrainBounds));
        if (_time is not null && !_time.HasDimension(dimension)) _time.AddDimension(dimension, clock);
    }

    public void RemoveDimension(DimensionId dimension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dimension == DimensionId.Default || !_dimensions.ContainsKey(dimension)) throw new ArgumentException($"The {dimension} cannot be removed.", nameof(dimension));
        if (_grids.Values.Any(grid => grid.Dimension == dimension)) throw new ArgumentException($"The {dimension} still has grids.", nameof(dimension));
        _dimensions.Remove(dimension);
        _time?.RemoveDimension(dimension);
    }

    public bool TryGet(DimensionId dimension, GridHandle handle, out Grid? grid)
    {
        if (_grids.TryGetValue(handle, out grid) && grid.Dimension == dimension) return true;
        grid = null;
        return false;
    }

    internal Grid Resolve(ulong identifier)
    {
        if (TryResolve(identifier, out Grid? grid)) return grid!;
        throw new ArgumentException($"The voxel grid identifier {identifier} is unknown.", nameof(identifier));
    }

    internal bool TryResolve(ulong identifier, out Grid? grid)
    {
        grid = _grids.Values.FirstOrDefault(candidate => candidate.Identifier == identifier || (ulong)candidate.Entity.Index == identifier);
        return grid is not null;
    }

    internal Grid? ResolveOrNull(ulong identifier) => TryResolve(identifier, out Grid? grid) ? grid : null;

    public Grid CreateGrid(ModuleContext owner, DimensionId dimension, GridOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessions is null || _sessions.State is not (SessionState.Starting or SessionState.Active)) throw new InvalidOperationException("A grid belongs to a world session.");
        if (!_dimensions.ContainsKey(dimension)) throw new ArgumentException($"The {dimension} is not declared.", nameof(dimension));
        if (!_entities!.TryGet(dimension, out EntityWorld? world)) throw new InvalidOperationException($"The entity world of {dimension} does not exist.");
        options ??= new GridOptions();
        if (options.IsTerrain && _grids.Values.Any(grid => grid.Dimension == dimension && grid.IsTerrain)) throw new ArgumentException($"The {dimension} already has a terrain grid.", nameof(options));

        EntityArchetype archetype = GetArchetype(world);
        GridHandle handle = new(_nextHandle++, 1);
        EntityId entity = world.Create(archetype);
        world.Set(entity, GridComponent, new GridComponent { Handle = handle });
        world.Set(entity, _entities!.WorldTransform, options.Transform);
        Func<ChunkCoordinate, JobData> chunkDataFactory = _ => _chunkAccess;
        var grid = new Grid(owner, world, entity, handle, _entities!.WorldTransform, options, chunkDataFactory, _spatialIndex);
        _grids.Add(handle, grid);
        _gridView.Add(grid);
        _spatialIndex.Insert(grid);
        _loader?.OnGridCreated(grid);
        CountLeaks(owner);
        return grid;
    }

    public void Destroy(ModuleContext owner, Grid grid)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(grid);
        if (!_grids.TryGetValue(grid.Handle, out Grid? current) || !ReferenceEquals(current, grid)) throw new ArgumentException("The grid does not belong to this voxel context.", nameof(grid));
        if (grid.OwnerModuleId != owner.Module.Id) throw new ArgumentException($"Module '{owner.Module.Id}' did not create this grid.", nameof(owner));
        DestroyCore(grid);
    }

    public bool TryGetTerrain(DimensionId dimension, out Grid? terrain)
    {
        terrain = _grids.Values.FirstOrDefault(grid => grid.Dimension == dimension && grid.IsTerrain);
        return terrain is not null;
    }

    internal void AttachBatches(VoxelBatches batches) => _batches = batches;

    public Grid Split(ModuleContext owner, Grid source, IReadOnlyCollection<BlockCoordinate> selection)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        EnsureOwner(owner, source);
        HashSet<BlockCoordinate> requested = selection.ToHashSet();
        var moved = new HashSet<BlockCoordinate>();
        var targetChanges = new List<BlockChange>();
        var sourceChanges = new List<BlockChange>();
        foreach (BlockCoordinate cell in requested)
        {
            if (!moved.Add(cell)) continue;
            uint state = source.GetBlock(cell);
            if (state == 0) continue;
            IReadOnlyList<BlockCell> footprint = _batches!.MultiBlocks.IsMultiBlock(state) ? _batches.MultiBlocks.GetFootprint(state) : [new BlockCell(0, 0, 0)];
            BlockCoordinate origin = _batches.MultiBlocks.IsMultiBlock(state) ? _batches.MultiBlocks.GetOrigin(state, cell) : cell;
            var cells = new List<(BlockCoordinate Cell, uint State)>();
            bool complete = true;
            for (int part = 0; part < footprint.Count; part++)
            {
                BlockCell offset = footprint[part];
                BlockCoordinate partCell = new(checked(origin.X + offset.X), checked(origin.Y + offset.Y), checked(origin.Z + offset.Z));
                if (!requested.Contains(partCell)) complete = false;
                cells.Add((partCell, source.GetBlock(partCell)));
            }

            if (!complete) continue;
            foreach ((BlockCoordinate partCell, uint partState) in cells)
            {
                moved.Add(partCell);
                targetChanges.Add(new BlockChange(new BlockPosition(0, partCell.X, partCell.Y, partCell.Z), 0, partState));
                sourceChanges.Add(new BlockChange(new BlockPosition(source.Identifier, partCell.X, partCell.Y, partCell.Z), partState, 0));
            }
        }

        Grid target = CreateGrid(owner, source.Dimension, new GridOptions { Transform = source.Transform });
        var targetBatch = new BlockBatch(targetChanges.Count) { Cause = BlockCause.Split, PreserveData = true };
        foreach (BlockChange change in targetChanges) targetBatch.Add(change with { Position = new BlockPosition(target.Identifier, change.Position.X, change.Position.Y, change.Position.Z) });
        var sourceBatch = new BlockBatch(sourceChanges.Count) { Cause = BlockCause.Split };
        foreach (BlockChange change in sourceChanges) sourceBatch.Add(change);

        CopyMetadata(source, target, requested, moved);
        _batches!.SubmitTechnical(targetBatch);
        _batches.SubmitTechnical(sourceBatch);
        return target;
    }

    public void Merge(ModuleContext owner, Grid target, Grid absorbed, int quarterTurns, BlockCoordinate offset)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(absorbed);
        EnsureOwner(owner, target);
        EnsureOwner(owner, absorbed);
        if (target.Dimension != absorbed.Dimension) throw new ArgumentException("The grids must be in the same dimension.");
        if ((uint)quarterTurns >= 4) throw new ArgumentOutOfRangeException(nameof(quarterTurns));
        Vector3 targetUp = Vector3.Transform(Vector3.UnitY, target.Transform.Rotation);
        Vector3 absorbedUp = Vector3.Transform(Vector3.UnitY, absorbed.Transform.Rotation);
        if (Vector3.Dot(targetUp, absorbedUp) < 0.999f) throw new ArgumentException("The grids must have the same vertical direction.");

        var targetBatch = new BlockBatch { Cause = BlockCause.Merge, PreserveData = true };
        var sourceBatch = new BlockBatch { Cause = BlockCause.Merge };
        var occupied = new HashSet<BlockCoordinate>();
        foreach (KeyValuePair<ChunkCoordinate, Chunk> pair in absorbed.Storage.ResidentChunks)
        {
            for (int index = 0; index < Chunk.Volume; index++)
            {
                uint state = pair.Value.GetState(index);
                if (state == 0) continue;
                BlockCoordinate sourceCell = FromLocal(pair.Key, index);
                BlockCoordinate destination = Rotate(sourceCell, quarterTurns, offset);
                if (!occupied.Add(destination) || target.GetBlock(destination) != 0) throw new InvalidOperationException($"The merge target is occupied at {destination}.");
                uint rotated = BlockRotation.RotateState(_batches!.Blocks, state, quarterTurns);
                targetBatch.Add(new BlockChange(new BlockPosition(target.Identifier, destination.X, destination.Y, destination.Z), 0, rotated));
                sourceBatch.Add(new BlockChange(new BlockPosition(absorbed.Identifier, sourceCell.X, sourceCell.Y, sourceCell.Z), state, 0));
                Chunk targetChunk = target.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(destination));
                pair.Value.CopyCellMetadataTo(targetChunk, index, VoxelCoordinates.ToIndex(destination), quarterTurns);
            }
        }

        _batches!.SubmitTechnical(targetBatch);
        VoxelBatchHandle removal = _batches.SubmitTechnical(sourceBatch);
        removal.CompletedCallback = () => Destroy(owner, absorbed);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Grid grid in _grids.Values.ToArray()) DestroyCore(grid);
        _gridView.Clear();
        _dimensions.Clear();
    }

    private EntityArchetype GetArchetype(EntityWorld world)
    {
        string key = world.Dimension.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // An archetype belongs to one entity world: the world of a dimension is replaced at each session.
        if (_archetypes.TryGetValue(key, out EntityArchetype? archetype) && ReferenceEquals(archetype.World, world)) return archetype;
        archetype = world.GetArchetype(GridComponent, _entities!.WorldTransform);
        _archetypes[key] = archetype;
        return archetype;
    }

    private void DestroyCore(Grid grid)
    {
        _loader?.OnGridDestroying(grid);
        _spatialIndex.Remove(grid);
        _grids.Remove(grid.Handle);
        _gridView.Remove(grid);
        if (!grid.Entity.IsNull && _entities!.TryGet(grid.Dimension, out EntityWorld? world)) world.Destroy(grid.Entity);
        grid.Dispose();
    }

    private void DisposeLeftovers()
    {
        foreach (Grid grid in _grids.Values.ToArray())
        {
            _module.Logger.Warning($"Module '{grid.OwnerModuleId}' did not destroy grid {grid.Handle.Index} by the end of the previous world session; it is destroyed now.");
            DestroyCore(grid);
        }
    }

    private void CountLeaks(ModuleContext owner)
    {
        if (_countedOwners.Add(owner.Module.Id))
        {
            _sessions!.RegisterWorldStateCounter(owner, "grids", () => _grids.Values.LongCount(grid => grid.OwnerModuleId == owner.Module.Id));
            _sessions.RegisterWorldStateCounter(owner, "chunks", () => _grids.Values.Where(grid => grid.OwnerModuleId == owner.Module.Id).Sum(grid => (long)grid.Storage.ChunkCount));
        }
    }

    private void EnsureOwner(ModuleContext owner, Grid grid)
    {
        if (!_grids.TryGetValue(grid.Handle, out Grid? current) || !ReferenceEquals(current, grid) || grid.OwnerModuleId != owner.Module.Id)
        {
            throw new ArgumentException("The module does not own this grid.", nameof(grid));
        }
    }

    private static BlockCoordinate FromLocal(ChunkCoordinate chunk, int index) =>
        VoxelCoordinates.ToBlock(chunk, new LocalBlockCoordinate(index & 31, index >> 10, (index >> 5) & 31));

    private static BlockCoordinate Rotate(BlockCoordinate cell, int quarterTurns, BlockCoordinate offset)
    {
        int x = cell.X;
        int z = cell.Z;
        for (int i = 0; i < quarterTurns; i++) (x, z) = (-z, x);
        return new BlockCoordinate(checked(x + offset.X), checked(cell.Y + offset.Y), checked(z + offset.Z));
    }

    private static void CopyMetadata(Grid source, Grid target, HashSet<BlockCoordinate> requested, HashSet<BlockCoordinate> moved)
    {
        foreach (BlockCoordinate cell in moved)
        {
            if (!requested.Contains(cell)) continue;
            ChunkCoordinate sourceChunkCoordinate = VoxelCoordinates.ToChunk(cell);
            if (!source.Storage.TryGetChunk(sourceChunkCoordinate, out Chunk? sourceChunk)) continue;
            Chunk targetChunk = target.Storage.GetOrCreateChunk(sourceChunkCoordinate);
            int index = VoxelCoordinates.ToIndex(cell);
            sourceChunk!.CopyCellMetadataTo(targetChunk, index, index);
        }
    }

    internal readonly record struct DimensionDefinition(ulong Clock, GridBounds? TerrainBounds);
}
