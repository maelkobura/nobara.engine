// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Core.Jobs;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>The light of one grid: its chunks, the heights of its columns, and the light of the terrain around it.</summary>
internal sealed class GridLight
{
    public Dictionary<ChunkCoordinate, LightChunk> Chunks { get; } = [];

    public Dictionary<(int X, int Z), SkyColumn> Columns { get; } = [];

    public TerrainLightSample Sample;

    public bool HasSample;

    public WorldTransform SampleTransform;
}

/// <summary>
/// The heights of one column of chunks of a grid, as nLighting last read them
/// from nVoxel: a cell above the height of its column is exposed to the sky.
/// </summary>
/// <remarks>Written on the loop thread between rounds; read by the propagation jobs.</remarks>
internal sealed class SkyColumn
{
    /// <summary>The height of a column whose height is unknown: every cell is exposed.</summary>
    public const int Unknown = int.MinValue;

    public SkyColumn() => Array.Fill(Heights, Unknown);

    /// <summary>The coordinate along Y of the highest opaque block of each column of blocks, by <c>x + 32 z</c>.</summary>
    public int[] Heights { get; } = new int[32 * 32];

    /// <summary>The chunks of the column that have light.</summary>
    public List<LightChunk> Chunks { get; } = [];
}

/// <summary>The light of one context: storage by grid and chunk, the tick system, the events of nVoxel, and the queries.</summary>
internal sealed class Lighting : ILighting, ITickSystem, IDisposable
{
    public const string SystemName = "nobara.lighting.propagation";

    private readonly ModuleContext _context;
    private readonly LightSourceRegistry _sources;
    private readonly JobData _access;
    private readonly Dictionary<Grid, GridLight> _grids = [];
    private readonly List<LightChangeSubscription> _subscriptions = [];
    private readonly List<LightChunkChange> _changes = [];
    private readonly List<LightFocus> _focuses = [];
    private readonly List<(DimensionId Dimension, WorldPosition Position)> _points = [];
    private readonly Dictionary<EntityWorld, EntityQuery> _dynamicQueries = [];
    private readonly ulong[] _touched = new ulong[16];
    private readonly Action _stepCallback;
    private IReadOnlyList<DimensionId> _dimensions = [];
    private Propagator _propagator = null!;
    private LoopAccess _eventAccess = null!;
    private IVoxelWorlds _worlds = null!;
    private IHeightMaps _heights = null!;
    private IChunkLoading _loading = null!;
    private IEntityWorlds _entities = null!;
    private VoxelNotificationHandle? _notifications;
    private ChunkResidencySubscription? _residency;
    private Func<int, int, int, LightChunk?>? _snapshotNeighbor;
    private LightChunk? _snapshotCenter;
    private Dictionary<ChunkCoordinate, LightChunk>? _snapshotChunks;
    private bool _live;
    private bool _terrainChanged;
    private int _chunkCount;
    private int _lastPassChunks;
    private int _lastPassRounds;
    private TimeSpan _lastPassDuration;
    private long _budget = long.MaxValue;
    private long _lastTickOperations;
    private TimeSpan _lastTickDuration;
    private long _tick;

    public Lighting(ModuleContext context, LightSourceRegistry sources)
    {
        _context = context;
        _sources = sources;
        _access = context.Jobs.CreateData("light storage");
        _stepCallback = () => Step(_budget);
    }

    public bool IsColored => _sources.IsColored;

    public JobData LightAccess => _access;

    public LightingStatistics Statistics
    {
        get
        {
            int arrays = 0;
            long bytes = 0;
            foreach (GridLight grid in _grids.Values)
            {
                foreach (LightChunk chunk in grid.Chunks.Values)
                {
                    if (chunk.IsUniform) continue;
                    arrays++;
                    bytes += chunk.StorageBytes;
                }
            }

            return new LightingStatistics(_chunkCount, arrays, bytes, _lastPassChunks, _lastPassRounds, _lastPassDuration);
        }
    }

    public LightingBacklog Backlog
    {
        get
        {
            int chunks = 0;
            long cells = 0;
            long oldest = 0;
            foreach (LightChunk chunk in _propagator.Active)
            {
                if (chunk.Work is not { } work || !(work.HasRemovalWork || work.HasAdditionWork)) continue;
                chunks++;
                cells += work.Seeds.Count + work.RemovalQueue.Count + (work.HasIncomingAdd || work.HasIncomingRemoval ? Faces.Cells : 0);
                foreach (ulong bits in work.Pending) cells += BitOperations.PopCount(bits);
                oldest = Math.Max(oldest, _tick - work.Since);
            }

            return new LightingBacklog(chunks, cells, oldest, _budget, _lastTickOperations, _lastTickDuration);
        }
    }

