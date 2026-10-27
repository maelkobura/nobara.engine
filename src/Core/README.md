# Core

This page is the module sheet of the core: what it provides to modules and
to the entry point of a game, and the rules it enforces. The core is
described in context in [the architecture page](../../docs/explanation/architecture.md).

## Purpose

The core loads the modules of a distribution, refuses compositions that
cannot run, and takes each module through its lifecycle in a context, with a
service registry, a loop of phases with a fixed tick, error policies, and
logging. It loads the resources of the modules, applies their overrides,
resolves their tags, and gives the modules their configuration keys.

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
manifests, resources, and schemas.

## Entry points

| Member | Use |
| --- | --- |
| `Engine.Run(EngineOptions)` | The start-up point of a game: loads the engine, runs the loop of the main context until a stop is requested, and returns the exit code. |
| `Engine.Load(EngineOptions)` | Reads the distribution, checks its modules (step 0), loads them (step 1), and creates the main context. |
| `EngineContext.Start()`, `EngineContext.Stop()` | Run steps 2 to 4, then steps 6 and 7, for every module of the context. |
| `EngineContext.RunFrame()`, `EngineContext.Run()`, `EngineContext.RequestStop()` | Run one frame of the loop, run frames until a stop is requested, and request that stop. |
| `IModule` | The code of a module, instantiated once per context. Each lifecycle step is a method that does nothing by default. |
| `[assembly: ModuleEntry(typeof(...))]` | Names the class that implements `IModule` in the assembly of a module. |
| `ModuleContext` | What a module receives at each step: `Publish`, `Resolve`, `TryResolve`, `IsModuleActive`, `AddEmergencyHook`, `VerifyStates`, `Post`, `SynchronizationContext`, `RequestStop`, `GetContextControl`, its logger, the loop, its settings, its resources, its module configuration, and the data folders. |
| `ModuleSettings` | `context.Settings`: the configuration keys of the module and of its dependencies, typed reads, the values the player chooses, and the settings file. |
| `ModuleResources`, `ResourceData`, `ResourceTag`, `FreeProperty` | `context.Resources`: decoders, decoded objects, loaded resources, tags, free properties, and binary files. |
| `ModuleContext.ModuleConfig`, `GenericElement` | The `<ModuleConfig>` block of `engine.nobara` that targets the module, in the generic representation. |
| `ModuleLoop`, `IPhaseHandler`, `LoopSubscription` | The loop as a module sees it: its phases, subscriptions to a phase or to the start of the frame, and `GetTickControl`. |
| `TickControl`, `ContextControl` | The levers of the core: on the fixed tick (pause, time scale, tick rate), and on contexts. |
| `IModuleSource`, `AssemblyModuleSource` | Where the modules are found: the single loading interface, and its implementation over the `system/`, `game/`, and `lib/` folders. |
| `Logger`, `ILogSink` | Structured logging. |
| `EnginePaths` | The data folders: config, worlds, mods, cache, logs, profiles. |
| `[HotPath]`, `[LoopCallback]`, `[UsesOptionalDependency]` | The attributes that [the engine analyzers](../../docs/reference/analyzers.md) read. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Starting a game

Every `main()` of a game calls the start-up point, after its own
preparations:

```csharp
public static int Main() => Engine.Run(new EngineOptions { Side = Side.Client });
```

`Engine.Run` loads the engine, starts the main context, runs its loop on the
calling thread until a module calls `RequestStop` or Ctrl+C is pressed, stops
the context, and returns 0. It returns 1 when the distribution cannot start or
a module stops the game on an error; the logs and the crash report say why.

A host that drives the frames itself, such as a test, loads the engine
instead:

