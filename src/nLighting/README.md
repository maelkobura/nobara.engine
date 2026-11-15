# nLighting

nLighting computes the light of voxel grids: a sky channel and a block
channel per block, the blocks that emit light and the blocks that filter it,
the propagation of both channels from block to block within a budget per
tick, the light of the terrain around the other grids, and the brightness
query that gameplay modules read, dynamic lights included.

The reasons for the design are in [voxel lighting](../../docs/explanation/voxel-lighting.md).

## Purpose

nLighting keeps the light of every loaded chunk of every grid of a context,
reads the sources and filters of the blocks from their free properties, takes
the sky from the height maps of nVoxel, and spreads the light after the
blocks change, in jobs, at the `Late` stage of the tick, within a budget of
operations per tick, nearest the points of interest first. It tells its
subscribers which chunks changed, gives frozen copies of the light of a chunk
that a job may read, such as a mesher, samples the light of the terrain
around each other grid for the renderer, and answers the brightness query.
On the client, [nVoxel.Renderer](../nVoxel/README.md) draws this light.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server.
<!-- nobara:generated:end -->

The light is only useful on a server when it changes the game, such as the
blocks where creatures appear. A distribution that does not need it there
disables `nobara.lighting` for its server target in `build.nobara`; a module
that needs the light on the server depends on `nobara.lighting`, and the build
of that target then fails with a missing required dependency.

