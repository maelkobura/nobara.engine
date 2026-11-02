# nContext

This page is the module sheet of nContext, the module that drives the states
of the application and its world sessions. It describes what the module provides to other modules
and the rules it enforces. Terms are defined in [the
glossary](../../docs/explanation/glossary.md).

## Purpose

nContext runs the path of the application through its states, such as a
boot screen, a main menu, the game, and a pause menu: it keeps the state
stack of each context, applies the transitions between two frames, and lets
the active state decide what a stop request does. It also runs the world
sessions of each context: it enters a world, waits for the loading tasks of
the modules, leaves the world, and enters another one, without restarting
the modules.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server.
<!-- nobara:generated:end -->

## Dependencies

<!-- nobara:generated:begin dependencies -->
None.
<!-- nobara:generated:end -->

## Entry points

| Member | Use |
| --- | --- |
| `IStateStack` | Registers the states, holds the stack, requests the transitions (`Push`, `Pop`, `Replace`, `PopAll`), and raises global events (`Raise`). |
| `ApplicationState` | The base class of a state: `Enter`, `Exit`, `Suspend`, `Resume`, `Update`, and `OnGlobalEvent`, which do nothing by default. |
| `StateOptions` | How a state behaves in the stack: `KeepStatesBelowRunning`. |
| `GlobalEvent`, `StopRequested` | Events of the whole application that the active state decides about first, and their default action. |
| `IWorldSessions` | Starts and ends the world sessions (`Start`, `End`), subscribes to their events, and registers world state counters. |
| `WorldSession` | A session: its number, its state, its loading tasks, and why it ended or failed. |
| `LoadingTasks`, `LoadingTask` | What a session waits for before it is active, and the aggregated progress for a loading screen. |
| `SessionSubscription` | A handler of the session events or a world state counter; dispose it to remove it. |

The services are resolved with `context.Resolve<IStateStack>()` and
`context.Resolve<IWorldSessions>()`; there is one of each per context. The engine provides no state: the modules of a game provide
them. nContext reserves `core.contexts`, which gives it the levers of the
core on contexts and on stop requests.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### States

