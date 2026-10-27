# The `engine.nobara` file

This page is the reference for `engine.nobara`, the file at the root of a
game that declares how the engine behaves for it: loop, initial states, error
policies, configuration values, and configuration blocks for modules. With
[`project.nobara`](project-nobara.md), it is compiled into the distribution
manifest. One file serves both sides; an element meant for one side has a
`side` attribute.

## Example

```xml
<NobaraEngine format="1">
  <Loop tickRate="30">
    <Phase name="Input" />
    <Phase name="Simulation" fixed="true" />
    <Phase name="PostSimulation" />
    <Phase name="Render" side="client" />
  </Loop>

  <States client="state:example.boot" server="state:example.server_boot" />

  <ErrorPolicy default="stop">
    <Module id="example.stats" policy="log" />
  </ErrorPolicy>

  <Settings>
    <Default key="voxel.viewRadius" value="192" />
    <Lock key="network.tickRate" value="30" />
    <Set key="blocks.maxStatesPerType" value="2048" />
  </Settings>

  <ModuleConfig module="nobara.game">
    <Stage name="example.combat" after="Entities" before="Physics" />
  </ModuleConfig>
</NobaraEngine>
```

## Root element

`<NobaraEngine>` is the root element. Its required `format` attribute holds
the version of the file format; the current format is `1`. Every element and
attribute must be one of those listed on this page, except inside
`<ModuleConfig>`.

## `<Loop>`

| Attribute or element | Content |
| --- | --- |
| `tickRate` | Required: ticks per second of the fixed phases, a positive whole number. |
| `<Phase name fixed side>` | One per phase, in order, at least one. `name` is unique; `fixed="true"` runs the phase at the tick rate; `side` is `client` or `server` for a phase of one side. |

## `<States>`

`client` and `server` attributes, at least one: the identifier of the initial
context state of each side, such as `state:example.boot`. An active module of
that side must provide the state.

## `<ErrorPolicy>`

`default` is the policy of the business modules that are not required and
have no policy of their own: `stop` (the default), `disable`, or `log`.
`<Module id="…" policy="…"/>` sets the policy of one module.

- Required modules and platform modules always have the policy `stop`; giving
  them another one is an error.
- A module in `disable` or `log` cannot be required by a module in `stop`:
  disabling it would disable that module too, and stop the game.

## `<Settings>`

| Element | Effect | Applies to |
| --- | --- | --- |
| `<Default key value/>` | Changes the default value. | A `user` key. |
| `<Lock key value/>` | Fixes the value: the player cannot change it. | A `user` key. |
| `<Set key value/>` | Gives its value to the key. | A `distribution` key. |

The key must be declared by a module of the distribution, and the value must
match its type, bounds, and values. Each key appears once. The player changes
the other `user` keys in the settings file `config/settings.xml`; see [the
core page](../../../src/Core/README.md#configuration-keys).

## `<ModuleConfig>`

`<ModuleConfig module="…">` holds a configuration that only the named module
understands. The compiler stores its content in the generic representation,
with positions in the file; the module receives it as `context.ModuleConfig`
and checks it when it starts. The module
must be part of the distribution, and has one block at most.

## Known limitations

- Activation rules for modules are not part of the format.
