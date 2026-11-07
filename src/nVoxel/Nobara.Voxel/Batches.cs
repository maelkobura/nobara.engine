// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Game;
using Nobara.Items;
using ItemBlockFace = Nobara.Items.BlockFace;

namespace Nobara.Voxel;

/// <summary>A value of block data detached while a block is replaced.</summary>
public readonly record struct BlockDataEntry(IBlockDataType Type, object Value);

/// <summary>The block data detached from one cell by a batch.</summary>
public sealed class RemovedBlockData
{
    internal RemovedBlockData(IReadOnlyList<BlockDataEntry> values) => Values = values;

    /// <summary>Gets the detached values, in their declaration order.</summary>
    public IReadOnlyList<BlockDataEntry> Values { get; }
}

/// <summary>One cell reported by a voxel batch notification.</summary>
public readonly struct VoxelBlockChange
{
    /// <summary>Initializes a changed-cell notification.</summary>
    [HotPath]
    public VoxelBlockChange(int localIndex, uint oldState, uint newState, bool dataChanged, RemovedBlockData? removedData)
    {
        LocalIndex = localIndex;
        OldState = oldState;
        NewState = newState;
        DataChanged = dataChanged;
        RemovedData = removedData;
    }

    /// <summary>Gets the local index in the chunk.</summary>
    public int LocalIndex { get; }

    /// <summary>Gets the state before the batch.</summary>
    public uint OldState { get; }

    /// <summary>Gets the state after the batch.</summary>
    public uint NewState { get; }

    /// <summary>Gets whether block data or channels changed.</summary>
    public bool DataChanged { get; }

    /// <summary>Gets the data detached from the old cell.</summary>
    public RemovedBlockData? RemovedData { get; }
}

/// <summary>A notification for one chunk after one batch was applied.</summary>
public sealed class VoxelBatchNotification
{
    internal VoxelBatchNotification(Grid grid, ChunkCoordinate chunk, IReadOnlyList<VoxelBlockChange> changes, BlockCause cause, bool technical)
    {
        Grid = grid;
        Chunk = chunk;
        Changes = changes;
        Cause = cause;
        IsTechnical = technical;
    }

    /// <summary>Gets the changed grid.</summary>
    public Grid Grid { get; }

    /// <summary>Gets the changed chunk.</summary>
    public ChunkCoordinate Chunk { get; }

    /// <summary>Gets the changes in local chunk-index order.</summary>
    public IReadOnlyList<VoxelBlockChange> Changes { get; }

    /// <summary>Gets the cause carried by the batch.</summary>
    public BlockCause Cause { get; }

    /// <summary>Gets whether the batch was a technical modification.</summary>
    public bool IsTechnical { get; }
}

/// <summary>A revocable voxel notification subscription.</summary>
public sealed class VoxelNotificationHandle : IDisposable
{
    private readonly VoxelBatches _batches;

    internal VoxelNotificationHandle(VoxelBatches batches, VoxelBatches.NotificationSubscription subscription)
    {
        _batches = batches;
        Subscription = subscription;
    }

    /// <summary>Gets whether the subscription was revoked.</summary>
    public bool IsRevoked => Subscription.Revoked;

    internal VoxelBatches.NotificationSubscription Subscription { get; }

    /// <summary>Revokes the subscription.</summary>
    public void Dispose() => _batches.Revoke(Subscription);
}

/// <summary>A handle for a batch submitted to the voxel tick.</summary>
public sealed class VoxelBatchHandle
{
    internal VoxelBatchHandle(BlockBatch batch) => Batch = batch;

    /// <summary>Gets the nBlocks batch, including the final removals.</summary>
    public BlockBatch Batch { get; }

    /// <summary>Gets whether the batch has been applied or cancelled.</summary>
    public bool IsCompleted { get; internal set; }

    internal Action? CompletedCallback { get; set; }

    /// <summary>Gets whether the batch was cancelled.</summary>
    public bool IsCancelled => Batch.IsCancelled;
}

