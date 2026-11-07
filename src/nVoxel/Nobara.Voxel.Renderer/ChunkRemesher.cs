// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Renderer;

namespace Nobara.Voxel.Renderer;

/// <summary>The scratch memory of one remesh: the copy, the mesher, and the geometry. Pooled.</summary>
internal sealed class MeshWork
{
    public MeshInput Input { get; } = new();

    public MeshOutput Output { get; } = new();

    public ChunkMesher Mesher { get; } = new();

    public BlockVisuals Visuals { get; set; } = null!;

    public ChunkEntry? Entry { get; set; }

    public int Generation { get; set; }

    public uint Version { get; set; }

    public JobHandle Handle { get; set; }

    /// <summary>Gets or sets whether the result is applied at the next frame, waiting for the job if needed: a block changed by a player shows at once.</summary>
    public bool Urgent { get; set; }

    [HotPath]
    public void Run() => Mesher.Mesh(Input, Visuals, Output);
}

/// <summary>The background job of a remesh.</summary>
internal readonly struct MeshJob(MeshWork work) : IJob
{
    [HotPath]
    public void Execute() => work.Run();
}

/// <summary>A grid with meshed chunks: its group of chunk geometry.</summary>
internal sealed class GridEntry(Grid grid)
{
    public Grid Grid { get; } = grid;

    public Dictionary<ChunkCoordinate, ChunkEntry> Chunks { get; } = [];

    public ChunkGroupId Group { get; set; }

    public WorldTransform Transform { get; set; }
}

/// <summary>A loaded chunk that the meshing follows.</summary>
internal sealed class ChunkEntry(GridEntry grid, ChunkCoordinate coordinate)
{
    public GridEntry Grid { get; } = grid;

    public ChunkCoordinate Coordinate { get; } = coordinate;

    /// <summary>Gets or sets the number of times the chunk was marked to remesh; a result of an older mark is stale.</summary>
    public int Generation { get; set; }

    /// <summary>Gets or sets the generation of the geometry applied last, or -1.</summary>
    public int Meshed { get; set; } = -1;

    public bool Dirty { get; set; }

    public bool Urgent { get; set; }

    public bool Removed { get; set; }

    public MeshWork? Running { get; set; }

    public ChunkSectionId Section { get; set; }

    public double Distance { get; set; }

    public bool IsMeshed => !Removed && !Dirty && Running is null && Meshed == Generation;
}

/// <summary>
/// Follows the chunks of nVoxel and keeps their geometry in the chunk geometry
/// of nRenderer: chunks marked by the notifications of batches and by their
/// loading, remeshed in background jobs nearest the camera first, at most once
/// per frame each, and their results applied during the extraction of the
/// renderer, unless the chunk changed meanwhile.
/// </summary>
internal sealed class ChunkRemesher : IRenderExtractor, IVoxelMeshing
{
    private readonly ModuleContext _context;
    private readonly Dictionary<Grid, GridEntry> _grids = [];
    private readonly List<ChunkEntry> _dirty = [];
    private readonly List<MeshWork> _running = [];
    private readonly Stack<MeshWork> _pool = new();
    private readonly List<ChunkGroupId> _destroyGroups = [];
    private readonly List<ChunkSectionId> _removeSections = [];
    private readonly List<GridEntry> _gridScratch = [];
    private readonly DistanceOrder _order = new();
    private readonly Dictionary<string, IBlockMesher> _meshers = new(StringComparer.Ordinal);
    private IChunkLoading _loading = null!;
    private IShapeRegistry _shapes = null!;
    private Nobara.Blocks.IBlockRegistry _blocks = null!;
    private BlockVisuals? _visuals;
    private LoadingTask? _task;
    private LoadingTask? _voxelTask;
    private bool _started;
    private int _maxRunning;
    private int _chunkCount;
    private int _sectionCount;
    private long _startedCount;
    private long _applied;
    private long _discarded;

    public ChunkRemesher(ModuleContext context)
    {
        _context = context;
        _maxRunning = Math.Max(1, context.Jobs.WorkerCount * 2);
    }

