# The `build.nobara` file

This page is the reference for `build.nobara`, the file at the root of a game
that declares its build targets. It is read when the game is built, by
[`nobara build`](../cli.md#nobara-build-target), and is not compiled into the
distribution.

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

A client target holds the modules of both sides, because a client hosts an
integrated server, and is checked on both sides; a server target holds the
modules whose side includes the server. The disabled modules are left out of
either. A required module cannot be disabled, and a module that another
active module requires on a side of the target cannot be disabled either.

`win-x64` is the only platform that the command-line tool builds in this
version.

## Known limitations

- The compilation mode and the output format of a target are not part of the
  format: the command-line tool chooses them, release or
  [development](../cli.md#development-builds), and a zip archive.
- Variants are not built.
