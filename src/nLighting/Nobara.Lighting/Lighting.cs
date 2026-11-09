// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>
/// The light of the grids of one context: its storage, its propagation, the
/// chunks whose light changed, and frozen copies of the light of a chunk.
/// Resolve it with <c>context.Resolve&lt;ILighting&gt;()</c>.
/// </summary>
/// <remarks>
/// Use it on the loop thread of the context. Every context computes its own
/// light from its own blocks; nothing is shared or sent between contexts.
/// </remarks>
public interface ILighting
{
    /// <summary>Gets whether the block channel has three components (<see cref="LightingSettings.Color"/>).</summary>
    bool IsColored { get; }

    /// <summary>Gets the size of the light and the cost of the last propagation pass.</summary>
    LightingStatistics Statistics { get; }

    /// <summary>
    /// Copies the light of a chunk and of the blocks around it, one block
    /// deep, into a copy that a job may read while the light changes.
    /// </summary>
    /// <param name="grid">The grid.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="destination">The copy to fill; reuse it from copy to copy.</param>
    /// <returns><see langword="false"/> when nLighting holds no light for that chunk: it is not loaded, or no session is active.</returns>
    /// <remarks>It allocates nothing.</remarks>
    bool CopyFrozen(Grid grid, ChunkCoordinate chunk, LightSnapshot destination);

    /// <summary>
    /// Subscribes to the chunks whose light changed. The handler runs on the
    /// loop thread, after each propagation pass, once per chunk that changed.
    /// </summary>
    /// <param name="module">The module that subscribes.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The handle that ends the subscription.</returns>
    LightChangeSubscription SubscribeChanges(ModuleContext module, Action<LightChunkChange> handler);
}

/// <summary>A chunk whose light changed in a propagation pass.</summary>
/// <param name="Grid">The grid.</param>
/// <param name="Chunk">The chunk.</param>
/// <param name="TouchesBorder">Whether a block on the border of the chunk changed, which the chunks around it may read.</param>
/// <param name="Version">The version of the light of the chunk after the pass, as <see cref="LightSnapshot.Version"/> gives it.</param>
public readonly record struct LightChunkChange(Grid Grid, ChunkCoordinate Chunk, bool TouchesBorder, uint Version);

/// <summary>The size of the light of a context and the cost of its last propagation pass.</summary>
/// <param name="Chunks">The chunks that have light.</param>
/// <param name="ArrayChunks">The chunks whose light varies, which hold an array.</param>
/// <param name="StorageBytes">The bytes of those arrays, in native memory.</param>
/// <param name="LastPassChunks">The chunks that the last pass worked on.</param>
/// <param name="LastPassRounds">The rounds of jobs of the last pass.</param>
/// <param name="LastPassDuration">The time of the last pass on the loop thread, jobs included.</param>
public readonly record struct LightingStatistics(int Chunks, int ArrayChunks, long StorageBytes, int LastPassChunks, int LastPassRounds, TimeSpan LastPassDuration);

/// <summary>A subscription to the chunks whose light changed.</summary>
public sealed class LightChangeSubscription : IDisposable
{
    internal LightChangeSubscription(Action<LightChunkChange> handler) => Handler = handler;

    /// <summary>Gets whether the subscription ended.</summary>
    public bool IsRevoked { get; private set; }

    internal Action<LightChunkChange> Handler { get; }

    /// <summary>Ends the subscription; the handler is not called again. Disposing twice does nothing.</summary>
    public void Dispose() => IsRevoked = true;
}

/// <summary>
/// A frozen copy of the light of a chunk and of the blocks around it, one
/// block deep: 34 × 34 × 34 blocks. A job may read it while the light changes.
/// </summary>
/// <remarks>
/// Create it once and fill it again with <see cref="ILighting.CopyFrozen"/>:
/// it holds 76 KiB. A block of a chunk that is not loaded reads as full sky
/// without block light; <see cref="HasNeighbor"/> tells which chunks were loaded.
/// </remarks>
public sealed class LightSnapshot
{
    /// <summary>The number of blocks along each axis: the chunk and one block on each side.</summary>
    public const int Size = 34;

    private readonly ushort[] _values = new ushort[Size * Size * Size];
    private int _neighbors;

    /// <summary>Gets the copied chunk.</summary>
    public ChunkCoordinate Chunk { get; private set; }

    /// <summary>Gets the version of the light of the chunk when it was copied.</summary>
    public uint Version { get; private set; }

