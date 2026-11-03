# nVoxel

nVoxel stores voxel worlds in dimensions, ECS-backed grids, resident 32³ chunks, compact palettes, and extensible block metadata.

## Purpose

nVoxel owns the dimensions of a context and stores the terrain and other voxel grids of each world session. It is independent of rendering, meshing, persistence, and block modification batches.

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
<!-- nobara:generated:end -->

## Entry points

| Member | Use |
| --- | --- |
| `IVoxelWorlds` | Dimensions, terrain grids, grid creation and destruction, and the spatial index. |
| `BlockCoordinate`, `ChunkCoordinate`, `LocalBlockCoordinate`, `VoxelCoordinates` | Overflow-checked grid coordinates and 32³ chunk indexing. |
| `Grid`, `GridStorage`, `GridBounds` | ECS-backed grids and their resident chunk tables. |
| `Chunk`, `ChunkSnapshot`, `Palette` | Palette storage, versions, frozen copies, and compact native memory. |
| `DenseChannelDefinition`, `BlockDataType<T>` | Lazy dense channels and serializable block data declarations. |

Resolve the service with `context.Resolve<IVoxelWorlds>()`. Every context has its own dimensions, entity worlds, grids, chunks, and spatial index.

## Events

None. Session lifecycle is followed through `IWorldSessions`; block change notifications are part of a later batch API.

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

- Block modification batches, notifications, chunk loading, block ticks, height maps, shapes, and chunk serialization are not implemented yet.
- Grid transforms are updated explicitly; nPhysics is not yet the movement authority.
- An unbounded grid is always a candidate in the spatial index; finite grids use transformed axis-aligned bounds.
