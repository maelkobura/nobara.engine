# The `Bus` resource

A `Bus` resource defines a bus of the mixing tree of
[nAudio](../../../src/nAudio/README.md): the volume of a bus multiplies the
volume of its children, and every sound event plays on one bus, its
category. Its object identifier is `bus:` followed by the namespace and the
name (`bus:example.sounds.machines`). Its schema is `Schemas/audio.proto` of
the module `nobara.audio`. The common structure of a resource is described
in [the `.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Sounds">
  <Bus>
    <Name>Machines</Name>                               <!-- bus:example.sounds.machines -->
    <Content>
      <Parent>bus:nobara.audio.effects</Parent>
      <Adjustable>true</Adjustable>
      <DisplayName>audio.bus.machines</DisplayName>
    </Content>
  </Bus>
</Nobara>
```

## Content

| Element | Required | Content |
| --- | --- | --- |
| `<Parent>` | Yes, except for the master bus | The bus whose volume multiplies this one. |
| `<Adjustable>` | No | `true` to show the bus in the settings of the player. |
| `<DisplayName>` | When adjustable | The i18n key of the name shown in the settings. |

## The default tree

nAudio provides the root, `bus:nobara.audio.master`, and its children:

| Bus | Parent | Adjustable |
| --- | --- | --- |
| `bus:nobara.audio.master` | none | yes |
| `bus:nobara.audio.music` | master | yes |
| `bus:nobara.audio.ambient` | master | yes |
| `bus:nobara.audio.effects` | master | yes |
| `bus:nobara.audio.effects.blocks` | effects | no |
| `bus:nobara.audio.effects.entities` | effects | no |
| `bus:nobara.audio.effects.players` | effects | no |
| `bus:nobara.audio.ui` | master | yes |

A module adds buses below these; only the master bus has no parent. nAudio
checks the tree when it loads it, overrides applied: a parent that no loaded
module defines, or a cycle, stops the context with an `AudioLoadException`.

## Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `NAUD004` | Error | A bus has no parent, is its own parent, or is adjustable without a `<DisplayName>`. |

An unknown `<Parent>` is reported by the compiler at its line, like every
reference.
