# nGame

This page is the module sheet of nGame, the master of the simulation tick. It
describes what the module provides to other modules and the rules it
enforces. Terms are defined in [the glossary](../../docs/explanation/glossary.md).

## Purpose

nGame runs the simulation tick of each context: it splits the tick into
ordered stages, runs the tick systems of every module in a deterministic
order, keeps game time (pause, time scale, tick rate, dimension clocks), and
schedules tasks.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server.
<!-- nobara:generated:end -->

## Dependencies

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.event` | `^1.0` | Optional |
<!-- nobara:generated:end -->

nGame works without the event module: the levers then emit no event.

## Entry points

| Member | Use |
| --- | --- |
| `ITickSchedule` | Stages and tick systems: `AddStage`, `AddSystem`, and the resolved order with `GetOrder` and `FormatOrder`. |
| `ITickSystem`, `TickTime` | The code of a tick system, which runs once per tick. |
| `TickSystemOptions`, `TickSystemHandle` | The constraints, declared accesses, and sides of a system; the handle that removes it. |
| `TickStages` | The names of the six standard stages, and of the system of the scheduler. |
| `IGameTime`, `DimensionId` | The tick number, real time, the levers (pause, time scale, tick rate), and the clocks of the dimensions. |
| `IScheduler`, `IScheduledTask`, `TaskRun`, `TaskHandle` | Tasks of game time and of real time. |
| `TickScheduleException` | Why the order of the tick cannot be resolved. |

The three services are resolved with `context.Resolve<ITickSchedule>()`,
`context.Resolve<IGameTime>()`, and `context.Resolve<IScheduler>()`. There is
one of each per context. nGame reserves `loop.simulation`: no other module
subscribes to the simulation phase, every module registers its tick systems
with nGame instead. The loop needs a fixed phase named `Simulation`.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Stages

A stage is a milestone of the tick: it holds no code, it orders the systems
that join it. The six standard stages run in this order:

| # | Stage | Content |
| --- | --- | --- |
| 1 | `Intents` | Inputs and intents received, their validation, the controller of the player. |
| 2 | `Schedule` | The tasks of the scheduler that are due. |
| 3 | `World` | The world itself, such as scheduled and random block ticks. |
| 4 | `Entities` | Gameplay systems of entities, and their artificial intelligence. |
| 5 | `Physics` | Physics and motion. |
| 6 | `Late` | Reactions to the changes of the tick, and last corrections. |

A module inserts a stage while it initializes, with a qualified name that
starts with its identifier:

```csharp
schedule.AddStage(context, "example.fluid.flow", after: TickStages.World);
schedule.AddStage(context, "example.fluid.settle", after: "example.fluid.flow", before: TickStages.Physics);
```

- With `after`, the stage runs after that stage and after the stages already
  inserted after it, directly or through other inserted stages, and before
  any other stage.
- With only `before`, the stage runs right before that stage.
- With both, it is placed by `after`, and `before` must still hold.
- Stages are inserted in the load order of their modules, then by name.

### Tick systems

Every piece of code that runs during the tick is a tick system. A module
registers its systems while it initializes:

```csharp
public sealed class GrowthSystem : ITickSystem
{
    public void Run(in TickTime time)
    {
        // One tick of crop growth.
    }
}

