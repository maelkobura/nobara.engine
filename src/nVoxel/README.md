# nVoxel

nVoxel stores voxel worlds in dimensions, ECS-backed grids, resident 32³ chunks, compact palettes, and extensible block metadata. Its client extension nVoxel.Renderer meshes the chunks into the chunk geometry of [nRenderer](../nRenderer/README.md).

The reasons for the design of the meshing are in [the meshing of chunks](../../docs/explanation/chunk-meshing.md).

## Purpose

nVoxel owns the dimensions of a context and stores the terrain and other voxel grids of each world session. It also applies block batches at the World tick, expands multiblocks, reports chunk notifications, and exposes split/merge operations. It loads chunks through tickets and a chain of chunk providers, runs scheduled and random block ticks, keeps height maps, and answers spatial queries. It remains independent of rendering, meshing, and persistence.

nVoxel.Renderer, on the client, turns the blocks of every grid into chunk geometry: cubes merged face by face with hidden faces removed, models placed and turned with their block, shapes of other modules through their meshers, and ambient occlusion at each vertex. It remeshes chunks in background jobs, nearest the camera first, when their blocks change or they load.

## Status

`experimental`

## Level

nVoxel:

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server.
<!-- nobara:generated:end -->

nVoxel.Renderer:

<!-- nobara:generated:begin level nobara.voxel.renderer -->
Platform module, on the client.
<!-- nobara:generated:end -->

## Dependencies

nVoxel:

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.blocks` | `^1.0` | Required |
| `nobara.context` | `^1.0` | Required |
| `nobara.entity` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
| `nobara.items` | `^1.0` | Required |
<!-- nobara:generated:end -->

nVoxel.Renderer:

<!-- nobara:generated:begin dependencies nobara.voxel.renderer -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.blocks` | `^1.0` | Required |
| `nobara.context` | `^1.0` | Required |
| `nobara.renderer` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |
<!-- nobara:generated:end -->

nVoxel.Renderer reads the blocks of nBlocks and nVoxel, and gives its geometry
to nRenderer. It declares no dependency toward a lighting module: until one
exists, chunks are meshed at full light.

## Entry points

| Member | Use |
| --- | --- |
| `IVoxelWorlds` | Dimensions, terrain grids, grid creation and destruction, and the spatial index. |
| `BlockCoordinate`, `ChunkCoordinate`, `LocalBlockCoordinate`, `VoxelCoordinates` | Overflow-checked grid coordinates and 32³ chunk indexing. |
| `Grid`, `GridStorage`, `GridBounds` | ECS-backed grids and their resident chunk tables. |
| `Chunk`, `ChunkSnapshot`, `Palette` | Palette storage, versions, frozen copies, and compact native memory. |
| `BlockDataView` | The block data of a cell, read live (`Chunk.GetBlockDataView`) or from a frozen copy that a job may read (`Chunk.CopyBlockData`). |
| `DenseChannelDefinition`, `BlockDataType<T>` | Lazy dense channels and serializable block data declarations. |
| `IShapeRegistry`, `IBlockShape`, `ShapeEvaluation` | Shape registration, cached oriented collision, face opacity, contact masks, instance-dependent queries, and the rotation of the orientation of a state (`GetOrientation`). |
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

nVoxel.Renderer:

| Member | Use |
| --- | --- |
| `IVoxelMeshing` | The meshing of chunks: the registration of meshers, the visual of each state, whether a chunk is meshed, and statistics. |
| `IBlockMesher`, `BlockMeshContext`, `BlockMeshWriter` | The mesher of a shape added by a module: it receives a block of the frozen copy of its chunk, with its neighbors and its block data, and writes vertices and triangles. |
| `BlockVisual` | The visual of a state: the texture of each face after its orientation, its tint, its category, and its orientation. |
| `VoxelMeshingStatistics` | The chunks followed, waiting, and running, the sections, and the remeshes started, applied, and discarded. |
| `BlockVisualException` | A visual that cannot be used, such as a texture that no module provides. |
| `VoxelRendererModule` | The identifier of the module. |

A module that adds a shape to nVoxel registers its mesher while it registers
or initializes, from its client side:

```csharp
public void Initialize(ModuleContext context)
{
    context.Resolve<IVoxelMeshing>().RegisterMesher(context, "shape:example.sculpted", new SculptedMesher(_carving));
}
```

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