## Dependencies

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.blocks` | `^1.0` | Required |
| `nobara.context` | `^1.0` | Required |
| `nobara.entity` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |
| `nobara.debug` | `^1.0` | Optional |
<!-- nobara:generated:end -->

## Entry points

| Member | Use |
| --- | --- |
| `ILighting` | Whether the block channel has color, the chunks whose light changed (`SubscribeChanges`), frozen copies of the light of a chunk (`CopyFrozen`), the brightness query (`TryGetBrightness`), the terrain light sample of a grid (`TryGetTerrainSample`), the points of interest (`AddFocus`), the job data of the light (`LightAccess`), statistics, and the work left (`Backlog`). |
| `ILightSources` | The states that emit light, and the emission, the filter, and the open faces of each state; the component of the dynamic lights, and the dynamic lights of a dimension. |
| `Brightness` | The light at a block, channel by channel: sky, block, and dynamic. |
| `DynamicLight`, `DynamicLightSource` | The component `nobara.lighting.dynamic_light` (a color and a radius), and a dynamic light as the last tick read it. |
| `TerrainLightSample` | The average light of the terrain around a grid that is not the terrain. |
| `LightFocus` | A point of interest, such as the camera, near which the propagation works first. |
| `LightingBacklog` | The chunks and cells that still have work, the age of the oldest work, and the operations of the last tick. |
| `LightLevel` | The light of one block: the sky channel and the three components of the block channel, from 0 to 15. |
| `LightColor` | A color of light, each component from 0 to 15: an emission or a filter. |
| `LightSnapshot` | The light of a chunk and of the blocks around it, one block deep, copied for a job. |
| `LightChunkChange` | A chunk whose light changed in a pass, and whether a block on its border changed. |
| `LightingStatistics` | The chunks that have light, the memory of their arrays, and the cost of the last pass. |
| `LightingSettings` | The configuration keys. |
| `LightingModule` | The identifier of the module. |

Resolve the services with `context.Resolve<ILighting>()` and
`context.Resolve<ILightSources>()`. Every context computes the light of its
own grids from its own blocks: nothing is shared or sent between contexts.
On a server target that disables nLighting, the services do not exist.

```csharp
public void Initialize(ModuleContext context)
{
    ILighting lighting = context.Resolve<ILighting>();
    _changes = lighting.SubscribeChanges(context, change => _dirty.Add((change.Grid, change.Chunk)));
}
```

nLighting joins the `Late` stage with the tick system
`nobara.lighting.propagation`, after `nobara.entity.transforms`. A module
whose changes to blocks the light must follow in the same tick, such as
structural integrity, orders its system before it with
`Before = ["nobara.lighting.propagation"]`.

### Brightness query

`TryGetBrightness` gives the light at a block of a grid, channel by channel,
without allocating:

```csharp
if (lighting.TryGetBrightness(terrain, block, out Brightness light)
    && light.Sky < 8 && light.Block.Red < 8 && light.Dynamic.Red < 8)
{
    // dark enough for a creature to appear
}
```

- `Sky` is the stored sky channel, from 0 to 15: it is never combined with
  the time of day. A module that needs the real brightness scales it with the
  time of its day and night cycle.
- `Block` is the block channel; without color, its three components are
  equal.
- `Dynamic` is the light of the dynamic lights of the dimension of the grid,
  read at the last tick: each component gives
  `floor(level × (1 − distance / radius))`, measured from the center of the
  block, and the brightest light wins.
- It returns `false` when the chunk of the block has no light: not loaded,
  or no session.
- A tick system that calls it declares `ILighting.LightAccess` in its
  `Reads`.

### Dynamic lights

An entity carries light with the component `nobara.lighting.dynamic_light`
(`ILightSources.DynamicLight`) and its `nobara.entity.transform`:

```csharp
EntityArchetype held = world.GetArchetype(sources.DynamicLight, entities.WorldTransform);
EntityId torch = world.Create(held);
world.Set(torch, sources.DynamicLight, new DynamicLight { Color = new LightColor(14, 11, 6), Radius = 10 });
```

The tick system reads them once per tick; `ILightSources.GetDynamicSources`
gives those of a dimension. A dynamic light is not spread through the
blocks, so moving it costs nothing, and the stored light does not change.

### Grids and the terrain

Each grid has its own light, in its own frame: its sources and its own sky,
above its height map, which a grid with bounds computes on demand. For a
grid that is not the terrain, `TryGetTerrainSample` gives the average light
of the terrain at its center and its eight corners, without the points in a
block closed on six faces or in a chunk that is not loaded. The sample is
taken again at the end of a tick when the grid moved or when the light of
the terrain changed; `Version` grows when a value changes. A grid gives no
light to the terrain.

### Budget and points of interest

A tick does at most `lighting.operationsPerTick` operations of propagation
(cells taken from a queue, and cells whose light starts to change), give or
take one round of its lanes. A larger change takes a few ticks: the light is
late, then exact. Each round takes the chunks nearest a point of interest
first:

- a focus added with `AddFocus` during a world session, such as one that a
  client module moves with the camera; it ends with the session, and no
  module of the engine adds one yet;
- the centers of the area tickets of nVoxel at the `Active` or
  `ReducedActive` level, such as those of the viewers.

`Backlog` tells how late the light is: the chunks and cells that still have
work, the age of the oldest work in ticks, and the operations and time of
the last tick.

### Debug window

With nDebug active, nLighting adds the tool window *Lighting*: the light in
front of the block aimed at by the first focus that has a direction, the
budget used in the last tick, the work left, the chunks held as one value or
as an array, and the memory of the arrays.

## Events

nLighting listens to the notifications of nVoxel batches and to the chunks
that enter and leave memory. On the loop thread, as they come:

- a block whose emission, filter, or closed faces change loses the light
  its sources no longer give (all of it when its faces change), which then
  leaves the blocks it reached, and its sources and its neighbors spread
  again;
- a block that moves the height of its column changes the sky of the blocks
  between the old and the new height, in every loaded chunk of the column;
- a chunk that loads takes the sky above the height of its columns, the
  light of its sources, and the light that waits on the faces of its loaded
  neighbors; when it raises the height of its columns, the chunks below
  lose that sky;
- a chunk that leaves memory takes back from its neighbors the light that
  came from it, since it may come back different.

The propagation itself runs in the system `nobara.lighting.propagation`, once
per tick, whatever the number of batches: a batch of thousands of blocks
gives one pass, which may last a few ticks within the budget. After each
tick, `SubscribeChanges` handlers are called once per chunk whose light
changed in that tick, on the loop thread, synchronously.

During a world session, the world state counter "light chunks" counts the
chunks that have light. nLighting releases them all when the session ends.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `lighting.color` | distribution | bool | `false` |  | both |
| `lighting.operationsPerTick` | distribution | int | `100000` | 1024 to … | both |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `lighting.color` | When `true`, the block channel has a red, a green, and a blue component, and a filter can let some of them through: 2 bytes per block. When `false`, it has one level: 1 byte per block. Read when the context starts. |
| `lighting.operationsPerTick` | The operations of propagation in one tick, from 1,024. The default, 100,000, took about 3.4 ms in a release build on a machine with 12 hardware threads. Read when the context starts. |

## Resources

nLighting reads two free properties of the `Block` resources, which it
declares, variants applied, when it starts:

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `LightEmission` | one level from 0 to 15, or three levels (red, green, blue) | `0` | The light that the block emits. |
| `LightFilter` | the same | `0` | The levels that the block takes from the light that enters it, on top of the level that light loses at each block. |

```xml
<Block>
  <Name>Lantern</Name>
  <Content>
    <Shape>shape:nobara.cube</Shape>
    <States><Bool name="lit" default="false"/></States>
    <Variants>
      <Variant when="lit=true">
        <Properties><LightEmission>15 11 6</LightEmission></Properties>
      </Variant>
    </Variants>
  </Content>