/// <summary>A block requested by a placing action.</summary>
public readonly struct BlockPlacement
{
    /// <summary>Initializes a placement.</summary>
    [HotPath]
    public BlockPlacement(BlockCoordinate position, uint state) => (Position, State) = (position, state);

    /// <summary>Gets the origin cell.</summary>
    public BlockCoordinate Position { get; }

    /// <summary>Gets the state to place.</summary>
    public uint State { get; }
}

/// <summary>
/// The voxel batches, block actions, and chunk notifications of one context.
/// </summary>
public interface IVoxelBatches
{
    /// <summary>Submits an action batch for the next World tick.</summary>
    VoxelBatchHandle Submit(BlockBatch batch);

    /// <summary>Submits a technical batch without action hooks.</summary>
    VoxelBatchHandle SubmitTechnical(BlockBatch batch);

    /// <summary>Queues a placing action, expanding multi-blocks atomically.</summary>
    VoxelBatchHandle Place(Grid grid, BlockActor actor, BlockCause cause, IReadOnlyList<BlockPlacement> placements);

    /// <summary>Queues a breaking action, expanding multi-blocks atomically.</summary>
    VoxelBatchHandle Break(Grid grid, BlockActor actor, BlockCause cause, IReadOnlyList<BlockCoordinate> positions);

    /// <summary>Uses one block and fires the consumable interaction hook.</summary>
    bool Interact(Grid grid, BlockActor actor, BlockCoordinate position, ItemBlockFace face);

    /// <summary>Subscribes to chunk notifications.</summary>
    VoxelNotificationHandle Subscribe(ModuleContext module, Action<VoxelBatchNotification> handler, BlockType? type = null, BlockTag? tag = null, IBlockDataType? dataType = null);
}

internal sealed class VoxelBatches : IVoxelBatches, ITickSystem, IItemHandler<PlaceableBlock>, IDisposable
{
    private readonly ModuleContext context;
    private readonly VoxelWorlds worlds;
    private IBlockRegistry blocks = null!;
    private IBlockHooks hooks = null!;
    private IMultiBlockRegistry multiBlocks = null!;
    private readonly List<PendingBatch> _pending = [];
    private readonly List<NeighborChange> _neighbors = [];
    private readonly List<NotificationSubscription> _subscriptions = [];
    private readonly Action _runCallback;
    private readonly Action<Grid, BlockActor, BlockCause, BlockPlacement> _placeOneCallback;
    private readonly Func<uint, ItemBlockFace, uint> _orientCallback;
    private readonly Func<ulong, Grid?> _resolveGridCallback;
    private ItemHandlerHandle? _placeableHandler;
    private ulong _nextMultiBlock = 1;
    private readonly List<IVoxelChangeObserver> _observers = [];
    private readonly List<PendingBatch> _waiting = [];
    private readonly Action<Grid, Chunk, List<VoxelBlockChange>?> _notifyFillCallback;
    private ChunkLoader? _loader;

    public VoxelBatches(ModuleContext context, VoxelWorlds worlds)
    {
        this.context = context;
        this.worlds = worlds;
        _runCallback = ProcessTick;
        _placeOneCallback = PlaceOne;
        _orientCallback = Orient;
        _resolveGridCallback = worlds.ResolveOrNull;
        _notifyFillCallback = NotifyFill;
    }

    internal void AttachLoading(ChunkLoader loader, params IVoxelChangeObserver[] observers)
    {
        _loader = loader;
        _observers.AddRange(observers);
    }

    /// <summary>Queues the result of a chunk provider, applied in the order of batches as a technical batch of cause generation.</summary>
    internal void SubmitFill(ProductionSlot slot) => _pending.Add(new PendingBatch(null!, true, BatchAction.Fill, null!, slot));

    public void Initialize(IBlockRegistry blocks, IBlockHooks hooks, IMultiBlockRegistry multiBlocks, ITickSchedule schedule, IItemUsage usage)
    {
        this.blocks = blocks;
        this.hooks = hooks;
        this.multiBlocks = multiBlocks;
        schedule.AddSystem(context, "nobara.voxel.batches", TickStages.World, this, new TickSystemOptions { Writes = [worlds.ChunkAccess, worlds.EntityStructure] });
        _placeableHandler = usage.AddHandler(context, blocks.Placeable, usage.UseOnBlock, this);
    }