```csharp
using Engine engine = Engine.Load(new EngineOptions { Side = Side.Client });
engine.MainContext.Start();
engine.MainContext.RunFrame();
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

### Loop

Each context runs its loop on its loop thread. The phases, their order, the
fixed phase, and the tick rate come from `<Loop>` in
[`engine.nobara`](../../docs/reference/formats/engine-nobara.md). The core
does not know what a phase contains; a phase declared for one side does not
exist in the context of the other side. A distribution without `<Loop>` runs
`Input`, `Simulation` (fixed), `PostSimulation`, and `Render` (client only),
at 30 ticks per second.

Each frame runs, in this order:

1. the synchronization point: the callbacks posted since the previous frame;
2. the handlers subscribed to the start of the frame, where the states of the
   application change between two frames;
3. each phase in order. A phase that is not fixed runs once. A fixed phase
   runs once per tick due: the elapsed time feeds an accumulator, and each
   tick consumes the duration of one tick, so a frame runs zero, one, or
   several ticks. When the loop has several fixed phases, they share the
   tick, and each runs all the ticks of the frame at its own place.

A module subscribes an `IPhaseHandler` to a phase during registration, with
`context.Loop.Subscribe("Input", handler)`. The handlers of a phase run in
the order they subscribed, which is the load order. Disposing the
subscription removes the handler at once, even from the frame that runs. The
handler receives a `PhaseTime`: the frame and tick numbers, the duration of a tick in
a fixed phase or the real frame time elsewhere, and outside fixed phases the
fraction of a tick accumulated since the last one, to interpolate what is
drawn.

After a long pause, such as a breakpoint, one frame accounts for at most
`EngineOptions.MaxFrameTime` (250 ms by default), and a warning is logged.
`EngineOptions.FramePacing` sets how `Run` paces the frames: `Free` for a
client, whose rendering paces the loop, and `Tick` for a dedicated server,
which waits for each tick. `EngineOptions.TimeProvider` replaces the clock,
for tests that move the time themselves.

### Reserved phases and levers

A module reserves a phase by listing the exclusive resource `loop.` and the
name of the phase in lower case, such as `loop.simulation`, in the
`<Reserves>` of its `module.nobara`. Only that module subscribes to the phase;
the others register with it. Discovery refuses two modules that reserve the
same phase. Without a reservation, the phase is open to every module and the
tick runs in real time at the declared rate.

The levers of the core go to the modules that hold the matching reservation:

| Lever | Reservation | What it does |
| --- | --- | --- |
| `context.Loop.GetTickControl()` | a fixed phase, such as `loop.simulation` | `Paused` suspends the fixed phases; `TimeScale` changes how fast time accumulates, so the number of ticks per second, and not the duration of a tick; `TickRate` changes the duration of a tick. The time scale goes up to 100 and the tick rate up to 1000 ticks per second. Changes take effect at the next frame. |
| `context.GetContextControl()` | `core.contexts` | Creates and destroys the server context of an integrated server. This version runs only the main context. |

### Synchronization point

Code that runs on another thread, such as a library that has its own
threads, hands its result to the loop thread with
`context.Post(callback, state)`. The callback runs at the start of the next
frame; a callback posted while the synchronization point runs waits for the
frame after.

`context.SynchronizationContext` is the same queue for `await`. The core
makes it the current synchronization context while it calls the lifecycle
methods of the module and the callbacks it posted, so every `await` there
resumes on the loop thread:

```csharp
public void Initialize(ModuleContext context) => _loading = LoadAsync();

private async Task LoadAsync()
{
    byte[] data = await File.ReadAllBytesAsync(_path);
    // Here the code runs on the loop thread again, at the start of a frame.
}
```

An exception thrown by a posted callback or a continuation is charged to the
module that posted it. The callbacks of a disabled module are dropped.
Phase handlers do not use `async`.

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

### Configuration keys

A module declares its keys in `<Settings>` of its `module.nobara`, and reads
them with `context.Settings`, typed: `GetBool`, `GetInt`, `GetFloat`,
`GetString`, and `GetEnum`. It reads its own keys and those of its declared
dependencies, on the side of its context; any other key throws
`SettingException`, whose `Failure` says why.

```csharp
public void Initialize(ModuleContext context)
{
    _viewRadius = context.Settings.GetInt("voxel.viewRadius");
}
```

The value of a key comes from, in this order:

1. the settings file of the player, for a `user` key that the distribution
   does not lock;
2. the distribution: `<Default>`, `<Lock>`, or `<Set>` in `engine.nobara`;
3. the default the key declares.

The settings file is `config/settings.xml` in the data folders: the user data
of the operating system on a client, the folder of the server on a dedicated
server. It is plain XML that a player or a host can edit:

```xml
<Settings>
  <Setting key="voxel.viewRadius" value="192" />
