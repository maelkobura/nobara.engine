# nVoxel

nVoxel stores voxel worlds in dimensions, ECS-backed grids, resident 32³ chunks, compact palettes, and extensible block metadata.

## Purpose

nVoxel owns the dimensions of a context and stores the terrain and other voxel grids of each world session. It also applies block batches at the World tick, expands multiblocks, reports chunk notifications, and exposes split/merge operations. It remains independent of rendering, meshing, and persistence.

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

Resolve the service with `context.Resolve<IVoxelWorlds>()`. Every context has its own dimensions, entity worlds, grids, chunks, and spatial index.

## Events

`IVoxelBatches` applies submitted batches in the `World` stage of the next
simulation tick. Each touched chunk produces one notification for that batch;
subscribers may filter by block type, tag, or block-data type. Direct neighbors
are notified on the following tick, so a neighbor handler that submits another
batch cannot cascade inside the current application pass. Action batches fire
the nBlocks `Placing`, `Breaking`, `Placed`, `Broken`, and `Interact` hooks;
technical batches used by generation, split, and merge do not fire action hooks.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

## Resources

None. Block state indices are supplied by nBlocks and are never persisted by nVoxel in this version.

## Constraints

- Grid and chunk state belongs to a world session and is created on the context loop thread.
- Chunk reads use native palette and channel memory and do not allocate.
- A chunk has one `JobData` access declaration. Background consumers use `ChunkSnapshot`, which copies the chunk and requested borders.
- Palette compaction is explicit and can be scheduled as background work by a later consumer.

## Memory notes

The native palette payload is 0 bytes for a uniform chunk, 320 bytes for a
4-bit chunk with 16 entries, and 67,584 bytes for a 16-bit chunk with 257
entries. The 4-bit payload is 576,000 bytes (562.5 KiB) for 1,800 chunks.
These figures exclude managed object and dictionary overhead; the chunk mix and
metadata determine the final client footprint.

## Known limitations

- Model shapes use a full unit-block collision fallback; a renderer supplies the visual mesh separately.
- Chunk loading, block ticks, height maps, spatial block queries, and chunk serialization are not implemented yet.
- Grid transforms are updated explicitly; nPhysics is not yet the movement authority.
- An unbounded grid is always a candidate in the spatial index; finite grids use transformed axis-aligned bounds.