    public void Dispose()
    {
        _placeableHandler?.Dispose();
        _placeableHandler = null;
    }

    internal IBlockRegistry Blocks => blocks;

    internal IMultiBlockRegistry MultiBlocks => multiBlocks;

    [HotPath]
    public UseResult Use(in ItemUse<PlaceableBlock> use)
    {
        Grid? grid = use.Target.Kind == UseTargetKind.Block ? _resolveGridCallback(use.Target.Grid) : null;
        if (grid is null) return UseResult.Pass;
        BlockCoordinate position = new(use.Target.X, use.Target.Y, use.Target.Z);
        BlockCoordinate placement = use.Target.Face switch
        {
            ItemBlockFace.Down => new(position.X, checked(position.Y - 1), position.Z),
            ItemBlockFace.Up => new(position.X, checked(position.Y + 1), position.Z),
            ItemBlockFace.North => new(position.X, position.Y, checked(position.Z - 1)),
            ItemBlockFace.South => new(position.X, position.Y, checked(position.Z + 1)),
            ItemBlockFace.West => new(checked(position.X - 1), position.Y, position.Z),
            ItemBlockFace.East => new(checked(position.X + 1), position.Y, position.Z),
            _ => position,
        };
        uint state = _orientCallback(use.Value.State, use.Target.Face);
        _placeOneCallback(grid, BlockActor.OfEntity(use.Actor.Entity), BlockCause.Hand, new BlockPlacement(placement, state));
        return UseResult.Handled;
    }

    private void PlaceOne(Grid grid, BlockActor actor, BlockCause cause, BlockPlacement placement) => Place(grid, actor, cause, [placement]);

    private uint Orient(uint state, ItemBlockFace face)
    {
        BlockType type = blocks.TypeOf(state);
        if (!type.TryGetProperty("orientation", out StateProperty? property)) return state;
        string name = face switch
        {
            ItemBlockFace.Down => "down",
            ItemBlockFace.Up => "up",
            ItemBlockFace.East => "east",
            ItemBlockFace.South => "south",
            ItemBlockFace.West => "west",
            _ => "north",
        };
        for (int value = 0; value < property.Count; value++)
        {
            if (string.Equals(property.Values[value], name, StringComparison.Ordinal)) return blocks.With(state, property, value);
        }

        return state;
    }

    public VoxelBatchHandle Submit(BlockBatch batch) => Enqueue(batch, technical: false, InferKind(batch));

    public VoxelBatchHandle SubmitTechnical(BlockBatch batch) => Enqueue(batch, technical: true, BatchAction.Technical);

    public VoxelBatchHandle Place(Grid grid, BlockActor actor, BlockCause cause, IReadOnlyList<BlockPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(cause);
        ArgumentNullException.ThrowIfNull(placements);
        var batch = new BlockBatch(placements.Count) { Actor = actor, Cause = cause };
        var occupied = new HashSet<BlockCoordinate>();
        foreach (BlockPlacement placement in placements)
        {
            BlockType type = blocks.TypeOf(placement.State);
            IReadOnlyList<BlockCell> footprint = multiBlocks.IsMultiBlock(placement.State) ? multiBlocks.GetFootprint(placement.State) : [new BlockCell(0, 0, 0)];
            ulong group = footprint.Count > 1 ? _nextMultiBlock++ : 0;
            for (int part = 0; part < footprint.Count; part++)
            {
                BlockCell offset = footprint[part];
                BlockCoordinate position = new(checked(placement.Position.X + offset.X), checked(placement.Position.Y + offset.Y), checked(placement.Position.Z + offset.Z));
                ValidateCell(grid, position);
                if (!occupied.Add(position) || grid.GetBlock(position) != blocks.Air.DefaultState)
                {
                    throw new InvalidOperationException($"Cannot place '{type.ObjectId}' at {position}: the cell is occupied or repeated.");
                }

                uint state = placement.State;
                if (type.TryGetProperty("part", out StateProperty? partProperty)) state = blocks.With(state, partProperty, part);
                batch.Add(new BlockChange(ToPosition(grid, position), 0, state, group));
            }
        }

        return Enqueue(batch, technical: false, BatchAction.Placing);
    }

