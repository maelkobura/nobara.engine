# nInput

This page is the module sheet of nInput, the input module. It describes what
the module provides to other modules and the rules it enforces. Terms are
defined in [the glossary](../../docs/explanation/glossary.md).

## Purpose

nInput turns the raw input of the window into input actions: the game reads
"jump" or "move", never a key. It defines the resource types `InputAction`
and `InputMap`, evaluates the actions from the raw input of nFrames, runs
their interactions, keeps the stack of input layers with their captures, the
text focus, and the mode of the mouse, samples the actions at the fixed tick
without losing a press, and keeps the bindings of the player in
`config/input.xml`.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client.
<!-- nobara:generated:end -->

## Dependencies

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.frames` | `^1.0` | Required |
<!-- nobara:generated:end -->

nFrames provides the raw input events of each frame, the names of the keys
on the layout of the player, and the primitives of the mouse and of text
input. nInput uses no third-party library.

## Entry points

| Member | Use |
| --- | --- |
| `IInputSystem` | The service: the actions (`Actions`, `GetAction`, `TryGetAction`), the maps (`Maps`, `GetMap`, `TryGetMap`), the layers (`AddLayer`, `Layers`), the names of the keys (`GetLayoutName`, `LayoutVersion`), and the bindings of the player (`Rebinding`). |
| `InputAction`, `InputMap`, `InputValueKind` | An action with its kind of value, map, threshold, default and current bindings, and interaction; a map that the game turns on and off. |
| `InputLayer`, `InputLayerOptions`, `InputLayerPriority` | A layer of the stack: its priority, captures, cursor request, handler, text focus, and the reading of its actions (`Read`, `CreateSampler`). |
| `IInputLayerHandler`, `InputResult`, `ActionEdge` | What a layer does with the raw events, the edges of its actions, and typed text. |
| `InputCapture`, `CursorRequest` | The devices a layer keeps from the layers below, and what it needs of the cursor. |
| `ActionState`, `InputSampler` | The state of an action in a frame or since the previous tick, and the sampler of the fixed tick. |
| `InputControl`, `InputDevice`, `InputControlKind`, `MouseControl`, `GamepadControl` | The closed list of the keys, buttons, and axes that bindings name. |
| `InputBinding`, `BindingKind` | A binding: a key, an axis, a composite, or a combination. |
| `InputInteraction`, `InteractionKind` | An interaction and its duration. |
| `IInputRebinding`, `BindingConflict`, `InputListening` | The bindings of the player: changes, conflicts, defaults, listening for the next input, and saving. |
| `InputSettings` | The names of the configuration keys. |

The service is resolved with `context.Resolve<IInputSystem>()` after
declaring a dependency on `nobara.input`. nInput reads the actions, the maps,
and `config/input.xml` while it initializes: a module that depends on it finds
them from its own initialization on. Use the service on the loop thread of
the client context.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Actions

Actions are resources of type `InputAction`, and maps of type `InputMap`,
described in [their reference](../../docs/reference/formats/input-action.md).

```xml
<InputAction>
  <Name>Jump</Name>                                    <!-- inputaction:example.input.jump -->
  <Content>
    <Value>Button</Value>
    <Map>inputmap:example.input.on_foot</Map>
    <Bindings>
      <Key>Key.Space</Key>
      <Combo><Key>Key.Shift</Key><Key>Mouse.Right</Key></Combo>
    </Bindings>
  </Content>
