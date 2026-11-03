# The `Entity` resource

An `Entity` resource defines an entity of the game world: its components and
their initial values. [nEntity](../../../src/nEntity/README.md) loads it as an
entity definition and creates entities from it in an entity world. Its schema
is `Schemas/entity.proto` of the module `nobara.entity`; a module that writes
entities declares a dependency on `nobara.entity`. The common structure of a
resource is described in [the `.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Animals">
  <Entity>
    <Name>Pig</Name>                                    <!-- entity:example.animals.pig -->
    <Content>
      <Component ref="example.animals.stats">
        <Health>12</Health>
        <Size><X>1</X><Y>0.75</Y><Z>1.5</Z></Size>
        <Diet>Grass</Diet>
      </Component>
      <Component ref="nobara.entity.transform">
        <Scale><X>0.5</X><Y>0.5</Y><Z>0.5</Z></Scale>
      </Component>
      <Component ref="example.particles.emitter" optional="true">
        <Effect>particle:example.fx.mud</Effect>
      </Component>
    </Content>
    <Properties>
      <Weight>80</Weight>                               <!-- read by the modules that convert it -->
    </Properties>
  </Entity>
</Nobara>
```

## Identifier

The object identifier of an entity definition is `entity:` followed by the
namespace and the name, in lowercase and separated by dots:
`entity:example.animals.pig`. Code asks for a definition by this identifier.

## Content

| Element | Required | Content | Default |
| --- | --- | --- | --- |
| `<Component>` | No, repeated | A component of the entity and its initial values. | None: an entity without components. |

### Components

Each `<Component>` names a component type registered in code by a module,
with the attributes of [generic content](nof.md#generic-content): `ref`, the
qualified name, and `optional`. A component appears once per entity. Its
child elements are the initial values of the component; a value that is not
written keeps the default value that the module registered with the
component.

Values are written as the C# type of the component declares them:

| Field of the component | Written as | Example |
| --- | --- | --- |
| `bool` | `true` or `false` | `<Tame>true</Tame>` |
| Whole number (`sbyte` to `ulong`) | A whole number that fits in the type, up to the range of `long` | `<Health>12</Health>` |
| `float`, `double` | A number; a whole number is accepted | `<Speed>1.5</Speed>` |
| Enumeration | The name of a member, with its case | `<Diet>Grass</Diet>` |
| Struct, such as `Vector3` or `Quaternion` | Its public fields as child elements | `<Size><X>1</X><Y>0.75</Y><Z>1.5</Z></Size>` |

Each child element names a public field of the struct, with its case. A
module can instead read the values of a component with a reader of its own,
for a value that a field cannot hold as written, such as an object identifier
turned into an index; the module documents the values it reads.

The components of nEntity take these values:

| Component | Values |
| --- | --- |
| `nobara.entity.transform` | `<Position>`, a vector in blocks; `<Rotation>`, a quaternion `<X>`, `<Y>`, `<Z>`, `<W>`, normalized; `<Scale>`, a vector. Each defaults to the identity. |
| `nobara.entity.local_transform` | The same values, in the frame of the parent. |
| `nobara.entity.parent` | Never written: the game sets the parent of an entity once it exists. |

### Optional components

A component marked `optional="true"` is ignored when the module that its name
starts with is declared by the distribution but not active in the context,
such as a module of the other side. The definition then has fewer
components, which is expected: its composition is fixed when it is loaded.

| Component not found | Result |
| --- | --- |
| Optional, its module is declared but not active | Ignored, without a message. |
| Optional, its module is active | Error: a typing error in the name. |
| Optional, no module of the distribution starts its name | Error. |
| Not optional | Error, whatever the module. |

### Free properties

`<Properties>` holds [free properties](nof.md#free-properties). The modules
that read them convert them, when the definition is loaded, into components
of their own: a module that reads `Weight` may add a body to the entity.
Without such a module, the property adds nothing.

## When the values are checked

The compiler checks the form of each `<Component>` and the object
identifiers written in its values. The values themselves are checked when
the game loads the definition, against the C# types of the components:

- in a development build, every definition is loaded when nEntity starts;
- otherwise, a definition is loaded the first time the game asks for it.

An unknown component, a field that the component does not have, a value of
the wrong type or out of the range of its field, and a value written twice
are errors. Each error names the definition, the component, and the position
of the value in the `.nof` file, as in
`Resources/animals.nof(6,7): entity:example.animals.pig: component 'example.animals.stats': <Healt> is not a field of the component`.
In a development build, the context does not start.

## Errors

The plugin `Nobara.Entity.Build` and the compiler report these errors when
the module is built, with the file, the line, and the column:

| Code | Error |
| --- | --- |
| `NRC311` | A `ref` is not a qualified component name. |
| `NRC308` | A `<Component>` has no `ref`. |
| `NRC309` | `optional` is neither `true` nor `false`. |
| `NRC306` | An attribute other than `ref` and `optional`, or an element of `<Content>` other than `<Component>`. |
| `NRC401` | An object identifier in the values of a component that is not optional is not defined by the module or its dependencies. |
| `NENT001` | A component is declared twice in one entity. |
| `NENT002` | The parent link `nobara.entity.parent` is written in a definition. |

The other codes are those of [the resource
compiler](../../../tools/Nobara.ResourceCompiler/README.md#diagnostics).

## Hot reload

In a development build, a change of the values of a definition applies while
the game runs, at the next frame: the entities created from then on have the
new values, and the entities created before keep theirs. A change of the
components of a definition is reported in the logs, and applies once no
entity world exists, when the world is reloaded. A definition added applies
at once; a definition removed, or a value that fails its checks, is reported
and the definition keeps its previous values.