nVoxel.Renderer listens to the notifications of batches and to the chunks
that load and unload, and marks the chunks to remesh: the chunk itself, and
its neighbors when a change touches its border or when a neighbor loads.
During the extraction of nRenderer, at every frame, it applies the remeshes
that finished, then starts the next ones, chunks changed by a player first,
then the nearest the camera, at most `IVoxelMeshing.MaxRunning` at once. A
chunk has at most one remesh at a time, and is remeshed at most once per
frame, however many batches changed it. A remesh whose chunk changed while it
ran is discarded, and the chunk is remeshed again. A change made by an action
batch shows at the next frame: its remesh is waited for if needed.

During `SessionStarting`, nVoxel.Renderer adds the loading task "meshed
chunks of the simulation area": it completes once the loading task of nVoxel
completed and every chunk held by an active ticket is meshed. At
`SessionEnded`, it waits for its remeshes and forgets its chunks; their
sections leave the chunk geometry at the next frame. Its world state counter,
"meshed chunks", counts the chunks it follows and its remeshes.

## Configuration

nVoxel:

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

nVoxel.Renderer:

<!-- nobara:generated:begin configuration nobara.voxel.renderer -->
None.
<!-- nobara:generated:end -->

## Resources

nVoxel: none. Block state indices are supplied by nBlocks and are never persisted by nVoxel in this version.

nVoxel.Renderer reads free properties of the `Block` resources, which it
declares, variants applied, when it starts:

| Property | Type | Effect |
| --- | --- | --- |
| `RenderTexture` | resource identifier of a `Texture` | The texture of every face of the block. |
| `RenderTextureSide`, `RenderTextureTop`, `RenderTextureBottom` | the same | The texture of the four sides, of the top, or of the bottom, over `RenderTexture`. |
| `RenderTextureNorth`, `RenderTextureEast`, `RenderTextureSouth`, `RenderTextureWest` | the same | The texture of one side, over `RenderTextureSide`. |
| `RenderModel` | resource identifier of a `Mesh` | The model of a block of the shape `shape:nobara.model`. |
| `RenderTint` | `#RRGGBB`, in sRGB | A color that multiplies the textures of the block; white without it. |

Each property of a texture takes an optional attribute `turn`, `0`, `90`,
`180`, or `270`: the texture is turned by that many degrees in the face,
clockwise seen from the front of the face, such as
`<RenderTextureEast turn="90">`. On a property that several faces use, it
turns the texture of each of them.

```xml
<Block>
  <Name>Grass</Name>
  <Content><Shape>shape:nobara.cube</Shape></Content>
  <Properties>
    <RenderTexture>texture://Example/Blocks/Dirt</RenderTexture>
    <RenderTextureTop>texture://Example/Blocks/GrassTop</RenderTextureTop>
    <RenderTint>#7CBD6B</RenderTint>
  </Properties>
</Block>
```

Faces are named as the block is drawn in its reference orientation, north
toward -Z: an oriented state turns them with it, each texture turned in its
face as the block turns. A block with `facing` orientations, such as a log,
lies along Z in its reference orientation: give its east face its side
texture with `turn="90"` and its west face with `turn="270"`, so that the
bark runs along the log in every orientation, and stands upright on every
side of a standing log. The textures are those of
nRenderer, in its texture arrays, one per size and settings: a block may mix
textures of several sizes, each face in its own array. A model is placed with
its origin at the center of the bottom of its cell, then turned with the
orientation of the state around the center of the cell; a multi-block draws
its model from its origin cell only. Each part of a model takes the `Albedo`
texture of the material of its slot, or else the `RenderTexture` of the
block. The category of the faces comes from the `Opacity` of the state:
`opaque`, `cutout`, or `translucent`. A texture or a mesh that no module
provides, a tint that is not a color, or more than 127 tints stop the start
with a `BlockVisualException`; a cube without texture, a model without
`RenderModel`, and a shape without mesher are drawn by nothing, with a
warning.

## Constraints

nVoxel:

