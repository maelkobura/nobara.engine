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

The SDK imports `Microsoft.NET.Sdk`, reads and validates `module.nobara` next to
the project file, and sets what the project file leaves out:

- the assembly name and the package identifier, which are the project name;
- the version of the assembly and of the package, which is `<Version>`;
- one reference per module in `<Dependencies>`: a project reference when the
  module map names a local project for it, a package reference with the
  declared range otherwise;
- one package reference per `<Package>` in `<Libraries>`;
- `net10.0` as the target framework, unless the project file sets one;
- the [engine analyzers](#engine-analyzers);
- the compilation of the [schemas and resources](#schemas-and-resources) of the
  module, embedded in its assembly.

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
| `NobaraEngineAnalyzers` | `true` for modules and `.Scripting` projects, `false` otherwise | Read only. |
| `NobaraResourcesDirectory` | `Resources` in the project directory | Folder of the resources. |
| `NobaraProtobufVersion` | `3.33.0` | Version of `Google.Protobuf` added to modules with schemas. |
| `NobaraGrpcToolsVersion` | `2.80.0` | Version of `Grpc.Tools` added to modules with schemas. |

## Optional dependencies

An optional dependency is an ordinary reference. The SDK also passes the
identifiers of the optional dependencies to the C# compiler in the
`NobaraOptionalModules` build property, which an analyzer reads as
`build_property.NobaraOptionalModules` in the global analyzer config options.
An analyzer compares an identifier with an assembly name without case. The
engine analyzers use it to keep the types of an optional dependency inside
isolation classes (see [NOB401](../../docs/reference/analyzers.md#nob401)).

## Engine analyzers

The SDK package contains the engine analyzers, built from
[`tools/Nobara.Analyzers`](../Nobara.Analyzers/README.md), in its `analyzers/`
folder. The `NobaraAttachEngineAnalyzers` target, which runs through the
`NobaraAnalyzers` extension point, adds them to the compilation when
`NobaraEngineAnalyzers` is `true`:

| Project | Engine analyzers |
| --- | --- |
| Module, extensions included | Yes |
| `.Scripting` | Yes: script facades run in a context. |
| `.Build`, `.Generators`, `.Analyzers`, `.TypeGen` | No: these tools run at build time. |
| `.Tests`, `.Benchmarks` | No |

The diagnostics are listed in [the analyzer reference](../../docs/reference/analyzers.md).

## Schemas and resources

A module may hold `.proto` schemas in `Schemas/` and resources in
`Resources/`. The SDK:

1. adds `Google.Protobuf` and `Grpc.Tools` to a module that has schemas, and
   generates the C# classes of the schemas with `protoc`, with an internal
   copy of the classes of `nobara/options.proto`, which the SDK package
   provides in `protos/`;
2. builds the descriptor set of the schemas (`NobaraCompileSchemaSet`);
3. compiles `module.nobara` and `Resources/**/*.nof` with the [resource
   compiler task](../Nobara.ResourceCompiler.MSBuild/README.md)
   (`NobaraCompileResources`), against the schemas and resources of the
   referenced modules;
4. embeds the results in the assembly (`NobaraEmbedResources`).

Every module embeds `nobara/module.pb` and `nobara/resources.pb`; a module with
schemas also embeds `nobara/schemas.pb`, and the binary files referenced by its
resources are embedded as `nobara/files/<path>`.

The plugin of a dependency is the project `Nobara.X.Build` in the folder of
the dependency `Nobara.X`, next to its project. When it exists, the SDK adds
it as a project reference that does not reference its assembly, so that it is
restored and built first, and passes its output to the resource compiler.

`NobaraCompileResources` is skipped when none of its inputs changed: the
`.nof` files, the other files of `Resources/`, `module.nobara`, the descriptor
set, the referenced assemblies, the plugins, and the task. When it runs, the
compiler parses again only the files that changed.

The formats are described in [the `.nof`
reference](../../docs/reference/formats/nof.md) and [the schema options
reference](../../docs/reference/formats/proto-options.md).

## Extension points

The SDK defines three targets that run before `CoreCompile`, in this order.
Another tool plugs in by adding its own targets to the matching `DependsOn`
property.

| Target | Property | Use |
| --- | --- | --- |
| `NobaraAnalyzers` | `NobaraAnalyzersDependsOn` | Attach analyzers. The SDK attaches the engine analyzers here. |
| `NobaraSchemas` | `NobaraSchemasDependsOn` | Process the `NobaraSchema` items, which default to `Schemas/**/*.proto`. The SDK builds their descriptor set here. |
| `NobaraResources` | `NobaraResourcesDependsOn` | Compile and embed resources. The SDK runs the resource compiler here. |

## Errors

Errors name `module.nobara` and the line when the position is known.

| Code | Meaning |
| --- | --- |
| `NSDK001` | `module.nobara` is missing for a project that is not an auxiliary project. |
| `NSDK005` | `<Id>` does not match the project name. |
| `NRC100` to `NRC110` | The manifest is invalid; see [the resource compiler page](../Nobara.ResourceCompiler/README.md#diagnostics). |
| `NRC001` to `NRC602` | A resource or a schema is invalid, from the resource compiler. |

## Building and testing

```sh
dotnet build Nobara.slnx
dotnet test Nobara.slnx
```

The tests in `tools/Nobara.Sdk.Tests` pack the SDK into a temporary feed and
build sample projects against it. They need no network: the protobuf packages
that modules with schemas use come from the global packages folder, where
restoring the solution puts them.

## Known limitations

- The version range of a dependency is not checked against the version of a
  local project.
- Plugins of dependencies restored as packages are not found.
- The resource compiler runs in a .NET task host: an MSBuild that cannot start
  one cannot build modules.
- The module map is written by hand.
- The SDK is not published as a package.
