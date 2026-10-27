# The `nobara` command-line tool

This page is the reference for `nobara`, the tool that generates the solution
of a game, builds and packs its targets, and keeps the generated sections of
the module sheets up to date. How it works inside is described in [the tool
page](../../tools/Nobara.Cli/README.md).

## Usage

```text
nobara <command> [options]
```

| Option | Applies to | Effect |
| --- | --- | --- |
| `-C`, `--directory <dir>` | Every command | Runs in this folder instead of the current one. |
| `--check` | `generate`, `docs` | Fails when a file kept in version control is out of date, without changing it. Files under `obj/` are still written. |
| `--development` | `build` | Builds for development: see [development builds](#development-builds). |
| `-h`, `--help` | | Shows the usage. |
| `--version` | | Shows the version of the tool. |

| Exit code | Meaning |
| --- | --- |
| 0 | The command succeeded. |
| 1 | The command found problems, reported on the error output. |
| 2 | The command line cannot be understood. |

Problems name their file, with its line and column when they are known, as
in `engine.nobara(4,6): error NRC212: <Set> names the unknown key
'example.mobs.cuont'`. The codes are listed in [the resource compiler
page](../../tools/Nobara.ResourceCompiler/README.md#diagnostics) and [the tool
page](../../tools/Nobara.Cli/README.md#errors).

## `nobara generate`

In the folder of a game, next to [`project.nobara`](formats/project-nobara.md),
resolves the modules of the game and writes:

| File | Kept in version control | Content |
| --- | --- | --- |
| `<Name>.slnx` | Yes | The solution: the projects of the local modules and of the overrides, their auxiliary projects, and the entry assembly. |
| [`project.nobara.lock`](formats/project-nobara-lock.md) | Yes | The modules of the game, transitive ones included, with their versions. |
| `obj/nobara/modules.props` | No | The [module map](../../tools/Nobara.Sdk/README.md#local-modules) that `Nobara.Sdk` reads. |
| `obj/nobara/entry/` | No | The project of the entry assembly, its `main()` for each side, and the icon of the executables. |

`<Name>` is the `<Name>` of the game, without the characters that a file name
cannot hold. The modules of the game are its `<Local>` and `<Package>`
entries and, transitively, the modules they require; optional dependencies
are not pulled in. Packages are restored with NuGet, and the version of the
lock file is kept while it satisfies every range.

In a folder without `project.nobara` that has a `src/` or a `tools/` folder,
such as the engine repository, `generate` writes the solution `Nobara.slnx`
from the projects `src/<folder>/<name>/<name>.csproj` and
`tools/<name>/<name>.csproj`, and the module map from those that have a
`module.nobara`.

## `nobara build [target]`

Builds one target of [`build.nobara`](formats/build-nobara.md), or every
target. It runs `generate` first, then, for each target:

1. checks the modules of the target and [`engine.nobara`](formats/engine-nobara.md);
2. compiles and publishes the modules of the target and the entry assembly;
3. arranges the installed game in `out/<target>/`;
4. records the hash of each module and library in the distribution manifest;
5. packs `out/<target>.zip`.

A client target ships the modules of both sides, since a client hosts an
integrated server; a server target ships those of the server side. The
modules that a target disables are left out.

The build stops at the first problem, before anything is compiled when the
composition is refused:

| Refusal | Code |
| --- | --- |
| A required dependency is missing, or disabled by the target. | `NRC201`, `NCLI004` |
| A module or a dependency is outside its version range. | `NRC202` |
| A platform module depends on a business module. | `NRC203` |
| The dependencies form a cycle. | `NRC204` |
| Two active modules conflict. | `NRC205` |
| Two active modules reserve the same resource. | `NRC206` |
| A short name, a configuration key, or a state is declared twice. | `NRC207` to `NRC209` |
| An initial state is not provided by an active module of its side. | `NRC210` |
| A base module has no active backend on a side where it requires one. | `NRC211` |
| `engine.nobara` names an unknown key, gives an invalid value, or names an absent module. | `NRC212`, `NRC213`, `NRC216` |
| An error policy is not allowed, or the cascade of a `disable` or `log` module would stop the game. | `NRC214`, `NRC215` |
| The target is unknown, disables an unknown or required module. | `NRC217` |
| The target is for a platform other than `win-x64`. | `NCLI001` |
| Two modules need incompatible versions of a library. | `NCLI002` |

### The installed game

```text
out/<target>/
  bin/            .NET runtime, core, entry assembly with the distribution manifest
  lib/            libraries that modules use
  system/         platform modules
  game/           business modules
  <Name>.exe      client target; <Name>Server.exe for a server target
```

The archive `out/<target>.zip` holds the same files under a folder named
after the executable. At start-up, the core refuses to load a module or a
library whose file differs from the one the build hashed, and names the file.

### Development builds

`nobara build <target> --development` builds with the `Debug` configuration,
without ReadyToRun, and does not pack the target. Its executable accepts
`--hot-reload`: the game then runs in development mode and reloads the `.nof`
files of the modules built from projects when they change. See [the hot reload
page](../../tools/Nobara.HotReload/README.md). A release executable refuses
`--hot-reload`.

## `nobara package [target]`

Packs the installed game of one target, or of every target, into
`out/<target>.zip`, as the last step of `build` does. The target must have
been built.

## `nobara docs`

Rewrites, in each `src/<folder>/README.md`, the sections between
`nobara:generated` markers, from the `module.nobara` of the module of the
folder:

```markdown
<!-- nobara:generated:begin dependencies -->
<!-- nobara:generated:end -->
```

| Section | Content |
| --- | --- |
| `level` | Level and side, the sides where the module requires a backend, and the module it is a backend of. |
| `dependencies` | Required and optional dependencies with their ranges, and third-party libraries. |
| `states` | States provided, with their side. |
| `configuration` | Configuration keys: scope, type, default, bounds or values, side. |

The module of a folder is the one whose identifier starts the identifiers of
the others, such as `example.voxel` for `example.voxel.renderer`. A marker
can name another module of the folder after its section:
`<!-- nobara:generated:begin level example.voxel.renderer -->`. The sheet of
the core, which is not a module, gets fixed text.

With `--check`, `docs` fails when a section is out of date; the CI runs it.

## Known limitations

- Only `win-x64` is built, and only the main executable of each side.
- `nobara test`, `nobara run`, and `nobara new` do not exist yet.
