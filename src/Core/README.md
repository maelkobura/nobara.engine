# Core

This page is the module sheet of the core: what it provides to modules and
to the entry point of a game, and the rules it enforces. The core is
described in context in [the architecture page](../../docs/explanation/architecture.md).

## Purpose

The core loads the modules of a distribution, refuses compositions that
cannot run, and takes each module through its lifecycle in a context, with a
service registry, error policies, and logging.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Core, on the client and on the server. The core is not a module: it has no
`module.nobara`.
<!-- nobara:generated:end -->

## Dependencies

<!-- nobara:generated:begin dependencies -->
None. The core depends on no module.
<!-- nobara:generated:end -->

Third-party library: `Google.Protobuf` (BSD-3-Clause), to read the compiled
manifests.

## Entry points

| Member | Use |
| --- | --- |
| `Engine.Load(EngineOptions)` | Reads the distribution, checks its modules (step 0), loads them (step 1), and creates the main context. |
| `EngineContext.Start()`, `EngineContext.Stop()` | Run steps 2 to 4, then steps 6 and 7, for every module of the context. |
| `IModule` | The code of a module, instantiated once per context. Each lifecycle step is a method that does nothing by default. |
| `[assembly: ModuleEntry(typeof(...))]` | Names the class that implements `IModule` in the assembly of a module. |
| `ModuleContext` | What a module receives at each step: `Publish`, `Resolve`, `TryResolve`, `IsModuleActive`, `AddEmergencyHook`, `VerifyStates`, its logger, and the data folders. |
| `IModuleSource`, `AssemblyModuleSource` | Where the modules are found: the single loading interface, and its implementation over the `system/`, `game/`, and `lib/` folders. |
| `Logger`, `ILogSink` | Structured logging. |
| `EnginePaths` | The data folders: config, worlds, mods, cache, logs, profiles. |
| `[HotPath]`, `[LoopCallback]`, `[UsesOptionalDependency]` | The attributes that [the engine analyzers](../../docs/reference/analyzers.md) read. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Starting a game

The entry point of a game loads the engine and starts its main context:

```csharp
using Engine engine = Engine.Load(new EngineOptions { Side = Side.Client });
engine.MainContext.Start();
// The loop runs here.
engine.MainContext.Stop();
```

By default the distribution manifest is the resource `nobara/distribution.pb`
of the entry assembly, the modules are in the parent folder of the folder of
the entry assembly, and the data folders are in the user data of the
operating system for a client, or in the install folder for a dedicated
server.

### Lifecycle

| Step | Once per | What happens |
| --- | --- | --- |
| 0. Discovery | Process | Integrity, manifests, and checks of the composition. No module code runs. |
| 1. Loading | Process | Assemblies are loaded: platform modules, then business modules, each after its dependencies. |
| 2. Registration | Context | Each module publishes its services. Resolving one throws. |
| 3. Initialization | Context | Each module resolves the services of its dependencies and creates its state. |
| 4. Start | Context | Last preparations. |
| 5. Execution | Context | The loop runs. |
| 6. Stop | Context, reverse order | Each module ends its activity. |
| 7. Release | Context, reverse order | Each module frees its resources. |

Each step runs for every module before the next step starts. Steps 6 and 7
run for the modules that reached the matching earlier step.

### Discovery checks

For each module of the distribution, `AssemblyModuleSource` finds its
assembly in `system/` or `game/` (the file name is compared without case),
checks its SHA-256 against the hash that the distribution records, and reads
`nobara/module.pb` from its metadata without loading it. The core then checks,
on each side the process may host (both for a client, which may host an
integrated server; the server for a dedicated server):

- the manifest matches the distribution: identifier, version, format;
- every required dependency is present and runs on the sides of its
  dependent;
- every present dependency, optional or not, is in its version range;
- no dependency cycle, optional dependencies included;
- no platform module depends on a business module;
- no two active modules conflict or reserve the same exclusive resource;
- every base module has an active backend on the sides where it requires one.