</InputAction>
```

A binding names keys by their physical position: `Key.W` is the key labeled
Z on an AZERTY keyboard. `GetLayoutName` gives the name to show to the
player, which follows the layout. Every binding of an action is active at
once; for a float or an axis, the binding of largest amplitude gives the
value.

| Kind | Value |
| --- | --- |
| `Button` | Pressed or not, with the presses, the releases, and the time held. |
| `Float` | From 0 to 1; a key gives 0 or 1. It reads as pressed from its threshold on. |
| `Axis1D` | From -1 to 1 for keys and composites; the movement of the mouse or the notches of the wheel for those axes. |
| `Axis2D` | Two values, up and right positive: a `<Composite2D>` of four keys, or `Mouse.Delta`. |

A combination triggers when its last key is pressed while the others are
held: `Key.Control` then `Key.Digit4` triggers `Ctrl+4`, the reverse order
does not. `Key.Control`, `Key.Shift`, `Key.Alt`, and `Key.Meta` stand for
either side. The most specific binding wins: when `Ctrl+4` triggers, an
action bound to `4` alone does not, and between two combinations the one
with more keys wins. A combination ends when one of its keys is released.

### Interactions

An action of kind button or float has one interaction, declared in its
resource:

| Interaction | The action is pressed |
| --- | --- |
| `<Press/>` | While a binding is held. The default. |
| `<Release/>` | When the binding is released, and released at once. |
| `<Hold duration="…"/>` | Once the binding has been held for `duration` seconds, until it is released. A press that lasted long enough between two frames presses and releases at once. |
| `<Tap max="…"/>` | When a press shorter than `max` seconds ends, and released at once. |
| `<DoubleTap window="…"/>` | At a second press that starts at most `window` seconds after the first, until it ends. |
| `<Toggle/>` | At one press, released at the next: held in between. |

Two behaviors on one key are two actions on the same key, such as a tap and a
hold: once an action with a hold on the key has passed its duration, the tap
does not trigger. Time is the time of the frame that reads the input, at most
0.25 s per frame as the core counts it.

### Maps

An action belongs to one map, or to none and is then always active. The game
turns maps on and off with `InputMap.IsActive`, such as the map of a vehicle
when the player gets in. A key may be bound in two maps that are not active
together. An action whose map turns off reads as released from the next
frame on; an action whose map turns on waits for a new press.

### Layers

Modules that read input register a layer: the debug overlay, the screens, the
heads-up display, and the player controller. Input goes from the layer of
highest priority to the lowest; among layers of the same priority, the one
registered last receives it first.

```csharp
_layer = input.AddLayer(context, new InputLayerOptions
{
    Name = "inventory",
    Priority = InputLayerPriority.Modal,
    Capture = InputCapture.All,
    Cursor = CursorRequest.Cursor,
    Handler = this,
});
```

| Priority | Value | Layer |
| --- | --- | --- |
| `InputLayerPriority.Debug` | 400 | The debug overlay. |
| `InputLayerPriority.Modal` | 300 | Modal screens: inventory, pause menu, chat. |
| `InputLayerPriority.Hud` | 200 | The heads-up display. |
| `InputLayerPriority.Game` | 100 | The player controller. |

Each layer evaluates the actions from the input that reaches it. For each
key or button pressed, in order:

1. a layer that asks for raw input receives the event; consuming it keeps it
   from the actions of the layer and from the layers below;
2. the actions of the layer see it; a handler that consumes a press keeps the
   key from the layers below;
3. a layer that captures the device keeps the event from the layers below.

Movements of the mouse and of the wheel go down the same way. A release
reaches every layer that saw the press, whatever happens above. When a
device stops reaching a layer, because a layer above captures it, takes the
text focus, or the layer is disabled, the actions of the layer are released
at the next frame and it forgets the keys held; a toggle stays on. The same
happens to every layer when the window loses the focus.

At the end of each frame, the highest enabled layer that asks for the cursor,
asks for relative mode, or captures the mouse decides the mode of the mouse:
relative mode when it asks for it, the cursor otherwise. Without such a
layer, the mouse is not in relative mode.

The handler of a layer runs on the loop thread. An exception it throws is
charged to the module of the layer, and the input passes.

### Text input

`InputLayer.TakeTextFocus` gives a layer the text focus, for a chat field: the
window starts text input, typed text and the compositions of input methods go
to `IInputLayerHandler.OnText` of that layer only, the keyboard reaches no
layer below it, and no keyboard input triggers an action in any layer. A
layer that asks for raw input still receives the keys, for Enter and
Backspace.
`ReleaseTextFocus`, disabling the layer, or removing it gives the focus back.

### Reading actions

`InputLayer.Read` gives the state of an action in the frame: held, presses,
releases, time held, and value. The camera reads the movement of the mouse of
each frame there, for a smooth rotation at any tick rate.

Code that runs at the fixed tick, such as the player controller in stage
`Intents` of nGame, reads through a sampler:

```csharp
_sampler = _layer.CreateSampler();

