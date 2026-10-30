# Core

This page is the module sheet of the core: what it provides to modules and
to the entry point of a game, and the rules it enforces. The core is
described in context in [the architecture page](../../docs/explanation/architecture.md).

## Purpose

The core loads the modules of a distribution, refuses compositions that
cannot run, and takes each module through its lifecycle in a context, with a
service registry, a loop of phases with a fixed tick, error policies, and
logging. It loads the resources of the modules, applies their overrides,
resolves their tags, and gives the modules their configuration keys. It runs
the jobs of the modules on a pool of worker threads, measures what each
context, module, phase, and job costs, and provides the low-level
foundations that modules share: unmanaged arrays, pools, bit sets, and
integer math.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Core, on the client and on the server. The core is not a module: it has no `module.nobara`.
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
| `ModuleContext` | What a module receives at each step: `Publish`, `Resolve`, `TryResolve`, `IsModuleActive`, `AddEmergencyHook`, `VerifyStates`, `Post`, `SynchronizationContext`, `RequestStop`, `InterceptStopRequests`, `GetContextControl`, `ReportFault`, its rank in the load order (`LoadOrder`), its logger, the identity of the game and its initial states (`Distribution`), the loop, its settings, its resources, its module configuration, its jobs, its profiler, and the data folders. |
| `ModuleSettings` | `context.Settings`: the configuration keys of the module and of its dependencies, typed reads, the values the player chooses, and the settings file. |
| `ModuleResources`, `ResourceData`, `ResourceTag`, `FreeProperty` | `context.Resources`: decoders, decoded objects, loaded resources, tags, free properties, and binary files. |
| `ModuleContext.ModuleConfig`, `GenericElement` | The `<ModuleConfig>` block of `engine.nobara` that targets the module, in the generic representation. |
| `ModuleLoop`, `IPhaseHandler`, `LoopSubscription` | The loop as a module sees it: its phases, subscriptions to a phase or to the start of the frame, and `GetTickControl`. |
| `TickControl`, `ContextControl` | The levers of the core: on the fixed tick (pause, time scale, tick rate), and on contexts. |
| `IModuleSource`, `AssemblyModuleSource`, `ExpectedLibrary` | Where the modules are found: the single loading interface, and its implementation over the `system/`, `game/`, and `lib/` folders. |
| `Engine.ReloadResources`, `ModuleResources.RegisterReloadHandler`, `ResourceReload` | [Reloading resources](#reloading-resources) while the game runs, in development mode. |
| `EngineOptions.OnLoaded` | Code that `Engine.Run` calls once the engine is loaded, before the main context starts, such as a development tool. |
| `ModuleJobs`, `IJob`, `IJobFor`, `JobHandle`, `JobPriority` | `context.Jobs`: jobs that run once or once per index, their dependencies, priorities, and waits. |
| `JobAccess`, `JobData`, `LoopAccess` | The data that a job, or code on the loop thread, reads and writes, and the checks of development mode. |
| `Profiler`, `ModuleProfiler`, `ProfilerMarker`, `ProfileEntry` | `Engine.Profiler` and `context.Profiler`: what each context, module, phase, job, and scope costs. |
| `NativeArray<T>`, `ObjectPool<T>`, `BitSet`, `IntMath` | The low-level foundations, in `Nobara.Core.Memory`, `Nobara.Core.Collections`, and `Nobara.Core.Mathematics`. |
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
the context, and returns 0. When a module intercepts stop requests, Ctrl+C
goes to that module, which stops the context when it decides to (see
[Reserved phases and levers](#reserved-phases-and-levers)); a second Ctrl+C
ends the process. It returns 1 when the distribution cannot start or
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
`context.LoadOrder` is the rank of the module in the load order of its
context, from 0: a module that orders the code of other modules breaks ties
with it.

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
| `context.InterceptStopRequests(handler)` | `core.contexts` | Hands the stop requests of the context to the module instead of stopping it: Ctrl+C, the cancellation of `Run`, and `EngineContext.RequestStop`. The handler runs on the loop thread at the start of the next frame; requests made before it runs are merged. The module stops the context with `ModuleContext.RequestStop`, which is never intercepted. Called during registration. |

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

### Jobs

No module creates threads. A module that has work to spread over the
processors schedules jobs with `context.Jobs`, and the worker threads of the
process run them. A job is a struct: scheduling copies it and allocates
nothing.

```csharp
public struct ScaleJob : IJobFor
{
    public NativeArray<float> Heights;
    public float Factor;

    public void Execute(int index) => Heights[index] *= Factor;
}

JobHandle scale = context.Jobs.ScheduleFor(new ScaleJob { Heights = _heights, Factor = 2 }, _heights.Length, batchSize: 256);
JobHandle sum = context.Jobs.Schedule(new SumJob { Heights = _heights, Total = _total }, dependsOn: scale);
sum.Complete();
```

- `IJob` runs once; `IJobFor` runs once per index of a range, in batches that
  run in parallel, each on its own copy of the struct. `Execute` is hot path
  code, checked by the engine analyzers.
- A job runs once the job it depends on is done. `Combine` makes one handle
  of several.
- `Complete` waits on the loop thread. While it waits, the loop thread runs
  ready tick jobs itself, starting with the one it waits for. `IsCompleted`
  tests without waiting, for a background job polled from frame to frame.
- `JobPriority.Tick`, the default, is for the work of the frame that runs.
  `JobPriority.Background` is for work that spans frames, such as generating
  chunks: tick jobs run first, and background jobs never take every worker
  thread when there are several.
- Jobs neither schedule nor wait for other jobs: they declare dependencies
  instead. Schedule and wait from the loop thread of the context.

The worker threads are shared by every context of the process: about one per
processor, minus the loop thread (`EngineOptions.WorkerCount`). Once the jobs
of a frame are stable, scheduling and waiting allocate nothing: the tests
measure it.

A job that throws fails. The jobs that depend on it, directly or not, do not
run. `Complete` does not throw: the exception is charged, at the next
synchronization point, to the module whose code is closest to the throw
point, under its error policy, as `JobFailedException`. A job that its waiter
must know failed catches the exception in `Execute` and records it in its
result.

Before a module is released, when its context stops or when its error policy
disables it, the core waits for its jobs. It schedules no more of them.

#### Declared accesses

A job that shares data with other jobs declares what it reads and writes. The
data is named by a `JobData`, which the module creates once per piece of data:

```csharp
_heightData = context.Jobs.CreateData("heights");
context.Jobs.ScheduleFor(job, length, 256, new JobAccess(reads: [_terrainData], writes: [_heightData]));
```

The job system runs a job that reads a piece of data after the earlier job
that writes it, and a job that writes it after the earlier jobs that read or
write it. Jobs that only read the same data run together.

In development mode (`EngineOptions.DevelopmentMode`), the code that owns the
data calls `CheckRead` or `CheckWrite` where it reads or writes it. They throw
`JobAccessException`, with the names of the job, its module, and the data,
when a job touches data it did not declare, or declares as read only data it
writes, and when a loop thread touches data that an unfinished job writes, or
writes data that an unfinished job reads. Outside development mode, they
return at once.

Code that runs on the loop thread can declare its accesses too, such as a
system of the simulation tick. The module declares them once, then runs the
code inside the declaration:

```csharp
_access = context.Jobs.DeclareLoopAccess("tick system 'example.farm.growth'", reads: [_terrainData], writes: [_heightData]);

using (_access.Enter())
{
    Grow();
}
```

In development mode, inside the scope, `CheckRead` and `CheckWrite` on that
thread throw `JobAccessException` for data that the declaration does not
name, or names as read only and that is written, and scheduling a job that
declares data outside the declaration throws too. Scopes nest. Outside
development mode, `Enter` does nothing.

Data that one job prepares for the loop thread, such as a chunk generated in
the background, needs no declaration: the job works on its own copy, and the
loop thread applies the result once `IsCompleted` is true, or the job posts it
to the synchronization point.

### Profiler

The profiler measures, for each context, module, phase, and job, the time
spent on the thread that ran the code, the managed memory it allocated, and
the pauses of the garbage collector during it. Turn it on with
`EngineOptions.Profiling`, or at any time with `Engine.Profiler.Enabled`.

| Kind | What it measures |
| --- | --- |
| `Phase` | A whole phase, or the start of the frame, every module included. |
| `Handler` | The handlers of one module in one phase. |
| `Synchronization` | The callbacks of one module at the synchronization point. |
| `Job` | The batches of one job type of one module. |
| `Scope` | A scope that a module measures itself. |

A module measures its own scopes, such as each of its systems, with a marker
it creates once:

```csharp
_growth = context.Profiler.CreateMarker("Crop growth");

using (_growth.Begin())
{
    GrowCrops();
}
```

`Engine.Profiler.Snapshot()` returns the measures since the last `Reset()`, as
`ProfileEntry` records; `FormatReport()` formats them as a table. A disabled
profiler costs one test per measured scope. An enabled one allocates nothing.
Measures include the scopes nested in them; a pause of the garbage collector
counts for every scope it falls in.

### Low-level foundations

The core provides the low-level types that several modules use. Their
methods that hot path code calls are marked `[HotPath]` and allocate nothing.

| Type | Use |
| --- | --- |
| `NativeArray<T>` | A fixed-length array of unmanaged values outside the managed heap, for large data such as the cells of a grid. Dispose it when it is no longer used. |
| `ObjectPool<T>` | Objects kept to be used again instead of allocated, with a fixed capacity. It is not thread-safe. |
| `BitSet` | A set of small integers, one bit per value, such as the members of a tag: membership, union, intersection, and enumeration without allocating. |
| `IntMath` | Division rounded down or up, and a remainder that is never negative, for grid coordinates and batch counts. |

### Discovery checks

First, `AssemblyModuleSource` checks the SHA-256 of each file of `lib/` that
the distribution records, and lets modules load only those libraries. Then,
for each module of the distribution, it finds its assembly in `system/` or
`game/` (the file name is compared without case), checks its SHA-256 against
the hash that the distribution records, and reads `nobara/module.pb` from its
metadata without loading it. The core then checks,
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

### Reloading resources

In development mode (`EngineOptions.DevelopmentMode`), a development tool
replaces the compiled resources of a loaded module with
`Engine.ReloadResources`, from any thread. The development builds of the
command-line tool do it when a `.nof` file changes (see [the hot reload
page](../../tools/Nobara.HotReload/README.md)).

At the synchronization point of its next frame, each context takes the new
resources: the core reads them with those of the other modules, applies the
overrides and resolves the tags again, checks the free properties, then hands
each changed type to the module that defines it, on the loop thread, through
the handler that the module registered:

```csharp
public void Register(ModuleContext context)
{
    context.Resources.RegisterDecoder("block", data => new BlockType(data));
    context.Resources.RegisterReloadHandler("block", reload =>
    {
        foreach (ResourceData added in reload.Added)
        {
            reload.Refuse(added.ObjectId, "a new block needs a restart of the context");
        }

        RebuildTables(context.Resources.GetAll<BlockType>("block"));
    });
}
```

`ResourceReload` lists the resources added, changed (content, content hash,
or free properties), and removed, and the tags of the registry that changed.
When the handler runs, `GetAll`, `Get`, and the other reads already return
the new resources. The core logs each change that the handler refuses, and
each changed type without a handler, as warnings. Resources that cannot be
used together, such as an override whose target disappeared, are refused
with an error, and the context keeps the previous ones.

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
it: in a lifecycle step, in a phase handler, in a posted callback, or in a
job. The exception is charged to the module whose code is closest to the
throw point in the stack trace: the called module, or a module whose service
it called. The policy of that module applies:

| Policy | Effect |
| --- | --- |
| `Stop` | The emergency hooks run, a crash report is written to `logs/`, the modules that started are stopped and released, and `EngineStopException` names the module and the step. |
| `Disable` | The module and every module that requires it are stopped, released, their services withdrawn, and their phase handlers removed. Modules that optionally depend on them receive `OnDependencyDisabled` and continue. In the loop, the frame goes on. |
| `Log` | The exception is logged and the module continues. |

The distribution chooses the policy of each module, with `Stop` by default.
Required modules and platform modules always use `Stop`. A cascade that would
disable a module whose policy is `Stop` stops the context instead.

Exceptions thrown during steps 6 and 7, and by emergency hooks, are logged.

A module that calls code of other modules itself, such as the systems of the
simulation tick, catches their exceptions and hands each one to
`ModuleContext.ReportFault` on the context of the module it called, then goes
on. The exception is charged as above, without counting the method that
caught it, and the policy applies before the call returns: `Stop` throws
`EngineStopException`, which the caller lets through.

### States

The module that owns the states of the application calls
`ModuleContext.VerifyStates` with every state registered in code. The core
compares them with the states that the active modules of the context declare
on its side, and throws `StateMismatchException` on any difference.
`context.Distribution.GetInitialState(side)` gives the initial state that
`<States>` of `engine.nobara` declares for a side, or `null`. The module that
does this in a game is [nContext](../nContext/README.md).

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

`EngineOptions.MinimumLogLevel`, `LogToConsole`, and `LogToFile` set the
logging of the process; `TimeProvider`, `FramePacing`, and `MaxFrameTime` set
the loop; `WorkerCount` sets the number of worker threads; `DevelopmentMode`
turns on development mode, its checks and the reloading of resources;
`Profiling` turns on the profiler at start-up; `OnLoaded` runs code once the
engine is loaded.

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
- Jobs run on the worker threads, and on a loop thread that waits for them.
  `JobHandle`, `JobData`, and the profiler can be used from any thread;
  `ModuleJobs` from the loop thread of its context only.
- Scheduling and waiting for jobs, measuring a scope, and the hot path methods
  of the low-level foundations allocate nothing once warm: the tests measure
  it. The bookkeeping of the job system grows when more jobs are in flight than
  ever before, and never shrinks.

## Known limitations

- The lever on contexts cannot create an integrated server yet:
  `CreateIntegratedServer` throws `NotSupportedException`.
- The levers on the tick and on contexts do not check that they are used
  from the loop thread.
- The worker threads are shared by the contexts of a process first come,
  first served: no share is kept for the tick of a server context.
- `Complete` does not tell its caller that a job failed; the error policy of
  the module applies at the next synchronization point. A job that depends on
  a job that already failed when it is scheduled runs.
- Jobs cannot be canceled: the core waits for them before it releases their
  module.
- A job canceled because a job it depends on failed is done without running,
  and nothing tells the module that scheduled it, even when the failed job
  belongs to another module.
- The job system does not check that a module schedules and waits from the
  loop thread of its context; it only refuses calls from inside a job. In
  development mode, any thread that does not run a job is checked as a loop
  thread.
- Accesses are checked only where the code that owns the data calls
  `CheckRead` or `CheckWrite`. `NativeArray<T>` does not check them by itself.
- One lock protects the bookkeeping of the job system, and every completion
  wakes every waiting thread. Very small jobs pay for it: make batches large
  enough.
- The profiler measures elapsed time on the thread that ran the code, not the
  processor time of that thread. It keeps totals since the last reset, not a
  history per frame.
- The low-level foundations hold only what several modules need now: no
  vector math, no growable unmanaged list.
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
- The core checks the files of `lib/` that the distribution records; a
  native library that a managed library of `lib/` loads itself is checked
  only when the build recorded it.
- A reload replaces the resources of whole modules: schemas, binary files,
  and code are not reloaded. A context that a reload finds in the middle of a
  frame takes it at its next frame; contexts created later start from the
  reloaded resources.
- An exception is charged to a module from the stack trace. A method that the
  JIT inlined into its caller is charged to the caller, and an assembly that
  holds several modules is charged to the called module.
- The `Log` policy leaves a module that threw during registration or
  initialization active, whatever state it is in.
- The folder of a module (`system/` or `game/`) is not compared with its
  level.
- Publications and emergency hooks cannot be withdrawn by the module that
  made them; the core withdraws them when it disables the module.
