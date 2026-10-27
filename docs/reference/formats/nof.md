# The `.nof` format

This page is the reference for `.nof` files, the XML files that define the
resources of a module. The resource compiler turns them into protobuf data
embedded in the assembly of the module. The fields of each resource type are
defined by a `.proto` schema; the options that a schema uses are described in
[the schema options reference](proto-options.md).

## Location

A module keeps its `.nof` files and the binary files they describe in its
`Resources/` folder, at any depth. `Nobara.Sdk` compiles every
`Resources/**/*.nof` file when the module is built. An error in a file fails
the build and names the file, the line, and the column.

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Blocks">
  <Block>
    <Name>Dirt</Name>
    <Content>
      <DisplayName>block.natural.dirt</DisplayName>
      <Hardness>15</Hardness>
      <Drops>
        <Drop item="item:example.blocks.dirt" min="1" max="1" chance="1.0" />
      </Drops>
    </Content>
    <Properties>
      <Weight>1</Weight>
      <Motion.Weight>1.2</Motion.Weight>
    </Properties>
    <Tags>
      <Tag>#example.soil</Tag>
    </Tags>
  </Block>
</Nobara>
```

## Root element

`<Nobara>` is the root element. It takes two required attributes and nothing
else:

| Attribute | Content |
| --- | --- |
| `version` | Version of the `.nof` format: `1`, `1.0`, or a `1.x.y` version. |
| `namespace` | Namespace of the resources of the file: names of letters, digits, and underscores separated by `/`, such as `Example/Blocks`. |

The root holds one element per resource. A namespace belongs to no module:
several modules may use the same one.

The file is strict XML: an attribute always has a value, and a DTD is
rejected. Comments are allowed anywhere.

## Resource element

The name of a resource element is the name of its type, which is the name of
a message of a schema: `<Block>`, `<Item>`. A schema declares that a message
is a resource type with [`(nobara.resource_type)`](proto-options.md#message-options).
The element `<Tag>` is built in; see [Tags](#tags).

| Attribute | Required | Content |
| --- | --- | --- |
| `version` | No | Version of the schema of the type the content follows, a whole number from 1. When it is absent, the content follows the current version. |
| `override` | No | `true` makes the resource an [override](#overrides). |
| `optional` | No | `true` for an override whose target may be missing. Overrides only. |
| `priority` | No | Whole number; overrides of a resource apply in increasing priority. Overrides only. |

A resource element holds these child elements, in any order, each at most
once:

| Element | Required | Content |
| --- | --- | --- |
| `<Name>` | Yes | Name of the resource: a letter, then letters, digits, and underscores. |
| `<Content>` | Yes | Fields of the type; see [Content](#content). |
| `<Version>` | No | Free text for people, such as a version number. The compiler does not use it. |
| `<Properties>` | No | [Free properties](#free-properties). |
| `<Tags>` | No | Tags the resource joins; see [Tags](#tags). |

### Schema versions

When `version` names an older version of the schema, the compiler converts
the element with the converters that the plugins of the type provide, one
version at a time. A version without a converter, or above the current
version, is an error. A new version never gives a new meaning to an existing
syntax: a file without `version` compiles the same way, or fails to compile.

## Identifiers

The compiler derives two identifiers from the type, the namespace, and the
name of each resource:

| Identifier | Form | Example |
| --- | --- | --- |
| Object identifier | `type:namespace.name`, with `/` replaced by `.` and in lowercase | `block:example.blocks.dirt` |
| Resource identifier | `type://Namespace/Name`, as written | `block://Example/Blocks/Dirt` |

The type prefix is the value of `(nobara.resource_type)`. It names the type,
never the module. Two resources of the same type, namespace, and name are a
duplicate, even when their names differ only by case. A duplicate is an error
when both are in the module, or when one of them comes from a dependency.

Identifiers are always derived: a resource has no identifier element of its
own, and `<Id>` next to `<Name>` is an error.

## Content

