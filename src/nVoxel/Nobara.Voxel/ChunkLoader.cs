// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Voxel;

/// <summary>Told when a chunk becomes resident outside a provider result.</summary>
internal interface IChunkResidencyObserver
{
    void OnResident(Grid grid, Chunk chunk);
}

/// <summary>Told on the loop thread when a batch changed cells of a chunk, before the notifications.</summary>
internal interface IVoxelChangeObserver
{
    void OnApplied(Grid grid, Chunk chunk, List<VoxelBlockChange> changes);
}

/// <summary>What nVoxel knows of one chunk: its tickets, its loading, its block ticks.</summary>
internal sealed class ChunkEntry
{
    public ChunkEntry(Grid grid, ChunkCoordinate coordinate) => (Grid, Coordinate) = (grid, coordinate);

    public Grid Grid { get; }

    public ChunkCoordinate Coordinate { get; }

    public int Loaded;
    public int Reduced;
    public int Active;
    public int Linked;
    public ChunkLoadState State;
    public long ZeroSince = -1;
    public int Priority = int.MaxValue;
    public int ActiveIndex = -1;
    public bool IsCandidate;
    public bool IsRequested;
    public bool HoldsLinks;
    public ProductionSlot? Slot;
    public Chunk? Chunk;
    public List<ChunkCoordinate>? Links;
    public List<ScheduledBlockTick>? Scheduled;

    public int Tickets => Loaded + Reduced + Active;

    public int Total => Loaded + Reduced + Active + Linked;

    public TicketLevel? Level => Active > 0 ? TicketLevel.Active : Reduced > 0 ? TicketLevel.ReducedActive : Loaded + Linked > 0 ? TicketLevel.Loaded : null;
}

/// <summary>A buffer and a job that produce one chunk.</summary>
internal sealed class ProductionSlot
{
    public uint[] States { get; } = new uint[Chunk.Volume];

    public ChunkEntry? Entry;
    public ChunkProviderHandle? Provider;
    public ChunkRequest Request;
    public JobHandle Handle;
    public Exception? Error;
}

/// <summary>The tickets, providers, and residency of the chunks of one context.</summary>
internal sealed class ChunkLoader : IChunkLoading, IChunkResidencyObserver, IVoxelChangeObserver, ITickSystem
{
    private readonly ModuleContext _module;
    private readonly VoxelWorlds _worlds;
    private readonly Dictionary<Grid, Dictionary<ChunkCoordinate, ChunkEntry>> _grids = [];
    private readonly List<ChunkEntry> _active = [];
    private readonly Dictionary<DimensionId, int> _activeByDimension = [];
    private readonly HashSet<DimensionId> _frozenByVoxel = [];
    private readonly List<ChunkEntry> _requests = [];
    private readonly List<ProductionSlot> _inFlight = [];
    private readonly Stack<ProductionSlot> _slots = new();
    private readonly List<ChunkEntry> _candidates = [];
    private readonly List<ChunkProviderHandle> _providers = [];
    private readonly List<ChunkResidencySubscription> _subscriptions = [];
    private readonly List<LoadingRegistration> _tickets = [];
    private readonly Dictionary<string, long> _ticketsByOwner = new(StringComparer.Ordinal);
    private readonly HashSet<string> _countedOwners = new(StringComparer.Ordinal);
    private readonly RequestOrder _requestOrder = new();
    private VoxelBatches _batches = null!;
    private HeightMaps _heightMaps = null!;
    private IMultiBlockRegistry _multiBlocks = null!;
    private IBlockRegistry _blocks = null!;
    private IGameTime _time = null!;
    private IWorldSessions _sessions = null!;
    private LoadingTask? _loadingTask;
    private int _unloadDelay;
    private bool _freezeInactive;
    private int _providerSequence;
    private long _tick;
    private int _pending;
    private readonly Action _loadCallback;

    public ChunkLoader(ModuleContext module, VoxelWorlds worlds)
    {
        _module = module;
        _worlds = worlds;
        _loadCallback = Load;
    }

    public ChunkArea ViewArea => new(_module.Settings.GetInt(VoxelSettings.ViewRadius), _module.Settings.GetInt(VoxelSettings.ViewHeight));

    public ChunkArea SimulationArea => new(_module.Settings.GetInt(VoxelSettings.SimulationRadius), _module.Settings.GetInt(VoxelSettings.SimulationHeight));

