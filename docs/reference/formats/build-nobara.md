# The `build.nobara` file

This page is the reference for `build.nobara`, the file at the root of a game
that declares its build targets. It is read when the game is built and is not
compiled into the distribution.

## Example

```xml
<NobaraBuild format="1">
  <Target name="client-win">
    <Platform>win-x64</Platform>
    <Side>client</Side>
  </Target>
  <Target name="server-win">
    <Platform>win-x64</Platform>
    <Side>server</Side>
    <Disable module="example.debug.tools" />
  </Target>
</NobaraBuild>
```

## Root element

`<NobaraBuild>` is the root element. Its required `format` attribute holds the
version of the file format; the current format is `1`. It holds `<Target>`
elements.

## `<Target>`

| Attribute or element | Required | Content |
| --- | --- | --- |
| `name` | Yes | Name of the target: letters, digits, dots, dashes, and underscores. Unique without regard to case. |
| `<Platform>` | Yes | .NET runtime identifier, such as `win-x64`. |
| `<Side>` | Yes | `client` or `server`, without regard to case. |
| `<Disable module="…"/>` | No | A module of the distribution left out of this target. A target also disables the backends it does not need this way. |
| `<Variants>` | No | Reserved: `<BattlEye/>` and `<Play/>` are accepted with a warning and not built. |

The modules of a target are those whose side includes the side of the target,
minus the disabled ones. A required module cannot be disabled, and a module
that another active module requires cannot be disabled either.

## Known limitations

- The compilation mode and the output format of a target are not part of the
  format.
- Variants are not built.
