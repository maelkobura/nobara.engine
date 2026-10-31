# The `Block` resource

A `Block` resource defines a type of block in the block registry of
[nBlocks](../../../src/nBlocks/README.md): its native fields, its state
properties, the cells it occupies, the values of some of its states, and the
item it generates. Its schema is `Schemas/block.proto` of the module
`nobara.blocks`; a module that writes blocks declares a dependency on
`nobara.blocks`. The common structure of a resource is described in [the
`.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Blocks">
  <Block>
    <Name>Lamp</Name>                                 <!-- block:example.blocks.lamp -->
    <Content>
      <Shape>shape:nobara.cube</Shape>
      <Hardness>0.3</Hardness>
      <Orientations>horizontal</Orientations>
      <States>
        <Bool name="lit" default="false"/>
        <Int name="power" min="0" max="3"/>
        <Enum name="half" values="bottom,top" default="bottom"/>
      </States>
      <Variants>
        <Variant when="lit=true" opacity="translucent">
          <Properties><Light>15</Light></Properties>
        </Variant>
      </Variants>
      <CreateItem maxStack="16">
        <DisplayName>block.example.lamp</DisplayName>
      </CreateItem>
    </Content>
    <Properties>
      <Light>0</Light>
    </Properties>
    <Tags>
      <Tag>#example.lights</Tag>
    </Tags>
  </Block>
</Nobara>
```

## Identifier

The object identifier of a block is `block:` followed by the namespace and
the name, in lowercase and separated by dots: `block:example.blocks.lamp`.
Disk and network name a type by this identifier only. nBlocks defines the
air, `block:nobara.air`, which an override cannot change.

A state is written as the identifier of its type followed by the values that
differ from the defaults, or all its values, in brackets:
`block:example.blocks.lamp[lit=true,half=top]`.

## Content

| Element | Required | Content | Default |
| --- | --- | --- | --- |
| `<Shape>` | No | A `shape:` object identifier, such as `shape:nobara.cube`. | `shape:nobara.cube`. |
| `<Collision>` | No | `shape`, the collision of the shape, or `none`. | `shape`. |
| `<Opacity>` | No | `opaque`, `cutout`, or `translucent`. | `opaque`. |
| `<Hardness>` | No | A number of at least 0, or `unbreakable`. | `1`. |
| `<Orientations>` | No | `none`, `horizontal`, `facing`, or `full`. | `none`. |
| `<Footprint>` | No | The other cells of a block of several cells. | One cell. |
| `<States>` | No | The state properties. | None: one state. |
| `<Variants>` | No | Values for the states that match a condition. | None. |
| `<CreateItem>` | No | Generates the item of the block. | No item. |

nBlocks keeps the shape as an identifier; the module that provides shapes
checks it.

### Orientations

`<Orientations>` adds the state property `orientation`:

| Value | Orientations |
| --- | --- |
| `none` | No property. |
| `horizontal` | `north`, `east`, `south`, `west`. |
| `facing` | The four horizontal ones, then `up` and `down`. |
| `full` | The 24 rotations of a cube: each of the six directions of `facing`, then the same rolled by 90, 180, and 270 degrees around the facing axis, such as `north`, `north_90`, `north_180`, `north_270`, `east`, and so on. |

The first orientation, `north`, is the reference: the block as its shape is
drawn. Axes are X toward the east, Y up, and Z toward the south.

### Footprint

A block of several cells, such as a door, lists its other cells relative to
its origin, which is always occupied and is not listed:

```xml
<Footprint>
  <Cell y="1"/>
</Footprint>
```

Each `<Cell>` takes `x`, `y`, and `z`, from -7 to 7, 0 when absent. A cell
appears once, and the footprint spans at most 8 cells along each axis. The
footprint adds the state property `part`, whose values number the cells
from `0`, the origin, in the order written.

### States

`<States>` holds the state properties, each with a `name` made of a
lowercase letter followed by lowercase letters, digits, and underscores.
`orientation` and `part` are reserved.

| Element | Attributes | Values |
| --- | --- | --- |
| `<Bool>` | `name`, `default` (`false` when absent), `rotatesTo`. | `false`, `true`. |
| `<Int>` | `name`, `min` (0 when absent), `max`, `default` (the minimum when absent), `rotatesTo`. | The whole numbers from `min` to `max`. |
| `<Enum>` | `name`, `values` (separated by commas), `default` (the first value when absent), `rotatesTo`, `rotation`. | The names listed, made of lowercase letters, digits, and underscores. |

The states of a type are every combination of the values of its
properties. They are numbered in this order of properties: `orientation`,
`part`, the `<Bool>` properties, the `<Int>` ones, then the `<Enum>` ones,
each kind in the order written; the last property varies fastest.

A type has at most `blocks.maxStatesPerType` states, 1024 unless the
distribution raises it in `engine.nobara`. Keep in states only what is
discrete and changes the look or the rules of the block.

### Rotation

A quarter turn of a block is a rotation of 90 degrees around the vertical
axis, clockwise seen from above: north becomes east. The orientation turns
by itself. Two attributes describe how the other properties turn:

- `rotatesTo` names the property that takes the value of this one after a
  quarter turn. The properties linked this way form a cycle, have the same
  kind and the same values, and a property is the target of one property
  only: `<Bool name="north" rotatesTo="east"/>`, `east` to `south`, `south`
  to `west`, and `west` to `north`.
- `rotation`, on an `<Enum>`, maps its values after a quarter turn, as pairs
  `from:to` separated by commas: `rotation="x:z,z:x"`. The values it maps
  from and to are the same; a value it does not list keeps its value.

### Variants

Each `<Variant>` applies to the states that match its condition `when`, a
list of `property=value` pairs separated by commas, such as
`when="lit=true,power=3"`. It may set `collision`, `opacity`, and
`hardness`, as attributes or child elements, and free properties in a
`<Properties>` block. The variants apply in the order written: the last one
that matches a state wins for each value it sets.

### CreateItem

`<CreateItem>` generates the item of the block: the identifier `item:` with
the namespace and the name of the block, such as `item:example.blocks.lamp`.
The item has the item component `nobara.blocks.placeable`, which names the
block and the state it places. The module must also depend on
`nobara.items`.

| Element or attribute | Content |
| --- | --- |
| `maxStack` | The size of a stack, at least 1; the default of nItems when absent. |
| `<DisplayName>` | The [localization key](nof.md#content) of the name of the item. |
| `state` | The state the item places, as `property=value` pairs; the default state when absent. |
| `<Properties>` | Free properties of the item. |

## Free properties

The `<Properties>` block of the resource holds the free properties of every
state; a variant changes them for the states it matches. A module reads
them per state with `IBlockRegistry.ResolveProperty`, described in [the
module sheet](../../../src/nBlocks/README.md#free-properties-per-state).

## Tags

The block registry accepts tags. A block joins a tag with the `<Tags>`
block, and a `<Tag registry="block">` resource lists members; see
[Tags](nof.md#tags). Block tags are distinct from the tags of other
registries.

## Errors

The schema and the plugin `Nobara.Blocks.Build` report these problems when
the module is built, with the file, the line, and the column:

| Code | Problem |
| --- | --- |
| `NBLK001` | `<Shape>` is not a `shape:` object identifier. |
| `NBLK002` | A collision other than `shape` or `none`. |
| `NBLK003` | An opacity other than `opaque`, `cutout`, or `translucent`. |
| `NBLK004` | A hardness that is neither a number of at least 0 nor `unbreakable`. |
| `NBLK005` | An unknown value of `<Orientations>`. |
| `NBLK006` | A state property with an invalid or reserved name, declared twice, with bounds or values that do not hold, or a default outside its values. |
| `NBLK007` | A `rotatesTo` or a `rotation` that does not describe a quarter turn. |
| `NBLK008` | A cell of `<Footprint>` that is the origin, listed twice, or too far. |
| `NBLK009` | A condition of `<Variant>` that names an unknown property or value. |
| `NBLK010` | The `state` of `<CreateItem>` names an unknown property or value. |
| `NBLK011` | Warning: the type has more states than the default of `blocks.maxStatesPerType`. |

The other codes are those of [the resource
compiler](../../../tools/Nobara.ResourceCompiler/README.md#diagnostics).
nBlocks checks the blocks again when they load, overrides applied: a type
with more states than the value of the distribution stops the context.

## Hot reload

In a development build, a change of a native field, of a variant, of a
default value, or of the tags of a block applies while the game runs, at the
next frame. A block added or removed, or a change of its state properties or
of their values, changes the states: it is reported in the logs and needs a
restart of the context, and the type keeps its previous values.