A state is a class that derives from `ApplicationState`. A module declares
each state it provides in the `<States>` of its
[`module.nobara`](../../docs/reference/formats/module-nobara.md#states), and
registers it under the same identifier while it initializes, on the side
where it declares it:

```csharp
public sealed class MenuState(IStateStack states) : ApplicationState
{
    public override void Enter()
    {
        // Show the menu.
    }

    public override void Update(in PhaseTime time)
    {
        if (_playClicked)
        {
            states.Replace("state:example.loading");
        }
    }
}

public void Initialize(ModuleContext context)
{
    IStateStack states = context.Resolve<IStateStack>();
    if (context.Side == Side.Client)
    {
        states.Register(context, "state:example.menu", () => new MenuState(states));
        states.Register(context, "state:example.pause", () => new PauseState(states), new StateOptions { KeepStatesBelowRunning = true });
    }
}
```

- `Register` takes a factory: nContext creates a new instance each time the
  state enters the stack, when the transition is requested, and drops it once
  the state has exited.
- When nContext starts, after every module has initialized, it compares the
  states registered in code with the states that the active modules declare
  on the side of the context. A state declared and not registered, or
  registered and not declared, fails the start with a
  `StateMismatchException` that names the module and the state:

  ```text
  State 'state:example.credits' is declared by module 'example.menus' and not registered on the client side.
  ```

- Registering a state outside initialization, an identifier that does not
  start with `state:`, or a state already registered throws.
- `StateOptions.RequiresSession` marks a state that only makes sense in a
  world, such as the game: see [States that require a
  session](#states-that-require-a-session).

### The stack

The states form a stack. The state at the top is the active state. A state
runs when every state above it keeps the states below it running
(`KeepStatesBelowRunning`), such as the game under a pause menu in
multiplayer; otherwise it is suspended while it stays in the stack.

| Request | Effect |
| --- | --- |
| `Push(id)` | Puts the state on top, such as a pause menu over the game. |
| `Pop()` | Removes the active state. |
| `Replace(id)` | Replaces the active state, such as a menu by a loading screen. |
| `PopAll()` | Removes every state, from the top to the bottom. |

A request is checked when it is made, against the stack as it will be once
the requests already made apply: pushing a state that is already in the
stack, popping an empty stack, or replacing the active state with a state
that is below it throws in the code that makes the request.

### Transitions

Transitions never apply in the middle of a frame. Requests are queued, and
applied at the start of the next frame, after the synchronization point and
before the first phase of the loop, in the order they were made. A request
made during the simulation tick therefore applies at the next frame, and the
rest of the frame sees the stack unchanged.

At the start of each frame, nContext:

1. delivers the global events raised since the previous frame;
2. applies the transitions requested since the previous frame. A transition
   requested while they apply, such as from `Enter`, waits for the next
   frame;
3. applies the session requests, and follows the loading tasks of a session
   that starts (see [World sessions](#world-sessions));
4. calls `Update` on each running state, from the bottom of the stack to its
   top.

Each transition calls, in this order: `Exit` on the states that leave the
stack, from the top down; `Suspend` on the states that stop running, from
the top down; `Resume` on the states that run again, from the bottom up;
`Enter` on the state that enters the stack. A state that just entered is
neither suspended nor resumed.

### Start and stop

When nContext starts, it reads the initial state of its side from
`<States>` in [`engine.nobara`](../../docs/reference/formats/engine-nobara.md#states),
and requests it: the initial state enters at the first frame. A side without
an initial state, or an initial state that no active module registers, fails
the start.

The context stops when the stack becomes empty: once the transitions of a
frame have applied, if the stack is empty and no transition waits, nContext
stops the context after the frame. For the main context, the game ends and
`Engine.Run` returns 0. To quit, a state calls `PopAll`; a dedicated server
replaces its running state with a state that saves, then pops it.

A context stopped another way, such as by a module that calls
`ModuleContext.RequestStop` or by an error, does not empty the stack: the
states left in it do not exit, and nContext logs them.

### Global events

A global event is something that happens to the whole application, which the
active state decides about first. Each one derives from `GlobalEvent`. At the
start of the next frame, the active state receives it in `OnGlobalEvent`; when
it returns `false`, or when the stack is empty, the default action of the
event applies.

The only global event of this version is `StopRequested`: Ctrl+C in the
console, the cancellation of `EngineContext.Run`,
`EngineContext.RequestStop` by the host, or a request that a module relays
with `ModuleContext.ForwardStopRequest`, such as nFrames when the player
closes the window. nContext intercepts the stop
requests of its context through the core, so the context does not stop by
itself. Its default action pops every state, which stops the context; a state
that handles it decides instead:

```csharp
public override bool OnGlobalEvent(GlobalEvent e)
{
    if (e is StopRequested)
    {
        states.Replace("state:example.server_stopping");
        return true;
    }

    return false;
}
```

A module raises its own global event with `Raise`, and gives it a default
action by overriding `ApplyDefault`.

### World sessions

A world session covers the presence of a context in a world, from the entry
to the exit. A context runs one session at a time and chains as many as it
needs: a game in a world, back to the menu, another world. The modules are
not restarted between two sessions; they keep their state tied to a world,
such as chunks, entities, or grids, between the events of the session, which
create it and free it. Client and server use the same service, and each
context starts its own sessions.

A state starts a session, such as a loading screen, and reads the session it
gets back to know when to go on:

```csharp
public sealed class LoadingState(IStateStack states, IWorldSessions sessions) : ApplicationState
{
    private WorldSession? _session;

    public override void Enter() => _session = sessions.Start();

    public override void Update(in PhaseTime time)
    {
        if (_session!.Failure is { } failure)
        {
            // Show failure.Reason, then go back to the menu.
            states.Replace("state:example.disconnected");
        }
        else if (_session.State == SessionState.Active)
        {
            states.Replace("state:example.game");
        }
        else
        {
            // Draw _session.Loading.Progress.Fraction.
        }
    }
}
```

`Start` and `End` are requests, like transitions: they apply at the start of
the next frame, after the transitions. `Start` throws while a session exists,
so a state ends the current session and starts the next one once `State` is
`None`. `End` ends a session that is active or still starting.

The events of a session come in this order:

| Event | When | What modules do |
| --- | --- | --- |
| `SessionStarting` | The frame after `Start`. | Create their state tied to the world, and add loading tasks. |
| `SessionStarted` | Every loading task completed; the session is active. | Begin to simulate the world. |
| `SessionEnding` | The frame after `End`, a failed task, or a stop. | Save, disconnect cleanly. |
| `SessionEnded` | Right after `SessionEnding`. | Free their state tied to the world, and wait for their background jobs that use it. |

A session cancelled while it starts skips `SessionStarted`. Handlers run in
the load order of their modules for `SessionStarting` and `SessionStarted`,
and in the reverse order for `SessionEnding` and `SessionEnded`, so a module
frees its world after the modules that depend on it. Each subscription
returns a `SessionSubscription`; disposing it removes the handler at once,
even while the event is dispatched. An exception that a handler throws
follows the error policy of its module. The handlers of a disabled module
are no longer called.

```csharp
public void Initialize(ModuleContext context)
{
    IWorldSessions sessions = context.Resolve<IWorldSessions>();
    _starting = sessions.SubscribeStarting(context, session =>
    {
        _grids = new GridSet();
        session.Loading.AddAsync("read the grids", (task, cancel) => _grids.LoadAsync(task, cancel));
    });
    _ended = sessions.SubscribeEnded(context, _ =>
    {
        _grids.Dispose();
        _grids = null;
    });
}
```

### Loading tasks

While its `SessionStarting` handler runs, a module adds the tasks the
session waits for to `WorldSession.Loading`. A task has a name, a weight,
and a progress from 0 to 1; it belongs to the module whose handler adds it.
nContext does not know what the world is made of: the session is active
once every task completed, and each module adds the tasks that carry its
share of the end of the loading, such as the chunks around the player that
are meshed.

| Method | Task | Completes when |
| --- | --- | --- |
| `Add(name, weight)` | Driven by the module, which calls `Report`, `Complete`, or `Fail`. | The module completes it. |
| `AddAsync(name, load, weight)` | Input or output, such as reading a save. nContext calls `load` at once; each `await` resumes on the loop thread, through the synchronization point of the module. | The returned `Task` completes. |
| `AddJob(name, job, completed, weight)` | A computation in a job. | The job finished, and `completed`, if any, applied its result on the loop thread. |

`LoadingTasks.Progress` gives the progress of the whole loading: the
progress of each task weighted by its weight, and the number of completed
tasks. Reading it allocates nothing, so a loading screen reads it at each
frame. `Report` can be called from any thread; `Complete` and `Fail` are
called on the loop thread.

A task fails when the module calls `Fail` with a reason, when the `Task` of
`AddAsync` throws, or when `completed` throws. At the start of the next
frame, the session is cancelled: the other tasks are canceled, the token
`LoadingTasks.Cancellation` is cancelled, the `SessionEnding` and
`SessionEnded` handlers run, and `WorldSession.Failure` names the task, its
module, and the reason, which the state that started the session shows on
its way to a disconnected screen. A failed task is logged as an error; it
does not apply the error policy of the module, since a missing save or a
refused connection does not stop the game. A job that throws, on the other
hand, follows the error policy of its module: a job whose failure must
cancel the session catches its exception, writes it in its result, and
`completed` fails the task.

### Stop during a session

A stop request that reaches the context during a session ends the session
first, `SessionEnding` then `SessionEnded`; the active state then receives
the request, as without a session. When the state stack becomes empty during
a session, the states exit, then the session ends, then the context stops. A
context stopped another way, such as by an error, does not end its session;
nContext logs it.

### States that require a session

A state registered with `StateOptions.RequiresSession`, such as the game,
enters the stack only while a session is active and no end of it is
requested: pushing it, or replacing the active state with it, throws
otherwise. The check applies when the state enters; a session that ends
while the state is in the stack, such as on a stop request, leaves the state
there, and the state leads the application elsewhere.

### World state left behind

In development mode (`ModuleContext.DevelopmentMode`), nContext checks that
the modules freed their world once a session ended. A module registers a
counter of the objects tied to a world that it holds:

```csharp
sessions.RegisterWorldStateCounter(context, "grids", () => _grids?.Count ?? 0);
```

Right after the `SessionEnded` handlers, nContext reads each counter, in the
load order of the modules. A counter other than 0 is an error charged to the
module that registered it, as a `WorldStateLeakException` that names the
module, the counter, and its value; the error policy of the module applies.
Since the check runs as soon as `SessionEnded` ends, a module waits for its
background jobs in its handler. Outside development mode, the counters are
never read.

### Errors in states

nContext catches an exception thrown by a method of a state, and hands it to
the core with `ModuleContext.ReportFault` on the context of the module that
registered the state. Its error policy applies: with `log`, the state goes
on; with `stop`, the context stops. The methods of the states of a disabled
module are no longer called, and its states stay in the stack.

## Events

None. nContext uses no event bus: the stop request reaches the active state as
a global event.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

The initial state of each side is `<States client="…" server="…"/>` in
`engine.nobara`.

## Resources

None.

## Constraints

- Use the services on the loop thread of the context. Requests are not
  thread-safe; code on another thread posts them to the synchronization
  point. `LoadingTask.Report` is the exception.
- The methods of the states run on the loop thread, at the start of a frame;
  they are loop callbacks and are not `async`.
- Updating the running states allocates nothing; a transition allocates.
- The frames of a session that starts or is active allocate nothing; the
  start and the end of a session allocate.
- nContext keeps no static state: each context has its own stack.

## Known limitations

- States have neither input layers nor screens yet.
- There is no change of dimension. Loading tasks carry a scope
  (`LoadingScope`), and only the scope of the world is used.
- A loading task of `AddAsync` that still runs when its session is
  cancelled is not awaited: its cancellation token is cancelled, and nContext
  ignores what the task does afterwards.
- The world state check runs once the `SessionEnded` handlers ran, not
  later: a counter that a background job lowers afterwards is reported.
- The integrated server is never created: each context of this version is
  the main context of its process.
- Closing the window, losing the focus, and disconnecting are not global
  events yet: the only one is the stop request.
- A state can be in the stack only once.
