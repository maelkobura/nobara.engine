# The `project.nobara` file

This page is the reference for `project.nobara`, the file at the root of a
game that declares its structure: identity, modules, required modules,
protected namespaces, and modding. With [`engine.nobara`](engine-nobara.md),
it is compiled into the distribution manifest.

## Example

```xml
<NobaraProject format="1">
  <Id>example.game</Id>
  <Name>Example Game</Name>
  <Version>0.1.0</Version>
  <Icon>assets/icon.png</Icon>

  <Modules>
    <Local path="src/Example.Mobs" />
    <Local path="src/Example.Mobs.Zombie" required="true" />
    <Package id="nobara.voxel" version="^1.2" required="true" />
    <Package id="nobara.renderer" version="^1.0" />
  </Modules>

  <Overrides>
    <Project id="nobara.voxel" path="../engine/src/nVoxel/Nobara.Voxel" />
  </Overrides>

  <ProtectedNamespaces>
    <Namespace>Example/*</Namespace>
  </ProtectedNamespaces>

  <Modding enabled="false" />
</NobaraProject>
```

## Root element

`<NobaraProject>` is the root element. Its required `format` attribute holds
the version of the file format; the current format is `1`. Every element and
attribute must be one of those listed on this page.

## Elements

| Element | Required | Content |
| --- | --- | --- |
| `<Id>` | Yes | Identifier of the game: lowercase dot-separated names. |
| `<Name>` | Yes | Display name of the game. |
| `<Version>` | Yes | Semantic version of the game. |
| `<Icon>` | No | Path of the icon (PNG, at most 256x256 pixels), relative to the file; the icon of the executables. |
| [`<Modules>`](#modules) | Yes | Modules chosen by the game. |
| [`<Overrides>`](#overrides) | No | Packages replaced by projects. |
| `<ProtectedNamespaces>` | No | `<Namespace>` elements: namespaces of resources that mods cannot override or extend, such as `Example/Blocks` or `Example/*`. |
| `<Modding>` | No | `enabled="true"` or `"false"`; `false` by default. |

## `<Modules>`

| Entry | Attributes | Content |
| --- | --- | --- |
| `<Local>` | `path`, `required` | A module of the solution: the folder of its project and `module.nobara`, relative to the file. |
| `<Package>` | `id`, `version`, `required` | A module restored as a NuGet package, with a [version range](module-nobara.md#version-ranges). |

`required="true"` marks a required module: its error policy is always `stop`,
and a build target cannot disable it. Each folder and each identifier appears
once. Modules that the listed modules require, and that are not listed, are
part of the distribution too: an override provides them when `<Overrides>`
names them, a package otherwise. [`nobara generate`](../cli.md#nobara-generate)
records every module with its version in
[`project.nobara.lock`](project-nobara-lock.md).

## `<Overrides>`

`<Project id="…" path="…"/>` replaces the package of a module by a project,
whose path is relative to the file. It serves to develop the engine and a
game together. When an override points into a clone of the engine, the game
also builds against the core of that clone.

## Known limitations

- The detailed form of `<Modding>` is not defined; only `enabled` is read.
