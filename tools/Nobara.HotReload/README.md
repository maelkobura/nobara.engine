# Nobara.HotReload

`Nobara.HotReload` reloads the resources of modules while a game runs, in
development builds: the `.nof` files and the files they reference, such as
textures or the GLSL sources of shaders. It watches the sources of the modules built from
projects, compiles a module again with [the resource
compiler](../Nobara.ResourceCompiler/README.md) when one of its files changes,
and hands the result to the core, which applies it at the next frame. Only the
entry assembly of a development build references it: a release build never
ships it.

## Using it

Build a target for development, then launch it with `--hot-reload`:

```sh
nobara build server --development
"out/server/Example GameServer.exe" --hot-reload
```

Edit a `.nof` file of a local module, or a file in its `Resources/` folder,
and save it: the game logs the compilation, then each context takes the new resources at the synchronization
point of its next frame. Errors are logged with their file, line, and column,
and the game keeps the previous resources.

The entry assembly that `nobara generate` writes does this in its `main()`:

```csharp
using var hotReload = new HotReloadHost(typeof(EntryPoint).Assembly);
return Engine.Run(new EngineOptions { Side = side, DevelopmentMode = true, OnLoaded = hotReload.Attach });
```

## Members

| Member | Use |
| --- | --- |
| `HotReloadHost` | Reads the watched modules from the resource `nobara/hot-reload.json` of the entry assembly, and starts a watcher once the engine is loaded (`Attach`, given as `EngineOptions.OnLoaded`). `Option` is `--hot-reload`. |
| `ResourceWatcher` | Watches the resources folders of modules, compiles a module after its files stop changing (`Delay`), or at once with `Compile`, and calls `Engine.ReloadResources` with the resources and the binary files that changed. |
| `WatchedModule` | A watched module: its identifier, its resources folder, and the plugin assemblies of its dependencies. |

A module is compiled against the schemas and the manifest embedded in its
installed assembly, the resources of its declared dependencies (the reloaded
ones when they were reloaded), and the plugins of its dependencies.

The binary files go to the core with the resources, as the plugins of their
types process them (a GLSL source becomes SPIR-V), when their bytes differ
from those the watcher handed last; the first compilation of a module hands
them all. The resources that reference a changed file have a new content hash,
so their module receives them as changed.

## What the module does

The module that defines a resource type registers a reload handler with
`ModuleResources.RegisterReloadHandler`, described in [the core
page](../../src/Core/README.md#reloading-resources). It applies what it can and
refuses what needs a restart; the core logs each refusal. A type without a
handler is reported as not reloaded.

## Building and testing

```sh
dotnet test tools/Nobara.HotReload.Tests
```

The tests run an engine with a module whose sources change on disk. The
command-line tool tests a development build of a whole game.

## Known limitations

- Schemas, `module.nobara`, and code are not reloaded.
- A module is compiled again in full, without the cache of incremental
  compilation.
- A dependent module is not compiled again when a resource it references
  changes in another module.
- The plugins of dependencies are read from their `Debug` build output.