    public VoxelMeshingStatistics Statistics => new()
    {
        Chunks = _chunkCount,
        Dirty = _dirty.Count,
        Running = _running.Count,
        Sections = _sectionCount,
        Started = _startedCount,
        Applied = _applied,
        Discarded = _discarded,
    };

    public int MaxRunning
    {
        get => _maxRunning;
        set => _maxRunning = Math.Max(1, value);
    }

    /// <summary>Gets the number of chunks and remeshes tied to the world session, for the leak counter.</summary>
    public long WorldState => _chunkCount + _running.Count;

    internal BlockVisuals Visuals => _visuals ?? throw new InvalidOperationException("The visual of blocks exists once nVoxel.Renderer started.");

    public IDisposable RegisterMesher(ModuleContext owner, string shapeId, IBlockMesher mesher)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(shapeId);
        ArgumentNullException.ThrowIfNull(mesher);
        if (_started)
        {
            throw new InvalidOperationException("Meshers are registered before nVoxel.Renderer starts, while modules register or initialize.");
        }

        if (shapeId is ShapeIds.Cube or ShapeIds.Model)
        {
            throw new ArgumentException($"The shape '{shapeId}' is meshed by nVoxel.Renderer itself.", nameof(shapeId));
        }

        if (!_meshers.TryAdd(shapeId, mesher))
        {
            throw new ArgumentException($"The shape '{shapeId}' has a mesher already.", nameof(shapeId));
        }

