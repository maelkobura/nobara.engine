# The `Item` resource

An `Item` resource defines a type of item in the item registry of
[nItems](../../../src/nItems/README.md): its native fields, its look, and the
default values of its components. Its schema is `Schemas/item.proto` of the
module `nobara.items`; a module that writes items declares a dependency on
`nobara.items`. The common structure of a resource is described in [the
`.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Food">
  <Item>
    <Name>Steak</Name>                                <!-- item:example.food.steak -->
    <Content>
      <MaxStack>16</MaxStack>
      <DisplayName>item.food.steak</DisplayName>
      <Visual>
        <Sprite>texture://Example/Textures/Steak</Sprite>
      </Visual>
      <Component ref="example.food.edible">
        <Food>3</Food>
        <Duration>1.5</Duration>                      <!-- seconds -->
      </Component>
    </Content>
    <Tags>
      <Tag>#example.meat</Tag>
    </Tags>
  </Item>
</Nobara>
```

## Identifier

The object identifier of an item is `item:` followed by the namespace and
the name, in lowercase and separated by dots: `item:example.food.steak`. Disk
and network name an item by this identifier only.

## Content

| Element | Required | Content | Default |
| --- | --- | --- | --- |
| `<MaxStack>` | No | Number of items in one stack: a whole number, at least 1. | The configuration key `items.defaultMaxStack` of the distribution, 64 unless `engine.nobara` sets it. |
| `<DisplayName>` | No | [Localization key](nof.md#content) of the name. | `item.` followed by the identifier without its type: `item.example.food.steak`. |
| `<Visual>` | No | The look of the item: a `<Model>` or a `<Sprite>`, never both. | No look of its own. |
| `<Component>` | No, repeated | A component of the item and its default values. | None. |

### Visual

| Element | Content |
| --- | --- |
| `<Model>` | A `mesh:` object identifier: the item shows as this 3D model. |
| `<Sprite>` | A `texture://` resource identifier: the item shows as this texture, with thickness. |

The referenced object or resource must be defined by the module or one of its
declared dependencies. An item without `<Visual>`, such as an item derived
from a block, has no look of its own. nItems checks and keeps the
declaration; it draws nothing.

### Components

Each `<Component>` names a type of item component registered in code by a
module, with the attributes of [generic content](nof.md#generic-content):
`ref`, the qualified name, and `optional`. Its child elements are the
default values of the component; their names and types are those that the
component type reads. A component appears once per item.

The values are checked when the game starts, not when the module is built:

- a component that no active module registers is an error, unless it is
  `optional="true"` and the module that its name starts with is declared by
  the distribution but not active in the context, such as a module of the
  other side;
- an optional component whose name starts with an active module that does
  not register it, or with no module of the distribution, is an error;
- a value of the wrong type, a missing required value, a value written
  twice, and a child element that the component does not read are errors;
- a duration is written in seconds and converted to ticks at the tick rate
  of the distribution, rounded to the nearest tick.

Each error names the item, the component, and the position of the value in
the `.nof` file, and the context does not start.

## Tags

The item registry accepts tags. An item joins a tag with the `<Tags>` block,
and a `<Tag registry="item">` resource lists members; see [Tags](nof.md#tags).
Item tags are distinct from the tags of other registries.

## Derived items

The plugin of another resource type can generate items from its own
resources, such as the item of a block. A derived item takes the namespace
and the name of its source resource, with the type `item`: the block
`block:example.blocks.dirt` gives `item:example.blocks.dirt`. It is compiled
like a written item. The plugin builds its content with `DerivedItem.Create`
of `Nobara.Items.Build`, and the module whose resources are compiled must
depend on `nobara.items`.

## Errors

The schema and the plugin `Nobara.Items.Build` report these errors when the
module is built, with the file, the line, and the column:

| Code | Error |
| --- | --- |
| `NRC310` | `<MaxStack>` is less than 1. |
| `NRC311` | `<DisplayName>` is not a localization key, or a `ref` is not a qualified component name. |
| `NRC307` | `<Visual>` has both a `<Model>` and a `<Sprite>`. |
| `NRC401`, `NRC402` | The model or the sprite is not defined by the module or its dependencies. |
| `NITM001` | `<Visual>` has neither a `<Model>` nor a `<Sprite>`. |
| `NITM002` | A component is declared twice in one item. |

The other codes are those of [the resource
compiler](../../../tools/Nobara.ResourceCompiler/README.md#diagnostics).

## Hot reload

In a development build, a change of a native field, of a component value, of
the look, or of the tags of an item applies while the game runs, at the next
frame. An item added or removed, or a value that fails its checks, is
reported in the logs and needs a restart of the context; the item keeps its
previous values.