- Grid and chunk state belongs to a world session and is created on the context loop thread.
- Chunk reads use native palette and channel memory and do not allocate.
- A chunk has one `JobData` access declaration. Background consumers use `ChunkSnapshot`, which copies the chunk and requested borders.
- Palette compaction is explicit and can be scheduled as background work by a later consumer.
- A terrain grid loads its chunks through tickets (`GridResidency.Tickets`): a chunk outside every ticket is not loaded, and queries report it as such, never as air. Other grids keep every chunk they have written (`GridResidency.Kept`), and an absent chunk is air. `GridOptions.Residency` chooses otherwise.
- `IChunkProvider.Produce` runs in a background job and is checked as hot path code: it reads nothing that the loop thread changes and does not allocate. A chunk that no provider accepts is air.
- Linked tickets keep a multi-block whole: while the chunk of its origin is held by a ticket, the other chunks of its footprint are held loaded. A chunk held only by a linked ticket does not hold others.
- A tick allocates nothing once the chunks are loaded; loading and unloading do.

nVoxel.Renderer:

- Client only; use `IVoxelMeshing` on the loop thread of the client context.
- Remeshes run in background jobs on a frozen copy of the chunk and of a border of one cell from its 26 neighbors, made on the loop thread during extraction; the result returns to the loop thread. A mesher reads nothing else, and is checked as hot path code: the mesher allocates nothing once its scratch memory has grown to the largest chunk.
- A neighboring chunk that is not loaded hides the faces next to it, in a grid that loads its chunks with tickets; in other grids, an absent chunk is air.
- Meshers are registered before nVoxel.Renderer starts. The visual of every state is built once, when it starts.

## Memory notes

The native palette payload is 0 bytes for a uniform chunk, 320 bytes for a
4-bit chunk with 16 entries, and 67,584 bytes for a 16-bit chunk with 257
entries. The 4-bit payload is 576,000 bytes (562.5 KiB) for 1,800 chunks.
These figures exclude managed object and dictionary overhead; the chunk mix and
metadata determine the final client footprint.

A remesh of nVoxel.Renderer keeps a frozen copy of 34³ states (154 KiB) and
its geometry, which grows to the largest chunk it meshed and is kept; there
are at most `IVoxelMeshing.MaxRunning` of them, twice the number of workers
by default. The tables of the visual hold about 100 bytes per block state.

## Known limitations

nVoxel:

- Model shapes use a full unit-block collision fallback; a renderer supplies the visual mesh separately.
- Chunk serialization is not implemented yet: an unloaded chunk loses its changes, its scheduled ticks, and its block data; its column of the height map is released once none of its chunks is in memory.
- Each context has its own providers; no provider reads the network yet.
- A batch that writes in a chunk that a provider is producing waits until the chunk is loaded, then applies over the generated blocks; batches submitted after it may apply first. A batch that writes in a chunk that nothing loads creates it empty, and the chunk is unloaded later if no ticket holds it.
- The freeze of a dimension is one flag shared with other modules: nVoxel unfreezes only a dimension it froze itself, but when another module also freezes a dimension that nVoxel froze, nVoxel still unfreezes it once a chunk becomes active; and a dimension without an active chunk that another module unfreezes is frozen again at the next tick while `voxel.freezeInactiveDimensions` is `true`.
- A box or sphere query visits every cell of the box around the region in each grid: keep queries small.
- Random ticks draw cells uniformly: a block of a subscribed type is ticked about `voxel.randomTicksPerChunk` / 32,768 times per tick, whatever the other blocks of its chunk.
- Grid transforms are updated explicitly; nPhysics is not yet the movement authority.
- An unbounded grid is always a candidate in the spatial index; finite grids use transformed axis-aligned bounds.

nVoxel.Renderer:

- No light is computed: every vertex is at full light, with ambient occlusion only.
- Models do not hide the faces of their neighbors and cast no ambient occlusion. A model is read when the module starts: a mesh changed by a hot reload is meshed again only after a restart.
- A multi-block whose origin is in a chunk that is not loaded is not drawn.
- When a chunk loads, its loaded neighbors are remeshed, so the chunks of an area that loads are remeshed several times.
- Ambient occlusion is cast by blocks that hide all their faces; quads whose corners differ in occlusion are not merged.
- The turn of the texture of a face applies to cubes: the mesher of another shape reads the texture of each face (`BlockVisual.TryGetTexture`), not its turn.
