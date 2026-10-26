# The `module.nobara` file

This page is the reference for the parts of `module.nobara` that
[`Nobara.Sdk`](../../../tools/Nobara.Sdk/README.md) reads. The SDK ignores every
other element without checking it.

`module.nobara` sits next to the project file of a module, including an
extension. It is the only place that declares the identity, version, and
dependencies of the module. The project file declares none of them.

## Example

```xml
<NobaraModule format="1">
  <Id>nobara.voxel</Id>
  <Version>1.2.0</Version>

  <Dependencies>
    <Module id="nobara.game" version="^1.0" />
    <Module id="nobara.lighting" version="^1.0" optional="true" />
  </Dependencies>

  <Libraries>
    <Package id="K4os.Compression.LZ4" version="1.3.8" />
  </Libraries>
</NobaraModule>
```

## Root element

`<NobaraModule>` is the root element. Its `format` attribute holds the version
of the file format. The SDK reads format `1` and reports an error when the
attribute has any other value. XML comments are allowed anywhere.

## `<Id>`

Required. The hierarchical name of the module, written in lowercase: `nobara.voxel`,
`nobara.voxel.renderer`. It is the identifier of the NuGet package, the name of
the assembly, and the name of the project, compared without case: the project
`Nobara.Voxel` holds the module `nobara.voxel`.

## `<Version>`

Required. A semantic version with three numbers and an optional pre-release
suffix, and optionally build metadata: `1.2.0`, `2.0.0-beta.1`, `1.0.0+7`. It becomes the version of the assembly and of
the package.

## `<Dependencies>`

Optional. One `<Module>` element per module that this module depends on.

| Attribute | Required | Content |
| --- | --- | --- |
| `id` | Yes | The identifier of the module. It differs from the module's own `<Id>`, and appears once. |
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

Optional. One `<Package>` element per third-party NuGet package that is not a
module.

| Attribute | Required | Content |
| --- | --- | --- |
| `id` | Yes | The package identifier. It appears once. |
| `version` | Yes | The version, written as a NuGet version: the minimum accepted version. |

## Errors

The SDK reports a problem as `module.nobara(line,column): error NSDKnnn:`. The
codes are listed in [the SDK page](../../../tools/Nobara.Sdk/README.md#errors).

## Known limitations

- The SDK does not validate `<ShortName>`, `<Level>`, `<Side>`, `<Conflicts>`,
  `<Reserves>`, `<States>`, or `<Settings>`.
- A version range is not checked against the version of a local project.