    /// <summary>Gets the light of a block.</summary>
    /// <param name="x">The local coordinate along X, from -1 to 32.</param>
    /// <param name="y">The local coordinate along Y, from -1 to 32.</param>
    /// <param name="z">The local coordinate along Z, from -1 to 32.</param>
    /// <returns>The light; without color, the three components of the block channel are equal.</returns>
    /// <exception cref="IndexOutOfRangeException">A coordinate is outside -1 to 32.</exception>
    [HotPath]
    public LightLevel Get(int x, int y, int z) => new(_values[(x + 1) + (Size * ((z + 1) + (Size * (y + 1))))]);

    /// <summary>Tells whether a chunk around the copied one was loaded, so that its blocks in the copy hold its light.</summary>
    /// <param name="dx">The offset in chunks along X, from -1 to 1.</param>
    /// <param name="dy">The offset in chunks along Y, from -1 to 1.</param>
    /// <param name="dz">The offset in chunks along Z, from -1 to 1.</param>
    /// <returns><see langword="true"/> when that chunk was loaded; always for (0, 0, 0).</returns>
    [HotPath]
    public bool HasNeighbor(int dx, int dy, int dz) => (_neighbors & (1 << Bit(dx, dy, dz))) != 0;

    internal void Fill(LightChunk center, Func<int, int, int, LightChunk?> neighbor, bool colored)
    {
        Chunk = center.Coordinate;
        Version = center.Version;
        _neighbors = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    LightChunk? source = dx == 0 && dy == 0 && dz == 0 ? center : neighbor(dx, dy, dz);
                    if (source is not null) _neighbors |= 1 << Bit(dx, dy, dz);
                    CopyRegion(source, dx, dy, dz, colored);
                }
            }
        }
    }

    private void CopyRegion(LightChunk? source, int dx, int dy, int dz, bool colored)
    {
        (int fromX, int toX) = Range(dx);
        (int fromY, int toY) = Range(dy);
        (int fromZ, int toZ) = Range(dz);
        for (int y = fromY; y <= toY; y++)
        {
            for (int z = fromZ; z <= toZ; z++)
            {
                for (int x = fromX; x <= toX; x++)
                {
                    ushort value = source is null ? LightChunk.DefaultValue : source.Get(x + (32 * (z + (32 * y))));
                    if (!colored)
                    {
                        int level = value & 15;
                        value = (ushort)((value & 0xF000) | (level << 8) | (level << 4) | level);
                    }

                    int sx = x + (dx * 32) + 1;
                    int sy = y + (dy * 32) + 1;
                    int sz = z + (dz * 32) + 1;
                    _values[sx + (Size * (sz + (Size * sy)))] = value;
                }
            }
        }
    }

    // The local cells of a chunk at an offset that the copy holds: its last layer, all of it, or its first layer.
    private static (int From, int To) Range(int offset) => offset < 0 ? (31, 31) : offset > 0 ? (0, 0) : (0, 31);

    [HotPath]
    private static int Bit(int dx, int dy, int dz) => (dx + 1) + (3 * ((dz + 1) + (3 * (dy + 1))));
}

/// <summary>The light of one context: storage by grid and chunk, the tick system, and the events of nVoxel.</summary>
internal sealed class Lighting : ILighting, ITickSystem, IDisposable
{
    public const string SystemName = "nobara.lighting.propagation";

    private readonly ModuleContext _context;
    private readonly LightSourceRegistry _sources;
    private readonly JobData _access;
    private readonly Dictionary<Grid, Dictionary<ChunkCoordinate, LightChunk>> _grids = [];
    private readonly List<LightChangeSubscription> _subscriptions = [];
    private readonly List<LightChunkChange> _changes = [];
    private readonly Action _passCallback;
    private Propagator _propagator = null!;
    private LoopAccess _eventAccess = null!;
    private IVoxelWorlds _worlds = null!;
    private VoxelNotificationHandle? _notifications;
    private ChunkResidencySubscription? _residency;
    private Func<int, int, int, LightChunk?>? _snapshotNeighbor;
    private LightChunk? _snapshotCenter;
    private Dictionary<ChunkCoordinate, LightChunk>? _snapshotChunks;
    private bool _live;
    private int _chunkCount;
    private int _lastPassChunks;
    private int _lastPassRounds;
    private TimeSpan _lastPassDuration;

    public Lighting(ModuleContext context, LightSourceRegistry sources)
    {
        _context = context;
        _sources = sources;
        _access = context.Jobs.CreateData("light storage");
        _passCallback = Pass;
    }

    public bool IsColored => _sources.IsColored;

    public LightingStatistics Statistics
    {
        get
        {
            int arrays = 0;
            long bytes = 0;
            foreach (Dictionary<ChunkCoordinate, LightChunk> chunks in _grids.Values)
            {
                foreach (LightChunk chunk in chunks.Values)
                {
                    if (chunk.IsUniform) continue;
                    arrays++;
                    bytes += chunk.StorageBytes;
                }
            }

            return new LightingStatistics(_chunkCount, arrays, bytes, _lastPassChunks, _lastPassRounds, _lastPassDuration);
        }
    }

