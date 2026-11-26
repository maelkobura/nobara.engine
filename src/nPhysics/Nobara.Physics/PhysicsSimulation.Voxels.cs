// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The voxel layer: the chunks of every grid around the dynamic bodies of a
/// world enter it, read in place by the backend, with no geometry to rebuild;
/// a block that changes wakes the bodies around it; a body that reaches a chunk
/// of the terrain that is not loaded or not active is frozen.
/// </summary>
/// <remarks>
/// Each dynamic body, and each dynamic ghost, holds the chunks its bounds reach,
/// grown by its velocity over a step and <see cref="VoxelMargin"/>: its
/// footprint. A sleeping body keeps its footprint without computing it again.
/// A chunk stays in a world while a footprint holds it, then
/// <see cref="VoxelGrace"/> more steps, so that a body on the border of a chunk
/// does not add and remove it at each step.
/// </remarks>
internal sealed partial class PhysicsSimulation
{
    /// <summary>How far past the bounds of a body, and its velocity over a step, its world holds the chunks of voxels, in blocks.</summary>
    internal const float VoxelMargin = 1f;

    /// <summary>How far past the bounds of a body, and its velocity over a step, a chunk of the terrain that is not active freezes it, in blocks.</summary>
    internal const float FreezeMargin = 1f;

    /// <summary>How many steps a chunk stays in a world once no body needs it.</summary>
    internal const int VoxelGrace = 30;

    private readonly Dictionary<VoxelChunkKey, VoxelChunkCells> _cells = [];
    private readonly List<Grid> _candidates = [];
    private readonly List<PendingWake> _wakeQueue = [];
    private int[] _footprint = new int[16];
    private Grid[] _terrains = new Grid[2];
    private int _terrainCount;
    private VoxelShapes? _voxelShapes;
    private IShapeRegistry? _shapeRegistry;
    private IBlockRegistry? _blocks;
    private ChunkResidencySubscription? _residency;
    private VoxelNotificationHandle? _notifications;
    private long _voxelStep;
    private float _placeDelta;
    private bool _voxelsRecompute;
    private bool _voxelsReset;
    private bool _chunksChanged;
    private int _voxelChunkCount;
    private long _wakes;

    /// <summary>Gets the number of chunks of voxels held by the physics worlds.</summary>
    internal int VoxelChunkCount => _voxelChunkCount;

    /// <summary>Reads the collision of every block state from nVoxel, once nVoxel and nBlocks have started.</summary>
    private void BuildVoxelShapes()
    {
        IShapeRegistry shapes = _shapeRegistry!;
        IBlockRegistry blocks = _blocks!;
        _voxelShapes = VoxelShapes.Build(
            blocks.StateCount,
            state => shapes.TryGet(blocks.TypeOf(state).Shape, out IBlockShape? shape) && shape!.IsInstanceDependent,
            state => shapes.Get(state, BlockDataView.Empty).Collision);
    }

    /// <summary>
    /// Gives each dynamic body and ghost the chunks its bounds reach, adds the
    /// chunks a world lacks, and removes those no body has needed for
    /// <see cref="VoxelGrace"/> steps.
    /// </summary>
    private void UpdateVoxels(float delta)
    {
        _voxelStep++;
        RemoveChunksOfDestroyedGrids();
        WakeAroundChangedBlocks();
        if (_voxelsReset)
        {
            ResetFootprints();
        }

        bool all = _voxelsReset || _voxelsRecompute;
        _voxelsReset = false;
        _voxelsRecompute = false;
        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (!body.Alive)
            {
                continue;
            }

            if (body.World is null || body.Kind != BodyKind.Dynamic || body.Grid is not null)
            {
                Release(ref body.Voxels);
                continue;
            }

            if (all || body.Voxels.World != body.World || !body.Asleep || body.Dirty)
            {
                Cover(ref body.Voxels, body.World, body.Dimension, body.Position, Reach(body, delta));
            }
        }

        for (int i = 0; i < _ghostHigh; i++)
        {
            ref GhostRecord ghost = ref _ghosts[i];
            if (!ghost.Alive)
            {
                continue;
            }

            ref BodyRecord body = ref _bodies[ghost.Source];
            if (ghost.Kind != BodyKind.Dynamic || body.Grid is not null)
            {
                Release(ref ghost.Voxels);
                continue;
            }

            if (all || ghost.Voxels.World != ghost.Host || !body.Asleep || body.Dirty)
            {
                Cover(ref ghost.Voxels, ghost.Host!, body.Dimension, body.Position, Reach(body, delta));
            }
        }