</Block>
<Block>
  <Name>RedGlass</Name>
  <Content><Shape>shape:nobara.cube</Shape><Opacity>translucent</Opacity></Content>
  <Properties><LightFilter>0 15 15</LightFilter></Properties>
</Block>
```

Without color, a source emits its brightest component, and a filter takes
its smallest one. The sky is white light: a filter always takes its smallest
component from the sky channel. A value that is not a level from 0 to 15, or three of them,
stops the start with a `BlockLoadException` that names the block.

Light does not cross a face of a block whose `<Opacity>` is `opaque` and
whose oriented shape covers that face (`IShapeRegistry`): a full cube stops it
on every side, a bottom slab only downward. A block closed on its six faces
lets its own light out, and no light in. The faces of a block of the shape
`shape:nobara.model` are open, as nVoxel.Renderer draws them: its shape is a
box for collision only. The faces of an instance-dependent shape are read
from the block data of each block.

## Constraints

- Use `ILighting` on the loop thread of the context. `LightSnapshot` may be
  read on any thread once filled.
- Space above a column that is not loaded counts as sky: a chunk loaded with
  nothing known above it is lit as in the open until the height of its column
  is known.
- The values are integers: two contexts that apply the same batches hold the
  same light, byte for byte.
- The light of a chunk is one value while every block holds it, and an array
  in native memory otherwise; the array is released when the light becomes
  uniform again, at the end of a pass.
- The propagation runs in jobs, one lane per index, at most one per worker:
  a lane works through its share of the chunks that have work, so a chunk has
  one writer in a round. The jobs declare the blocks of nVoxel
  (`IVoxelWorlds.ChunkAccess`) as read and the light as written. A pass, and the system when nothing changed, allocate nothing once
  their buffers have grown.
- An instance-dependent shape is evaluated in those jobs: its `Evaluate`
  reads only its block data.
- A light front that reaches a chunk that is not loaded stops; it goes on
  when that chunk loads, from the face of the loaded chunk.

## Memory notes

Per chunk, without color: 0 bytes of native memory when the light is
uniform, such as full sky without block light; 32,768 bytes once it varies.
With color: 0 or 65,536 bytes. The managed object of a chunk takes about 150
bytes. A pass takes about 55 KiB of buffers per chunk it works on, from a pool
that keeps them, and about 200 KiB of queues for each worker thread that runs
it, kept once used.

A pass after a batch that places 10,000 light sources across 16 chunks took
40 to 46 ms in a release build, on a machine with 12 hardware threads, and
the pass that removes them 32 to 39 ms, compilation included. A pass after one source placed or
removed in a chunk took about 1 ms.

With the default budget, in the same build, 50,000 light sources across 56
chunks took 16 ticks of at most 3.4 ms each, instead of one tick of 20 ms.
Loading the default view area (1,773 chunks of a rough ground with holes)
took about 590,000 operations, never more than 16,000 in a tick. The heights
of a column of chunks take 4 KiB, once per column that has light.

## Known limitations

- Blocks do not hide a dynamic light from the brightness query: its light
  goes through walls.
- Dynamic lights are not drawn, and a `.nof` entity definition cannot set
  `nobara.lighting.dynamic_light` yet: set it from code.
- A grid receives the light of the terrain only through its terrain light
  sample, and no light from other grids; it gives none.
- No module of the engine runs a day and night cycle: a game sets the day
  and night multiplier of the view of nRenderer (`RenderView.SkyLight`).
- In a grid that keeps its chunks, a chunk that does not exist holds no
  light: light stops at the border of the chunks that exist.