</Settings>
```

The core reads it once, when the engine loads, before any context starts.
A value of the wrong type or out of bounds, an unknown key, a `distribution`
key, and any other element are ignored with a warning, and the key keeps its
default. A key that the distribution locks ignores the file. A file that
cannot be read gives every key its default.

`Set` gives a `user` key a value chosen by the player, `Reset` gives it its
default back, and `Save` writes the file. Only the values the player chose
are written, so a later default of the distribution applies to the other
keys. `Set` refuses a `distribution` key, a locked key, and a value out of
bounds. Every context of the process sees the same values.

A duplicate key, an invalid default, and an invalid value of the
distribution are refused when the engine loads, with a
`ModuleCompositionException` of kind `InvalidConfiguration`.

### Module configuration

A module that needs structured configuration receives the
`<ModuleConfig module="...">` block of `engine.nobara` that targets it as
`context.ModuleConfig`, a `GenericElement`: the element, its attributes, its
children, their typed values, and their positions in the file. The module
checks the block itself and names the position of an error with
`GenericElement.Source`. Without a block, `ModuleConfig` is `null`.

### Loading resources

The core reads the resources of every loaded module once per process, when
the engine loads and before any module code runs: `nobara/resources.pb` and
`nobara/schemas.pb` from the assembly of each module. It refuses, with a
`ResourceLoadException` that lists every problem:

- two modules that define the same object without `override`;
- a resource whose type no loaded schema defines;
- an override whose target is missing, or a query that selects nothing,
  unless the override is `optional="true"`;
- a tag member that is not defined, unless it is optional, and tags that
  contain each other;
- a qualified free property whose short name no module of the distribution
  has. A module that this process does not load, such as a client module on a
  dedicated server, counts as a module of the distribution.

Overrides of a resource apply in increasing `priority`, then in load order of
their modules, then by module name. Each applies to the result of the ones
before. Simple fields replace the values of the target. A query selects
among the elements under its scope, depth first: the first match, the match
at `index`, or every match. It runs its nested queries in each selected
element, then its operations. The elements that an operation inserts are
fields of the parent of the selected element, or of the selected element for
`<Add>`, whose `index` is a position among the values of the same field. An
element of a field that holds one value replaces that value. The free
properties of an override replace those of the same name, or are added. The
content hash of a resource combines the hash it was compiled with and the
hashes of its overrides.

A tag is the union of the members that every module contributes; a member is
required when one contribution requires it. Its overrides apply like those of
a resource, on its `<Member>` elements. Nested tags are then expanded.

The module whose schemas define a resource type registers its decoder during
registration. The core decodes every resource of the type before that module
initializes, once per process, and every context shares the decoded objects:

```csharp
public void Register(ModuleContext context)
{
    context.Resources.RegisterDecoder("block", data => new BlockType(data.ObjectId, Block.Parser.ParseFrom(data.Content.Span)));
}
```

From initialization on, a module reads the resources of the types defined by
itself and by its declared dependencies: `GetAll<T>`, `Get<T>`, `TryGet<T>`
for decoded objects, `GetData` and `Find` for the loaded resources, `GetTags`
and `TryGetTag` for tags, and `OpenFile` for the binary files that a
resource names. A decoder that throws follows the error policy of its
module.

A module declares each free property it reads, with its type, during
registration:

```csharp
_weight = context.Resources.DeclareProperty("Weight", PropertyType.Float);
```

Once every module is registered, the core checks that the modules that read
a property agree on its type, and that every value has it. Otherwise the
context stops with a `ResourceLoadException`. `FreeProperty.TryGetFloat` and
its siblings read the property qualified by the short name of the module
first, such as `Motion.Weight`, then the plain property; `false` means the
resource does not have it and the module applies its own default.

The distribution can protect namespaces (`<ProtectedNamespaces>` of
`project.nobara`, `context.Resources.ProtectedNamespaces`). The protection
applies to modules loaded dynamically, such as mods; the modules of the
distribution are not concerned.

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
it: in a lifecycle step, in a phase handler, or in a posted callback. The
exception is charged to the module whose code is closest to the throw
point in the stack trace: the called module, or a module whose service it
called. The policy of that module applies:

| Policy | Effect |
| --- | --- |
| `Stop` | The emergency hooks run, a crash report is written to `logs/`, the modules that started are stopped and released, and `EngineStopException` names the module and the step. |
| `Disable` | The module and every module that requires it are stopped, released, their services withdrawn, and their phase handlers removed. Modules that optionally depend on them receive `OnDependencyDisabled` and continue. In the loop, the frame goes on. |
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
logging of the process; `TimeProvider`, `FramePacing`, and `MaxFrameTime` set
the loop.

## Resources

The core reads the compiled formats, whose definitions are in
[the resource compiler](../../tools/Nobara.ResourceCompiler/README.md#compiled-formats):

- `nobara/module.pb`, the module manifest, from the assembly of each module;
- `nobara/resources.pb`, `nobara/schemas.pb`, and `nobara/files/`, the
  resources, schemas, and binary files of each module;
- `nobara/distribution.pb`, the distribution manifest, from the entry
  assembly of the game.

It provides no resource type.

## Constraints

- Lifecycle methods and phase handlers run on the thread that calls `Start`,
  `RunFrame`, and `Stop`: the loop thread of the context. `ModuleContext` and
  the service registry are not thread-safe, except `Post`,
  `SynchronizationContext`, and `RequestStop`; loggers and sinks are.
- A frame allocates no managed memory once the subscriptions are stable: the
  tests measure it. The dispatch to handlers is not marked `[HotPath]`, so that
  a handler of a phase that is not fixed does not have to follow the hot path
  rules. With `FramePacing.Tick`, the wait between frames reuses one timer.
- A frame does not run inside another frame, and a context does not stop
  during a frame: a module calls `RequestStop` instead.
- Assemblies are loaded once per process, in a load context named `Nobara
  modules` that shares the core and every assembly of the default context.
- No mutable static state: two contexts of one process each have their own
  instance of every module. The engine analyzers check the core and the
  modules.
- A message logged with an interpolated string at a disabled level is neither
  formatted nor allocated.

## Known limitations

- The lever on contexts cannot create an integrated server yet:
  `CreateIntegratedServer` throws `NotSupportedException`.
- The levers on the tick and on contexts do not check that they are used
  from the loop thread.
- The job system and the profiler are not implemented.
- The low-level foundations (pools, unmanaged memory, collections, math) are
  not implemented; only the attributes are.
- Resources are not reloaded while the game runs.
- No module is loaded dynamically yet, so the protected namespaces never
  refuse anything.
- A query selects the elements under the content of its target, never the
  content itself: a field of the content changes with a simple field of the
  override. A field in the generic representation, such as a component, is a
  single value for queries.
- Resources and tags stay loaded when the module that defines them is
  disabled; only its decoders and free properties are withdrawn.
- Free properties that no module reads are not reported.
- Modules are not told when the value of a key changes, and the settings
  file is written only by `Save`.
- `Save` writes the keys of the modules that the process loads: a value that
  the file holds for a module absent from the process, such as a client key
  in a server folder, is lost.
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