    public int LoadedChunks
    {
        get
        {
            int count = 0;
            foreach (Grid grid in _worlds.Grids) count += grid.Storage.ChunkCount;
            return count;
        }
    }

    public int PendingChunks => _requests.Count + _inFlight.Count;

    /// <summary>Gets the resident chunks whose tickets make them tick, in a stable order.</summary>
    internal List<ChunkEntry> ActiveChunks
    {
        [HotPath]
        get => _active;
    }

    internal int RandomTicksPerChunk { get; private set; }

    internal int ReducedTickInterval { get; private set; } = 4;

    public void Initialize(VoxelBatches batches, HeightMaps heightMaps, IMultiBlockRegistry multiBlocks, IBlockRegistry blocks, IGameTime time, IWorldSessions sessions, ITickSchedule schedule)
    {
        _batches = batches;
        _heightMaps = heightMaps;
        _multiBlocks = multiBlocks;
        _blocks = blocks;
        _time = time;
        _sessions = sessions;
        schedule.AddSystem(_module, "nobara.voxel.loading", TickStages.World, this, new TickSystemOptions
        {
            Before = ["nobara.voxel.batches"],
            Writes = [_worlds.ChunkAccess],
        });
        sessions.SubscribeStarting(_module, OnSessionStarting);
        sessions.SubscribeEnded(_module, OnSessionEnded);
    }