    /// <summary>Gets the focuses that are not disposed, for the debug window.</summary>
    internal List<LightFocus> Focuses => _focuses;

    /// <summary>Gets or sets whether the end of a session keeps the light, to test the world state counter.</summary>
    internal bool KeepChunksAtSessionEnd { get; set; }

    public void Initialize(IVoxelWorlds worlds, IVoxelBatches batches, IChunkLoading loading, IWorldSessions sessions, ITickSchedule schedule, IHeightMaps heights, IEntityWorlds entities)
    {
        _worlds = worlds;
        _heights = heights;
        _loading = loading;
        _entities = entities;
        _sources.Initialize(_context, entities);
        _eventAccess = _context.Jobs.DeclareLoopAccess("voxel events of nLighting", [worlds.ChunkAccess], [_access]);
        schedule.AddSystem(_context, SystemName, TickStages.Late, this, new TickSystemOptions
        {
            // The positions of the dynamic lights are those of this tick, children included.
            After = ["nobara.entity.transforms"],
            Reads = [worlds.ChunkAccess, entities.Structure, entities.GetComponent(_sources.DynamicLight).Data, entities.GetComponent(entities.WorldTransform).Data],
            Writes = [_access],
        });
        _notifications = batches.Subscribe(_context, OnBatch);
        _residency = loading.SubscribeResidency(_context, OnResidency);
        sessions.SubscribeStarting(_context, _ =>
        {
            // Dimensions are declared outside sessions: read them once, as their list is a copy.
            _dimensions = _worlds.Dimensions;
            _live = true;
        });
        sessions.SubscribeEnded(_context, _ => Clear(KeepChunksAtSessionEnd));
        sessions.RegisterWorldStateCounter(_context, "light chunks", () => _chunkCount);
    }

    public void Start(IShapeRegistry shapes, long budget)
    {
        _propagator = new Propagator(_context, _sources, shapes, _worlds.ChunkAccess, _access);
        _snapshotNeighbor = SnapshotNeighbor;
        _budget = budget;
    }

    // The pass is not hot path code as a whole (it sorts and notifies), but allocates nothing once warm, which a test checks.
    public void Run(in TickTime time) => _stepCallback();

