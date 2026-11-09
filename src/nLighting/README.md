# nLighting

nLighting computes the light of voxel grids: a sky channel and a block
channel per block, the blocks that emit light and the blocks that filter it,
and the propagation of block light from block to block.

The reasons for the design are in [voxel lighting](../../docs/explanation/voxel-lighting.md).

## Purpose

nLighting keeps the light of every loaded chunk of every grid of a context,
reads the sources and filters of the blocks from their free properties, and
spreads the light of the sources after the blocks change, in jobs, at the
`Late` stage of the tick. It tells its subscribers which chunks changed, and
gives frozen copies of the light of a chunk that a job may read, such as a
mesher.

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
| `nobara.game` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |
<!-- nobara:generated:end -->

## Entry points

| Member | Use |
| --- | --- |
| `ILighting` | Whether the block channel has color, the chunks whose light changed (`SubscribeChanges`), frozen copies of the light of a chunk (`CopyFrozen`), and statistics. |
| `ILightSources` | The states that emit light, and the emission, the filter, and the open faces of each state. |
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

```csharp
public void Initialize(ModuleContext context)
{
    ILighting lighting = context.Resolve<ILighting>();
    _changes = lighting.SubscribeChanges(context, change => _dirty.Add((change.Grid, change.Chunk)));
}
```

nLighting joins the `Late` stage with the tick system
`nobara.lighting.propagation`. A module whose changes to blocks the light
must follow in the same tick, such as structural integrity, orders its system
before it with `Before = ["nobara.lighting.propagation"]`.

## Events

nLighting listens to the notifications of nVoxel batches and to the chunks
that enter and leave memory. On the loop thread, as they come:

- a block whose emission, filter, or closed faces change loses its light,
  which then leaves the blocks it reached, and its emission and its neighbors
  spread again;
- a chunk that loads takes the light of its sources and the light that waits
  on the faces of its loaded neighbors;
- a chunk that leaves memory takes back from its neighbors the light that
  came from it, since it may come back different.

The propagation itself runs in the system `nobara.lighting.propagation`, once
per tick, whatever the number of batches: a batch of thousands of blocks
gives one pass. After the pass, `SubscribeChanges` handlers are called once
per chunk whose light changed, on the loop thread, synchronously.

During a world session, the world state counter "light chunks" counts the
chunks that have light. nLighting releases them all when the session ends.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `lighting.color` | distribution | bool | `false` |  | both |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `lighting.color` | When `true`, the block channel has a red, a green, and a blue component, and a filter can let some of them through: 2 bytes per block. When `false`, it has one level: 1 byte per block. Read when the context starts. |

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
its smallest one. A value that is not a level from 0 to 15, or three of them,
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
- The values are integers: two contexts that apply the same batches hold the
  same light, byte for byte.
- The light of a chunk is one value while every block holds it, and an array
  in native memory otherwise; the array is released when the light becomes
  uniform again, at the end of a pass.
- The propagation runs in jobs, one chunk per index; the jobs declare the
  blocks of nVoxel (`IVoxelWorlds.ChunkAccess`) as read and the light as
  written. A pass, and the system when nothing changed, allocate nothing once
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
25 to 35 ms in a release build, on a machine with 12 hardware threads, and
the pass that removes them about as long. A pass after one source placed or
removed in a chunk took about 1 ms.

## Known limitations

- The sky channel holds full sky (15) in every block: its propagation from
  the height map is not computed yet, and the day-night cycle does not apply
  to it.
- The registry holds the blocks that emit light only: moving sources, such as
  an entity that carries a torch, do not exist yet. There is no query of the
  light at a point for gameplay modules.
- A pass spreads every change of its tick at once, however large: there is no
  budget per tick and no priority by distance yet.
- Each grid has its own light, in its own frame: a grid receives no light
  from the terrain or from other grids, and gives none.
- In a grid that keeps its chunks, a chunk that does not exist holds no
  light: light stops at the border of the chunks that exist.
- The light is not drawn yet: nVoxel.Renderer meshes chunks at full light.