    public VoxelBatchHandle Break(Grid grid, BlockActor actor, BlockCause cause, IReadOnlyList<BlockCoordinate> positions)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(cause);
        ArgumentNullException.ThrowIfNull(positions);
        var batch = new BlockBatch(positions.Count) { Actor = actor, Cause = cause };
        var visited = new HashSet<BlockCoordinate>();
        foreach (BlockCoordinate position in positions)
        {
            ValidateCell(grid, position);
            uint state = grid.GetBlock(position);
            if (state == blocks.Air.DefaultState || !visited.Add(position)) continue;
            IReadOnlyList<BlockCell> footprint = multiBlocks.IsMultiBlock(state) ? multiBlocks.GetFootprint(state) : [new BlockCell(0, 0, 0)];
            ulong group = footprint.Count > 1 ? _nextMultiBlock++ : 0;
            BlockCoordinate origin = multiBlocks.IsMultiBlock(state) ? multiBlocks.GetOrigin(state, position) : position;
            for (int part = 0; part < footprint.Count; part++)
            {
                BlockCell offset = footprint[part];
                BlockCoordinate cell = new(checked(origin.X + offset.X), checked(origin.Y + offset.Y), checked(origin.Z + offset.Z));
                ValidateCell(grid, cell);
                uint cellState = grid.GetBlock(cell);
                if (cellState == blocks.Air.DefaultState) throw new InvalidOperationException($"Cannot break incomplete multi-block at {origin}.");
                visited.Add(cell);
                batch.Add(new BlockChange(ToPosition(grid, cell), cellState, blocks.Air.DefaultState, group));
            }
        }

