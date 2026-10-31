# nContext

This page is the module sheet of nContext, the module that drives the states
of the application. It describes what the module provides to other modules
and the rules it enforces. Terms are defined in [the
glossary](../../docs/explanation/glossary.md).

## Purpose

nContext runs the path of the application through its states, such as a
boot screen, a main menu, the game, and a pause menu: it keeps the state
stack of each context, applies the transitions between two frames, and lets
the active state decide what a stop request does.

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

The service is resolved with `context.Resolve<IStateStack>()`; there is one
per context. The engine provides no state: the modules of a game provide
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
3. calls `Update` on each running state, from the bottom of the stack to its
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

- Use the service on the loop thread of the context. Requests are not
  thread-safe; code on another thread posts them to the synchronization
  point.
- The methods of the states run on the loop thread, at the start of a frame;
  they are loop callbacks and are not `async`.
- Updating the running states allocates nothing; a transition allocates.
- nContext keeps no static state: each context has its own stack.

## Known limitations

- States have neither input layers nor screens yet.
- There are no world sessions, no loading tasks, and no change of dimension.
- The integrated server is never created: each context of this version is
  the main context of its process.
- Closing the window, losing the focus, and disconnecting are not global
  events yet: the only one is the stop request.
- A state can be in the stack only once.