        return new MesherRegistration(this, shapeId, mesher);
    }

    public BlockVisual GetVisual(uint state) => Visuals.Get(state);

    public bool IsMeshed(Grid grid, ChunkCoordinate chunk) =>
        _grids.TryGetValue(grid, out GridEntry? entry) && entry.Chunks.TryGetValue(chunk, out ChunkEntry? chunkEntry) && chunkEntry.IsMeshed;

    public void Initialize(Nobara.Blocks.IBlockRegistry blocks, IVoxelWorlds worlds, IChunkLoading loading, IVoxelBatches batches, IWorldSessions sessions, IShapeRegistry shapes)
    {
        _blocks = blocks;
        _loading = loading;
        _shapes = shapes;
        loading.SubscribeResidency(_context, OnResidency);
        batches.Subscribe(_context, OnBatch);
        sessions.SubscribeStarting(_context, OnSessionStarting);
        sessions.SubscribeEnded(_context, OnSessionEnded);
        sessions.RegisterWorldStateCounter(_context, "meshed chunks", () => WorldState);
    }

    public void Start(BlockVisuals visuals)
    {
        _visuals = visuals;
        _started = true;
    }

    internal IReadOnlyDictionary<string, IBlockMesher> Meshers => _meshers;

    /// <summary>Gets or sets a function that sees the geometry of each remesh as it is applied, for tests.</summary>
    internal Action<Grid, ChunkCoordinate, MeshOutput>? Applied { get; set; }

    public void Extract(RenderExtraction extraction)
    {
        if (_visuals is null)
        {
            return;
        }

        ChunkGeometry chunks = extraction.Chunks;
        RemoveRetired(chunks);
        UpdateGroups(chunks);
        Collect(chunks);
        Schedule(extraction.MainView.Camera.Position);
        TrackLoading();
    }

    private void OnResidency(ChunkResidencyChange change)
    {
        if (_visuals is null)
        {
            return;
        }

        if (change.Kind == ChunkResidencyKind.Loaded)
        {
            if (!_grids.TryGetValue(change.Grid, out GridEntry? grid))
            {
                // The transform is read during extraction: this runs inside the systems of nVoxel, which do not declare it.
                grid = new GridEntry(change.Grid);
                _grids.Add(change.Grid, grid);
            }

            if (!grid.Chunks.TryGetValue(change.Chunk, out ChunkEntry? entry))
            {
                entry = new ChunkEntry(grid, change.Chunk);
                grid.Chunks.Add(change.Chunk, entry);
                _chunkCount++;
            }

            Mark(entry, urgent: false);
            MarkNeighbors(grid, change.Chunk, -1, -1, -1, 1, 1, 1);
        }
        else if (_grids.TryGetValue(change.Grid, out GridEntry? grid) && grid.Chunks.Remove(change.Chunk, out ChunkEntry? entry))
        {
            Retire(entry);
        }
    }

    private void OnBatch(VoxelBatchNotification notification)
    {
        if (!_grids.TryGetValue(notification.Grid, out GridEntry? grid) || !grid.Chunks.TryGetValue(notification.Chunk, out ChunkEntry? entry))
        {
            return;
        }

        bool urgent = !notification.IsTechnical;
        Mark(entry, urgent);

        // A change on the border of the chunk changes the faces and the shadows of its neighbors.
        int minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        foreach (VoxelBlockChange change in notification.Changes)
        {
            int index = change.LocalIndex;
            int x = index & 31;
            int z = (index >> 5) & 31;
            int y = index >> 10;
            if (x == 0) minX = -1;
            if (x == 31) maxX = 1;
            if (y == 0) minY = -1;
            if (y == 31) maxY = 1;
            if (z == 0) minZ = -1;
            if (z == 31) maxZ = 1;
        }

        if (minX < 0 || minY < 0 || minZ < 0 || maxX > 0 || maxY > 0 || maxZ > 0)
        {
            MarkNeighbors(grid, notification.Chunk, minX, minY, minZ, maxX, maxY, maxZ, urgent);
        }
    }

    private void MarkNeighbors(GridEntry grid, ChunkCoordinate chunk, int minX, int minY, int minZ, int maxX, int maxY, int maxZ, bool urgent = false)
    {
        for (int dy = minY; dy <= maxY; dy++)
        {
            for (int dz = minZ; dz <= maxZ; dz++)
            {
                for (int dx = minX; dx <= maxX; dx++)
                {
                    if ((dx | dy | dz) == 0)
                    {
                        continue;
                    }

                    var neighbor = new ChunkCoordinate(chunk.X + dx, chunk.Y + dy, chunk.Z + dz);
                    if (grid.Chunks.TryGetValue(neighbor, out ChunkEntry? entry) && (entry.Meshed >= 0 || entry.Running is not null))
                    {
                        Mark(entry, urgent);
                    }
                }
            }
        }
    }

    private void Mark(ChunkEntry entry, bool urgent)
    {
        entry.Generation++;
        entry.Urgent |= urgent;
        if (!entry.Dirty)
        {
            entry.Dirty = true;
            _dirty.Add(entry);
        }
    }

    private void Retire(ChunkEntry entry)
    {
        entry.Removed = true;
        _chunkCount--;
        if (entry.Section.IsValid)
        {
            _removeSections.Add(entry.Section);
            entry.Section = default;
            _sectionCount--;
        }
    }

    private void RemoveRetired(ChunkGeometry chunks)
    {
        foreach (ChunkSectionId section in _removeSections)
        {
            if (chunks.Contains(section))
            {
                chunks.RemoveSection(section);
            }
        }

        _removeSections.Clear();
        foreach (ChunkGroupId group in _destroyGroups)
        {
            if (chunks.Contains(group))
            {
                chunks.DestroyGroup(group);
            }
        }

        _destroyGroups.Clear();
    }

    private void UpdateGroups(ChunkGeometry chunks)
    {
        _gridScratch.Clear();
        foreach (GridEntry grid in _grids.Values)
        {
            if (grid.Grid.IsDestroyed || (grid.Chunks.Count == 0 && !grid.Group.IsValid))
            {
                _gridScratch.Add(grid);
                continue;
            }

            WorldTransform transform = grid.Grid.Transform;
            if (grid.Group.IsValid && transform != grid.Transform)
            {
                chunks.MoveGroup(grid.Group, transform);
            }

            grid.Transform = transform;
        }

        foreach (GridEntry grid in _gridScratch)
        {
            if (grid.Group.IsValid && chunks.Contains(grid.Group))
            {
                chunks.DestroyGroup(grid.Group);
            }

            foreach (ChunkEntry entry in grid.Chunks.Values)
            {
                // Destroying the group removed its sections.
                if (entry.Section.IsValid)
                {
                    entry.Section = default;
                    _sectionCount--;
                }

                Retire(entry);
            }

            grid.Chunks.Clear();
            _grids.Remove(grid.Grid);
        }
    }

    // Applies the remeshes that finished, and those of changed blocks, which do not wait more than a frame.
    private void Collect(ChunkGeometry chunks)
    {
        for (int i = _running.Count - 1; i >= 0; i--)
        {
            MeshWork work = _running[i];
            if (!work.Handle.IsCompleted)
            {
                if (!work.Urgent)
                {
                    continue;
                }

                work.Handle.Complete();
            }

            _running.RemoveAt(i);
            ChunkEntry entry = work.Entry!;
            entry.Running = null;
            if (entry.Removed || entry.Generation != work.Generation || IsChanged(entry, work.Version))
            {
                _discarded++;
                if (!entry.Removed && !entry.Dirty)
                {
                    Mark(entry, work.Urgent);
                }
            }
            else
            {
                Apply(chunks, entry, work.Output);
                entry.Meshed = work.Generation;
                _applied++;
            }

            Recycle(work);
        }
    }

    private bool IsChanged(ChunkEntry entry, uint version) =>
        !entry.Grid.Grid.IsDestroyed && entry.Grid.Grid.Storage.TryGetChunk(entry.Coordinate, out Chunk? chunk) && chunk!.Version != version;

    private void Apply(ChunkGeometry chunks, ChunkEntry entry, MeshOutput output)
    {
        Applied?.Invoke(entry.Grid.Grid, entry.Coordinate, output);
        bool empty = output.TriangleCount == 0 && output.Connectivity == SectionConnectivity.All;
        if (empty)
        {
            if (entry.Section.IsValid)
            {
                chunks.RemoveSection(entry.Section);
                entry.Section = default;
                _sectionCount--;
            }

            return;
        }

        var geometry = new ChunkSectionGeometry(output.Vertices, output.Indices(ChunkCategory.Opaque), output.Indices(ChunkCategory.Cutout), output.Indices(ChunkCategory.Transparent))
        {
            Visibility = output.Connectivity,
        };
        if (entry.Section.IsValid && chunks.Contains(entry.Section))
        {
            chunks.ReplaceSection(entry.Section, geometry);
            return;
        }

        GridEntry grid = entry.Grid;
        if (!grid.Group.IsValid || !chunks.Contains(grid.Group))
        {
            grid.Group = chunks.CreateGroup(grid.Grid.Transform);
            grid.Transform = grid.Grid.Transform;
        }

        entry.Section = chunks.AddSection(grid.Group, new SectionOrigin(entry.Coordinate.X, entry.Coordinate.Y, entry.Coordinate.Z), geometry);
        _sectionCount++;
    }

    // Starts the remeshes of the dirty chunks, changed blocks first, then the nearest the camera.
    private void Schedule(in WorldPosition camera)
    {
        if (_dirty.Count == 0 || _running.Count >= _maxRunning)
        {
            return;
        }

        foreach (ChunkEntry entry in _dirty)
        {
            entry.Distance = DistanceSquared(entry, camera);
        }

        _dirty.Sort(_order);
        int kept = 0;
        for (int i = 0; i < _dirty.Count; i++)
        {
            ChunkEntry entry = _dirty[i];
            if (entry.Removed)
            {
                entry.Dirty = false;
                continue;
            }

            if (entry.Running is not null || _running.Count >= _maxRunning)
            {
                _dirty[kept++] = entry;
                continue;
            }

            entry.Dirty = false;
            Start(entry);
        }

        _dirty.RemoveRange(kept, _dirty.Count - kept);
    }

    private static double DistanceSquared(ChunkEntry entry, in WorldPosition camera)
    {
        Vector3 local = entry.Grid.Transform.InverseTransformPoint(camera);
        double dx = (entry.Coordinate.X * 32.0) + 16 - local.X;
        double dy = (entry.Coordinate.Y * 32.0) + 16 - local.Y;
        double dz = (entry.Coordinate.Z * 32.0) + 16 - local.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private void Start(ChunkEntry entry)
    {
        Grid grid = entry.Grid.Grid;
        if (!grid.Storage.TryGetChunk(entry.Coordinate, out Chunk? chunk))
        {
            return;
        }

        bool urgent = entry.Urgent;
        entry.Urgent = false;
        _startedCount++;

        // A chunk of air has no geometry, whatever its neighbors: no job.
        if (chunk!.Palette.IsUniform && chunk.Palette.GetEntry(0) == 0)
        {
            if (entry.Section.IsValid)
            {
                _removeSections.Add(entry.Section);
                entry.Section = default;
                _sectionCount--;
            }

            entry.Meshed = entry.Generation;
            _applied++;
            return;
        }

        MeshWork work = _pool.Count > 0 ? _pool.Pop() : new MeshWork();
        work.Visuals = _visuals!;
        work.Entry = entry;
        work.Generation = entry.Generation;
        work.Version = chunk.Version;
        work.Urgent = urgent;
        Copy(grid, entry.Coordinate, chunk, work.Input);
        entry.Running = work;
        work.Handle = _context.Jobs.Schedule(new MeshJob(work), priority: JobPriority.Background);
        _running.Add(work);
    }

    private void Recycle(MeshWork work)
    {
        work.Entry = null;
        work.Input.Instances.Clear();
        _pool.Push(work);
    }

    // The frozen copy: the cells of the chunk and a border of one cell from its 26 neighbors.
    private void Copy(Grid grid, ChunkCoordinate coordinate, Chunk chunk, MeshInput input)
    {
        uint[] states = input.States;
        uint absent = grid.Residency == GridResidency.Kept ? 0 : MeshInput.NotLoaded;
        BlockVisuals visuals = _visuals!;
        input.Instances.Clear();
        for (int cy = -1; cy <= 1; cy++)
        {
            for (int cz = -1; cz <= 1; cz++)
            {
                for (int cx = -1; cx <= 1; cx++)
                {
                    Chunk? source = chunk;
                    if ((cx | cy | cz) != 0 && !grid.Storage.TryGetChunk(new ChunkCoordinate(coordinate.X + cx, coordinate.Y + cy, coordinate.Z + cz), out source))
                    {
                        source = null;
                    }

                    Range(cx, out int x0, out int x1);
                    Range(cy, out int y0, out int y1);
                    Range(cz, out int z0, out int z1);
                    bool instances = source is not null && visuals.HasInstanceStates && HasInstanceState(source.Palette, visuals);
                    for (int y = y0; y < y1; y++)
                    {
                        for (int z = z0; z < z1; z++)
                        {
                            int row = MeshInput.Index(x0, y, z);
                            if (source is null)
                            {
                                states.AsSpan(row, x1 - x0).Fill(absent);
                                continue;
                            }

                            Palette palette = source.Palette;
                            int local = VoxelCoordinates.ToIndex(new LocalBlockCoordinate(x0 & 31, y & 31, z & 31));
                            if (palette.IsUniform)
                            {
                                states.AsSpan(row, x1 - x0).Fill(palette.GetEntry(0));
                            }
                            else
                            {
                                for (int x = 0; x < x1 - x0; x++)
                                {
                                    states[row + x] = palette.Get(local + x);
                                }
                            }

                            if (instances)
                            {
                                for (int x = 0; x < x1 - x0; x++)
                                {
                                    CopyInstance(input, source, row + x, local + x, (cx | cy | cz) == 0);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private void CopyInstance(MeshInput input, Chunk source, int cell, int local, bool own)
    {
        uint state = input.States[cell];
        BlockVisuals visuals = _visuals!;
        if ((visuals.Occludes(state) & BlockVisuals.InstanceBit) == 0)
        {
            return;
        }

        byte occludes = 0;
        if (_blocks.GetOpacity(state) == Nobara.Blocks.BlockOpacity.Opaque)
        {
            FaceMask mask = _shapes.Get(state, source.GetBlockDataView(local)).Opacity;
            for (int direction = 0; direction < 6; direction++)
            {
                if (mask[BlockVisuals.FaceOf(direction)])
                {
                    occludes |= (byte)(1 << direction);
                }
            }
        }

        input.Instances.Add(new InstanceCell(cell, occludes, own ? source.CopyBlockData(local) : BlockDataView.Empty));
    }

    private static bool HasInstanceState(Palette palette, BlockVisuals visuals)
    {
        for (int i = 0; i < palette.Count; i++)
        {
            if ((visuals.Occludes(palette.GetEntry(i)) & BlockVisuals.InstanceBit) != 0)
            {
                return true;
            }
        }

        return false;
    }

    // The cells of a neighbor that the copy holds along an axis: the last layer, the chunk, or the first layer.
    private static void Range(int offset, out int from, out int to)
    {
        (from, to) = offset switch
        {
            < 0 => (-1, 0),
            > 0 => (32, 33),
            _ => (0, 32),
        };
    }

    private void OnSessionStarting(WorldSession session)
    {
        _task = session.Loading.Add("meshed chunks of the simulation area");
        _voxelTask = null;
        foreach (LoadingTask task in session.Loading.Tasks)
        {
            if (task.Module == "nobara.voxel" && task.Name == "chunks of the simulation area")
            {
                _voxelTask = task;
            }
        }
    }

    // The session waits until every chunk of the simulation area is loaded and meshed.
    private void TrackLoading()
    {
        if (_task is not { State: LoadingTaskState.Running })
        {
            return;
        }

        if (_voxelTask is { State: not LoadingTaskState.Completed })
        {
            return;
        }

        int active = 0;
        int meshed = 0;
        foreach (GridEntry grid in _grids.Values)
        {
            foreach (ChunkEntry entry in grid.Chunks.Values)
            {
                if (_loading.GetStatus(grid.Grid, entry.Coordinate).Level == TicketLevel.Active)
                {
                    active++;
                    meshed += entry.IsMeshed ? 1 : 0;
                }
            }
        }

        if (meshed == active)
        {
            _task.Complete();
        }
        else
        {
            _task.Report((double)meshed / active);
        }
    }

    private void OnSessionEnded(WorldSession session)
    {
        foreach (MeshWork work in _running)
        {
            work.Handle.Complete();
            if (work.Entry is { } entry)
            {
                entry.Running = null;
            }

            Recycle(work);
        }

        _running.Clear();
        foreach (GridEntry grid in _grids.Values)
        {
            if (grid.Group.IsValid)
            {
                _destroyGroups.Add(grid.Group);
            }

            foreach (ChunkEntry entry in grid.Chunks.Values)
            {
                entry.Removed = true;
            }
        }

        _grids.Clear();
        _dirty.Clear();
        _chunkCount = 0;
        _sectionCount = 0;
        _task = null;
        _voxelTask = null;
    }

    /// <summary>The order of the remeshes: chunks changed by an action first, then the nearest the camera.</summary>
    internal sealed class DistanceOrder : IComparer<ChunkEntry>
    {
        public int Compare(ChunkEntry? x, ChunkEntry? y) =>
            x!.Urgent != y!.Urgent ? (x.Urgent ? -1 : 1) : x.Distance.CompareTo(y.Distance);
    }

    private sealed class MesherRegistration(ChunkRemesher owner, string shapeId, IBlockMesher mesher) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed || owner._started)
            {
                return;
            }

            _disposed = true;
            if (owner._meshers.TryGetValue(shapeId, out IBlockMesher? registered) && ReferenceEquals(registered, mesher))
            {
                owner._meshers.Remove(shapeId);
            }
        }
    }
}