        RemoveUnusedChunks();
    }

    /// <summary>Gets how far from its center a body reaches during a step: its bounding radius plus its velocity over the step, in blocks.</summary>
    [HotPath]
    private float Reach(in BodyRecord body, float delta) => _shapes[body.Shape.Index].Radius + (body.Linear.Length() * delta);

    /// <summary>Computes the footprint of a body or a ghost in a world: the chunks of voxels its bounds reach.</summary>
    private void Cover(ref VoxelFootprint footprint, WorldEntry world, DimensionId dimension, in WorldPosition center, float reach)
    {
        if (footprint.World != world)
        {
            Release(ref footprint);
        }

        float half = reach + VoxelMargin;
        int count = 0;
        _candidates.Clear();
        _voxels!.SpatialIndex.Query(WorldBox.Around(center, new Vector3(half)), _candidates);
        for (int g = 0; g < _candidates.Count; g++)
        {
            Grid grid = _candidates[g];
            if (grid.IsDestroyed || grid.Dimension != dimension || grid.Transform.Scale != Vector3.One || IsBody(grid)
                || !ChunkRange(grid, center, half, out ChunkCoordinate minimum, out ChunkCoordinate maximum))
            {
                continue;
            }

            for (int y = minimum.Y; y <= maximum.Y; y++)
            {
                for (int z = minimum.Z; z <= maximum.Z; z++)
                {
                    for (int x = minimum.X; x <= maximum.X; x++)
                    {
                        var coordinate = new ChunkCoordinate(x, y, z);
                        if (!grid.Storage.Overlaps(coordinate) || !grid.Storage.TryGetChunk(coordinate, out Chunk? chunk) || chunk is null || IsEmpty(chunk))
                        {
                            continue;
                        }

                        if (count == _footprint.Length)
                        {
                            Array.Resize(ref _footprint, count * 2);
                        }

                        _footprint[count++] = AcquireChunk(world, grid, chunk);
                    }
                }
            }
        }

        if (world.Voxels is not { } table)
        {
            footprint.Count = 0;
            footprint.World = world;   // no chunk yet: nothing held
            return;
        }

        int[] previous = footprint.Slots ?? [];
        if (footprint.World == world)
        {
            for (int i = 0; i < count; i++)
            {
                if (Array.IndexOf(previous, _footprint[i], 0, footprint.Count) < 0)
                {
                    table.Slots[_footprint[i]].Refs++;
                }
            }

            for (int i = 0; i < footprint.Count; i++)
            {
                if (Array.IndexOf(_footprint, previous[i], 0, count) < 0)
                {
                    Unref(table, previous[i]);
                }
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                table.Slots[_footprint[i]].Refs++;
            }
        }

        if (footprint.Slots is null || footprint.Slots.Length < count)
        {
            footprint.Slots = new int[Math.Max(4, count)];
        }

        Array.Copy(_footprint, footprint.Slots, count);
        footprint.Count = count;
        footprint.World = world;
    }

    /// <summary>Lets go of the chunks of a footprint.</summary>
    [HotPath]
    private void Release(ref VoxelFootprint footprint)
    {
        if (footprint.World?.Voxels is { } table && !_voxelsReset)
        {
            for (int i = 0; i < footprint.Count; i++)
            {
                Unref(table, footprint.Slots![i]);
            }
        }

        footprint.Count = 0;
        footprint.World = null;
    }

    [HotPath]
    private void Unref(VoxelTable table, int slot)
    {
        ref VoxelSlot entry = ref table.Slots[slot];
        if (entry.Used && entry.Refs > 0 && --entry.Refs == 0)
        {
            entry.ZeroStep = _voxelStep;
        }
    }

    /// <summary>Forgets every footprint after a chunk was removed from under it: every dynamic body computes its footprint again.</summary>
    private void ResetFootprints()
    {
        foreach (WorldEntry world in _worlds)
        {
            if (world.Voxels is { } table)
            {
                for (int i = 0; i < table.High; i++)
                {
                    table.Slots[i].Refs = 0;
                    table.Slots[i].ZeroStep = _voxelStep;
                }
            }
        }

        for (int i = 0; i < _bodyHigh; i++)
        {
            _bodies[i].Voxels.Count = 0;
            _bodies[i].Voxels.World = null;
        }

        for (int i = 0; i < _ghostHigh; i++)
        {
            _ghosts[i].Voxels.Count = 0;
            _ghosts[i].Voxels.World = null;
        }
    }

    /// <summary>Gets the slot of a chunk in a world, adding the chunk to the world when it lacks it.</summary>
    private int AcquireChunk(WorldEntry world, Grid grid, Chunk chunk)
    {
        VoxelTable table = world.Voxels ??= new VoxelTable();
        var key = new VoxelChunkKey(grid, chunk.Coordinate);
        if (table.Map.TryGetValue(key, out int existing))
        {
            if (ReferenceEquals(table.Slots[existing].Cells!.Chunk, chunk))
            {
                return existing;
            }

            RemoveChunk(world, existing);   // the chunk left memory and came back without nPhysics seeing it
            _voxelsReset = true;
        }

        VoxelChunkCells cells = CellsOf(key, grid, chunk);
        ChunkCorner(grid, chunk.Coordinate, out WorldPosition corner);
        int id = world.Backend.AddVoxelChunk(new VoxelChunkCollider(grid, chunk, PhysicsFrame.ToLocal(world.Origin, corner), Quaternion.Normalize(grid.Transform.Rotation)) { Cells = cells });
        int slot = table.Allocate();
        table.Slots[slot] = new VoxelSlot { Used = true, Key = key, Cells = cells, BackendId = id, ZeroStep = _voxelStep, NextFree = -1 };
        table.Map.Add(key, slot);
        cells.Users++;
        _voxelChunkCount++;
        return slot;
    }

    /// <summary>Gets the cells of a chunk, shared by the worlds that hold it.</summary>
    private VoxelChunkCells CellsOf(VoxelChunkKey key, Grid grid, Chunk chunk)
    {
        if (_cells.TryGetValue(key, out VoxelChunkCells? cells) && ReferenceEquals(cells.Chunk, chunk))
        {
            return cells;
        }

        cells = new VoxelChunkCells(_voxelShapes, grid, chunk, Quaternion.Normalize(grid.Transform.Rotation));
        cells.EvaluateInstances(_shapeRegistry!);
        _cells[key] = cells;
        return cells;
    }

    /// <summary>Removes a chunk from a world.</summary>
    private void RemoveChunk(WorldEntry world, int slot)
    {
        VoxelTable table = world.Voxels!;
        ref VoxelSlot entry = ref table.Slots[slot];
        world.Backend.RemoveVoxelChunk(entry.BackendId);
        table.Map.Remove(entry.Key);
        ReleaseCells(entry.Key, entry.Cells!);
        entry = new VoxelSlot { NextFree = table.Free };
        table.Free = slot;
        table.Count--;
        _voxelChunkCount--;
    }

    [HotPath]
    private void ReleaseCells(VoxelChunkKey key, VoxelChunkCells cells)
    {
        if (--cells.Users == 0 && _cells.TryGetValue(key, out VoxelChunkCells? held) && held == cells)
        {
            _cells.Remove(key);
        }
    }

    /// <summary>Removes the chunks that no footprint has held for <see cref="VoxelGrace"/> steps.</summary>
    private void RemoveUnusedChunks()
    {
        foreach (WorldEntry world in _worlds)
        {
            if (world.Voxels is not { } table)
            {
                continue;
            }

            for (int i = 0; i < table.High; i++)
            {
                ref VoxelSlot entry = ref table.Slots[i];
                if (entry.Used && entry.Refs == 0 && _voxelStep - entry.ZeroStep >= VoxelGrace)
                {
                    RemoveChunk(world, i);
                }
            }
        }
    }

    /// <summary>Removes the chunks of the grids destroyed since the last step: their storage is gone.</summary>
    private void RemoveChunksOfDestroyedGrids()
    {
        foreach (WorldEntry world in _worlds)
        {
            if (world.Voxels is not { } table)
            {
                continue;
            }

            for (int i = 0; i < table.High; i++)
            {
                if (table.Slots[i].Used && table.Slots[i].Key.Grid.IsDestroyed)
                {
                    RemoveChunk(world, i);
                    _voxelsReset = true;
                }
            }
        }
    }

    /// <summary>Removes a chunk from every world that holds it, before it leaves memory.</summary>
    private void RemoveChunkEverywhere(VoxelChunkKey key)
    {
        RemoveChunkFrom(_worlds, key);
        RemoveChunkFrom(_retiring, key);
    }

    private void RemoveChunkFrom(List<WorldEntry> worlds, VoxelChunkKey key)
    {
        foreach (WorldEntry world in worlds)
        {
            if (world.Voxels is { } table && table.Map.TryGetValue(key, out int slot))
            {
                RemoveChunk(world, slot);
                _voxelsReset = true;
            }
        }
    }

    /// <summary>Lets go of the chunks of a world that is disposed with them.</summary>
    [HotPath]
    private void ForgetVoxels(WorldEntry world)
    {
        if (world.Voxels is not { } table)
        {
            return;
        }

        for (int i = 0; i < table.High; i++)
        {
            if (table.Slots[i].Used)
            {
                ReleaseCells(table.Slots[i].Key, table.Slots[i].Cells!);
            }
        }

        _voxelChunkCount -= table.Count;
        world.Voxels = null;
    }

    /// <summary>
    /// Follows the chunks that enter and leave memory: a chunk that leaves is
    /// removed from every world before it goes; either way, the footprints and
    /// the frozen bodies are looked at again at the next step.
    /// </summary>
    private void OnResidency(ChunkResidencyChange change)
    {
        if (change.Kind == ChunkResidencyKind.Unloading)
        {
            RemoveChunkEverywhere(new VoxelChunkKey(change.Grid, change.Chunk));
        }

        _voxelsRecompute = true;
        _chunksChanged = true;
    }

    /// <summary>
    /// Follows a batch of changed blocks: the cells whose boxes depend on their
    /// block data are evaluated again, a chunk that no world holds yet is looked
    /// for at the next step, and the bodies around the changed blocks wake up.
    /// </summary>
    private void OnBlocksChanged(VoxelBatchNotification notification)
    {
        Grid grid = notification.Grid;
        IReadOnlyList<VoxelBlockChange> changes = notification.Changes;
        if (changes.Count == 0 || grid.IsDestroyed)
        {
            return;
        }

        if (_cells.TryGetValue(new VoxelChunkKey(grid, notification.Chunk), out VoxelChunkCells? cells))
        {
            if (_voxelShapes is { HasInstanceShapes: true } shapes)
            {
                for (int i = 0; i < changes.Count; i++)
                {
                    VoxelBlockChange change = changes[i];
                    if (shapes.Get(change.NewState).Instance || shapes.Get(change.OldState).Instance)
                    {
                        cells.Refresh(_shapeRegistry!, change.LocalIndex);
                    }
                }
            }
        }
        else
        {
            _voxelsRecompute = true;   // a chunk that gets its first blocks
        }

        if (notification.Cause == BlockCause.Generation)
        {
            return;   // a chunk filled as it loads: no body rested on it
        }

        const int size = VoxelCoordinates.ChunkSize;
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        for (int i = 0; i < changes.Count; i++)
        {
            int index = changes[i].LocalIndex;
            var cell = new Vector3(index % size, index / (size * size), index / size % size);
            minimum = Vector3.Min(minimum, cell);
            maximum = Vector3.Max(maximum, cell);
        }

        _wakeQueue.Add(new PendingWake(grid, notification.Chunk, minimum - Vector3.One, maximum + new Vector3(2f)));
    }

    /// <summary>Wakes the bodies around the blocks changed since the last step: the transform of a grid is read here, at the stage of the step, which may read it.</summary>
    private void WakeAroundChangedBlocks()
    {
        for (int i = 0; i < _wakeQueue.Count; i++)
        {
            PendingWake wake = _wakeQueue[i];
            if (!wake.Grid.IsDestroyed)
            {
                WakeAround(wake.Grid, wake.Chunk, wake.Minimum, wake.Maximum);
            }
        }

        _wakeQueue.Clear();
    }

    /// <summary>Wakes the bodies of every world of the dimension of a grid that overlap a box of cells of one of its chunks.</summary>
    private void WakeAround(Grid grid, ChunkCoordinate chunk, Vector3 minimum, Vector3 maximum)
    {
        ChunkCorner(grid, chunk, out WorldPosition corner);
        Quaternion rotation = Quaternion.Normalize(grid.Transform.Rotation);
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var point = new Vector3((i & 1) == 0 ? minimum.X : maximum.X, (i & 2) == 0 ? minimum.Y : maximum.Y, (i & 4) == 0 ? minimum.Z : maximum.Z);
            Vector3 turned = Vector3.Transform(point * grid.Transform.Scale, rotation);
            low = Vector3.Min(low, turned);
            high = Vector3.Max(high, turned);
        }

        foreach (WorldEntry world in _worlds)
        {
            if (world.Region.Dimension == grid.Dimension)
            {
                Vector3 origin = PhysicsFrame.ToLocal(world.Origin, corner);
                world.Backend.WakeArea(origin + low, origin + high);
                _wakes++;
            }
        }
    }

    /// <summary>Tells whether a movable body reaches a chunk of the terrain of its dimension that is not loaded or not active: such a body is frozen.</summary>
    [HotPath]
    private bool TouchesInactive(in BodyRecord body)
    {
        if (body.Kind == BodyKind.Static || _loading is null || TerrainOf(body.Dimension) is not { } terrain || terrain.Residency != GridResidency.Tickets)
        {
            return false;
        }

        float half = Reach(body, _placeDelta) + FreezeMargin;
        if (!ChunkRange(terrain, body.Position, half, out ChunkCoordinate minimum, out ChunkCoordinate maximum))
        {
            return false;
        }

        for (int y = minimum.Y; y <= maximum.Y; y++)
        {
            for (int z = minimum.Z; z <= maximum.Z; z++)
            {
                for (int x = minimum.X; x <= maximum.X; x++)
                {
                    var coordinate = new ChunkCoordinate(x, y, z);
                    if (terrain.Storage.Overlaps(coordinate) && !_loading.GetStatus(terrain, coordinate).IsActive)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Gets the terrain grid of a dimension, kept from one step to the next: nVoxel finds it with a query that allocates.</summary>
    [HotPath]
    private Grid? TerrainOf(DimensionId dimension)
    {
        for (int i = 0; i < _terrainCount; i++)
        {
            if (_terrains[i].Dimension == dimension && !_terrains[i].IsDestroyed)
            {
                return _terrains[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps the terrain grid of each dimension that has a region, the only
    /// ones where a body can be frozen by a chunk, asking nVoxel only for those
    /// it lacks: once per session, or after one was destroyed.
    /// </summary>
    private void KeepTerrains()
    {
        foreach (RegionEntry region in _regions)
        {
            if (TerrainOf(region.Dimension) is null)
            {
                FindTerrain(region.Dimension);
            }
        }
    }

    [HotPath]
    private void FindTerrain(DimensionId dimension)
    {
        if (!_voxels!.TryGetTerrain(dimension, out Grid? terrain) || terrain is null)
        {
            return;
        }

        int count = 0;
        for (int i = 0; i < _terrainCount; i++)
        {
            if (!_terrains[i].IsDestroyed)
            {
                _terrains[count++] = _terrains[i];
            }
        }

        if (count == _terrains.Length)
        {
            Array.Resize(ref _terrains, count * 2);
        }

        _terrains[count++] = terrain;
        _terrainCount = count;
    }

    /// <summary>Tells whether a grid is a body: its voxels are not part of the voxel layer.</summary>
    [HotPath]
    private bool IsBody(Grid grid) =>
        _entities!.TryGet(grid.Dimension, out Nobara.Entity.EntityWorld? world) && world.IsAlive(grid.Entity) && world.Has(grid.Entity, _bodyComponent);

    [HotPath]
    private static bool IsEmpty(Chunk chunk) => chunk.Palette.IsUniform && chunk.Palette.GetEntry(0) == 0;

    /// <summary>
    /// Gets the chunks of a grid that a cube around a position reaches. The
    /// offset to the grid is computed in whole chunks when the grid is neither
    /// turned nor scaled, as the terrain is, so that the result is exact a
    /// billion blocks away.
    /// </summary>
    /// <returns><see langword="false"/> when the range does not fit the coordinates of chunks.</returns>
    [HotPath]
    internal static bool ChunkRange(Grid grid, in WorldPosition center, float half, out ChunkCoordinate minimum, out ChunkCoordinate maximum)
    {
        const double size = VoxelCoordinates.ChunkSize;
        WorldTransform transform = grid.Transform;
        WorldPosition origin = transform.Position;
        double x;
        double y;
        double z;
        double reach;
        if (transform.Rotation == Quaternion.Identity && transform.Scale == Vector3.One)
        {
            x = (((long)center.ChunkX - origin.ChunkX) * size) + (center.Local.X - origin.Local.X);
            y = (((long)center.ChunkY - origin.ChunkY) * size) + (center.Local.Y - origin.Local.Y);
            z = (((long)center.ChunkZ - origin.ChunkZ) * size) + (center.Local.Z - origin.Local.Z);
            reach = half;
        }
        else
        {
            Vector3 scale = transform.Scale;
            Vector3 local = Vector3.Transform(center.RelativeTo(origin), Quaternion.Conjugate(Quaternion.Normalize(transform.Rotation))) / scale;
            x = local.X;
            y = local.Y;
            z = local.Z;
            reach = half * 1.7320508f / MathF.Min(MathF.Abs(scale.X), MathF.Min(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));   // the box of the turned cube
        }

        double minX = Math.Floor((x - reach) / size);
        double minY = Math.Floor((y - reach) / size);
        double minZ = Math.Floor((z - reach) / size);
        double maxX = Math.Floor((x + reach) / size);
        double maxY = Math.Floor((y + reach) / size);
        double maxZ = Math.Floor((z + reach) / size);
        if (!Fits(minX) || !Fits(minY) || !Fits(minZ) || !Fits(maxX) || !Fits(maxY) || !Fits(maxZ))
        {
            minimum = maximum = default;
            return false;
        }

        minimum = new ChunkCoordinate((int)minX, (int)minY, (int)minZ);
        maximum = new ChunkCoordinate((int)maxX, (int)maxY, (int)maxZ);
        return true;
    }

    [HotPath]
    private static bool Fits(double value) => value >= int.MinValue && value <= int.MaxValue;

    /// <summary>Gets the corner of a chunk of a grid in the world: exactly, in whole chunks, when the grid is neither turned nor scaled.</summary>
    [HotPath]
    internal static void ChunkCorner(Grid grid, ChunkCoordinate chunk, out WorldPosition corner)
    {
        WorldTransform transform = grid.Transform;
        WorldPosition origin = transform.Position;
        if (transform.Rotation == Quaternion.Identity && transform.Scale == Vector3.One)
        {
            corner = new WorldPosition(origin.ChunkX + chunk.X, origin.ChunkY + chunk.Y, origin.ChunkZ + chunk.Z, origin.Local);
            return;
        }

        const float size = VoxelCoordinates.ChunkSize;
        corner = transform.TransformPoint(new Vector3(chunk.X * size, chunk.Y * size, chunk.Z * size));
    }

    /// <summary>A chunk of a grid.</summary>
    private readonly record struct VoxelChunkKey
    {
        [HotPath]
        public VoxelChunkKey(Grid grid, ChunkCoordinate chunk) => (Grid, Chunk) = (grid, chunk);

        public Grid Grid { get; }

        public ChunkCoordinate Chunk { get; }
    }

    /// <summary>Changed cells of a chunk, around which bodies wake at the next step, in the frame of the chunk.</summary>
    private readonly record struct PendingWake(Grid Grid, ChunkCoordinate Chunk, Vector3 Minimum, Vector3 Maximum);

    /// <summary>The chunks of voxels that a world holds.</summary>
    private sealed class VoxelTable
    {
        public Dictionary<VoxelChunkKey, int> Map { get; } = [];

        public VoxelSlot[] Slots { get; private set; } = new VoxelSlot[16];

        public int High { get; private set; }

        public int Free { get; set; } = -1;

        public int Count { get; set; }

        public int Allocate()
        {
            int slot;
            if (Free >= 0)
            {
                slot = Free;
                Free = Slots[slot].NextFree;
            }
            else
            {
                if (High == Slots.Length)
                {
                    VoxelSlot[] slots = Slots;
                    Array.Resize(ref slots, slots.Length * 2);
                    Slots = slots;
                }

                slot = High++;
            }

            Count++;
            return slot;
        }
    }

    /// <summary>A chunk of voxels in a world.</summary>
    private struct VoxelSlot
    {
        public bool Used;
        public VoxelChunkKey Key;
        public VoxelChunkCells? Cells;
        public int BackendId;

        /// <summary>The number of footprints that hold the chunk.</summary>
        public int Refs;

        /// <summary>The step at which the last footprint let go of the chunk.</summary>
        public long ZeroStep;
        public int NextFree;
    }

    /// <summary>The chunks of voxels that a body or a ghost holds in a world: the slots of its world.</summary>
    private struct VoxelFootprint
    {
        public WorldEntry? World;
        public int[]? Slots;
        public int Count;
    }
}