    public bool CopyFrozen(Grid grid, ChunkCoordinate chunk, LightSnapshot destination)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(destination);
        if (!_grids.TryGetValue(grid, out GridLight? light) || !light.Chunks.TryGetValue(chunk, out LightChunk? center)) return false;
        _access.CheckRead();
        _snapshotCenter = center;
        _snapshotChunks = light.Chunks;
        destination.Fill(center, _snapshotNeighbor!, _sources.IsColored);
        _snapshotCenter = null;
        _snapshotChunks = null;
        return true;
    }

    public LightChangeSubscription SubscribeChanges(ModuleContext module, Action<LightChunkChange> handler)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new LightChangeSubscription(handler);
        _subscriptions.Add(subscription);
        return subscription;
    }

    [HotPath]
    public bool TryGetBrightness(Grid grid, BlockCoordinate block, out Brightness brightness)
    {
        brightness = default;
        if (grid is null || !TryGetChunk(grid, VoxelCoordinates.ToChunk(block), out LightChunk? chunk)) return false;
        _access.CheckRead();
        ushort value = chunk!.Get(VoxelCoordinates.ToIndex(block));
        int red = (value >> 8) & 15;
        int green = (value >> 4) & 15;
        int blue = value & 15;
        if (!_sources.IsColored) red = green = blue;

        int dynamicRed = 0;
        int dynamicGreen = 0;
        int dynamicBlue = 0;
        ReadOnlySpan<DynamicLightSource> dynamic = _sources.GetDynamicSources(grid.Dimension);
        if (!dynamic.IsEmpty)
        {
            WorldPosition center = BlockCenter(grid, block);
            for (int i = 0; i < dynamic.Length; i++)
            {
                ref readonly DynamicLightSource source = ref dynamic[i];
                float distance = source.Position.RelativeTo(center).Length();
                if (!(distance < source.Radius)) continue;
                dynamicRed = Math.Max(dynamicRed, DynamicLight.LevelAt(source.Color.Red, distance, source.Radius));
                dynamicGreen = Math.Max(dynamicGreen, DynamicLight.LevelAt(source.Color.Green, distance, source.Radius));
                dynamicBlue = Math.Max(dynamicBlue, DynamicLight.LevelAt(source.Color.Blue, distance, source.Radius));
            }
        }

        brightness = new Brightness(value >> 12, new LightColor(red, green, blue), new LightColor(dynamicRed, dynamicGreen, dynamicBlue));
        return true;
    }

    public bool TryGetTerrainSample(Grid grid, out TerrainLightSample sample)
    {
        ArgumentNullException.ThrowIfNull(grid);
        sample = default;
        if (grid.IsTerrain || !_grids.TryGetValue(grid, out GridLight? light) || !light.HasSample) return false;
        sample = light.Sample;
        return true;
    }

    public LightFocus AddFocus(ModuleContext module, DimensionId dimension, in WorldPosition position, Vector3 direction = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!_live) throw new InvalidOperationException("A light focus belongs to a world session.");
        var focus = new LightFocus(dimension, position, direction);
        _focuses.Add(focus);
        return focus;
    }

    /// <summary>Reads the light of a block, for tests and diagnostics.</summary>
    internal bool TryGetLight(Grid grid, BlockCoordinate block, out LightLevel light)
    {
        light = default;
        if (!TryGetChunk(grid, VoxelCoordinates.ToChunk(block), out LightChunk? chunk)) return false;
        ushort value = chunk!.Get(VoxelCoordinates.ToIndex(block));
        if (!_sources.IsColored)
        {
            int level = value & 15;
            value = (ushort)((value & 0xF000) | (level << 8) | (level << 4) | level);
        }

        light = new LightLevel(value);
        return true;
    }

    [HotPath]
    internal bool TryGetChunk(Grid grid, ChunkCoordinate coordinate, out LightChunk? chunk)
    {
        chunk = null;
        return _grids.TryGetValue(grid, out GridLight? light) && light.Chunks.TryGetValue(coordinate, out chunk);
    }

    /// <summary>Starts the propagation from a block as if it changed, for tests.</summary>
    internal void Reseed(Grid grid, BlockCoordinate block)
    {
        if (!TryGetChunk(grid, VoxelCoordinates.ToChunk(block), out LightChunk? chunk)) return;
        Seed(chunk!, VoxelCoordinates.ToIndex(block), whole: true);
    }

    /// <summary>Runs the propagation until no work remains, outside the tick, for tests.</summary>
    internal void RunPass() => Step(long.MaxValue);

    public void Dispose()
    {
        Clear(keep: false);
        _notifications?.Dispose();
        _residency?.Dispose();
    }

    // One tick of the light: rounds within the budget, nearest chunks first, then the changes, the dynamic lights, and the samples.
    private void Step(long budget)
    {
        _propagator.Tick = ++_tick;
        long start = Stopwatch.GetTimestamp();
        _terrainChanged = false;
        if (_propagator.Active.Count > 0)
        {
            UpdatePriorities();
            _lastTickOperations = _propagator.Run(budget);
            _lastPassChunks = _propagator.Active.Count;
            foreach (LightChunk chunk in _propagator.Active)
            {
                if (!chunk.Changed) continue;
                chunk.Version++;
                chunk.Compact();
                _changes.Add(new LightChunkChange(chunk.Grid, chunk.Coordinate, chunk.BorderChanged, chunk.Version));
                if (chunk.Grid.IsTerrain) _terrainChanged = true;
                chunk.Changed = false;
                chunk.BorderChanged = false;
            }

            _propagator.Release();
            _lastPassRounds = _propagator.LastRounds;
            _lastPassDuration = Stopwatch.GetElapsedTime(start);
            _lastTickDuration = _lastPassDuration;
            for (int i = 0; i < _changes.Count; i++)
            {
                for (int s = 0; s < _subscriptions.Count; s++)
                {
                    if (!_subscriptions[s].IsRevoked) _subscriptions[s].Handler(_changes[i]);
                }
            }

            _changes.Clear();
            _subscriptions.RemoveAll(static subscription => subscription.IsRevoked);
        }
        else
        {
            _lastTickOperations = 0;
            _lastTickDuration = TimeSpan.Zero;
        }

        if (_live)
        {
            ReadDynamicLights();
            UpdateSamples();
        }
    }

    // The squared distance of each chunk with work to the nearest point of interest of its dimension.
    private void UpdatePriorities()
    {
        _points.Clear();
        for (int i = _focuses.Count - 1; i >= 0; i--)
        {
            if (_focuses[i].IsRevoked) _focuses.RemoveAt(i);
        }

        foreach (LightFocus focus in _focuses) _points.Add((focus.Dimension, focus.Position));
        IReadOnlyList<AreaTicketView> areas = _loading.AreaTickets;
        for (int i = 0; i < areas.Count; i++)
        {
            AreaTicketView area = areas[i];
            if (area.Level == TicketLevel.Loaded || area.Grid.IsDestroyed) continue;
            _points.Add((area.Grid.Dimension, ChunkCenter(area.Grid, area.Center)));
        }

        foreach (LightChunk chunk in _propagator.Active)
        {
            float nearest = float.MaxValue;
            if (_points.Count > 0)
            {
                DimensionId dimension = chunk.Grid.Dimension;
                WorldPosition center = ChunkCenter(chunk.Grid, chunk.Coordinate);
                foreach ((DimensionId pointDimension, WorldPosition position) in _points)
                {
                    if (pointDimension == dimension) nearest = Math.Min(nearest, center.RelativeTo(position).LengthSquared());
                }
            }

            chunk.Priority = nearest;
        }
    }

    // Reads the entities that carry a dynamic light, in every world of the context.
    private void ReadDynamicLights()
    {
        _sources.ClearDynamic();
        IReadOnlyList<DimensionId> dimensions = _dimensions;
        ComponentType<DynamicLight> light = _sources.DynamicLight;
        ComponentType<WorldTransform> transform = _entities.WorldTransform;
        for (int d = 0; d < dimensions.Count; d++)
        {
            if (!_entities.TryGet(dimensions[d], out EntityWorld? world)) continue;
            if (!_dynamicQueries.TryGetValue(world, out EntityQuery? query))
            {
                query = world.Query(new QueryDescription { All = [light, transform] });
                _dynamicQueries.Add(world, query);
            }

            foreach (ChunkView block in query)
            {
                ReadOnlySpan<DynamicLight> lights = block.Read(light);
                ReadOnlySpan<WorldTransform> transforms = block.Read(transform);
                ReadOnlySpan<EntityId> entities = block.Entities;
                for (int i = 0; i < block.Count; i++)
                {
                    if (!(lights[i].Radius > 0)) continue;
                    _sources.AddDynamic(dimensions[d], new DynamicLightSource(entities[i], transforms[i].Position, _sources.Normalize(lights[i].Color), lights[i].Radius));
                }
            }
        }
    }

    // Takes again the light of the terrain around each other grid that moved, or when the light of the terrain changed.
    private void UpdateSamples()
    {
        IReadOnlyList<Grid> grids = _worlds.Grids;
        for (int i = 0; i < grids.Count; i++)
        {
            Grid grid = grids[i];
            if (grid.IsTerrain || grid.IsDestroyed || !_grids.TryGetValue(grid, out GridLight? light)) continue;
            WorldTransform transform = grid.Transform;
            if (light.HasSample && !_terrainChanged && light.SampleTransform == transform) continue;
            light.SampleTransform = transform;
            Sample(grid, light, transform);
        }
    }

    private void Sample(Grid grid, GridLight light, in WorldTransform transform)
    {
        TerrainLightSample previous = light.HasSample ? light.Sample : new TerrainLightSample(15, 0, 0, 0, 0, 0);
        int points = 0;
        int sky = 0;
        int red = 0;
        int green = 0;
        int blue = 0;
        if (_worlds.TryGetTerrain(grid.Dimension, out Grid? terrain) && terrain is not null && _grids.TryGetValue(terrain, out GridLight? ground))
        {
            Vector3 minimum;
            Vector3 maximum;
            if (grid.Bounds is { } bounds)
            {
                minimum = new Vector3(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z);
                maximum = new Vector3(bounds.Maximum.X + 1f, bounds.Maximum.Y + 1f, bounds.Maximum.Z + 1f);
            }
            else
            {
                minimum = maximum = Vector3.Zero;
            }

            // The center, then the eight corners of the box of the grid.
            for (int corner = -1; corner < 8; corner++)
            {
                Vector3 local = corner < 0
                    ? (minimum + maximum) * 0.5f
                    : new Vector3((corner & 1) == 0 ? minimum.X : maximum.X, (corner & 2) == 0 ? minimum.Y : maximum.Y, (corner & 4) == 0 ? minimum.Z : maximum.Z);
                if (!TryGetTerrainBlock(terrain, transform.TransformPoint(local), out BlockCoordinate block)) continue;
                if (!ground.Chunks.TryGetValue(VoxelCoordinates.ToChunk(block), out LightChunk? chunk)) continue;
                int index = VoxelCoordinates.ToIndex(block);
                if (_propagator.ClosedFaces(chunk, index) == 0x3F) continue;
                ushort value = chunk.Get(index);
                int level = value & 15;
                points++;
                sky += value >> 12;
                red += _sources.IsColored ? (value >> 8) & 15 : level;
                green += _sources.IsColored ? (value >> 4) & 15 : level;
                blue += level;
            }
        }

        TerrainLightSample next = points == 0
            ? previous with { Points = 0 }
            : new TerrainLightSample(sky / (float)points, red / (float)points, green / (float)points, blue / (float)points, points, previous.Version);
        if (!light.HasSample || next != previous) next = next with { Version = previous.Version + 1 };
        light.Sample = next;
        light.HasSample = true;
    }

    private void OnBatch(VoxelBatchNotification notification)
    {
        if (!_live || !_grids.TryGetValue(notification.Grid, out GridLight? light) || !light.Chunks.TryGetValue(notification.Chunk, out LightChunk? chunk)) return;
        using LoopAccessScope scope = _eventAccess.Enter();
        _access.CheckWrite();
        bool touched = false;
        foreach (VoxelBlockChange change in notification.Changes)
        {
            if (change.OldState == change.NewState && !change.DataChanged) continue;
            if (!_sources.Differs(change.OldState, change.NewState)) continue;
            Seed(chunk, change.LocalIndex, _sources.FacesDiffer(change.OldState, change.NewState));
            int column = change.LocalIndex & 1023;
            _touched[column >> 6] |= 1UL << (column & 63);
            touched = true;
        }

        if (!touched) return;

        // A block that becomes or stops being opaque may move the height of its column, which moves the sky of the cells between.
        for (int word = 0; word < _touched.Length; word++)
        {
            ulong bits = _touched[word];
            _touched[word] = 0;
            while (bits != 0)
            {
                int column = (word << 6) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                int x = (notification.Chunk.X * VoxelCoordinates.ChunkSize) + (column & 31);
                int z = (notification.Chunk.Z * VoxelCoordinates.ChunkSize) + (column >> 5);
                ChangeHeight(chunk.Column, column, ReadHeight(notification.Grid, x, z));
            }
        }
    }

    // A cell whose sources or faces changed: its light becomes its sources, a lost component leaves the blocks it reached, and its neighbors spread again.
    private void Seed(LightChunk chunk, int index, bool whole)
    {
        WorkState work = _propagator.Work(chunk);
        ushort light = chunk.Get(index);
        ushort source = _propagator.SourceOf(chunk, index);

        // When its faces change, light that crossed it may no longer: all of it goes. Otherwise only the components that its sources no longer give.
        ushort removed = whole ? light : Faces.Above(light, source);
        chunk.Set(index, source);
        if (removed != 0) work.Seeds.Add((uint)index | ((uint)removed << 16));
        if (source != 0) work.MarkPending(index);
        for (int face = 0; face < 6; face++)
        {
            if (Faces.Step(index, face, out int neighbor)) work.MarkPending(neighbor);
            else if (chunk.Neighbors[face] is { } next) _propagator.Work(next).MarkPending(Faces.Cell(Faces.Opposite(face), Faces.Slot(face, index)));
        }
    }

    // The height of a column moved: the cells between the two heights change exposure, in every chunk of the column that has light.
    private void ChangeHeight(SkyColumn column, int at, int height)
    {
        int previous = column.Heights[at];
        if (previous == height) return;
        column.Heights[at] = height;
        long low = (long)Math.Min(previous, height) + 1;
        long high = Math.Max(previous, height);
        int x = at & 31;
        int z = at >> 5;
        foreach (LightChunk chunk in column.Chunks)
        {
            long from = Math.Max(low, chunk.BaseY);
            long to = Math.Min(high, chunk.BaseY + 31L);
            for (long y = from; y <= to; y++) Seed(chunk, x + (32 * (z + (32 * (int)(y - chunk.BaseY)))), whole: false);
        }
    }

    private int ReadHeight(Grid grid, int x, int z) => _heights.TryGetHeight(grid, x, z, out int height) ? height : SkyColumn.Unknown;

    // The height of a column of blocks of a grid, from the columns of nLighting, or from nVoxel for a column without light.
    private int HeightAt(Grid grid, GridLight light, int x, int z) =>
        light.Columns.TryGetValue((x >> 5, z >> 5), out SkyColumn? column) ? column.Heights[(x & 31) + ((z & 31) * 32)] : ReadHeight(grid, x, z);

    private void OnResidency(ChunkResidencyChange change)
    {
        if (!_live) return;
        using LoopAccessScope scope = _eventAccess.Enter();
        _access.CheckWrite();
        if (change.Kind == ChunkResidencyKind.Loaded)
        {
            if (change.Grid.Storage.TryGetChunk(change.Chunk, out Chunk? blocks)) Load(change.Grid, change.Chunk, blocks!);
        }
        else
        {
            Unload(change.Grid, change.Chunk);
        }
    }

    private void Load(Grid grid, ChunkCoordinate coordinate, Chunk blocks)
    {
        if (!_grids.TryGetValue(grid, out GridLight? light))
        {
            light = new GridLight();
            _grids.Add(grid, light);
        }

        if (light.Chunks.ContainsKey(coordinate)) return;

        // The heights of the column, which this chunk may raise for the chunks below it.
        int baseX = coordinate.X * VoxelCoordinates.ChunkSize;
        int baseZ = coordinate.Z * VoxelCoordinates.ChunkSize;
        if (!light.Columns.TryGetValue((coordinate.X, coordinate.Z), out SkyColumn? column))
        {
            column = new SkyColumn();
            for (int at = 0; at < 1024; at++) column.Heights[at] = ReadHeight(grid, baseX + (at & 31), baseZ + (at >> 5));
            light.Columns.Add((coordinate.X, coordinate.Z), column);
        }
        else
        {
            for (int at = 0; at < 1024; at++) ChangeHeight(column, at, ReadHeight(grid, baseX + (at & 31), baseZ + (at >> 5)));
        }

        int baseY = coordinate.Y * VoxelCoordinates.ChunkSize;
        bool anyExposed = false;
        bool allExposed = true;
        foreach (int height in column.Heights)
        {
            if (height < baseY + 31) anyExposed = true;
            if (height >= baseY) allExposed = false;
        }

        var chunk = new LightChunk(grid, coordinate, blocks, _sources.IsColored, column, allExposed ? LightChunk.DefaultValue : (ushort)0);
        light.Chunks.Add(coordinate, chunk);
        column.Chunks.Add(chunk);
        _chunkCount++;
        for (int face = 0; face < 6; face++)
        {
            if (!light.Chunks.TryGetValue(Offset(coordinate, face), out LightChunk? neighbor)) continue;
            chunk.Neighbors[face] = neighbor;
            neighbor.Neighbors[Faces.Opposite(face)] = chunk;
        }

        // The sky above the height of each column.
        if (anyExposed && !allExposed)
        {
            for (int at = 0; at < 1024; at++)
            {
                for (int y = Floor(column.Heights[at], baseY); y < 32; y++) chunk.Set(at + (1024 * y), LightChunk.DefaultValue);
            }
        }

        // Its sources shine, read only when the palette of the chunk holds one.
        StateLight[] states = _sources.States;
        Palette palette = blocks.Palette;
        bool emits = false;
        for (int entry = 0; entry < palette.Count && !emits; entry++) emits = states[palette.GetEntry(entry)].Emission != 0;
        WorkState? work = null;
        if (emits)
        {
            for (int index = 0; index < LightChunk.Volume; index++)
            {
                if (states[blocks.GetState(index)].Emission == 0) continue;
                chunk.Set(index, _propagator.SourceOf(chunk, index));
                (work ??= _propagator.Work(chunk)).MarkPending(index);
            }
        }

        // The sky enters the cells below the height of their column from the exposed cells beside them.
        if (anyExposed) MarkSkyEdges(grid, light, chunk, ref work);

        // The fronts that waited on the faces of its neighbors enter it, where they make it brighter.
        for (int face = 0; face < 6; face++)
        {
            if (chunk.Neighbors[face] is not { } neighbor || (neighbor.IsUniform && neighbor.UniformValue == 0)) continue;
            int side = Faces.Opposite(face);
            WorkState? beside = null;
            for (int slot = 0; slot < Faces.Cells; slot++)
            {
                int cell = Faces.Cell(side, slot);
                ushort entering = Faces.Subtract(neighbor.Get(cell), 0, 1);
                if (entering == 0 || !Faces.Raises(entering, chunk.Get(Faces.Cell(face, slot)))) continue;
                (beside ??= _propagator.Work(neighbor)).MarkPending(cell);
            }
        }
    }

    // Marks the exposed cells of a new chunk that touch a cell below the height of its column: the lowest exposed cell of each column, and the cells beside a higher column.
    private void MarkSkyEdges(Grid grid, GridLight light, LightChunk chunk, ref WorkState? work)
    {
        int[] heights = chunk.Column.Heights;
        int baseY = chunk.BaseY;
        int baseX = chunk.Coordinate.X * VoxelCoordinates.ChunkSize;
        int baseZ = chunk.Coordinate.Z * VoxelCoordinates.ChunkSize;
        for (int at = 0; at < 1024; at++)
        {
            int floor = Floor(heights[at], baseY);
            if (floor == 32) continue;
            int x = at & 31;
            int z = at >> 5;

            // Below: the opaque block of the column, in this chunk or just under it.
            if (floor > 0 || (long)heights[at] == baseY - 1L) (work ??= _propagator.Work(chunk)).MarkPending(at + (1024 * floor));

            for (int side = 0; side < 4; side++)
            {
                int nx = x + (side == 0 ? 1 : side == 1 ? -1 : 0);
                int nz = z + (side == 2 ? 1 : side == 3 ? -1 : 0);
                int height = (nx & ~31) == 0 && (nz & ~31) == 0 ? heights[nx + (32 * nz)] : HeightAt(grid, light, baseX + nx, baseZ + nz);
                int beside = Floor(height, baseY);
                for (int y = floor; y < beside; y++) (work ??= _propagator.Work(chunk)).MarkPending(at + (1024 * y));
            }
        }
    }

    // The lowest layer of a chunk exposed to the sky in a column, from 0 (every layer) to 32 (none).
    private static int Floor(int height, int baseY) => (int)Math.Clamp((long)height - baseY + 1, 0, 32);

    private void Unload(Grid grid, ChunkCoordinate coordinate)
    {
        if (!_grids.TryGetValue(grid, out GridLight? light) || !light.Chunks.Remove(coordinate, out LightChunk? chunk)) return;
        SkyColumn column = chunk.Column;
        column.Chunks.Remove(chunk);
        if (column.Chunks.Count == 0) light.Columns.Remove((coordinate.X, coordinate.Z));

        // A removal that this chunk had not finished may have lowered its faces already: the light beside them is then removed whole.
        bool unfinished = chunk.Work is { HasRemovalWork: true };
        bool dark = chunk.IsUniform && chunk.UniformValue == 0;
        for (int face = 0; face < 6; face++)
        {
            if (chunk.Neighbors[face] is not { } neighbor) continue;
            int side = Faces.Opposite(face);
            neighbor.Neighbors[side] = null;

            // The light that waits on that face came from this chunk, and goes with it.
            if (neighbor.Work is { HasIncomingAdd: true } waiting) waiting.IncomingAdd.AsSpan(side * Faces.Cells, Faces.Cells).Clear();
            if (dark && !unfinished) continue;

            // The light that came from this chunk leaves its neighbors: it may come back different.
            WorkState? work = null;
            for (int slot = 0; slot < Faces.Cells; slot++)
            {
                ushort beside = neighbor.Get(Faces.Cell(side, slot));
                if (beside == 0) continue;
                ushort removed;
                if (unfinished)
                {
                    removed = 0xFFFF;
                }
                else
                {
                    int from = Faces.Cell(face, slot);
                    removed = chunk.Get(from);
                    if (removed == 0) continue;
                    int closed = _propagator.ClosedFaces(chunk, from);
                    if (closed != 0x3F && (closed & (1 << face)) != 0) continue;
                    if (!IsDimmer(beside, removed)) continue;
                }

                work ??= _propagator.Work(neighbor);
                ref ushort entering = ref work.IncomingRemoval[(side * Faces.Cells) + slot];
                entering = Faces.Max(entering, removed);
                work.HasIncomingRemoval = true;
            }
        }

        _propagator.Forget(chunk);
        chunk.Dispose();
        _chunkCount--;
        if (light.Chunks.Count == 0) _grids.Remove(grid);
    }

    // Whether a component of a block is lit and dimmer than the light beside it, so that it may have come from there.
    private static bool IsDimmer(ushort value, ushort than)
    {
        for (int shift = 0; shift < 16; shift += 4)
        {
            int here = (value >> shift) & 15;
            if (here != 0 && here < ((than >> shift) & 15)) return true;
        }

        return false;
    }

    private void Clear(bool keep)
    {
        _live = false;
        if (keep) return;
        _propagator?.Clear();
        foreach (GridLight light in _grids.Values)
        {
            foreach (LightChunk chunk in light.Chunks.Values) chunk.Dispose();
        }

        _grids.Clear();
        foreach (LightFocus focus in _focuses) focus.Dispose();
        _focuses.Clear();
        _dynamicQueries.Clear();
        _sources.ClearDynamic();
        _chunkCount = 0;
    }

    private LightChunk? SnapshotNeighbor(int dx, int dy, int dz)
    {
        ChunkCoordinate center = _snapshotCenter!.Coordinate;
        return _snapshotChunks!.TryGetValue(new ChunkCoordinate(center.X + dx, center.Y + dy, center.Z + dz), out LightChunk? chunk) ? chunk : null;
    }

    // The world position of the center of a chunk of a grid; exact far from the origin for a grid without rotation or scale, such as the terrain.
    [HotPath]
    private static WorldPosition ChunkCenter(Grid grid, ChunkCoordinate chunk)
    {
        WorldTransform transform = grid.Transform;
        if (IsTranslation(transform))
        {
            WorldPosition origin = transform.Position;
            return new WorldPosition(origin.ChunkX + chunk.X, origin.ChunkY + chunk.Y, origin.ChunkZ + chunk.Z, origin.Local + new Vector3(16));
        }

        return transform.TransformPoint(new Vector3((chunk.X * 32f) + 16, (chunk.Y * 32f) + 16, (chunk.Z * 32f) + 16));
    }

    // The world position of the center of a block of a grid, exact in the same way.
    [HotPath]
    private static WorldPosition BlockCenter(Grid grid, BlockCoordinate block)
    {
        WorldTransform transform = grid.Transform;
        if (IsTranslation(transform))
        {
            WorldPosition origin = transform.Position;
            var local = new Vector3((block.X & 31) + 0.5f, (block.Y & 31) + 0.5f, (block.Z & 31) + 0.5f);
            return new WorldPosition(origin.ChunkX + (block.X >> 5), origin.ChunkY + (block.Y >> 5), origin.ChunkZ + (block.Z >> 5), origin.Local + local);
        }

        return transform.TransformPoint(new Vector3(block.X + 0.5f, block.Y + 0.5f, block.Z + 0.5f));
    }

    // The block of the terrain that holds a world position.
    private static bool TryGetTerrainBlock(Grid terrain, in WorldPosition position, out BlockCoordinate block)
    {
        WorldTransform transform = terrain.Transform;
        double x;
        double y;
        double z;
        if (IsTranslation(transform))
        {
            WorldPosition origin = transform.Position;
            x = ((position.ChunkX - (long)origin.ChunkX) * 32.0) + ((double)position.Local.X - origin.Local.X);
            y = ((position.ChunkY - (long)origin.ChunkY) * 32.0) + ((double)position.Local.Y - origin.Local.Y);
            z = ((position.ChunkZ - (long)origin.ChunkZ) * 32.0) + ((double)position.Local.Z - origin.Local.Z);
        }
        else
        {
            Vector3 local = transform.InverseTransformPoint(position);
            (x, y, z) = (local.X, local.Y, local.Z);
        }

        x = Math.Floor(x);
        y = Math.Floor(y);
        z = Math.Floor(z);
        block = default;
        if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue || z < int.MinValue || z > int.MaxValue) return false;
        block = new BlockCoordinate((int)x, (int)y, (int)z);
        return true;
    }

    [HotPath]
    private static bool IsTranslation(in WorldTransform transform) => transform.Rotation == Quaternion.Identity && transform.Scale == Vector3.One;

    private static ChunkCoordinate Offset(ChunkCoordinate coordinate, int face) => face switch
    {
        0 => new ChunkCoordinate(coordinate.X, coordinate.Y, coordinate.Z - 1),
        1 => new ChunkCoordinate(coordinate.X + 1, coordinate.Y, coordinate.Z),
        2 => new ChunkCoordinate(coordinate.X, coordinate.Y, coordinate.Z + 1),
        3 => new ChunkCoordinate(coordinate.X - 1, coordinate.Y, coordinate.Z),
        4 => new ChunkCoordinate(coordinate.X, coordinate.Y + 1, coordinate.Z),
        _ => new ChunkCoordinate(coordinate.X, coordinate.Y - 1, coordinate.Z),
    };
}