_crops = context.Jobs.CreateData("crops");
_growth = schedule.AddSystem(context, "example.farm.growth", TickStages.Entities, new GrowthSystem(), new TickSystemOptions
{
    After = ["example.farm.watering"],
    Reads = [_soil],
    Writes = [_crops],
});
```

- The name is qualified by the identifier of the module, and unique among
  the stages and systems of the tick.
- `After` and `Before` name other systems. A system of an earlier stage
  always runs before; a constraint toward a later stage is a cycle. A name
  that no system has in the context is ignored, so a module can order itself
  against an optional module.
- `Reads` and `Writes` declare the data the system touches, as `JobData` of
  the job system. Declare them from the start: in development mode they are
  checked, and the order may use them later.
- `Side` restricts the system to the client or the server. On the other
  side, the system keeps its place in the order and does not run.
- `ITickSystem.Run` is hot path code, checked by the engine analyzers. It
  runs on the loop thread; work spread over processors goes to jobs that it
  schedules and completes.

The system of the scheduler, `nobara.game.scheduler`, runs in `Schedule`;
other systems can be ordered against it.

### Order

nGame resolves the order when it starts, after every module has initialized,
and the order stays fixed: adding a stage or a system later throws.

1. Stages are placed as described above.
2. In each stage, systems are sorted by their constraints. Among the systems
   that are free to run, the one of the module that loads first runs first,
   then the one with the smaller name.

The same composition always gives the same order. `GetOrder` returns it,
with the state of each system: `Runs`, `OtherSide`, `Disabled`, `Revoked`, or
`ModuleDisabled`; `FormatOrder` returns it as text, and nGame logs it at the
`Debug` level when it starts.

A cycle of constraints, a stage that cannot be placed, or a stage that no
module adds fails the start of the context with a `TickScheduleException`
that names the systems and the stages in cause:

```text
The tick systems of stage 'Entities' form a cycle: example.farm.reap -> example.farm.sow -> example.farm.water -> example.farm.reap.
```

Disposing the handle of a system removes it from the next tick on. A system
removed while a tick runs still runs in that tick when its turn comes.

### Errors in systems and tasks

nGame catches an exception thrown by a tick system or a task, and hands it to
the core with `ModuleContext.ReportFault`. The exception is charged to the
module whose code threw, and its error policy applies: with `log` or
`disable`, the tick goes on with the next systems; a disabled module runs no
more systems or tasks; with `stop`, the context stops.

### Declared accesses in development mode

In development mode (`EngineOptions.DevelopmentMode`), each system runs
inside the declaration of its accesses (`LoopAccess`). Where the code that
owns a piece of data calls `CheckRead` or `CheckWrite`, touching data that
the system does not declare, or writing data it declares as read only,
throws a `JobAccessException` that names the system, its module, and the
data. A job that the system schedules declares only data that the system
declares. Outside development mode nothing is checked.

### Game time

```csharp
IGameTime time = context.Resolve<IGameTime>();
time.Paused = true;
time.TimeScale = 2;
time.TickRate = 20;
ulong now = time.GetClock(DimensionId.Default);
```

| Member | Meaning |
| --- | --- |
| `Tick` | The number of the tick of the context, from 1. It counts the ticks that ran. |
| `RealTime` | The real time since the context started. It goes on during a pause and ignores the time scale. |
| `Paused` | Suspends the simulation: no tick runs and no clock moves. |
| `TimeScale` | Runs more or fewer ticks per second of real time, from above 0 to 100. The duration of a tick does not change. |
| `TickRate` | Changes the duration of a tick, from above 0 to 1000 ticks per second. |

The changes of pause, scale, and rate take effect at the next frame. They act
through the levers of the core on the fixed tick.

Each dimension has a clock: a raw count of game ticks on 64 bits, with no
notion of day or season. At the start of each tick, before the first stage,
each clock that is not frozen goes up by one.

- `DimensionId` is a number given by the module that owns the dimensions.
  Dimensions are ordered by that number.
- The clock of `DimensionId.Default` (number 0) exists from the start and is
  never removed. `AddDimension` adds a clock at a given value, such as a
  saved one; `RemoveDimension` removes it and cancels its tasks.
- `SetFrozen` freezes a clock: it does not move and its tasks wait. The tick
  number of the context still goes up.
- `Jump` moves a clock forward without simulating the ticks between. Tasks
  left behind run at the next tick.

The levers belong to the context that has authority over game time. Each
context of this version is the main context of its process and has it.

### Scheduler

```csharp
IScheduler scheduler = context.Resolve<IScheduler>();
scheduler.After(context, DimensionId.Default, 600, new RipenTask());
scheduler.EveryRealTime(context, TimeSpan.FromSeconds(5), new PingTask());
```

| Method | Runs the task |
| --- | --- |
| `At` | Once, when a dimension clock reaches a tick. |
| `After` | Once, a number of ticks of a dimension clock from now. |
| `Every` | Every period of a dimension clock, the first time one period from now. |
| `AfterRealTime` | Once, after a delay of real time. |
| `EveryRealTime` | Every period of real time. |

- Tasks of game time run in stage `Schedule`, in the order of their due
  tick, then of their scheduling, across dimensions. They follow the pause,
  the time scale, and frozen dimensions.
- Tasks of real time run at the start of each frame, before the phases, in
  the same order. They go on during a pause and ignore the time scale.
- A periodic task whose periods were skipped, by a jump or a slow frame,
  runs once, then goes on at its period.
- A task scheduled by a running task runs at a later run, never the same one.
- Disposing the handle cancels the task at once.
- The scheduler is not saved: a module that needs lasting tasks saves them
  itself. It suits tasks in small numbers.
- `IScheduledTask.Run` is hot path code. Tasks declare no accesses.

## Events

Emitted synchronously on the event bus, when it is present and the event has
handlers. nGame listens to no event.

| Event | When |
| --- | --- |
| `GamePauseChanged` | `Paused` changes. |
| `TimeScaleChanged` | `TimeScale` changes, with the previous and the new value. |
| `TickRateChanged` | `TickRate` changes, with the previous and the new value. |
| `DimensionClockJumped` | `Jump` moves a clock, with its value before and after. |
| `DimensionFreezeChanged` | `SetFrozen` freezes or releases a clock. |

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

The distribution inserts stages, adds constraints, and disables systems
without changing the code of the modules, in the `<ModuleConfig>` block of
[`engine.nobara`](../../docs/reference/formats/engine-nobara.md):

```xml
<ModuleConfig module="nobara.game">
  <Stage name="example.combat" after="Entities" before="Physics" />
  <Constraint system="example.mobs.ai" after="example.physics.step" />
  <Disable system="example.weather.debug" />
  <Disable system="example.weather.rain" side="client" />
