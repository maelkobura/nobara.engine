# nVoxel

nVoxel stores voxel worlds in dimensions, ECS-backed grids, resident 32³ chunks, compact palettes, and extensible block metadata. Its client extension nVoxel.Renderer meshes the chunks into the chunk geometry of [nRenderer](../nRenderer/README.md).

The reasons for the design of the meshing are in [the meshing of chunks](../../docs/explanation/chunk-meshing.md).

## Purpose

nVoxel owns the dimensions of a context and stores the terrain and other voxel grids of each world session. It also applies block batches at the World tick, expands multiblocks, reports chunk notifications, and exposes split/merge operations. It loads chunks through tickets and a chain of chunk providers, runs scheduled and random block ticks, keeps height maps, and answers spatial queries. It remains independent of rendering, meshing, and persistence.

nVoxel.Renderer, on the client, turns the blocks of every grid into chunk geometry: cubes merged face by face with hidden faces removed, models placed and turned with their block, shapes of other modules through their meshers, and ambient occlusion and smooth light at each vertex. It remeshes chunks in background jobs, nearest the camera first, when their blocks change, when their light changes, or when they load. With [nLighting](../nLighting/README.md), it also gives each grid that is not the terrain the light of the terrain around it.

nVoxel.Sound, on the client, plays the sounds of blocks that a player or a
module places and breaks. It reads the sounds that blocks declare and starts
them with [nAudio](../nAudio/README.md) at the position of the block, in the
frame of its grid. Neither nVoxel nor nAudio knows the other: the extension
links them.

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

nVoxel.Sound:

<!-- nobara:generated:begin level nobara.voxel.sound -->
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
| `nobara.lighting` | `^1.0` | Optional |
<!-- nobara:generated:end -->

nVoxel.Renderer reads the blocks of nBlocks and nVoxel, and gives its geometry
to nRenderer. nLighting is optional: when the distribution has it, the
vertices carry its light; without it, chunks are meshed at full light, with
ambient occlusion only, and the image is the same as before nLighting
existed. The code that uses nLighting is confined to one class, so the module
loads without it.

nVoxel.Sound:

<!-- nobara:generated:begin dependencies nobara.voxel.sound -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.audio` | `^1.0` | Required |
| `nobara.blocks` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |
<!-- nobara:generated:end -->

nVoxel.Sound reads the blocks of nBlocks and their hooks, the grids of
nVoxel, and plays through nAudio. A distribution without it has no sound of
blocks, and no module fails for that.

## Entry points

| Member | Use |
| --- | --- |
| `IVoxelWorlds` | Dimensions, terrain grids, grid creation and destruction, the spatial index, and the job data of the blocks of every chunk (`ChunkAccess`). |
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
| `IChunkLoading` | Chunk tickets, area and viewer tickets (`AreaTickets` lists the area tickets in effect), the chunk provider chain, residency subscriptions, and chunk status. |
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

nVoxel.Sound:

| Member | Use |
| --- | --- |
| `VoxelSoundModule` | The identifier of the module. It has no service: it works from what blocks declare. |
| `BlockSoundException` | Why the sounds of blocks cannot be used: a property names a sound event that no module provides. |

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
before the session ends, in a `SessionEnding` handler. `IChunkLoading.AreaTickets`
lists the area tickets in effect, those of viewers included, as read-only views, for modules that
favor the places around them, such as nLighting.

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
With nLighting, it also listens to the chunks whose light changed
(`ILighting.SubscribeChanges`), after each tick, and marks the chunk, and its
26 neighbors when the change touches its border. A change of light alone is
not waited for: its remesh comes after those of the changes made by a player.
During the extraction of nRenderer, at every frame, it applies the remeshes
that finished, then starts the next ones, chunks changed by a player first,
then the nearest the camera, at most `IVoxelMeshing.MaxRunning` at once. A
chunk has at most one remesh at a time, and is remeshed at most once per
frame, however many batches changed it. A remesh whose chunk changed while it
ran is discarded, and the chunk is remeshed again; a change of its light
discards it the same way. A change made by an action batch shows at the next
frame, with the light that the propagation computed in the same tick: its
remesh is waited for if needed. Light that spreads over more ticks, within
the budget of nLighting, reaches the image as its chunks are remeshed.

With nLighting, at every extraction, nVoxel.Renderer reads the terrain light
sample of each grid that is not the terrain (`ILighting.TryGetTerrainSample`)
and, when its version changed, gives it to the group of the grid
(`ChunkGeometry.SetGroupLight`): a ship that enters a cave grows darker, at
the next frame, without a remesh.

During `SessionStarting`, nVoxel.Renderer adds the loading task "meshed
chunks of the simulation area": it completes once the loading task of nVoxel
completed and every chunk held by an active ticket is meshed. At
`SessionEnded`, it waits for its remeshes and forgets its chunks; their
sections leave the chunk geometry at the next frame. Its world state counter,
"meshed chunks", counts the chunks it follows and its remeshes.

nVoxel.Sound subscribes to the nBlocks hooks `Placed` and `Broken`, after
a batch is applied, and only for the types of blocks that have a sound in at
least one state: a batch without such a block costs nothing. The guide
[Give sounds to a block](../../docs/guides/give-sounds-to-a-block.md) walks
through it.

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

nVoxel.Sound:

<!-- nobara:generated:begin configuration nobara.voxel.sound -->
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

nVoxel.Sound reads two free properties of the `Block` resources, which it
declares, variants applied, when it initializes:

| Property | Type | Effect |
| --- | --- | --- |
| `PlaceSound` | identifier of a `Sound` | The sound of the block when it is placed. |
| `BreakSound` | identifier of a `Sound` | The sound of the block when it is broken. |

```xml
<Block>
  <Name>Stone</Name>
  <Content><Shape>shape:nobara.cube</Shape></Content>
  <Properties>
    <PlaceSound>sound:example.world.stoneplace</PlaceSound>
    <BreakSound>sound:example.world.stonebreak</BreakSound>
  </Properties>