`<Content>` holds the fields of the message of the type, by convention:

| XML | Field |
| --- | --- |
| Element `<DisplayName>` | Field `display_name`: each underscore-separated word capitalized. |
| Attribute `displayName` | Field `display_name`, with a lowercase first letter. Scalar fields only. |
| Element repeated | A `repeated` field, one element per value. |
| Element with child elements or attributes | A field whose type is a message. |
| Text of an element | The value of a scalar field. |

Names are case-sensitive. A field is set once, as an attribute or as an
element; a repeated field is written as elements. Two fields of one `oneof`
cannot both be set. A list of messages is written either as repeated elements
in the parent, or through a message field that holds the repeated field, as
`<Drops>` holds `<Drop>` elements in the example: `Drops` is then a message
with a `repeated Drop drop` field.

Values by field type:

| Field type | Value |
| --- | --- |
| `string` | The text, trimmed. |
| `bool` | `true` or `false`. An empty element, such as `<CreateItem/>`, is `true`. |
| Integer types | A whole number in the range of the type. |
| `float`, `double` | A number with a dot as the decimal separator. |
| enum | The name of the value, either as declared (`SHAPE_HALF_SLAB`) or without the enum prefix and capitalized (`HalfSlab`). |
| `bytes` | Base64 text, unless the field holds [generic content](#generic-content). |
| message | Child elements and attributes. |

The compiler rejects an unknown element or attribute, a value of the wrong
type, a value outside the bounds of the schema, a missing required field, and
a reference that does not resolve. Map fields have no XML form.

### References

A field marked [`(nobara.ref)`](proto-options.md#ref) holds an object
identifier of the given type, such as `item:example.blocks.dirt`. A field
marked [`(nobara.resource)`](proto-options.md#resource) holds a resource
identifier, such as `texture://Example/Textures/Dirt`. The referenced object
must be defined by the module or by one of the dependencies declared in its
`module.nobara`. A module that is only a dependency of a dependency provides
neither types nor resources: declare a dependency on it to use them, which
also loads its plugin.

### Binary files

A binary file, such as a texture or a model, is described by a resource whose
type has a field marked [`(nobara.file)`](proto-options.md#file). The value
is the path of the file relative to `Resources/`, with forward slashes and
without `..`. The file is embedded in the assembly as it is, as the manifest
resource `nobara/files/` followed by the path.

### Generic content

A `bytes` field marked [`(nobara.generic)`](proto-options.md#generic) holds
its element in the generic representation: the element, its attributes,
its child elements, and its values, each with the type the compiler
recognized (boolean, integer, number, object identifier, resource identifier,
tag identifier, or text) and its position in the file. An element whose
children are `X`, `Y`, and optionally `Z` and `W`, all numbers, is also
recorded as a vector. The schema of this content is not known when it is
compiled: the module that reads it checks it when the resources are loaded.

A component, for a field marked `"component"`, takes these attributes:

| Attribute | Required | Content |
| --- | --- | --- |
| `ref` | Yes | Qualified name of the component: the identifier of the module that registers it, then the name of the component, in lowercase, as in `example.physics.rigidbody`. |
| `optional` | No | `true` when the component is ignored if its module is not active. |

The object identifiers in the values of a component must resolve, except in an
optional component.

## Free properties

`<Properties>` holds values that other modules may read. The type of the
resource does not declare them, and they are not checked when they are
compiled.

| Form | Example | Meaning |
| --- | --- | --- |
| Plain property | `<Weight>1</Weight>` | Read by every module that reads `Weight`. |
| Qualified property | `<Motion.Weight>1.2</Motion.Weight>` | Replaces `Weight` for the module whose short name is `Motion`. |

A property name is a capitalized name. A qualified property adds one short
module name and a dot. Each name appears once. A value is text or child
elements, stored in the generic representation.

## Tags

A tag groups objects of one registry under a name. A type accepts tags when
its schema has [`(nobara.accepts_tags)`](proto-options.md#message-options).
Each registry has its own tags: `#example.wood` of blocks and `#example.wood`
of items are two tags.

A tag identifier is `#namespace.name` where the registry is implied, and
`#type:namespace.name` in full.

### Tag resource

```xml
<Nobara version="1.0.0" namespace="Example">
  <Tag registry="block">
    <Name>Wood</Name>
    <Content>
      <Member>block:example.blocks.oak_log</Member>
      <Member>#example.planks</Member>
      <Member optional="true">block:other.blocks.birch_log</Member>
    </Content>
  </Tag>
</Nobara>
```

`registry` is the type prefix of the members. The tag identifier is derived
from the namespace and the name: `#block:example.wood`. A member is an object
identifier of the registry, or a tag of the same registry. A member marked
`optional="true"` may be missing; any other member must be defined by the
module or by its dependencies. Tags that contain each other are an error.

### Tags block

A resource joins tags with a `<Tags>` block of `<Tag>` elements:
`<Tags><Tag>#example.soil</Tag></Tags>`. The result is the same as a member
added to a `<Tag>` resource.

Tags are additive: several modules may add members to the same tag. Members
are removed with an override.

## Overrides

An override changes a resource defined by another module, or by the same
module. It is a resource element with `override="true"` whose type,
namespace, and name are those of its target. Its `<Content>` holds simple
fields and queries:

```xml
<Nobara version="1.0.0" namespace="Example/Blocks">
  <Block override="true">
    <Name>Dirt</Name>
    <Content>
      <Hardness>20</Hardness>
      <Query field="item" value="item:example.blocks.dirt">
        <InsertAfter>
          <Drop item="item:example.worm" min="0" max="1" chance="0.1" />
        </InsertAfter>
      </Query>
    </Content>
  </Block>
</Nobara>
```

A simple field is a scalar field of the type that is not repeated. It
replaces the value of the target.

### Queries

A `<Query>` selects elements of the target:

| Attribute | Content |
| --- | --- |
| `field`, `value` | The element has a field (attribute or child element) with this value. Written together. |
| `type` | The element has this name. |
| `index` | Which match to use: a position from 0, or `all`. The first match by default. |

A query needs `field` and `value`, `type`, or both. A query inside a query
searches the elements selected by its parent. A query holds operations:

| Operation | Effect |
| --- | --- |
| `<Replace>` | Replaces the element with the elements it holds. |
| `<Add>` | Adds the elements it holds as children of the element, last, or at the position given by `index`. |
| `<InsertBefore>`, `<InsertAfter>` | Inserts the elements it holds next to the element. |
| `<Remove/>` | Removes the element. |
| `<Set field="…" value="…"/>` | Changes one field of the element. |

The compiler checks every field and element name against the schema of the
type, and the elements to insert as content of that type. In the override of
a tag, the elements are `<Member>` elements, whose fields are `id` and
`optional`.

When the target is defined by the module or by a dependency, a query that
selects nothing in the target is a warning: another override may add the
element first. The compiler checks overrides; the core applies them when it
loads the resources, and a query that selects nothing then is an error unless
the override is optional. See [the core page](../../../src/Core/README.md#loading-resources)
for the order of the overrides and the exact effect of each operation.

## Content hash

Each compiled resource has a SHA-256 hash of its content, its free
properties, its binary files, and the hashes of the resources it references.
Changing a texture changes the hash of every block that uses it. Positions in
the file do not change the hash of the free properties.

## Errors

The compiler reports a problem as `file(line,column): error NRCnnn: message`.
The codes are listed in [the resource compiler page](../../../tools/Nobara.ResourceCompiler/README.md#diagnostics).

## Known limitations

- Localization keys are checked for their form only; the compiler does not
  know the localization catalogs.
- A `.proto` schema cannot import the schema of another module.
- Free properties are not checked when they are compiled.
- When it compiles, a query is tried on the target as defined, before any
  other override.