public void Run(in TickTime time)
{
    _sampler.Sample();
    if (_sampler.Read(_jump).WasPressed)
    {
        // The input of this tick jumps.
    }
}
```

Each `Sample` gathers what happened since the previous one: a key pressed and
released between two ticks reads as pressed and released, and the movements
of the mouse and of the wheel add up. When several ticks run in one frame,
the first receives the input of the frame and the next ones only what is
still held. nInput knows neither the network nor the format of the input of a
tick.

### Bindings of the player

`IInputSystem.Rebinding` changes the bindings of an action (`SetBindings`,
`Rebind`, `RemoveBinding`), its interaction (`SetInteraction`, such as a hold
turned into a toggle), and goes back to the defaults (`ResetToDefault`,
`ResetAllToDefault`). Changes apply at the next frame.

- `FindConflicts` lists the actions of the same map, of maps active together,
  or without a map, with a binding triggered by the same input. The user
  interface decides whether to refuse or to swap.
- `Listen` captures the next input for a rebinding screen: a key or a
  button with the modifiers held, a modifier released alone, a step of the
  wheel, or a movement of the mouse, as the kind of value of the action
  allows. Escape cancels. While it listens, the input reaches no layer.
- `Save` writes `config/input.xml` in the configuration folder, with the
  differences from the defaults only, by action identifier: an action whose
  default changes, after an update or with a mod, is not frozen by an old
  file. The format is described in [its
  reference](../../docs/reference/formats/input-config.md).

nInput reads the file while it initializes. An entry for an action that no
module declares is ignored with a warning, and written back as it was by the
next `Save`; an entry that cannot apply is ignored with a warning that names
the line.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `input.invertY` | user | bool | `false` |  | client |
| `input.mouseSensitivity` | user | float | `1` | 0.01 to 100 | client |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `input.mouseSensitivity` | Multiplies the movements of the mouse that actions read: `Mouse.Delta`, `Mouse.DeltaX`, `Mouse.DeltaY`. The wheel is not affected. |
| `input.invertY` | Reads a movement of the mouse up as down. |

Both apply from the frame after a change.

## Resources

The resource types `InputAction` and `InputMap`, described in [their
reference](../../docs/reference/formats/input-action.md). The plugin
`Nobara.Input.Build` checks what the schema cannot: the names of the
controls, the form of each binding, the bindings, interaction, and threshold
that fit the kind of value, and durations above 0 (`NINP001` to `NINP006`).
Another module may override an action, to change its default bindings for
example.

In development mode, nInput applies reloaded actions in place at the next
frame: bindings, interaction, threshold, map, and name. The choices of the
player stay. A new or removed action or map, a changed kind of value, and a
changed `<Active>` of a map are refused with a warning and need a restart.

## Constraints

- Client only: a dedicated server runs without nInput.
- Use the service, the layers, and the samplers on the loop thread of the
  client context. nInput evaluates the input at the start of each frame,
  right after nFrames pumps the events, before the first phase.
- Reading an action, sampling, and evaluating a frame allocate nothing.
  Registering a layer or a sampler allocates its state; changing the
  bindings or the interaction of an action, and reloading, rebuild the index
  of the bindings at the next frame.
- nInput keeps no static state.

## Known limitations

- No gamepad is read: gamepad bindings and `<DeadZone>` are accepted and
  checked, and give nothing.
- No touch input.
- The time of an event is the time of the frame that reads it: two events of
  one frame happen at the same time.
- A step of the wheel cannot be held, so it can only be the last key of a
  combination.
- A layer handler cannot consume the edges that the end of a frame causes: a
  hold that passes its duration, an axis that becomes held.
- Changing the bindings or the interaction of an action, or reloading
  actions, releases the actions of every layer.
- A listening for a 1D axis does not take a modifier key alone.
- `GetLayoutName` names keys of one side only; the game names the other
  controls.
