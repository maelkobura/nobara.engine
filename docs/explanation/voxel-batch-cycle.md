# The voxel batch cycle

`nVoxel` changes block storage through `IVoxelBatches`. A caller creates a
placement, breaking, or technical `BlockBatch` on the context loop thread and
submits it. The call returns a `VoxelBatchHandle`; the handle is incomplete
until the batch reaches the `World` stage of a simulation tick.

## One batch

The cycle has four boundaries:

1. The caller queues the batch. Reads still see the old chunk state.
2. At the next `World` tick, `Placing` or `Breaking` subscribers inspect the
   batch. A subscriber can remove cells, remove a whole multiblock through its
   shared group, or cancel the batch.
3. Remaining cells are grouped by grid and chunk, then applied in jobs. One
   `VoxelBatchNotification` is sent for each touched chunk. Replaced block data
   and dense channels are detached unless the batch sets `PreserveData` for a
   technical copy such as a split or merge.
4. `Placed` or `Broken` runs after storage is visible. Direct neighbors are
   queued for the next tick, and the handle is marked complete.

Technical batches skip action hooks but still update storage, notifications,
and neighbor queues. They carry their `BlockCause`, so persistence and network
modules can distinguish generation, split, and merge work without inventing a
second storage path.

## Notifications and neighbors

Notifications are per chunk and per batch, with changes ordered by local chunk
index. A change carries the old state, the new state, whether metadata changed,
and the block data detached from the old cell. A subscription can select a
block type, a tag, or a data type for the detached values.

Neighbor hooks run after the storage pass. The handler receives the current
state of the block being told and the old and new state of the direct neighbor
that changed. A batch submitted by that handler is included in the following
batch pass, never in the pass currently being processed; this prevents a
single edit from recursively walking the world in one tick.

## Placement and grid moves

`Place` expands every multiblock footprint and validates all target cells
before queueing, so a partial placement cannot become visible. `Break` resolves
the multiblock origin from any selected part and removes the complete
footprint.

`IVoxelWorlds.Split` and `Merge` use technical batches. Split moves complete
multiblocks and their metadata to a new grid. Merge rotates block states and
rotatable block data by zero to three quarter turns, applies an offset, rejects
occupied destinations, and destroys the absorbed grid after its removal batch
completes.

The item usage handler supplied by nVoxel turns a placeable block item into a
`Place` request on the face adjacent to the targeted block. If the block has
an orientation property, the face selects its matching orientation value. A
block with `horizontal` orientations placed on a top or bottom face, such as
a furnace on the ground, has no matching value: when the target carries the
direction of the ray (`UseTarget.OnBlock` with a direction), the block faces
the actor, turned toward the nearest horizontal direction opposite to the
ray in the frame of the grid; without it, the block keeps the state of its
item.
When a cell of the block is outside the bounds of the grid or does not hold
air, the handler returns `Pass` instead of submitting the request, so
`UseItem` reports that nothing handled the use and the game goes on. `Place`
called directly still throws for such a cell: game code checks the cells
before it places a block.
