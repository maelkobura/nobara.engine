# nVoxel

nVoxel stores voxel worlds in dimensions, ECS-backed grids, resident 32³ chunks, compact palettes, and extensible block metadata.

## Purpose

nVoxel owns the dimensions of a context and stores the terrain and other voxel grids of each world session. It also applies block batches at the World tick, expands multiblocks, reports chunk notifications, and exposes split/merge operations. It loads chunks through tickets and a chain of chunk providers, runs scheduled and random block ticks, keeps height maps, and answers spatial queries. It remains independent of rendering, meshing, and persistence.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server.
<!-- nobara:generated:end -->

## Dependencies

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.blocks` | `^1.0` | Required |
| `nobara.context` | `^1.0` | Required |
| `nobara.entity` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
| `nobara.items` | `^1.0` | Required |
<!-- nobara:generated:end -->

## Entry points

| Member | Use |
| --- | --- |
| `IVoxelWorlds` | Dimensions, terrain grids, grid creation and destruction, and the spatial index. |
| `BlockCoordinate`, `ChunkCoordinate`, `LocalBlockCoordinate`, `VoxelCoordinates` | Overflow-checked grid coordinates and 32³ chunk indexing. |
| `Grid`, `GridStorage`, `GridBounds` | ECS-backed grids and their resident chunk tables. |
| `Chunk`, `ChunkSnapshot`, `Palette` | Palette storage, versions, frozen copies, and compact native memory. |
| `DenseChannelDefinition`, `BlockDataType<T>` | Lazy dense channels and serializable block data declarations. |
| `IShapeRegistry`, `IBlockShape`, `ShapeEvaluation` | Shape registration, cached oriented collision, face opacity, contact masks, and instance-dependent queries. |
| `IMultiBlockRegistry`, `MultiBlockCell` | Rotated footprints, part indices, and calculated origins. |
| `BlockRotation` | Quarter-turn transformations for block states and block-data values. |
| `IVoxelBatches` | Tick-aligned placement, breaking, interaction, technical batches, and chunk notifications. |
| `VoxelBatchNotification`, `VoxelBlockChange`, `RemovedBlockData` | Per-chunk results, removed block data, and optional notification filters. |
| `IChunkLoading` | Chunk tickets, area and viewer tickets, the chunk provider chain, residency subscriptions, and chunk status. |
| `ChunkTicket`, `AreaTicket`, `ViewerTicket`, `TicketLevel`, `ChunkArea` | Revocable tickets that hold chunks loaded, active, or active at a reduced frequency. |
| `IChunkProvider`, `ChunkRequest` | A source of chunks; the first provider that accepts a chunk produces it in a background job. |
| `IBlockTicks` | Scheduled ticks in a queue per chunk, on the dimension clock. Random ticks need no call: subscribe to `RandomTick`. |
| `IHeightMaps` | The highest opaque block known in a column of blocks. |
| `IVoxelQueries` | Block reads in grid coordinates, and raycasts, boxes, and spheres in the world. |

Resolve the services with `context.Resolve<IVoxelWorlds>()` and the like. Every context has its own dimensions, entity worlds, grids, chunks, tickets, providers, and spatial index.

A viewer, such as the local player before the network, holds its areas with
`IChunkLoading.AddViewer`, and moves them with `ViewerTicket.Move`. Place the
tickets that the session waits for in a `SessionStarting` handler: nVoxel has
created the terrain grid by then, since its handler runs first. Dispose them
before the session ends, in a `SessionEnding` handler.

nVoxel joins the `World` stage with four tick systems, in this order:
`nobara.voxel.loading`, `nobara.voxel.batches`,
`nobara.voxel.scheduled_ticks`, and `nobara.voxel.random_ticks`.

## Events

`IVoxelBatches` applies submitted batches in the `World` stage of the next
simulation tick. Each touched chunk produces one notification for that batch;
subscribers may filter by block type, tag, or block-data type. Direct neighbors
are notified on the following tick, so a neighbor handler that submits another
batch cannot cascade inside the current application pass. Action batches fire
the nBlocks `Placing`, `Breaking`, `Placed`, `Broken`, and `Interact` hooks;
technical batches used by generation, split, and merge do not fire action hooks.

Chunk loading runs at the `World` stage, in the system
`nobara.voxel.loading`, before the batches: it collects the results of the
providers, which then apply in the order of batches as technical batches of
cause `generation`; unloads the chunks that no ticket held for
`voxel.unloadDelay` ticks; and asks the providers for the next chunks,
nearest chunks of the highest level first. `IChunkLoading.SubscribeResidency`
reports a chunk once it is loaded with its blocks, and before it leaves
memory, including when its grid is destroyed. The systems
`nobara.voxel.scheduled_ticks` and `nobara.voxel.random_ticks` run after the
batches and deliver the nBlocks `ScheduledTick` and `RandomTick` hooks.

During `SessionStarting`, nVoxel adds the loading task "chunks of the
simulation area": it completes once every chunk that an active ticket holds
is loaded, so a session without such a ticket becomes active one frame after
it starts. A provider that fails for one of these chunks fails the task, and
the session.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `voxel.freezeInactiveDimensions` | distribution | bool | `true` |  | both |
| `voxel.randomTicksPerChunk` | distribution | int | `3` | 0 to 4096 | both |
| `voxel.reducedTickInterval` | distribution | int | `4` | 2 to 64 | both |
| `voxel.simulationHeight` | distribution | int | `2` | 1 to 16 | both |
| `voxel.simulationRadius` | distribution | int | `4` | 1 to 32 | both |
| `voxel.unloadDelay` | distribution | int | `300` | 0 to 72000 | both |
| `voxel.viewHeight` | user | int | `4` | 1 to 16 | both |
| `voxel.viewRadius` | user | int | `8` | 2 to 32 | both |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `voxel.viewRadius`, `voxel.viewHeight` | The view area of `AddViewer`, in chunks: a cylinder of that horizontal radius and vertical half-height, held loaded. The defaults hold 1,773 chunks. |
| `voxel.simulationRadius`, `voxel.simulationHeight` | The simulation area of `AddViewer`, held active: its blocks tick. The defaults hold 245 chunks. |
| `voxel.randomTicksPerChunk` | The cells drawn at random in each active chunk at each tick; a drawn cell whose type subscribes to `RandomTick` receives it. 0 turns random ticks off. |
| `voxel.reducedTickInterval` | A chunk held at `ReducedActive` ticks once every this many ticks. |
| `voxel.unloadDelay` | The ticks a chunk stays in memory once no ticket holds it. |
| `voxel.freezeInactiveDimensions` | When `true`, nVoxel freezes the clock of a dimension that has no active chunk during a session, and unfreezes it when a chunk becomes active again. A dimension frozen by another module stays frozen. |

The keys are read when a world session starts, except the view and
simulation areas, which `AddViewer` reads when it is called.

## Resources

None. Block state indices are supplied by nBlocks and are never persisted by nVoxel in this version.

## Constraints

- Grid and chunk state belongs to a world session and is created on the context loop thread.
- Chunk reads use native palette and channel memory and do not allocate.
- A chunk has one `JobData` access declaration. Background consumers use `ChunkSnapshot`, which copies the chunk and requested borders.
- Palette compaction is explicit and can be scheduled as background work by a later consumer.
- A terrain grid loads its chunks through tickets (`GridResidency.Tickets`): a chunk outside every ticket is not loaded, and queries report it as such, never as air. Other grids keep every chunk they have written (`GridResidency.Kept`), and an absent chunk is air. `GridOptions.Residency` chooses otherwise.
- `IChunkProvider.Produce` runs in a background job and is checked as hot path code: it reads nothing that the loop thread changes and does not allocate. A chunk that no provider accepts is air.
- Linked tickets keep a multi-block whole: while the chunk of its origin is held by a ticket, the other chunks of its footprint are held loaded. A chunk held only by a linked ticket does not hold others.
- A tick allocates nothing once the chunks are loaded; loading and unloading do.

## Memory notes

The native palette payload is 0 bytes for a uniform chunk, 320 bytes for a
4-bit chunk with 16 entries, and 67,584 bytes for a 16-bit chunk with 257
entries. The 4-bit payload is 576,000 bytes (562.5 KiB) for 1,800 chunks.
These figures exclude managed object and dictionary overhead; the chunk mix and
metadata determine the final client footprint.

## Known limitations

- Model shapes use a full unit-block collision fallback; a renderer supplies the visual mesh separately.
- Chunk serialization is not implemented yet: an unloaded chunk loses its changes, its scheduled ticks, and its block data; its column of the height map is released once none of its chunks is in memory.
- Each context has its own providers; no provider reads the network yet.
- A batch that writes in a chunk that a provider is producing waits until the chunk is loaded, then applies over the generated blocks; batches submitted after it may apply first. A batch that writes in a chunk that nothing loads creates it empty, and the chunk is unloaded later if no ticket holds it.
- The freeze of a dimension is one flag shared with other modules: nVoxel unfreezes only a dimension it froze itself, but when another module also freezes a dimension that nVoxel froze, nVoxel still unfreezes it once a chunk becomes active; and a dimension without an active chunk that another module unfreezes is frozen again at the next tick while `voxel.freezeInactiveDimensions` is `true`.
- A box or sphere query visits every cell of the box around the region in each grid: keep queries small.
- Random ticks draw cells uniformly: a block of a subscribed type is ticked about `voxel.randomTicksPerChunk` / 32,768 times per tick, whatever the other blocks of its chunk.
- Grid transforms are updated explicitly; nPhysics is not yet the movement authority.
- An unbounded grid is always a candidate in the spatial index; finite grids use transformed axis-aligned bounds.
