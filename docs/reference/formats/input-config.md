# The `config/input.xml` file

`input.xml` holds the bindings that the player changed, as
[nInput](../../../src/nInput/README.md) writes them with
`IInputRebinding.Save`. It lives in the configuration folder of the game,
next to `settings.xml`. It is readable XML: a player may correct it by hand
while the game is closed.

## Example

```xml
<?xml version="1.0" encoding="utf-8"?>
<InputBindings format="1">
  <Action id="inputaction:example.input.jump">
    <Bindings>
      <Key>Key.J</Key>
      <Combo><Key>Key.Shift</Key><Key>Mouse.Right</Key></Combo>
    </Bindings>
  </Action>
  <Action id="inputaction:example.input.sprint">
    <Interaction><Toggle/></Interaction>
  </Action>
</InputBindings>
```

## Content

The root element `<InputBindings>` takes the attribute `format`, `1`, and
holds one `<Action>` element per action that differs from its defaults:

| Element | Content |
| --- | --- |
| `<Action id="…">` | The object identifier of the action, such as `inputaction:example.input.jump`. Each action appears once. |
| `<Bindings>` | When present, every binding of the action, in the form of [the `InputAction` resource](input-action.md#bindings): `<Key>`, `<Axis>`, `<Composite1D>`, `<Composite2D>`, `<Combo>`. They replace the defaults; an empty `<Bindings/>` unbinds the action. When absent, the default bindings apply. |
| `<Interaction>` | When present, the interaction the player chose, in the form of [the resource](input-action.md#interactions). When absent, the default interaction applies. |

Only the differences are written: an action whose default changes, after an
update or with a mod, takes its new default unless the player changed it.

## Reading

nInput reads the file when it initializes. A file that is not XML, or not of
format 1, leaves every default, with a warning. Each of these is ignored
with a warning that names the line:

- an entry for an action that no module declares; it is kept, and written
  back as it was by the next save;
- a second entry for the same action;
- bindings that name an unknown control, that do not fit their form, or that
  cannot give the value of the action; the default bindings apply;
- an interaction that is not valid, or one for an axis;
- an element other than `<Bindings>` and `<Interaction>` in an entry.

## Writing

`Save` writes the whole file again, atomically: entries of the actions that
differ from their defaults, sorted by identifier, then the kept entries of
unknown actions. The configuration folder is created when it is missing.
