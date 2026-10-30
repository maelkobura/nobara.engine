# The `module.nobara` file

This page is the reference for `module.nobara`, the manifest of a module.
[`Nobara.Sdk`](../../../tools/Nobara.Sdk/README.md) reads it before every
build, validates it, and turns its dependencies into references; the resource
compiler embeds its compiled form in the assembly of the module.

`module.nobara` sits next to the project file of a module, including an
extension. It is the only place that declares the identity, version, and
dependencies of the module. The project file declares none of them.

## Example

```xml
<NobaraModule format="1">
  <Id>nobara.voxel</Id>
  <ShortName>Voxel</ShortName>
  <Version>1.2.0</Version>
  <Level>platform</Level>
  <Side>both</Side>

  <Dependencies>
    <Module id="nobara.game" version="^1.0" />
    <Module id="nobara.lighting" version="^1.0" optional="true" />
  </Dependencies>

  <Libraries>
    <Package id="K4os.Compression.LZ4" version="1.3.8" />
  </Libraries>

  <Conflicts>
    <Module id="example.othervoxel" />
  </Conflicts>

  <Reserves>
    <Resource name="voxel.world" />
  </Reserves>

  <States>
    <State id="state:nobara.voxel.debug_world" side="client" />
  </States>

  <Settings>
    <Setting key="voxel.viewRadius" scope="user" type="int" min="32" max="1024" default="256" side="client" />
    <Setting key="voxel.maxLoadedChunks" scope="distribution" type="int" min="1" default="65536" />
  </Settings>
</NobaraModule>
```

## Root element

`<NobaraModule>` is the root element. Its required `format` attribute holds
the version of the file format; the current format is `1`. Every element and
attribute must be one of those listed on this page. Each element appears at
most once, except the entries of the lists. XML comments are allowed anywhere.

## Elements

