# Nobara.Cli

`Nobara.Cli` is the command-line tool `nobara`. It turns the folder of a game
into a generated solution, then into an installed game and a zip archive for
each build target, and refuses any composition of modules that cannot run. In
the engine repository, it generates the solution and keeps the generated
sections of the module sheets up to date. Every command is described in [the
command-line reference](../../docs/reference/cli.md).

## Running it

From a clone of this repository:

```sh
dotnet run --project tools/Nobara.Cli -- generate
```

As a local tool of a repository, from a package in a local feed:

```sh
dotnet pack tools/Nobara.Cli -c Release -o artifacts/packages
dotnet tool restore
dotnet nobara --help
```

The tool manifest `.config/dotnet-tools.json` of this repository names the
package `Nobara.Cli`. A game that builds against a clone of the engine adds
the same entry to its own manifest, and the feed of the clone to its
`NuGet.config`.

## What it does

| Command | Steps |
| --- | --- |
| `generate` | Resolves the modules of the game, then writes the solution, `obj/nobara/modules.props`, `project.nobara.lock`, and the entry assembly in `obj/nobara/entry/`. In the engine repository, writes `Nobara.slnx` and `obj/nobara/modules.props`. |
| `build` | Runs `generate`, then, for each target: checks the target, publishes its modules and the entry assembly, arranges the installed game in `out/<target>/`, records the hashes in the distribution manifest, and packs `out/<target>.zip`. |
| `package` | Packs a built target again. |
| `docs` | Rewrites the generated sections of `src/*/README.md`. |

### Resolution of the modules

The modules of a game are its local modules, the packages it lists, and the
modules they require, transitively. A required dependency that no entry of
`project.nobara` provides is a module of the game too: an override provides
it when `<Overrides>` names it, a NuGet package otherwise. Optional
dependencies are never pulled in, and backends never are either, since no
module depends on a backend.

Packages are restored with NuGet through a project generated in
`obj/nobara/restore/`, so the `NuGet.config` and the package cache of the
game apply. The version recorded in `project.nobara.lock` is kept while it
satisfies every range that requires the package; otherwise NuGet chooses the
lowest version that does. The manifest of a package module is read from its
assembly.

When an override points into a clone of the engine, the folder above it that
holds `src/Core/Nobara.Core/Nobara.Core.csproj`, the game builds against the
sources of that clone: the module map sets `NobaraCoreProject`, and the entry
assembly references the projects of the core and of the hot reload library.
Otherwise both are the packages `Nobara.Core` and `Nobara.HotReload`, in the
version of the tool.

### The entry assembly

`nobara generate` writes the project of the entry assembly,
`obj/nobara/entry/<Name>.csproj`, whose name is the name of the game without
spaces, and its `Program.g.cs`, which holds the `main()` of the client and of
the server. Each calls `Engine.Run`. A build of a target writes
`obj/nobara/entry/target.props`, imported by the project, with the platform,
the `main()` of the side of the target, the modules of the target, and the
distribution manifest to embed as `nobara/distribution.pb`.

### The installed game

A build publishes the entry assembly with the modules of the target:
self-contained, ReadyToRun outside development builds. It then arranges the
output from the dependency file of the entry assembly:

| Folder | Content |
| --- | --- |
| `bin/` | The .NET runtime, the core and its libraries, the entry assembly, and its dependency file, which names only what `bin/` holds. |
| `system/`, `game/` | The assembly of each module of the target, by level. |
| `lib/` | The libraries that only modules use. |
| Root | `<Name>.exe` for a client target, `<Name>Server.exe` for a server target. |

The executable is the launcher that `dotnet publish` produced, changed to
start `bin/<entry>.dll`. Modules of the distribution that the target leaves
out, and build-time assemblies (`.Build`, `.Generators`, `.Analyzers`,
`.TypeGen`, `.Tests`, `.Benchmarks`), are not installed.

The SHA-256 of each module assembly and of each file of `lib/` goes into the
distribution manifest, which the entry assembly is then built again to
embed. The core checks them before it loads anything.

### The modules of a target

A client target ships the modules of both sides, because a client hosts an
integrated server, and is checked on both sides. A server target ships the
modules of the server side. The modules that the target disables are left
out of both.

## Errors

Errors name the file, with its line and column when they are known, or the
module. The codes `NRC…` come from [the resource
compiler](../Nobara.ResourceCompiler/README.md#diagnostics); the tool adds:

| Code | Meaning |
| --- | --- |
| `NCLI001` | A target is for a platform that this version does not build. |
| `NCLI002` | Two modules need versions of a library that no single version satisfies. |
| `NCLI003` | A module folder has no project file named after the module. |
| `NCLI004` | The module packages cannot be restored. |

The exit code is 0 on success, 1 when problems were found, and 2 when the
command line cannot be understood.

## Building and testing

```sh
dotnet build tools/Nobara.Cli
dotnet test tools/Nobara.Cli.Tests
```

The tests of generation, refusals, and module sheets run in scratch folders.
The tests of a whole build pack the core, the resource compiler, and the hot
reload library into a scratch feed, build the sample game of
`tools/Nobara.Cli.Tests/SampleGame/` for its two targets, and run the server
executable. They need the SDK in `artifacts/packages` and, once, the .NET
runtime packs from nuget.org.

## Known limitations

- Only the platform `win-x64` is built, and only the main executable: no
  variants, no Native AOT.
- The icon of the game becomes the icon of the executables; it must be a PNG
  image of at most 256x256 pixels.
- Module packages are found only when their assembly is named after the
  module; their plugins are not found.
- A module project compiles against the lowest version of a package that its
  range admits, while the installed game holds the version of the lock file.
- A development build watches the resources of the modules built from sources
  in their default `Resources/` folder, with the plugins of their
  dependencies as built in the `Debug` configuration.
- `package` packs the last build of a target; it does not build it.