</ModuleConfig>
```

| Element | Attributes |
| --- | --- |
| `<Stage>` | `name`, qualified by the identifier of the game; `after`, `before`, or both. |
| `<Constraint>` | `system`; `after`, `before`, or both, each naming a system. |
| `<Disable>` | `system`. |

Every element takes an optional `side`, `client` or `server`; without it, the
element applies to both sides. nGame checks the elements and their attributes
when it initializes, and the names when it starts: an unknown element or
attribute, a missing attribute, a system that no module registers on the
side, or a cycle fails the start with the position in `engine.nobara`.

## Resources

None.

## Constraints

- Use the services on the loop thread of the context.
- Stages and systems are added while modules initialize; the order is fixed
  when nGame starts.
- Running a tick, its systems, and the due tasks is hot path code: it
  allocates nothing once the systems and tasks are stable, with the profiler
  on or off and in development mode. The tests measure it. Registering a
  system and scheduling a task allocate.
- The systems of a tick run one after another on the loop thread.
- nGame keeps no static state: each context has its own order, clocks, and
  tasks.
- The profiler measures each stage (`Tick stage <name>`, module
  `nobara.game`) and each system (`Tick system <name>`, module of the
  system), as scopes.

## Known limitations

- Systems never run in parallel with each other, and declared accesses are
  only checked; they do not change the order.
- A system cannot run once every few ticks.
- Game time is not sent to clients, and the clocks are not saved.
- Every context has authority over game time, and pausing is always allowed:
  the version has a single context and no network.
- No policy freezes a dimension on its own; frozen clocks are set by code.
- Tasks of game time cannot be scheduled on the tick number of the context,
  only on a dimension clock.