| Element | Required | Content |
| --- | --- | --- |
| [`<Id>`](#id) | Yes | Identifier of the module. |
| `<ShortName>` | No | Short name: letters, digits, and underscores, starting with a letter, without dots. It qualifies free properties (`<Motion.Weight>`) and may prefix configuration keys. |
| `<Version>` | Yes | A semantic version with three numbers, an optional pre-release suffix, and optional build metadata: `1.2.0`, `2.0.0-beta.1`, `1.0.0+7`. It becomes the version of the assembly and of the package. |
| `<Level>` | Yes | `platform` or `business`. A platform module never depends on a business module. |
| `<Side>` | Yes | `client`, `server`, or `both`. |
| [`<Dependencies>`](#dependencies) | No | Modules this module depends on. |
| [`<Libraries>`](#libraries) | No | Third-party NuGet packages that are not modules. |
| `<Conflicts>` | No | `<Module id="…"/>` entries: modules this module cannot run with. |
| `<Reserves>` | No | `<Resource name="…"/>` entries: exclusive resources this module holds, such as `window.main`. Names are letters, digits, dots, dashes, and underscores. |
| [`<States>`](#states) | No | Context states the module provides. |
| [`<Settings>`](#settings) | No | Configuration keys. |
| [`<RequiresBackend>`](#backends) | No | Sides where this base module needs a backend. |
| [`<BackendOf>`](#backends) | No | The base module this module is a backend of. |

Values of `<Level>`, `<Side>`, and the `side`, `scope`, and `type` attributes
are compared without regard to case.

## `<Id>`

The hierarchical name of the module: names of letters, digits, and
underscores separated by dots, each starting with a letter, such as
`nobara.voxel` or `nobara.voxel.renderer`. It is written in lowercase. It is
the identifier of the NuGet package, the name of the assembly, and the name of
the project, compared without case: the project `Nobara.Voxel` holds the
module `nobara.voxel`.

## `<Dependencies>`

One `<Module>` element per module that this module depends on.

| Attribute | Required | Content |
| --- | --- | --- |
| `id` | Yes | The identifier of the module. It differs from the module's own `<Id>`, and appears once, compared without case. |
| `version` | Yes | The accepted versions, as a range (see below). |
| `optional` | No | `true` or `false`; `false` by default. |

A dependency is a project reference when the module map names a local project
for the identifier, and a package reference with the range otherwise. An
optional dependency is referenced in the same way. Its identifier is also
exposed to the C# compiler in the `NobaraOptionalModules` build property, so
that the engine analyzers keep the types of the dependency inside isolation
classes (see [NOB401](../analyzers.md#nob401)).

### Version ranges

| Form | Accepts | NuGet range |
| --- | --- | --- |
| `^1.2` | `1.2` up to, not including, `2.0.0` | `[1.2,2.0.0)` |
| `^0.3.1` | `0.3.1` up to, not including, `0.4.0` | `[0.3.1,0.4.0)` |
| `~1.2` | `1.2` up to, not including, `1.3.0` | `[1.2,1.3.0)` |
| `>= 2.0` | `2.0` or later | `[2.0,)` |
| `> 2.0` | Later than `2.0` | `(2.0,)` |
| `<= 3.0` | `3.0` or earlier | `(,3.0]` |
| `< 3.0` | Earlier than `3.0` | `(,3.0)` |
| `>=1.0 <2.0` | A lower and an upper comparator, separated by spaces | `[1.0,2.0)` |

The caret `^` allows changes that keep the leftmost non-zero number, so `^0.3`
stops before `0.4.0` and `^0.0.3` stops before `0.0.4`. A bare version such as
`1.2.3` is not a range and is rejected.

## `<Libraries>`

One `<Package>` element per third-party NuGet package that is not a module.

| Attribute | Required | Content |
| --- | --- | --- |
| `id` | Yes | The package identifier. It appears once. |
| `version` | Yes | The version, written as a NuGet version: the minimum accepted version. |

## `<States>`

One `<State>` element per context state the module provides. The module also
registers each state in code; see [the nContext module
sheet](../../../src/nContext/README.md#states).

| Attribute | Required | Content |
| --- | --- | --- |
| `id` | Yes | `state:` followed by a lowercase namespace and name, such as `state:example.menu`. It appears once. |
| `side` | No | `client` or `server`, a side where the module runs. Without it, the state is provided on every side of the module. |

## `<Settings>`

One `<Setting>` element per configuration key.

| Attribute | Required | Content |
| --- | --- | --- |
| `key` | Yes | Dot-separated names. The key starts with the short name of the module (compared without case) or with its identifier, then a dot: `voxel.viewRadius` or `nobara.voxel.viewRadius`. It appears once. |
| `scope` | Yes | `user`: the player changes it within the limits of the distribution. `distribution`: only the distribution sets it. |
| `type` | Yes | `bool`, `int`, `float`, `string`, or `enum`. |
| `default` | Yes | The default value, valid for the type, the bounds, and the values. |
| `min`, `max` | No | Bounds of an `int` or `float` key, inclusive. |
| `values` | For `enum` | The accepted words, distinct, separated by commas. |
| `side` | No | `client` or `server`, for a key that concerns one side only. |

## Backends

A base module with technical variants declares where it needs a backend, and
each backend declares its base module:

```xml
<!-- The base module -->
<RequiresBackend side="client" />

<!-- A backend -->
<Dependencies>
  <Module id="nobara.audio" version="^1.0" />
</Dependencies>
<BackendOf module="nobara.audio" />
```

| Element | Attribute | Content |
| --- | --- | --- |
| `<RequiresBackend>` | `side` | `client`, `server`, or `both`: sides where the module needs at least one active backend. Each side is a side where the module runs. |
| `<BackendOf>` | `module` | Identifier of the base module. The backend declares a required dependency on it. |

A distribution in which a base module is active on a side where it requires a
backend, without an active backend of it on that side, fails to build.

## Errors

The SDK reports a problem as `module.nobara(line,column): error CODE: message`.
`NSDK001` (missing file) and `NSDK005` (`<Id>` that does not match the project
name) are listed in [the SDK page](../../../tools/Nobara.Sdk/README.md#errors);
the other codes, `NRC100` to `NRC110`, in [the resource compiler
page](../../../tools/Nobara.ResourceCompiler/README.md#diagnostics).

## Known limitations

- A version range is not checked against the version of a local project when
  a single module is built; the distribution checks do it.
