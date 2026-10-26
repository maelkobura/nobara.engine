# Schema options

This page is the reference for the options that a `.proto` schema of a
resource type uses. They are declared in `nobara/options.proto`, which
`Nobara.Sdk` provides to every module. The XML form of the resources is
described in [the `.nof` reference](nof.md).

## Schemas of a module

A module keeps its schemas in its `Schemas/` folder. When `Schemas/**/*.proto`
is not empty, `Nobara.Sdk`:

- adds the `Google.Protobuf` package to the module, and `Grpc.Tools` to its
  build;
- generates the C# classes of the schemas with `protoc`, and an internal copy
  of the classes of `nobara/options.proto`;
- builds the descriptor set of the schemas and embeds it in the assembly, so
  that modules that depend on this one can use its types.

A type whose constraints fit in these options needs no plugin code.

## Example

```proto
syntax = "proto3";
package example.blocks;

import "nobara/options.proto";

message Block {
  option (nobara.resource_type) = "block";
  option (nobara.accepts_tags) = true;

  string display_name = 1 [(nobara.i18n) = true, (nobara.required) = true];
  int32  hardness     = 2 [(nobara.min) = -1];
  string texture      = 3 [(nobara.resource) = "texture"];
  Drops  drops        = 4;
  bool   create_item  = 5;
}

message Drops {
  repeated Drop drop = 1;
}

message Drop {
  string item   = 1 [(nobara.ref) = "item", (nobara.required) = true];
  int32  min    = 2 [(nobara.min) = 0];
  int32  max    = 3 [(nobara.min) = 0];
  float  chance = 4 [(nobara.min) = 0, (nobara.max) = 1];
}
```

## Message options

### resource_type

`string`, number 50100. Makes the message a resource type and gives the type
prefix of its object identifiers: `block` for `block:example.blocks.dirt`. The
prefix is a lowercase word other than `tag`. The XML element of the type is the
name of the message, which is unique among the schemas a module sees, as is
the prefix. The names `Tag` and `Nobara` are reserved.

### accepts_tags

`bool`, number 50101. The registry of the type accepts tags: its resources may
have a `<Tags>` block, and a `<Tag>` resource may name it as its registry.

### schema_version

`uint32`, number 50102. Current version of the schema of the type, 1 when
absent. A resource element whose `version` attribute names an older version
is converted by the converters of the plugins of the type.

## Field options

### ref

`string`, number 50200, on a `string` field. The value is the object
identifier of an object of the given type, defined by the module or by its
dependencies: `[(nobara.ref) = "item"]`.

### resource

`string`, number 50201, on a `string` field. The value is the resource
identifier of a resource of the given type, defined by the module or by its
dependencies: `[(nobara.resource) = "texture"]`.

### i18n

`bool`, number 50202, on a `string` field. The value is a localization key of
dot-separated lowercase words, such as `block.natural.dirt`, not text.

### min

`double`, number 50203, on a numeric field. The smallest accepted value,
inclusive. On a repeated field, it applies to each value.

### max

`double`, number 50204, on a numeric field. The largest accepted value,
inclusive.

### required

`bool`, number 50205. The field must be written. A repeated field needs at
least one element.

### file

`bool`, number 50206, on a `string` field. The value is the path of a binary
file relative to `Resources/`. The file must exist; it is embedded in the
assembly.

### generic

`string`, number 50207, on a `bytes` field, single or repeated. The element
is stored in the generic representation, as a serialized
`nobara.formats.GenericNode`:

| Value | Element |
| --- | --- |
| `"tree"` | Any content. |
| `"component"` | A component of an entity or an item, with its `ref` and `optional` attributes. |
| `"properties"` | A block of free properties, checked like `<Properties>`. |

## Extension numbers

The options use numbers 50100 to 50207, in the range that protobuf leaves to
organizations for their own use. Numbers up to 50299 are kept for later
options.

## Errors

A schema that misuses an option, such as `(nobara.min)` on a `string` field,
or that defines a resource element or a type prefix twice, is reported as
`NRC600`. See [the resource compiler page](../../../tools/Nobara.ResourceCompiler/README.md#diagnostics).