        return Enqueue(batch, technical: false, BatchAction.Breaking);
    }

    public bool Interact(Grid grid, BlockActor actor, BlockCoordinate position, ItemBlockFace face)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ValidateCell(grid, position);
        uint state = grid.GetBlock(position);
        if (state == blocks.Air.DefaultState || !hooks.HasSubscribers(hooks.Interact, state)) return false;
        return hooks.Fire(hooks.Interact, state, new BlockInteraction(actor, ToPosition(grid, position), state, face));
    }

    public VoxelNotificationHandle Subscribe(ModuleContext module, Action<VoxelBatchNotification> handler, BlockType? type = null, BlockTag? tag = null, IBlockDataType? dataType = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(handler);
        if (type is not null && tag is not null) throw new ArgumentException("A notification subscription chooses a type or a tag, not both.");
        var subscription = new NotificationSubscription(module, handler, type, tag, dataType);
        _subscriptions.Add(subscription);
        return new VoxelNotificationHandle(this, subscription);
    }

    public void Run(in TickTime time)
    {
        _runCallback();
    }

    private void ProcessTick()
    {
        int count = _pending.Count;
        ProcessNeighbors();
        for (int i = 0; i < count; i++)
        {
            PendingBatch pending = _pending[i];

            // A batch that writes in a chunk a provider is producing waits for it, so it applies over the generated blocks.
            if (pending.Fill is null && WaitsForLoading(pending.Batch)) _waiting.Add(pending);
            else Process(pending);
        }

        if (count > 0) _pending.RemoveRange(0, count);
        if (_waiting.Count > 0)
        {
            _pending.InsertRange(0, _waiting);
            _waiting.Clear();
        }
    }

    private bool WaitsForLoading(BlockBatch batch)
    {
        if (_loader is null || batch.IsCancelled) return false;
        ReadOnlySpan<BlockChange> changes = batch.Changes;
        for (int i = 0; i < changes.Length; i++)
        {
            if (batch.IsRemoved(i) || !worlds.TryResolve(changes[i].Position.Grid, out Grid? grid) || grid!.Residency != GridResidency.Tickets) continue;
            ChunkCoordinate chunk = VoxelCoordinates.ToChunk(new BlockCoordinate(changes[i].Position.X, changes[i].Position.Y, changes[i].Position.Z));
            if (!grid.Storage.TryGetChunk(chunk, out _) && _loader.TryGetEntry(grid, chunk, out ChunkEntry? entry) && entry!.State == ChunkLoadState.Loading) return true;
        }

        return false;
    }

    internal void Revoke(NotificationSubscription subscription) => subscription.Revoked = true;

    private VoxelBatchHandle Enqueue(BlockBatch batch, bool technical, BatchAction action)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var handle = new VoxelBatchHandle(batch);
        _pending.Add(new PendingBatch(batch, technical, action, handle, null));
        return handle;
    }

    private static BatchAction InferKind(BlockBatch batch) =>
        batch.Changes.Length > 0 && batch.Changes[0].NewState == 0 ? BatchAction.Breaking : BatchAction.Placing;

    private void Process(PendingBatch pending)
    {
        if (pending.Fill is { } fill)
        {
            _loader!.ApplyFill(fill, HasSubscriptions() ? new List<VoxelBlockChange>() : null, _notifyFillCallback);
            return;
        }

        BlockBatch batch = pending.Batch;
        if (batch.IsCancelled || batch.RemainingCount == 0)
        {
            Complete(pending);
            return;
        }

        if (pending.Action == BatchAction.Placing && HasSubscribers(hooks.Placing, batch, usesOldState: false) && !hooks.Fire(hooks.Placing, batch))
        {
            Complete(pending);
            return;
        }

        if (pending.Action == BatchAction.Breaking && HasSubscribers(hooks.Breaking, batch, usesOldState: true) && !hooks.Fire(hooks.Breaking, batch))
        {
            Complete(pending);
            return;
        }

        var operations = new Dictionary<(ulong Grid, ChunkCoordinate Chunk), ChunkOperation>();
        ReadOnlySpan<BlockChange> changes = batch.Changes;
        for (int i = 0; i < changes.Length; i++)
        {
            if (batch.IsRemoved(i)) continue;
            BlockChange change = changes[i];
            Grid grid = worlds.Resolve(change.Position.Grid);
            BlockCoordinate position = new(change.Position.X, change.Position.Y, change.Position.Z);
            grid.Storage.Contains(position);
            ChunkCoordinate chunkCoordinate = VoxelCoordinates.ToChunk(position);
            Chunk chunk = grid.Storage.GetOrCreateChunk(chunkCoordinate);
            var key = (change.Position.Grid, chunkCoordinate);
            if (!operations.TryGetValue(key, out ChunkOperation? operation))
            {
                operation = new ChunkOperation(grid, chunk);
                operations.Add(key, operation);
            }

            operation.Cells.Add(new PendingCell(i, VoxelCoordinates.ToIndex(position), change));
        }

        if (operations.Count > 0)
        {
            var handles = new JobHandle[operations.Count];
            int handleIndex = 0;
            foreach (ChunkOperation operation in operations.Values)
            {
                var job = new ApplyChunkJob(operation, batch.Cause.Equals(BlockCause.Generation), batch.PreserveData);
                handles[handleIndex++] = context.Jobs.Schedule(job, new JobAccess([], [operation.Chunk.AccessData]));
            }

            context.Jobs.Combine(handles).Complete();
            foreach (ChunkOperation operation in operations.Values)
            {
                if (!batch.PreserveData)
                {
                    for (int i = 0; i < operation.Changes.Count; i++)
                    {
                        VoxelBlockChange change = operation.Changes[i];
                        RemovedBlockData? removed = operation.Chunk.DetachAndReset(change.LocalIndex);
                        operation.Changes[i] = new VoxelBlockChange(change.LocalIndex, change.OldState, change.NewState, change.DataChanged || removed is not null, removed);
                    }
                }

                foreach (IVoxelChangeObserver observer in _observers) observer.OnApplied(operation.Grid, operation.Chunk, operation.Changes);
                Notify(operation, batch, pending.Technical);
                QueueNeighbors(operation);
            }
        }

        if (pending.Action is BatchAction.Placing or BatchAction.Breaking)
        {
            BlockBatchHook after = pending.Action == BatchAction.Breaking ? hooks.Broken : hooks.Placed;
            hooks.Fire(after, batch);
        }

        Complete(pending);
    }

    private static void Complete(PendingBatch pending)
    {
        pending.Handle.IsCompleted = true;
        pending.Handle.CompletedCallback?.Invoke();
    }

    private void ProcessNeighbors()
    {
        if (_neighbors.Count == 0) return;
        NeighborChange[] current = _neighbors.ToArray();
        _neighbors.Clear();
        var seen = new HashSet<(ulong Grid, BlockCoordinate Position, BlockPosition Neighbor)>();
        foreach (NeighborChange change in current)
        {
            if (!seen.Add((change.Position.Grid, new BlockCoordinate(change.Position.X, change.Position.Y, change.Position.Z), change.Neighbor))) continue;
            if (!worlds.TryResolve(change.Position.Grid, out Grid? grid)) continue;
            BlockCoordinate position = new(change.Position.X, change.Position.Y, change.Position.Z);
            uint state = grid!.GetBlock(position);
            if (state != blocks.Air.DefaultState && hooks.HasSubscribers(hooks.NeighborChanged, state))
            {
                hooks.Fire(hooks.NeighborChanged, state, change with { State = state });
            }
        }
    }

    private void Notify(ChunkOperation operation, BlockBatch batch, bool technical)
    {
        if (operation.Changes.Count == 0) return;
        operation.Changes.Sort((left, right) => left.LocalIndex.CompareTo(right.LocalIndex));
        Notify(new VoxelBatchNotification(operation.Grid, operation.Chunk.Coordinate, operation.Changes, batch.Cause, technical));
    }

    private bool HasSubscriptions()
    {
        foreach (NotificationSubscription subscription in _subscriptions)
        {
            if (!subscription.Revoked) return true;
        }

        return false;
    }

    // A provider result: the cells it changed, already in local-index order.
    private void NotifyFill(Grid grid, Chunk chunk, List<VoxelBlockChange>? changes)
    {
        if (changes is null || changes.Count == 0) return;
        Notify(new VoxelBatchNotification(grid, chunk.Coordinate, changes, BlockCause.Generation, technical: true));
    }

    private void Notify(VoxelBatchNotification notification)
    {
        foreach (NotificationSubscription subscription in _subscriptions)
        {
            if (subscription.Revoked) continue;
            IReadOnlyList<VoxelBlockChange> filtered = Filter(notification.Changes, subscription);
            if (filtered.Count == 0) continue;
            subscription.Handler(new VoxelBatchNotification(notification.Grid, notification.Chunk, filtered, notification.Cause, notification.IsTechnical));
        }
    }

    private IReadOnlyList<VoxelBlockChange> Filter(IReadOnlyList<VoxelBlockChange> changes, NotificationSubscription subscription)
    {
        if (subscription.Type is null && subscription.Tag is null && subscription.DataType is null) return changes;
        var filtered = new List<VoxelBlockChange>(changes.Count);
        foreach (VoxelBlockChange change in changes)
        {
            uint state = change.NewState == blocks.Air.DefaultState ? change.OldState : change.NewState;
            if (subscription.Type is not null && !ReferenceEquals(blocks.TypeOf(state), subscription.Type)) continue;
            if (subscription.Tag is not null && !blocks.HasTag(state, subscription.Tag)) continue;
            RemovedBlockData? data = subscription.DataType is null || change.RemovedData is null ? change.RemovedData : Only(change.RemovedData, subscription.DataType);
            filtered.Add(new VoxelBlockChange(change.LocalIndex, change.OldState, change.NewState, change.DataChanged, data));
        }

        return filtered;
    }

    private static RemovedBlockData? Only(RemovedBlockData data, IBlockDataType type)
    {
        BlockDataEntry[] values = data.Values.Where(value => ReferenceEquals(value.Type, type)).ToArray();
        return values.Length == 0 ? null : new RemovedBlockData(values);
    }

    private void QueueNeighbors(ChunkOperation operation)
    {
        foreach (VoxelBlockChange change in operation.Changes)
        {
            BlockCoordinate cell = FromLocal(operation.Chunk.Coordinate, change.LocalIndex);
            QueueNeighbor(operation, cell, -1, 0, 0, change);
            QueueNeighbor(operation, cell, 1, 0, 0, change);
            QueueNeighbor(operation, cell, 0, -1, 0, change);
            QueueNeighbor(operation, cell, 0, 1, 0, change);
            QueueNeighbor(operation, cell, 0, 0, -1, change);
            QueueNeighbor(operation, cell, 0, 0, 1, change);
        }
    }

    private void QueueNeighbor(ChunkOperation operation, BlockCoordinate cell, int x, int y, int z, VoxelBlockChange change)
    {
        BlockCoordinate position;
        try
        {
            position = new BlockCoordinate(checked(cell.X + x), checked(cell.Y + y), checked(cell.Z + z));
            operation.Grid.Storage.Contains(position);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return;
        }

        _neighbors.Add(new NeighborChange(new BlockPosition(operation.Grid.Identifier, position.X, position.Y, position.Z), 0, new BlockPosition(operation.Grid.Identifier, cell.X, cell.Y, cell.Z), change.OldState, change.NewState));
    }

    private bool HasSubscribers(BlockBatchHook hook, BlockBatch batch, bool usesOldState)
    {
        ReadOnlySpan<BlockChange> changes = batch.Changes;
        for (int i = 0; i < changes.Length; i++)
        {
            if (!batch.IsRemoved(i) && hooks.HasSubscribers(hook, usesOldState ? changes[i].OldState : changes[i].NewState)) return true;
        }

        return false;
    }

    private static BlockPosition ToPosition(Grid grid, BlockCoordinate position) => new(grid.Identifier, position.X, position.Y, position.Z);

    private static BlockCoordinate FromLocal(ChunkCoordinate chunk, int index)
    {
        int x = index & 31;
        int z = (index >> 5) & 31;
        int y = index >> 10;
        return VoxelCoordinates.ToBlock(chunk, new LocalBlockCoordinate(x, y, z));
    }

    private static void ValidateCell(Grid grid, BlockCoordinate position) => grid.Storage.Contains(position);

    private enum BatchAction
    {
        Placing,
        Breaking,
        Technical,
        Fill,
    }

    private readonly record struct PendingBatch(BlockBatch Batch, bool Technical, BatchAction Action, VoxelBatchHandle Handle, ProductionSlot? Fill);

    private readonly record struct PendingCell(int BatchIndex, int LocalIndex, BlockChange Change);

    private sealed class ChunkOperation
    {
        public ChunkOperation(Grid grid, Chunk chunk)
        {
            Grid = grid;
            Chunk = chunk;
            Apply = ApplyStates;
        }

        public Grid Grid { get; }
        public Chunk Chunk { get; }
        public List<PendingCell> Cells { get; } = [];
        public List<VoxelBlockChange> Changes { get; } = [];
        public Action<bool?> Apply { get; }

        private void ApplyStates(bool? natural)
        {
            foreach (PendingCell cell in Cells)
            {
                BlockApplyResult result = Chunk.ApplyState(cell.LocalIndex, cell.Change.NewState, natural);
                Changes.Add(new VoxelBlockChange(cell.LocalIndex, result.OldState, result.NewState, result.DataChanged, null));
            }
        }
    }

    private struct ApplyChunkJob : IJob
    {
        private readonly ChunkOperation _operation;
        private readonly bool _natural;
        private readonly bool _preserveNatural;

        [HotPath]
        public ApplyChunkJob(ChunkOperation operation, bool natural, bool preserveNatural)
        {
            _operation = operation;
            _natural = natural;
            _preserveNatural = preserveNatural;
        }

        [HotPath]
        public void Execute()
        {
            _operation.Apply(_preserveNatural ? null : _natural);
        }
    }

    internal sealed class NotificationSubscription(ModuleContext module, Action<VoxelBatchNotification> handler, BlockType? type, BlockTag? tag, IBlockDataType? dataType)
    {
        public ModuleContext Module { get; } = module;
        public Action<VoxelBatchNotification> Handler { get; } = handler;
        public BlockType? Type { get; } = type;
        public BlockTag? Tag { get; } = tag;
        public IBlockDataType? DataType { get; } = dataType;
        public bool Revoked { get; set; }
    }
}