Every problem is reported at once in a `ModuleCompositionException`, whose
messages name the module and the cause. A dedicated server does not load
modules that only run on the client.

### Services

A module publishes a service under a type, usually an interface, during
registration. From initialization on, it resolves its own services and those
of its declared dependencies:

| Situation | `Resolve` | `TryResolve` |
| --- | --- | --- |
| Published by the module or a declared dependency | The instance | `true` |
| Published by a module that is not a declared dependency | Throws `ServiceResolutionException` | Throws |
| Defined by an inactive optional dependency | Throws | `false` |
| Published by nobody | Throws | `false` |

Each context has its own registry; contexts share no service.

### Error policies

The core intercepts every exception that a module throws when the core calls
it. The exception is charged to the module whose code is closest to the throw
point in the stack trace: the called module, or a module whose service it
called. The policy of that module applies:

| Policy | Effect |
| --- | --- |
| `Stop` | The emergency hooks run, a crash report is written to `logs/`, the modules that started are stopped and released, and `EngineStopException` names the module and the step. |
| `Disable` | The module and every module that requires it are stopped, released, and their services withdrawn. Modules that optionally depend on them receive `OnDependencyDisabled` and continue. |
| `Log` | The exception is logged and the module continues. |

The distribution chooses the policy of each module, with `Stop` by default.
Required modules and platform modules always use `Stop`. A cascade that would
disable a module whose policy is `Stop` stops the context instead.

Exceptions thrown during steps 6 and 7, and by emergency hooks, are logged.

### States

The module that owns the states of the application calls
`ModuleContext.VerifyStates` with every state registered in code. The core
compares them with the states that the active modules of the context declare
on its side, and throws `StateMismatchException` on any difference.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

`EngineOptions.MinimumLogLevel`, `LogToConsole`, and `LogToFile` set the
logging of the process.

## Resources

The core reads two compiled formats, whose definitions are in
[the resource compiler](../../tools/Nobara.ResourceCompiler/README.md#compiled-formats):

- `nobara/module.pb`, the module manifest, from the assembly of each module;
- `nobara/distribution.pb`, the distribution manifest, from the entry
  assembly of the game.

It provides no resource type.

## Constraints

- Lifecycle methods run on the thread that calls `Start` and `Stop`: the loop
  thread of the context. `ModuleContext` and the service registry are not
  thread-safe; loggers and sinks are.
- Assemblies are loaded once per process, in a load context named `Nobara
  modules` that shares the core and every assembly of the default context.
- No mutable static state: two contexts of one process each have their own
  instance of every module. The engine analyzers check the core and the
  modules.
- A message logged with an interpolated string at a disabled level is neither
  formatted nor allocated.

## Known limitations

- The loop, its phases, the fixed tick, and the synchronization point are
  not implemented: the main context is started and stopped by its host.
- No reusable start-up point runs the loop for the entry point of a game yet.
- The levers on the loop (suspending the simulation phase, time scale, tick
  rate) and the lever that creates or destroys a context are not
  implemented.
- The job system and the profiler are not implemented.
- The low-level foundations (pools, unmanaged memory, collections, math) are
  not implemented; only the attributes are.
- Resources (inventory, decoders, duplicates, overrides, tags, free
  properties) are not loaded.
- The configuration service, the player configuration file, and module
  configurations from the engine file are not implemented.
- Only the main context exists; an integrated server context cannot be
  created yet.
- The static loading mode is an interface only: no implementation of
  `IModuleSource` loads modules compiled into the executable.
- The distribution manifest records the hashes of module assemblies only, so
  the libraries of `lib/` are not checked.
- An exception is charged to a module from the stack trace. A method that the
  JIT inlined into its caller is charged to the caller, and an assembly that
  holds several modules is charged to the called module.
- The `Log` policy leaves a module that threw during registration or
  initialization active, whatever state it is in.
- The folder of a module (`system/` or `game/`) is not compared with its
  level.
- Publications and emergency hooks cannot be withdrawn by the module that
  made them; the core withdraws them when it disables the module.