    public ChunkTicket AddTicket(ModuleContext owner, Grid grid, ChunkCoordinate chunk, TicketLevel level)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Dictionary<ChunkCoordinate, ChunkEntry> entries = Require(grid);
        RequireLevel(level);
        var ticket = new ChunkTicket(this, owner.Module.Id, grid, chunk, level);
        Track(owner, ticket);
        Add(grid, entries, chunk, level, 1, 0);
        return ticket;
    }

    public AreaTicket AddAreaTicket(ModuleContext owner, Grid grid, in WorldPosition center, ChunkArea area, TicketLevel level)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Dictionary<ChunkCoordinate, ChunkEntry> entries = Require(grid);
        RequireLevel(level);
        if (area.Radius < 0 || area.Height < 0) throw new ArgumentOutOfRangeException(nameof(area), "The radius and the height of an area are 0 or more.");
        var ticket = new AreaTicket(this, owner.Module.Id, grid, area, level) { Center = new GridFrame(grid.Transform).ChunkOf(center) };
        Track(owner, ticket);
        ApplyArea(grid, entries, ticket.Center, area, level, 1);
        return ticket;
    }

    public ViewerTicket AddViewer(ModuleContext owner, Grid grid, in WorldPosition center)
    {
        AreaTicket view = AddAreaTicket(owner, grid, center, ViewArea, TicketLevel.Loaded);
        AreaTicket simulation = AddAreaTicket(owner, grid, center, SimulationArea, TicketLevel.Active);
        return new ViewerTicket(view, simulation);
    }

    public ChunkProviderHandle AddProvider(ModuleContext owner, IChunkProvider provider, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(provider);
        var handle = new ChunkProviderHandle(this, owner, provider, priority, _providerSequence++);
        _providers.Add(handle);
        _providers.Sort(static (a, b) =>
        {
            int order = b.Priority.CompareTo(a.Priority);
            if (order == 0) order = a.Owner.LoadOrder.CompareTo(b.Owner.LoadOrder);
            return order != 0 ? order : a.Sequence.CompareTo(b.Sequence);
        });
        return handle;
    }

    public ChunkResidencySubscription SubscribeResidency(ModuleContext owner, Action<ChunkResidencyChange> handler)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new ChunkResidencySubscription(this, owner, handler);
        _subscriptions.Add(subscription);
        return subscription;
    }

    public ChunkStatus GetStatus(Grid grid, ChunkCoordinate chunk)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ChunkEntry? entry = _grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries) && entries.TryGetValue(chunk, out ChunkEntry? found) ? found : null;
        if (grid.Residency == GridResidency.Kept) return new ChunkStatus(grid.IsDestroyed ? ChunkLoadState.NotLoaded : ChunkLoadState.Loaded, entry?.Level);
        return entry is null ? new ChunkStatus(ChunkLoadState.NotLoaded, null) : new ChunkStatus(entry.State, entry.Level);
    }

    /// <summary>Tells whether a chunk can be read: resident, or absent from a grid whose absent chunks are air.</summary>
    [HotPath]
    internal bool IsReadable(Grid grid, ChunkCoordinate chunk, out Chunk? resident)
    {
        if (grid.Storage.TryGetChunk(chunk, out resident)) return true;
        return grid.Residency == GridResidency.Kept;
    }

    internal bool TryGetEntry(Grid grid, ChunkCoordinate chunk, out ChunkEntry? entry)
    {
        entry = null;
        return _grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries) && entries.TryGetValue(chunk, out entry);
    }

    internal void OnGridCreated(Grid grid)
    {
        _grids.Add(grid, []);
        grid.Storage.Observer = this;
    }

    internal void OnGridDestroying(Grid grid)
    {
        if (!_grids.Remove(grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries)) return;
        grid.Storage.Observer = null;
        foreach (ChunkEntry entry in entries.Values)
        {
            if (entry.State == ChunkLoadState.Loaded && entry.Chunk is not null) Raise(new ChunkResidencyChange(ChunkResidencyKind.Unloading, grid, entry.Coordinate));
            SetActive(entry, false);
            entry.Scheduled = null;
            entry.Chunk = null;
            entry.State = ChunkLoadState.NotLoaded;
        }

        foreach (LoadingRegistration ticket in _tickets)
        {
            if (ticket is ChunkTicket chunkTicket && ReferenceEquals(chunkTicket.Grid, grid)) ticket.IsDetached = true;
            if (ticket is AreaTicket areaTicket && ReferenceEquals(areaTicket.Grid, grid)) ticket.IsDetached = true;
        }

        _heightMaps.OnGridDestroying(grid);
    }

    public void OnResident(Grid grid, Chunk chunk)
    {
        if (!_grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries)) return;
        ChunkEntry entry = GetOrCreate(grid, entries, chunk.Coordinate);
        entry.State = ChunkLoadState.Loaded;
        entry.Chunk = chunk;
        _heightMaps.OnLoaded(grid, chunk);
        Refresh(entry);
        Raise(new ChunkResidencyChange(ChunkResidencyKind.Loaded, grid, chunk.Coordinate));
    }

    /// <summary>Applies the result of a provider, called by the batches at its turn in the order of batches.</summary>
    internal void ApplyFill(ProductionSlot slot, List<VoxelBlockChange>? changes, Action<Grid, Chunk, List<VoxelBlockChange>?> notify)
    {
        ChunkEntry entry = slot.Entry!;
        Grid grid = entry.Grid;
        _pending--;
        entry.Slot = null;
        try
        {
            if (grid.IsDestroyed || !_grids.ContainsKey(grid) || grid.Storage.TryGetChunk(entry.Coordinate, out _))
            {
                if (entry.State == ChunkLoadState.Loading) entry.State = entry.Chunk is null ? ChunkLoadState.NotLoaded : ChunkLoadState.Loaded;
                Forget(entry);
                return;
            }

            Chunk chunk = grid.Storage.AddChunkSilently(entry.Coordinate);
            chunk.Fill(slot.States, changes);
            entry.Chunk = chunk;
            entry.State = ChunkLoadState.Loaded;
            _heightMaps.OnLoaded(grid, chunk);
            notify(grid, chunk, changes);
            Refresh(entry);
            if (entry.Total == 0) MarkCandidate(entry);
            Raise(new ChunkResidencyChange(ChunkResidencyKind.Loaded, grid, entry.Coordinate));
        }
        finally
        {
            Recycle(slot);
        }
    }

    public void OnApplied(Grid grid, Chunk chunk, List<VoxelBlockChange> changes)
    {
        if (!TryGetEntry(grid, chunk.Coordinate, out ChunkEntry? entry) || !entry!.HoldsLinks) return;
        BlockCoordinate corner = VoxelCoordinates.ToBlock(chunk.Coordinate, new LocalBlockCoordinate(0, 0, 0));
        foreach (VoxelBlockChange change in changes)
        {
            BlockCoordinate cell = Cell(corner, change.LocalIndex);
            if (IsOriginOfMultiBlock(change.OldState)) Link(entry, cell, change.OldState, -1);
            if (IsOriginOfMultiBlock(change.NewState)) Link(entry, cell, change.NewState, 1);
        }
    }

    [HotPath]
    public void Run(in TickTime time)
    {
        _tick = (long)time.Tick;
        if (_sessions.State is not (SessionState.Starting or SessionState.Active)) return;

        // Loading allocates (chunks, entries, notifications); a world that loads nothing does not reach it.
        if (_inFlight.Count > 0 || _candidates.Count > 0 || _requests.Count > 0 || _loadingTask is not null) _loadCallback();
        ApplyFreezePolicy();
    }

    private void Load()
    {
        CollectProductions();
        Unload();
        Dispatch();
        TrackLoadingTask();
    }

    internal void Release(ChunkTicket ticket)
    {
        Untrack(ticket);
        if (ticket.IsDetached || !_grids.TryGetValue(ticket.Grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries)) return;
        Add(ticket.Grid, entries, ticket.Chunk, ticket.Level, -1, 0);
    }

    internal void Release(AreaTicket ticket)
    {
        Untrack(ticket);
        if (ticket.IsDetached || !_grids.TryGetValue(ticket.Grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries)) return;
        ApplyArea(ticket.Grid, entries, ticket.Center, ticket.Area, ticket.Level, -1);
    }

    internal void Release(ChunkProviderHandle handle) => _providers.Remove(handle);

    internal void Release(ChunkResidencySubscription subscription) => _subscriptions.Remove(subscription);

    internal void Move(AreaTicket ticket, in WorldPosition position)
    {
        if (ticket.IsDetached || !_grids.TryGetValue(ticket.Grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries)) return;
        ChunkCoordinate center = new GridFrame(ticket.Grid.Transform).ChunkOf(position);
        if (center == ticket.Center) return;

        // The priorities follow the new center: those of the old area are dropped, and the new area gives its own; a chunk held by another ticket only is then served after the others.
        ApplyArea(ticket.Grid, entries, ticket.Center, ticket.Area, ticket.Level, 0);
        ApplyArea(ticket.Grid, entries, center, ticket.Area, ticket.Level, 1);
        ApplyArea(ticket.Grid, entries, ticket.Center, ticket.Area, ticket.Level, -1);
        ticket.Center = center;
    }

    private void OnSessionStarting(WorldSession session)
    {
        foreach (LoadingRegistration ticket in _tickets)
        {
            _module.Logger.Warning($"Module '{ticket.OwnerId}' did not dispose a chunk ticket by the end of the previous world session; it is dropped now.");
        }

        _tickets.Clear();
        foreach (string owner in _ticketsByOwner.Keys.ToArray()) _ticketsByOwner[owner] = 0;
        _unloadDelay = _module.Settings.GetInt(VoxelSettings.UnloadDelay);
        _freezeInactive = _module.Settings.GetBool(VoxelSettings.FreezeInactiveDimensions);
        RandomTicksPerChunk = _module.Settings.GetInt(VoxelSettings.RandomTicksPerChunk);
        ReducedTickInterval = _module.Settings.GetInt(VoxelSettings.ReducedTickInterval);
        _loadingTask = session.Loading.Add("chunks of the simulation area", 1);
    }

    private void OnSessionEnded(WorldSession session)
    {
        foreach (ProductionSlot slot in _inFlight)
        {
            slot.Handle.Complete();
            if (slot.Entry is not null) slot.Entry.Slot = null;
            Recycle(slot);
        }

        _inFlight.Clear();
        foreach (ChunkEntry entry in _requests) entry.IsRequested = false;
        foreach (ChunkEntry entry in _candidates) entry.IsCandidate = false;
        foreach (ChunkEntry entry in _active) entry.ActiveIndex = -1;
        _requests.Clear();
        _candidates.Clear();
        _active.Clear();
        _activeByDimension.Clear();
        _pending = 0;
        foreach (LoadingRegistration ticket in _tickets) ticket.IsDetached = true;
        foreach (DimensionId dimension in _frozenByVoxel)
        {
            if (_time.HasDimension(dimension)) _time.SetFrozen(dimension, false);
        }

        _frozenByVoxel.Clear();
        _loadingTask = null;
    }

    private Dictionary<ChunkCoordinate, ChunkEntry> Require(Grid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (_sessions.State is not (SessionState.Starting or SessionState.Active)) throw new InvalidOperationException("A chunk ticket belongs to a world session.");
        if (grid.IsDestroyed || !_grids.TryGetValue(grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries)) throw new ArgumentException("The grid is destroyed or belongs to another context.", nameof(grid));
        return entries;
    }

    private static void RequireLevel(TicketLevel level)
    {
        if (level is not (TicketLevel.Loaded or TicketLevel.ReducedActive or TicketLevel.Active)) throw new ArgumentOutOfRangeException(nameof(level));
    }

    private void Track(ModuleContext owner, LoadingRegistration ticket)
    {
        _tickets.Add(ticket);
        _ticketsByOwner[owner.Module.Id] = _ticketsByOwner.GetValueOrDefault(owner.Module.Id) + 1;
        if (_countedOwners.Add(owner.Module.Id))
        {
            string id = owner.Module.Id;
            _sessions.RegisterWorldStateCounter(owner, "chunk tickets", () => _ticketsByOwner.GetValueOrDefault(id));
        }
    }

    private void Untrack(LoadingRegistration ticket)
    {
        if (!_tickets.Remove(ticket)) return;
        _ticketsByOwner[ticket.OwnerId] = _ticketsByOwner.GetValueOrDefault(ticket.OwnerId) - 1;
    }

    private void ApplyArea(Grid grid, Dictionary<ChunkCoordinate, ChunkEntry> entries, ChunkCoordinate center, ChunkArea area, TicketLevel level, int delta)
    {
        for (int y = -area.Height; y <= area.Height; y++)
        {
            for (int z = -area.Radius; z <= area.Radius; z++)
            {
                for (int x = -area.Radius; x <= area.Radius; x++)
                {
                    if (!area.Contains(x, y, z)) continue;
                    long cx = (long)center.X + x, cy = (long)center.Y + y, cz = (long)center.Z + z;
                    if (cx is < int.MinValue or > int.MaxValue || cy is < int.MinValue or > int.MaxValue || cz is < int.MinValue or > int.MaxValue) continue;
                    Add(grid, entries, new ChunkCoordinate((int)cx, (int)cy, (int)cz), level, delta, (x * x) + (y * y) + (z * z));
                }
            }
        }
    }

    private void Add(Grid grid, Dictionary<ChunkCoordinate, ChunkEntry> entries, ChunkCoordinate chunk, TicketLevel level, int delta, int priority)
    {
        if (!grid.Storage.Overlaps(chunk)) return;
        ChunkEntry? entry;
        if (delta > 0) entry = GetOrCreate(grid, entries, chunk);
        else if (!entries.TryGetValue(chunk, out entry)) return;
        if (delta == 0)
        {
            entry!.Priority = int.MaxValue;
            return;
        }

        switch (level)
        {
            case TicketLevel.Active:
                entry!.Active += delta;
                break;
            case TicketLevel.ReducedActive:
                entry!.Reduced += delta;
                break;
            default:
                entry!.Loaded += delta;
                break;
        }

        if (delta > 0 && priority < entry.Priority) entry.Priority = priority;
        Refresh(entry);
    }

    private ChunkEntry GetOrCreate(Grid grid, Dictionary<ChunkCoordinate, ChunkEntry> entries, ChunkCoordinate chunk)
    {
        if (!entries.TryGetValue(chunk, out ChunkEntry? entry))
        {
            entry = new ChunkEntry(grid, chunk);
            if (grid.Storage.TryGetChunk(chunk, out Chunk? resident))
            {
                entry.State = ChunkLoadState.Loaded;
                entry.Chunk = resident;
            }

            entries.Add(chunk, entry);
        }

        return entry;
    }

    // Brings the loading, the activity, and the linked tickets of a chunk in line with its tickets.
    private void Refresh(ChunkEntry entry)
    {
        bool usesTickets = entry.Grid.Residency == GridResidency.Tickets;
        if (entry.Total > 0)
        {
            entry.ZeroSince = -1;
            if (usesTickets && entry.State == ChunkLoadState.NotLoaded)
            {
                entry.State = ChunkLoadState.Loading;
                if (!entry.IsRequested)
                {
                    entry.IsRequested = true;
                    _requests.Add(entry);
                }
            }
        }
        else
        {
            entry.Priority = int.MaxValue;
            if (entry.State == ChunkLoadState.Loaded && usesTickets) MarkCandidate(entry);
            if (entry.State == ChunkLoadState.Loading && entry.Slot is null) entry.State = ChunkLoadState.NotLoaded;

            if (entry.State == ChunkLoadState.Failed) entry.State = ChunkLoadState.NotLoaded;
        }

        bool resident = entry.State == ChunkLoadState.Loaded;
        SetActive(entry, resident && (entry.Active > 0 || entry.Reduced > 0));
        bool hold = resident && usesTickets && entry.Tickets > 0;
        if (hold != entry.HoldsLinks) SetLinks(entry, hold);
        if (!resident && entry.Total == 0 && entry.Slot is null) Forget(entry);
    }

    private void MarkCandidate(ChunkEntry entry)
    {
        if (entry.ZeroSince < 0) entry.ZeroSince = _tick;
        if (!entry.IsCandidate)
        {
            entry.IsCandidate = true;
            _candidates.Add(entry);
        }
    }

    private void Forget(ChunkEntry entry)
    {
        if (entry.State is ChunkLoadState.Loaded or ChunkLoadState.Loading || entry.Total > 0 || entry.Slot is not null || entry.IsRequested) return;
        if (_grids.TryGetValue(entry.Grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries) && entries.TryGetValue(entry.Coordinate, out ChunkEntry? current) && ReferenceEquals(current, entry))
        {
            entries.Remove(entry.Coordinate);
        }
    }

    private void SetActive(ChunkEntry entry, bool active)
    {
        if (active == entry.ActiveIndex >= 0) return;
        DimensionId dimension = entry.Grid.Dimension;
        if (active)
        {
            entry.ActiveIndex = _active.Count;
            _active.Add(entry);
            _activeByDimension[dimension] = _activeByDimension.GetValueOrDefault(dimension) + 1;
            return;
        }

        int index = entry.ActiveIndex;
        int last = _active.Count - 1;
        if (index > last || !ReferenceEquals(_active[index], entry))
        {
            entry.ActiveIndex = -1;
            return;
        }

        if (index != last)
        {
            ChunkEntry moved = _active[last];
            _active[index] = moved;
            moved.ActiveIndex = index;
        }

        _active.RemoveAt(last);
        entry.ActiveIndex = -1;
        _activeByDimension[dimension] = _activeByDimension.GetValueOrDefault(dimension) - 1;
    }

    // Linked tickets: a multi-block whose origin is in a chunk held by a ticket holds the other chunks of its footprint, loaded only.
    private void SetLinks(ChunkEntry entry, bool hold)
    {
        entry.HoldsLinks = hold;
        if (!hold)
        {
            if (entry.Links is not null && _grids.TryGetValue(entry.Grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries))
            {
                List<ChunkCoordinate> links = entry.Links;
                entry.Links = null;
                foreach (ChunkCoordinate target in links) AddLink(entry.Grid, entries, target, -1);
            }

            return;
        }

        Chunk? chunk = entry.Chunk;
        if (chunk is null || !HasMultiBlockOrigin(chunk.Palette)) return;
        BlockCoordinate corner = VoxelCoordinates.ToBlock(chunk.Coordinate, new LocalBlockCoordinate(0, 0, 0));
        for (int index = 0; index < Chunk.Volume; index++)
        {
            uint state = chunk.GetState(index);
            if (IsOriginOfMultiBlock(state)) Link(entry, Cell(corner, index), state, 1);
        }
    }

    private void Link(ChunkEntry entry, BlockCoordinate origin, uint state, int delta)
    {
        if (!_grids.TryGetValue(entry.Grid, out Dictionary<ChunkCoordinate, ChunkEntry>? entries)) return;
        foreach (BlockCell offset in _multiBlocks.GetFootprint(state))
        {
            long x = (long)origin.X + offset.X, y = (long)origin.Y + offset.Y, z = (long)origin.Z + offset.Z;
            if (x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue || z is < int.MinValue or > int.MaxValue) continue;
            ChunkCoordinate target = VoxelCoordinates.ToChunk(new BlockCoordinate((int)x, (int)y, (int)z));
            if (target == entry.Coordinate) continue;
            if (delta > 0)
            {
                (entry.Links ??= []).Add(target);
                AddLink(entry.Grid, entries, target, 1);
            }
            else if (entry.Links is not null && entry.Links.Remove(target))
            {
                AddLink(entry.Grid, entries, target, -1);
            }
        }
    }

    private void AddLink(Grid grid, Dictionary<ChunkCoordinate, ChunkEntry> entries, ChunkCoordinate target, int delta)
    {
        if (!grid.Storage.Overlaps(target)) return;
        ChunkEntry? entry;
        if (delta > 0) entry = GetOrCreate(grid, entries, target);
        else if (!entries.TryGetValue(target, out entry)) return;
        entry!.Linked += delta;
        Refresh(entry);
    }

    private bool HasMultiBlockOrigin(Palette palette)
    {
        for (int i = 0; i < palette.Count; i++)
        {
            if (IsOriginOfMultiBlock(palette.GetEntry(i))) return true;
        }

        return false;
    }

    private bool IsOriginOfMultiBlock(uint state) => state != 0 && _multiBlocks.IsMultiBlock(state) && _multiBlocks.IsOrigin(state);

    [HotPath]
    private static BlockCoordinate Cell(BlockCoordinate corner, int index) =>
        new(corner.X + (index & 31), corner.Y + (index >> 10), corner.Z + ((index >> 5) & 31));

    private void CollectProductions()
    {
        for (int i = _inFlight.Count - 1; i >= 0; i--)
        {
            ProductionSlot slot = _inFlight[i];
            if (!slot.Handle.IsCompleted) continue;
            slot.Handle.Complete();
            _inFlight.RemoveAt(i);
            ChunkEntry entry = slot.Entry!;
            entry.Slot = null;
            if (slot.Error is not null)
            {
                Failed(slot);
                continue;
            }

            if (entry.Total == 0 || entry.Grid.IsDestroyed || slot.Provider!.IsDisposed || entry.State != ChunkLoadState.Loading)
            {
                if (entry.State == ChunkLoadState.Loading) entry.State = ChunkLoadState.NotLoaded;
                if (entry.Total > 0 && !entry.Grid.IsDestroyed && entry.State == ChunkLoadState.NotLoaded) Refresh(entry);
                Forget(entry);
                Recycle(slot);
                continue;
            }

            entry.Slot = slot;
            _pending++;
            _batches.SubmitFill(slot);
        }
    }

    private void Failed(ProductionSlot slot)
    {
        ChunkEntry entry = slot.Entry!;
        Exception error = slot.Error!;
        ChunkProviderHandle provider = slot.Provider!;
        provider.Owner.ReportFault(error);
        entry.State = entry.Total > 0 ? ChunkLoadState.Failed : ChunkLoadState.NotLoaded;
        if (entry.Active > 0) _loadingTask?.Fail($"The chunk provider of module '{provider.OwnerId}' failed for chunk {entry.Coordinate}: {error.Message}", error);
        Forget(entry);
        Recycle(slot);
    }

    private void Unload()
    {
        for (int i = _candidates.Count - 1; i >= 0; i--)
        {
            ChunkEntry entry = _candidates[i];
            if (entry.Total > 0 || entry.State != ChunkLoadState.Loaded)
            {
                entry.IsCandidate = false;
                RemoveCandidate(i);
                continue;
            }

            if (_tick - entry.ZeroSince < _unloadDelay) continue;
            entry.IsCandidate = false;
            RemoveCandidate(i);
            UnloadEntry(entry);
        }
    }

    private void RemoveCandidate(int index)
    {
        int last = _candidates.Count - 1;
        _candidates[index] = _candidates[last];
        _candidates.RemoveAt(last);
    }

    private void UnloadEntry(ChunkEntry entry)
    {
        Grid grid = entry.Grid;
        Raise(new ChunkResidencyChange(ChunkResidencyKind.Unloading, grid, entry.Coordinate));
        if (entry.HoldsLinks) SetLinks(entry, false);
        SetActive(entry, false);
        if (entry.Chunk is { } chunk) _heightMaps.OnUnloading(grid, chunk);
        entry.Scheduled = null;
        entry.Chunk = null;
        entry.State = ChunkLoadState.NotLoaded;
        grid.Storage.RemoveChunk(entry.Coordinate);
        Forget(entry);
    }

    private void Dispatch()
    {
        if (_requests.Count == 0) return;
        int capacity = Math.Max(2, _module.Jobs.WorkerCount * 2) - _inFlight.Count - _pending;
        if (capacity <= 0) return;
        _requests.Sort(_requestOrder);
        int taken = 0;
        while (taken < _requests.Count && capacity > 0)
        {
            ChunkEntry entry = _requests[taken++];
            entry.IsRequested = false;
            if (entry.State != ChunkLoadState.Loading || entry.Slot is not null || entry.Grid.IsDestroyed)
            {
                Forget(entry);
                continue;
            }

            var request = new ChunkRequest(entry.Grid.Dimension, entry.Grid.Identifier, entry.Grid.IsTerrain, entry.Grid.Bounds, entry.Coordinate);
            ChunkProviderHandle? provider = Choose(request);
            if (provider is null)
            {
                // No provider produces the chunk: it is air.
                entry.Grid.Storage.GetOrCreateChunk(entry.Coordinate);
                continue;
            }

            ProductionSlot slot = _slots.Count > 0 ? _slots.Pop() : new ProductionSlot();
            slot.Entry = entry;
            slot.Provider = provider;
            slot.Request = request;
            slot.Error = null;
            entry.Slot = slot;
            slot.Handle = _module.Jobs.Schedule(new ProduceJob(slot), default, JobPriority.Background);
            _inFlight.Add(slot);
            capacity--;
        }

        _requests.RemoveRange(0, taken);
    }

    private ChunkProviderHandle? Choose(in ChunkRequest request)
    {
        foreach (ChunkProviderHandle provider in _providers)
        {
            try
            {
                if (provider.Provider.Accepts(request)) return provider;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                provider.Owner.ReportFault(exception);
            }
        }

        return null;
    }

    private void Recycle(ProductionSlot slot)
    {
        slot.Entry = null;
        slot.Provider = null;
        slot.Error = null;
        slot.Handle = default;
        _slots.Push(slot);
    }

    [HotPath]
    private void ApplyFreezePolicy()
    {
        if (!_freezeInactive) return;
        foreach (DimensionId dimension in _worlds.DimensionKeys)
        {
            if (!_time.HasDimension(dimension)) continue;
            bool inactive = _activeByDimension.GetValueOrDefault(dimension) == 0;
            if (inactive && !_time.IsFrozen(dimension))
            {
                _time.SetFrozen(dimension, true);
                _frozenByVoxel.Add(dimension);
            }
            else if (!inactive && _frozenByVoxel.Remove(dimension))
            {
                _time.SetFrozen(dimension, false);
            }
        }
    }

    private void TrackLoadingTask()
    {
        if (_loadingTask is null) return;
        if (_loadingTask.State != LoadingTaskState.Running)
        {
            _loadingTask = null;
            return;
        }

        int total = 0;
        int loaded = 0;
        foreach (Dictionary<ChunkCoordinate, ChunkEntry> entries in _grids.Values)
        {
            foreach (ChunkEntry entry in entries.Values)
            {
                if (entry.Active == 0 || entry.Grid.Residency != GridResidency.Tickets) continue;
                total++;
                if (entry.State == ChunkLoadState.Loaded) loaded++;
            }
        }

        if (loaded == total)
        {
            _loadingTask.Complete();
            _loadingTask = null;
        }
        else
        {
            _loadingTask.Report((double)loaded / total);
        }
    }

    private void Raise(in ChunkResidencyChange change)
    {
        for (int i = 0; i < _subscriptions.Count; i++)
        {
            ChunkResidencySubscription subscription = _subscriptions[i];
            if (subscription.IsDisposed) continue;
            try
            {
                subscription.Handler(change);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                subscription.Owner.ReportFault(exception);
            }
        }
    }

    private sealed class RequestOrder : IComparer<ChunkEntry>
    {
        public int Compare(ChunkEntry? x, ChunkEntry? y)
        {
            int levelX = x!.Active > 0 ? 2 : x.Reduced > 0 ? 1 : 0;
            int levelY = y!.Active > 0 ? 2 : y.Reduced > 0 ? 1 : 0;
            if (levelX != levelY) return levelY.CompareTo(levelX);
            return x.Priority.CompareTo(y.Priority);
        }
    }

    private readonly struct ProduceJob : IJob
    {
        private readonly ProductionSlot _slot;

        [HotPath]
        public ProduceJob(ProductionSlot slot) => _slot = slot;

        [HotPath]
        public void Execute()
        {
            try
            {
                Array.Clear(_slot.States);
                _slot.Provider!.Provider.Produce(_slot.Request, _slot.States);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _slot.Error = exception;
            }
        }
    }
}
