// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Jobs;
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
    private IGameTime? _time;
    private IWorldSessions? _sessions;
    private int _nextHandle;
    private bool _disposed;

    public VoxelWorlds(ModuleContext module)
    {
        _module = module;
        _dimensions.Add(DimensionId.Default, new DimensionDefinition(0, null));
    }

    public IReadOnlyList<DimensionId> Dimensions => _dimensions.Keys.OrderBy(d => d.Value).ToArray();

    public IReadOnlyList<Grid> Grids => _gridView;

    public GridSpatialIndex SpatialIndex => _spatialIndex;

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
        Func<ChunkCoordinate, JobData> chunkDataFactory = coordinate => owner.Jobs.CreateData($"chunk {coordinate} of grid {handle.Index}");
        var grid = new Grid(owner, world, entity, handle, _entities!.WorldTransform, options, chunkDataFactory, _spatialIndex);
        _grids.Add(handle, grid);
        _gridView.Add(grid);
        _spatialIndex.Insert(grid);
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
        if (_archetypes.TryGetValue(key, out EntityArchetype? archetype)) return archetype;
        archetype = world.GetArchetype(GridComponent, _entities!.WorldTransform);
        _archetypes.Add(key, archetype);
        return archetype;
    }

    private void DestroyCore(Grid grid)
    {
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

    private readonly record struct DimensionDefinition(ulong Clock, GridBounds? TerrainBounds);
}