</Block>
```

A property absent from a block, or from one of its states, is silence. A
variant of a state may override it, and so may the qualified form
`<VoxelSound.BreakSound>`, which only nVoxel.Sound reads. A sound that no
module provides stops the start with a `BlockSoundException` that names the
file and the line of the property.

The sound is the one of the state that was placed, or of the state that was
broken. It plays on the bus of its event, usually
`bus:nobara.audio.effects.blocks`, at the middle of the cell, spatialized
by the settings of the event. A block of several cells makes one sound, at
its origin. On a grid other than the terrain, the sound is attached to the
grid at the local position of the block, so it follows the grid when it
moves and turns.

A batch of many blocks does not make as many sounds. In one batch, the blocks
of one type that have the same sound event and stand in the same zone of 4 × 4 × 4 blocks
of a grid make one sound, at the middle of their cells, and the blocks of
all the batches of a tick start at most 16 sounds. The limits of instances of
nAudio apply on top of that.

## Constraints

nVoxel:

- Grid and chunk state belongs to a world session and is created on the context loop thread.
- Chunk reads use native palette and channel memory and do not allocate.
- The chunks of a context share one `JobData`, `IVoxelWorlds.ChunkAccess`:
  nVoxel writes it at the `World` stage, and a tick system or a job that
  reads blocks at a later stage declares it as read. Background consumers
  use `ChunkSnapshot`, which copies the chunk and requested borders.
- Palette compaction is explicit and can be scheduled as background work by a later consumer.
- A terrain grid loads its chunks through tickets (`GridResidency.Tickets`): a chunk outside every ticket is not loaded, and queries report it as such, never as air. Other grids keep every chunk they have written (`GridResidency.Kept`), and an absent chunk is air. `GridOptions.Residency` chooses otherwise.
- `IChunkProvider.Produce` runs in a background job and is checked as hot path code: it reads nothing that the loop thread changes and does not allocate. A chunk that no provider accepts is air.
- Linked tickets keep a multi-block whole: while the chunk of its origin is held by a ticket, the other chunks of its footprint are held loaded. A chunk held only by a linked ticket does not hold others.
- A tick allocates nothing once the chunks are loaded; loading and unloading do.

nVoxel.Renderer:

- Client only; use `IVoxelMeshing` on the loop thread of the client context.
- Remeshes run in background jobs on a frozen copy of the chunk and of a border of one cell from its 26 neighbors, made on the loop thread during extraction, and, with nLighting, on a frozen copy of its light over the same cells (`ILighting.CopyFrozen`), taken at the same time; the result returns to the loop thread. A mesher reads nothing else, and is checked as hot path code: the mesher allocates nothing once its scratch memory has grown to the largest chunk, and a frame allocates nothing once the world is loaded, lit, and meshed.
- Smooth light: each corner of a face of a cube takes the average of the light of the cells that its ambient occlusion reads (the cell in front of the face, the two beside the corner, and the one in the corner, which is left out when both sides are closed), leaving out the blocks that hide all their faces, as they hold no light. It is rounded to the half level, channel by channel: sky, then red, green, and blue of blocks, equal without color. Faces merge only when their light is the same at their four corners. A model, and a block of another shape, takes the light of its own cell at every vertex.
- A chunk that nLighting does not hold yet is meshed at full light; the change of light that follows meshes it again.
- A neighboring chunk that is not loaded hides the faces next to it, in a grid that loads its chunks with tickets; in other grids, an absent chunk is air.
- Meshers are registered before nVoxel.Renderer starts. The visual of every state is built once, when it starts.

nVoxel.Sound:

- Client only; the hooks run on the loop thread of the client context, which is where nAudio is called.
- The sound of every state is resolved once, when the module initializes: a hook reads a table, and a hot reload of the properties of blocks is not applied until a restart.
- A batch makes no allocation once its sounds have played once: the grouping reuses its tables, and a grid gets its sound anchor at its first sound.

## Memory notes

The native palette payload is 0 bytes for a uniform chunk, 320 bytes for a
4-bit chunk with 16 entries, and 67,584 bytes for a 16-bit chunk with 257
entries. The 4-bit payload is 576,000 bytes (562.5 KiB) for 1,800 chunks.
These figures exclude managed object and dictionary overhead; the chunk mix and
metadata determine the final client footprint.

A remesh of nVoxel.Renderer keeps a frozen copy of 34³ states (154 KiB) and
its geometry, which grows to the largest chunk it meshed and is kept; there
are at most `IVoxelMeshing.MaxRunning` of them, twice the number of workers
by default. Each also keeps the light of the cells of its copy (76 KiB), and,
with nLighting, the frozen copy of the light that nLighting fills (76 KiB).
The tables of the visual hold about 100 bytes per block state.

A remesh of a chunk of surface (hills of dirt on stone, 4,488 triangles) took
1.1 to 1.2 ms in a release build, with its code compiled, and 1.5 ms with
smooth light from a light that varies across the chunk.

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

- Without nLighting in the distribution, every vertex is at full light, with ambient occlusion only.
- A change of light remeshes the whole chunk, and its 26 neighbors when it touches its border, even when only a few cells changed. While a large change of light spreads over several ticks, the chunks it crosses are remeshed at each tick, and the remeshes still running are discarded.
- Models and the shapes of other modules are lit evenly by their cell, without smooth light; a mesher cannot give a vertex its own light.
- A cell of a neighboring chunk that is not loaded reads as full sky without light of blocks, as nLighting copies it.
- Dynamic lights are not drawn.
- The light of the terrain around a grid applies at once when it changes, without interpolation.
- Models do not hide the faces of their neighbors and cast no ambient occlusion. A model is read when the module starts: a mesh changed by a hot reload is meshed again only after a restart.
- A multi-block whose origin is in a chunk that is not loaded is not drawn.
- When a chunk loads, its loaded neighbors are remeshed, so the chunks of an area that loads are remeshed several times.
- Ambient occlusion is cast by blocks that hide all their faces; quads whose corners differ in occlusion or in light are not merged.
- The turn of the texture of a face applies to cubes: the mesher of another shape reads the texture of each face (`BlockVisual.TryGetTexture`), not its turn.

nVoxel.Sound:

- Only placing and breaking make sounds. Steps, falls, and hits are not played, and a block has no persistent sound, such as a machine that hums.
- The client plays the sound of the blocks of its own world and nothing is sent by a server.
- The sounds of a mass batch are the first 16 zones of the tick in the order of the batch, not the nearest to the listener.
- The anchors of destroyed grids stay in memory, one small object each.
- A sound attached to a grid stops when the grid is destroyed, as a merge does right after its removal batch.
- A block that changes state in a placing batch, such as a door that opens, makes the place sound of its new state.