    public void Initialize(IVoxelWorlds worlds, IVoxelBatches batches, IChunkLoading loading, IWorldSessions sessions, ITickSchedule schedule)
    {
        _worlds = worlds;
        _eventAccess = _context.Jobs.DeclareLoopAccess("voxel events of nLighting", [worlds.ChunkAccess], [_access]);
        schedule.AddSystem(_context, SystemName, TickStages.Late, this, new TickSystemOptions
        {
            Reads = [worlds.ChunkAccess],
            Writes = [_access],
        });
        _notifications = batches.Subscribe(_context, OnBatch);
        _residency = loading.SubscribeResidency(_context, OnResidency);
        sessions.SubscribeStarting(_context, _ => _live = true);
        sessions.SubscribeEnded(_context, _ => Clear(KeepChunksAtSessionEnd));
        sessions.RegisterWorldStateCounter(_context, "light chunks", () => _chunkCount);
    }

    public void Start(IShapeRegistry shapes)
    {
        _propagator = new Propagator(_context, _sources, shapes, _worlds.ChunkAccess, _access);
        _snapshotNeighbor = SnapshotNeighbor;
    }

    public void Run(in TickTime time) => _passCallback();

    public bool CopyFrozen(Grid grid, ChunkCoordinate chunk, LightSnapshot destination)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(destination);
        if (!_grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, LightChunk>? chunks) || !chunks.TryGetValue(chunk, out LightChunk? center)) return false;
        _access.CheckRead();
        _snapshotCenter = center;
        _snapshotChunks = chunks;
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

    internal bool TryGetChunk(Grid grid, ChunkCoordinate coordinate, out LightChunk? chunk)
    {
        chunk = null;
        return _grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, LightChunk>? chunks) && chunks.TryGetValue(coordinate, out chunk);
    }

    /// <summary>Gets or sets whether the end of a session keeps the light, to test the world state counter.</summary>
    internal bool KeepChunksAtSessionEnd { get; set; }

    /// <summary>Starts the propagation from a block as if it changed, for tests.</summary>
    internal void Reseed(Grid grid, BlockCoordinate block)
    {
        if (!TryGetChunk(grid, VoxelCoordinates.ToChunk(block), out LightChunk? chunk)) return;
        int index = VoxelCoordinates.ToIndex(block);
        Seed(chunk!, index, _sources.States[chunk!.Blocks.GetState(index)].Emission);
    }

    /// <summary>Runs a propagation pass outside the tick, for tests.</summary>
    internal void RunPass() => Pass();

    public void Dispose()
    {
        Clear(keep: false);
        _notifications?.Dispose();
        _residency?.Dispose();
    }

    private void Pass()
    {
        if (_propagator.Active.Count == 0) return;
        long start = Stopwatch.GetTimestamp();
        int worked = _propagator.Active.Count;
        _propagator.Run();
        foreach (LightChunk chunk in _propagator.Active)
        {
            if (!chunk.Changed) continue;
            chunk.Version++;
            chunk.Compact();
            _changes.Add(new LightChunkChange(chunk.Grid, chunk.Coordinate, chunk.BorderChanged, chunk.Version));
            chunk.Changed = false;
            chunk.BorderChanged = false;
        }

        _propagator.Finish();
        _lastPassChunks = Math.Max(worked, _propagator.Active.Count);
        _lastPassRounds = _propagator.LastRounds;
        _lastPassDuration = Stopwatch.GetElapsedTime(start);
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

    private void OnBatch(VoxelBatchNotification notification)
    {
        if (!_live || !TryGetChunk(notification.Grid, notification.Chunk, out LightChunk? chunk)) return;
        using LoopAccessScope scope = _eventAccess.Enter();
        _access.CheckWrite();
        StateLight[] states = _sources.States;
        foreach (VoxelBlockChange change in notification.Changes)
        {
            if (change.OldState == change.NewState && !change.DataChanged) continue;
            if (!_sources.Differs(change.OldState, change.NewState)) continue;
            Seed(chunk!, change.LocalIndex, states[change.NewState].Emission);
        }
    }

    // A block whose light changes: its light goes, through every face, then its emission and its neighbors spread again.
    private void Seed(LightChunk chunk, int index, ushort emission)
    {
        WorkState work = _propagator.Work(chunk);
        ushort light = chunk.GetBlock(index);
        if (light != 0)
        {
            chunk.SetBlock(index, 0);
            work.Seeds.Add((uint)index | ((uint)light << 16));
        }

        if (emission != 0)
        {
            chunk.SetBlock(index, emission);
            work.MarkPending(index);
        }

        for (int face = 0; face < 6; face++)
        {
            if (Faces.Step(index, face, out int neighbor)) work.MarkPending(neighbor);
            else if (chunk.Neighbors[face] is { } next) _propagator.Work(next).MarkPending(Faces.Cell(Faces.Opposite(face), Faces.Slot(face, index)));
        }
    }

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
        if (!_grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, LightChunk>? chunks))
        {
            chunks = [];
            _grids.Add(grid, chunks);
        }

        if (chunks.ContainsKey(coordinate)) return;
        var chunk = new LightChunk(grid, coordinate, blocks, _sources.IsColored);
        chunks.Add(coordinate, chunk);
        _chunkCount++;
        for (int face = 0; face < 6; face++)
        {
            if (!chunks.TryGetValue(Offset(coordinate, face), out LightChunk? neighbor)) continue;
            chunk.Neighbors[face] = neighbor;
            neighbor.Neighbors[Faces.Opposite(face)] = chunk;
        }

        // Its sources shine, read only when the palette of the chunk holds one.
        StateLight[] states = _sources.States;
        Palette palette = blocks.Palette;
        bool emits = false;
        for (int entry = 0; entry < palette.Count && !emits; entry++) emits = states[palette.GetEntry(entry)].Emission != 0;
        if (emits)
        {
            WorkState work = _propagator.Work(chunk);
            for (int index = 0; index < LightChunk.Volume; index++)
            {
                ushort emission = states[blocks.GetState(index)].Emission;
                if (emission == 0) continue;
                chunk.SetBlock(index, emission);
                work.MarkPending(index);
            }
        }

        // The fronts that waited on the faces of its neighbors enter it.
        for (int face = 0; face < 6; face++)
        {
            if (chunk.Neighbors[face] is not { } neighbor || (neighbor.IsUniform && (neighbor.UniformValue & 0x0FFF) == 0)) continue;
            int side = Faces.Opposite(face);
            WorkState? work = null;
            for (int slot = 0; slot < Faces.Cells; slot++)
            {
                int cell = Faces.Cell(side, slot);
                if (neighbor.GetBlock(cell) != 0) (work ??= _propagator.Work(neighbor)).MarkPending(cell);
            }
        }
    }

    private void Unload(Grid grid, ChunkCoordinate coordinate)
    {
        if (!_grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, LightChunk>? chunks) || !chunks.Remove(coordinate, out LightChunk? chunk)) return;
        bool dark = chunk.IsUniform && (chunk.UniformValue & 0x0FFF) == 0;
        for (int face = 0; face < 6; face++)
        {
            if (chunk.Neighbors[face] is not { } neighbor) continue;
            int side = Faces.Opposite(face);
            neighbor.Neighbors[side] = null;
            if (dark) continue;

            // The light that came from this chunk leaves its neighbors: it may come back different.
            WorkState? work = null;
            for (int slot = 0; slot < Faces.Cells; slot++)
            {
                int from = Faces.Cell(face, slot);
                ushort light = chunk.GetBlock(from);
                if (light == 0) continue;
                int closed = _propagator.ClosedFaces(chunk, from);
                if (closed != 0x3F && (closed & (1 << face)) != 0) continue;
                ushort beside = neighbor.GetBlock(Faces.Cell(side, slot));
                if (!IsDimmer(beside, light)) continue;
                work ??= _propagator.Work(neighbor);
                ref ushort entering = ref work.IncomingRemoval[(side * Faces.Cells) + slot];
                entering = Faces.Max(entering, light);
                work.HasIncomingRemoval = true;
            }
        }

        _propagator.Forget(chunk);
        chunk.Dispose();
        _chunkCount--;
        if (chunks.Count == 0) _grids.Remove(grid);
    }

    // Whether a component of a block is lit and dimmer than the light beside it, so that it may have come from there.
    private static bool IsDimmer(ushort value, ushort than)
    {
        for (int shift = 0; shift < 12; shift += 4)
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
        foreach (Dictionary<ChunkCoordinate, LightChunk> chunks in _grids.Values)
        {
            foreach (LightChunk chunk in chunks.Values) chunk.Dispose();
        }

        _grids.Clear();
        _chunkCount = 0;
    }

    private LightChunk? SnapshotNeighbor(int dx, int dy, int dz)
    {
        ChunkCoordinate center = _snapshotCenter!.Coordinate;
        return _snapshotChunks!.TryGetValue(new ChunkCoordinate(center.X + dx, center.Y + dy, center.Z + dz), out LightChunk? chunk) ? chunk : null;
    }

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
