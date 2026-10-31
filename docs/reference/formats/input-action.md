# The `InputAction` and `InputMap` resources

An `InputAction` resource defines an input action of
[nInput](../../../src/nInput/README.md): what the game reads instead of keys,
its kind of value, its default bindings, and its interaction. An `InputMap`
resource defines a map of actions that the game turns on and off together.
Their schema is `Schemas/input.proto` of the module `nobara.input`; a module
that writes actions declares a dependency on `nobara.input`. The common
structure of a resource is described in [the `.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Input">
  <InputMap>
    <Name>On_Foot</Name>                               <!-- inputmap:example.input.on_foot -->
    <Content>
      <DisplayName>input.map.on_foot</DisplayName>
      <Active>true</Active>
    </Content>
  </InputMap>
  <InputAction>
    <Name>Move</Name>                                  <!-- inputaction:example.input.move -->
    <Content>
      <DisplayName>input.move</DisplayName>
      <Value>Axis2D</Value>
      <Map>inputmap:example.input.on_foot</Map>
      <Bindings>
        <Composite2D up="Key.ArrowUp" down="Key.ArrowDown" left="Key.ArrowLeft" right="Key.ArrowRight"/>
      </Bindings>
    </Content>
  </InputAction>
  <InputAction>
    <Name>Aim</Name>
    <Content>
      <Value>Button</Value>
      <Map>inputmap:example.input.on_foot</Map>
      <Interaction><Hold duration="0.5"/></Interaction>
      <Bindings><Key>Mouse.Right</Key></Bindings>
    </Content>
  </InputAction>
  <InputAction>
    <Name>Quick_Save</Name>
    <Content>
      <Value>Button</Value>
      <Bindings>
        <Combo><Key>Key.Control</Key><Key>Key.S</Key></Combo>
      </Bindings>
    </Content>
  </InputAction>
</Nobara>
```

## Identifiers

The object identifier of an action is `inputaction:` followed by the
namespace and the name, in lowercase and separated by dots:
`inputaction:example.input.move`. A map is `inputmap:`:
`inputmap:example.input.on_foot`. The bindings of the player in
`config/input.xml` name actions by this identifier.

## `InputAction` content

| Element | Required | Content | Default |
| --- | --- | --- | --- |
| `<Value>` | Yes | The kind of value: `Button`, `Float`, `Axis1D`, or `Axis2D`. | — |
| `<DisplayName>` | No | [Localization key](nof.md#content) of the name. | `inputaction.` followed by the identifier without its type: `inputaction.example.input.move`. |
| `<Map>` | No | The `inputmap:` identifier of the map of the action, defined by the module or one of its declared dependencies. | No map: the action is always active. |
| `<Bindings>` | No | The default bindings; see [Bindings](#bindings). | None: the action triggers only once the player binds it. |
| `<Interaction>` | No | One interaction, for a `Button` or a `Float`; see [Interactions](#interactions). | `<Press/>`. |
| `<Threshold>` | No | For a `Button` or a `Float`: the value from which an axis binding, or a float, reads as pressed. A number above 0, at most 1. | 0.5. |
| `<DeadZone>` | No | The dead zone of the analog controls of a gamepad, from 0 to 0.95. | 0.15. |

Like every scalar field, `<Threshold>` and `<DeadZone>` can be written as
attributes of `<Content>`: `<Content threshold="0.4">`.

| Kind | Value | Bindings it accepts |
| --- | --- | --- |
| `Button` | Pressed or not. | `<Key>`, `<Combo>`, a 1D `<Axis>`. |
| `Float` | From 0 to 1; a key gives 0 or 1. | `<Key>`, `<Combo>`, a 1D `<Axis>`. |
| `Axis1D` | From -1 to 1 for keys and composites; the movement of the mouse, or notches of the wheel. | `<Key>` and `<Combo>` (1 while held), a 1D `<Axis>`, `<Composite1D>`. |
| `Axis2D` | Two values, up and right positive. | A 2D `<Axis>`, `<Composite2D>`. |

### Bindings

Every binding of an action is active at the same time. For a float or an
axis, the binding of largest amplitude gives the value.

| Element | Form | Binding |
| --- | --- | --- |
| `<Key>` | Text: a key or a button of any device. | `<Key>Key.Space</Key>`, `<Key>Mouse.Left</Key>`. |
| `<Axis>` | Text: an axis. | `<Axis>Mouse.Delta</Axis>`, `<Axis>Mouse.Wheel</Axis>`. |
| `<Composite1D>` | Attributes `negative` and `positive`: two keys or buttons. | -1 while `negative` is held, 1 while `positive` is. |
| `<Composite2D>` | Attributes `up`, `down`, `left`, and `right`: four keys or buttons. | Up is positive Y, right is positive X. Opposite keys cancel out; the diagonal is not normalized. |
| `<Combo>` | Two or more `<Key>` elements. | Held together; the last one triggers. |

A combination triggers when its last key is pressed while the others are
already held, and ends when one of its keys is released. When a key triggers
several bindings, only those with the most keys trigger: `Ctrl+4` keeps an
action bound to `4` alone from triggering. A step of the wheel can only be
the last key of a combination.

### Controls

Bindings name controls from a closed list, case-sensitive:

| Device | Names |
| --- | --- |
| Keyboard | `Key.` followed by the physical position of the key, as on a US keyboard: `Key.A` to `Key.Z`, `Key.Digit0` to `Key.Digit9`, `Key.F1` to `Key.F24`, `Key.Space`, `Key.Enter`, `Key.Escape`, `Key.Tab`, `Key.Backspace`, `Key.ArrowUp`, `Key.LeftShift`, `Key.RightControl`, `Key.Keypad1`, and every other name of `Scancode` of nFrames. |
| Keyboard, either side | `Key.Control`, `Key.Shift`, `Key.Alt`, `Key.Meta`: the left or the right key. |
| Mouse buttons | `Mouse.Left`, `Mouse.Right`, `Mouse.Middle`, `Mouse.Back`, `Mouse.Forward`. |
| Mouse wheel steps | `Mouse.WheelUp`, `Mouse.WheelDown`, `Mouse.WheelLeft`, `Mouse.WheelRight`: a button pressed and released at once. |
| Mouse axes | `Mouse.Delta` (2D), `Mouse.DeltaX`, `Mouse.DeltaY` (up positive), `Mouse.Wheel` (away from the player positive), `Mouse.WheelX` (right positive). |
| Gamepad buttons | `Gamepad.South`, `Gamepad.East`, `Gamepad.West`, `Gamepad.North`, `Gamepad.Back`, `Gamepad.Guide`, `Gamepad.Start`, `Gamepad.LeftStickPress`, `Gamepad.RightStickPress`, `Gamepad.LeftShoulder`, `Gamepad.RightShoulder`, `Gamepad.DPadUp`, `Gamepad.DPadDown`, `Gamepad.DPadLeft`, `Gamepad.DPadRight`. |
| Gamepad axes | `Gamepad.LeftTrigger`, `Gamepad.RightTrigger` (0 to 1), `Gamepad.LeftStickX`, `Gamepad.LeftStickY`, `Gamepad.RightStickX`, `Gamepad.RightStickY` (1D), `Gamepad.LeftStick`, `Gamepad.RightStick` (2D). |

A key names a physical position: `Key.W` is the key labeled Z on an AZERTY
keyboard. The movements of the mouse are in logical units of the window,
multiplied by `input.mouseSensitivity`. Gamepad controls are accepted and
give nothing yet: no gamepad is read.

### Interactions

`<Interaction>` holds one element:

| Element | Attribute | The action is pressed |
| --- | --- | --- |
| `<Press/>` | — | While a binding is held. |
| `<Release/>` | — | When the binding is released, and released at once. |
| `<Hold/>` | `duration`, in seconds, above 0 | Once the binding has been held for `duration`, until it is released. |
| `<Tap/>` | `max`, in seconds, above 0 | When a press shorter than `max` ends, and released at once. |
| `<DoubleTap/>` | `window`, in seconds, above 0 | At a second press that starts at most `window` after the first, until it ends. |
| `<Toggle/>` | — | At one press, released at the next one. |

Two behaviors on the same key are two actions bound to it. Once an action
with `<Hold/>` on the key has passed its duration, an action with `<Tap/>` on
the same key does not trigger.

## `InputMap` content

| Element | Required | Content | Default |
| --- | --- | --- | --- |
| `<DisplayName>` | No | Localization key of the name. | `inputmap.` followed by the identifier without its type. |
| `<Active>` | No | `true` when the map is active when the game starts. | `false`. |

Two actions of maps that are never active together may share a key.

## Overrides

Another module may override an action or a map, to change its default
bindings for example; see [Overrides](nof.md#overrides):

```xml
<Nobara version="1.0.0" namespace="Example/Input">
  <InputAction override="true">
    <Name>Aim</Name>
    <Content>
      <Query type="Bindings">
        <Replace><Bindings><Key>Key.LeftAlt</Key></Bindings></Replace>
      </Query>
    </Content>
  </InputAction>
</Nobara>
```

## Errors

The schema and the plugin `Nobara.Input.Build` report these errors when the
module is built, with the file, the line, and the column:

| Code | Error |
| --- | --- |
| `NRC308` | `<Value>` is missing, or an attribute of a composite or of an interaction is missing. |
| `NRC307` | `<Interaction>` holds two interactions. |
| `NRC310` | `<Threshold>` or `<DeadZone>` is out of bounds. |
| `NRC311` | `<DisplayName>` is not a localization key, or `<Map>` is not an `inputmap:` identifier. |
| `NRC401` | The map is not defined by the module or its dependencies. |
| `NINP001` | A name is not a control of the list. |
| `NINP002` | A binding does not fit its form: an axis in `<Key>`, a key in `<Axis>`, a combination of fewer than two keys, the same key twice, a step of the wheel before the last key. |
| `NINP003` | A binding cannot give the kind of value of the action. |
| `NINP004` | An axis has an interaction or a threshold. |
| `NINP005` | A duration or a threshold is 0. |
| `NINP006` | A binding is written twice in one action. |

The other codes are those of [the resource
compiler](../../../tools/Nobara.ResourceCompiler/README.md#diagnostics).

## Hot reload

In a development build, a change of the bindings, the interaction, the
threshold, the map, or the name of an action, and of the name of a map,
applies while the game runs, at the next frame. The bindings the player
chose stay. An action or a map added or removed, a changed `<Value>`, and a
changed `<Active>` are reported in the logs and need a restart.
