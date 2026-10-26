# Nobara.Sdk

`Nobara.Sdk` is an MSBuild SDK that turns a project file and a `module.nobara`
file into a Nobara module. This page covers what the SDK does, how to use it,
and the properties it recognizes. The file format is described in
[the `module.nobara` reference](../../docs/reference/formats/module-nobara.md).

## What it does

A module project file names the SDK and nothing else:

```xml
<Project Sdk="Nobara.Sdk/1.0.0">
</Project>
```

The SDK imports `Microsoft.NET.Sdk`, reads `module.nobara` next to the project
file, and sets what the project file leaves out:

- the assembly name and the package identifier, which are the project name;
- the version of the assembly and of the package, which is `<Version>`;
- one reference per module in `<Dependencies>`: a project reference when the
  module map names a local project for it, a package reference with the
  declared range otherwise;
- one package reference per `<Package>` in `<Libraries>`;
- `net10.0` as the target framework, unless the project file sets one.

The project file may still set ordinary C# properties such as `<Nullable>`.

## Project kinds

The project name selects the kind of project.

| Kind | Project name | `module.nobara` |
| --- | --- | --- |
| Module | `Nobara.X`, `Nobara.X.Client`, and extensions such as `Nobara.X.Renderer` | Required |
| Auxiliary project | Ends with `.Build`, `.Generators`, `.Analyzers`, `.Scripting`, `.TypeGen`, `.Tests`, or `.Benchmarks` | Not read |

The suffixes also apply to the projects of an extension, as in
`Nobara.X.Renderer.Tests`. An auxiliary project is not a module: it declares a
`ProjectReference` to the project of its module itself. `.Tests` and
`.Benchmarks` projects are not packable.

The project name must match `<Id>` when compared without case, because the
project name is the assembly name and the package identifier.

## Local modules

The module map `obj/nobara/modules.props` associates module identifiers with
local projects. The SDK imports the first such file it finds in the project
directory or in a parent directory, up to twelve levels up.

```xml
<Project>
  <ItemGroup>
    <NobaraLocalModule Include="nobara.game"
                       Project="$(MSBuildThisFileDirectory)../../src/Game/Nobara.Game/Nobara.Game.csproj" />
  </ItemGroup>
</Project>
```

A dependency whose identifier appears in the map is a project reference; the
version range is not checked against the project. Identifiers are compared
without case. `Project` is an absolute path, so write it from
`$(MSBuildThisFileDirectory)`.

The command-line tool is meant to generate this file. Until then it is written
by hand, and `obj/` is not tracked by Git, so each clone writes its own.

## Using the SDK

The SDK is a NuGet package. Resolve it from a local feed:

1. Pack it: `dotnet pack tools/Nobara.Sdk -c Release -o artifacts/packages`.
2. Add the folder to the package sources of the build, as the `NuGet.config` at
   the root of this repository does.
3. Name the SDK and its version in the project file.

Packing removes the extracted copy of that version from the global packages
folder, so the next build uses the SDK that was just packed.

A distribution that builds against a clone of this repository adds the feed of
that clone to its own `NuGet.config` (for example `../nobara/artifacts/packages`)
and maps the engine modules in its module map, with absolute paths into the
clone.

## Properties

| Property | Default | Effect |
| --- | --- | --- |
| `NobaraModuleFile` | `module.nobara` in the project directory | Path of the manifest to read. |
| `NobaraModulesFile` | Nearest `obj/nobara/modules.props` | Path of the module map. Set it in `Directory.Build.props` or on the command line: it is read before the project file body. |
| `NobaraProjectKind` | `Module` or `Auxiliary`, from the project name | Read only. |
| `NobaraModuleVersion` | `<Version>` | Read only. |
| `NobaraOptionalModules` | Identifiers of the optional dependencies, separated by semicolons | Read only. |

## Optional dependencies

An optional dependency is an ordinary reference. The SDK also passes the
identifiers of the optional dependencies to the C# compiler in the
`NobaraOptionalModules` build property, which an analyzer reads as
`build_property.NobaraOptionalModules` in the global analyzer config options.
An analyzer compares an identifier with an assembly name without case.

## Extension points

The SDK defines three empty targets that run before `CoreCompile`, in this
order. Another tool plugs in by adding its own targets to the matching
`DependsOn` property.

| Target | Property | Intended use |
| --- | --- | --- |
| `NobaraAnalyzers` | `NobaraAnalyzersDependsOn` | Attach the engine analyzers. |
| `NobaraSchemas` | `NobaraSchemasDependsOn` | Generate C# classes from the `NobaraSchema` items, which default to `Schemas/**/*.proto`. |
| `NobaraResources` | `NobaraResourcesDependsOn` | Run the resource compiler and embed its output. |

No tool is plugged in yet: these targets do nothing.

## Errors

Errors name `module.nobara` and the line when the position is known.

| Code | Meaning |
| --- | --- |
| `NSDK001` | `module.nobara` is missing for a project that is not an auxiliary project. |
| `NSDK002` | The file is not well-formed XML, its root is not `NobaraModule`, or its `format` is not `1`. |
| `NSDK003` | `<Id>` is missing or empty. |
| `NSDK004` | `<Version>` is missing, or is not a semantic version. |
| `NSDK005` | `<Id>` does not match the project name. |
| `NSDK006` | A `<Module>` dependency is invalid: no `id`, the module itself, a duplicate, or an `optional` that is not `true` or `false`. |
| `NSDK007` | A dependency has no `version`, or its range is invalid. |
| `NSDK008` | A `<Package>` library has no `id` or no `version`, or is a duplicate. |

## Building and testing

```sh
dotnet build Nobara.slnx
dotnet test Nobara.slnx
```

The tests in `tools/Nobara.Sdk.Tests` pack the SDK into a temporary feed and
build sample projects against it. They need no network.

## Known limitations

- The SDK reads only `<Id>`, `<Version>`, `<Dependencies>`, and `<Libraries>`.
  Other elements are ignored, not validated.
- The version range of a dependency is not checked against the version of a
  local project.
- The extension points have no tool behind them.
- The module map is written by hand.
- The SDK is not published as a package.
