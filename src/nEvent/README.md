# nEvent

This page is the module sheet of nEvent, the event bus. It describes what the
module provides to other modules and the rules it enforces. Terms are defined
in [the glossary](../../docs/explanation/glossary.md).

## Purpose

nEvent gives each context a bus of typed events: modules emit `struct`
events, other modules subscribe handlers with priorities, handlers cancel or
change events, and events can be delivered at once or queued for the end of
the tick.

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
| `IEventBus` | The service of the module, resolved with `context.Resolve<IEventBus>()`. One instance per context. |
| `IEventBus.Subscribe`, `SubscribeMonitor` | Subscribe a handler to one event type. They return an `EventHandle`. |
| `IEventBus.Emit`, `EmitCancellable` | Deliver an event to its handlers before the call returns. |
| `IEventBus.Post` | Queue an event for the end of the tick. |
| `IEventBus.HasSubscribers` | Tell whether an event type has handlers, to skip building a costly event. |
| `EventHandle` | Removes a handler when disposed. |
| `EventPriority` | `Lowest`, `Low`, `Normal`, `High`, `Highest`, and `Monitor`. |
| `ICancellable` | The marker of an event that handlers can cancel. |
| `EventCallback<TEvent>`, `MonitorCallback<TEvent>` | The handler signatures: the first receives the event by reference, the second by read-only reference. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

A module that publishes or listens to events declares a dependency on
`nobara.event`, or an optional one when it works without the bus.

### Declaring and emitting an event

An event is a `struct` that the module that emits it declares. A handler
receives exactly that type, never a related one: to let handlers listen to a
group of events, declare a more general event.

```csharp
public struct PlayerJoinRequested : ICancellable
{
    public string Name;
    public bool Cancelled { get; set; }
}

var request = new PlayerJoinRequested { Name = "Ada" };
bus.EmitCancellable(ref request);
if (request.Cancelled)
{
    return;
}
```

A handler may change the fields of the event; the emitter reads them after
the call. Adding a field to an event keeps its handlers working; removing or
renaming one is a breaking change of the module.

### Subscribing

```csharp
public void Initialize(ModuleContext context)
{
    var bus = context.Resolve<IEventBus>();
    _handle = bus.Subscribe<PlayerJoinRequested>(
        context,
        (ref PlayerJoinRequested e) => e.Cancelled = IsBanned(e.Name),
        EventPriority.High,
        ignoreCancelled: true);
}
```

Handlers of an event type run in this order:

1. by priority, from `Lowest` to `Highest`, so the highest priority has the
   last word;
2. `Monitor` handlers last: they see the final event and can neither cancel
   nor change it;
3. at the same priority, in the load order of their modules, then in the
   order they subscribed.

`ignoreCancelled` skips the handler once a handler that ran before it
cancelled the event. A handler that subscribes while a dispatch runs is first
called by the next dispatch. Disposing the handle removes the handler at once,
even from the dispatch that runs.

The load order of the modules is the order in which the bus first sees them
subscribe. It is the load order of the core for the modules that subscribe
while they initialize.

### Delivering events

| Delivery | Method | Thread | When handlers run |
| --- | --- | --- | --- |
| Synchronous | `Emit`, `EmitCancellable` | Loop thread of the context only | Before the call returns. |
| Deferred | `Post` | Any thread, jobs included | After the simulation, on the loop thread. |

A synchronous emission from another thread throws an
`InvalidOperationException` that says to post the event instead. A handler may
emit another event synchronously, up to a depth of 8 nested emissions; the
call beyond throws, and the handler that made it receives the exception.

A deferred event cannot be cancelled, since nobody waits for the answer. The
bus hands the queued events out in the order they were posted, across types.
An event posted while the bus hands events out waits for the next tick. An
event of a type that has no handler when it is posted is dropped.

The bus hands deferred events out in the first phase after the last fixed
phase, which is `PostSimulation` in the default loop. The phase runs once per
frame, so the events posted during the several ticks of a frame go out
together, after the last of them. When no phase follows the fixed phases, or
every phase that follows is reserved by another module, the events go out at
the start of the next frame.

### Errors in handlers

An exception in a handler is logged at the `Error` level, with the module
that subscribed the handler as the source and the event type in the message.
The dispatch goes on with the next handlers, and the emitter does not see
the exception. The bus does not apply an error policy to it.

## Events

None. nEvent defines no event of its own.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

## Resources

None.

## Constraints

- Emitting synchronously and handing out deferred events run on the loop
  thread of the context. Subscribing, `Post`, and disposing a handle run on
  any thread.
- `Emit`, `EmitCancellable`, `Post`, `HasSubscribers`, and the hand-out of
  deferred events are hot path code: they allocate nothing, call nothing by
  reflection, and take no lock. The tests measure it. Subscribing allocates.
- The bus keeps no static state. Each context has its own bus, with its own
  handlers and queues.
- Each event type that has handlers gets a queue of 1024 deferred events,
  fixed. `Post` throws when it is full.
- The bus never delivers an event to a module that the core disabled.
- When the context stops, the bus removes every handler. The modules that use
  the bus are stopped before it.
- The bus is not for hot paths such as voxel changes or physics contacts.
  Those use the services of their modules.

## Known limitations

- Events are not relayed to plugins, and the bus has no debug window.
- A handler that fails repeatedly is not disabled, and there is no option to
  stop at the first failure.
- A synchronous emission is checked against the thread that last ran a
  lifecycle step or the start of a frame of the context: the core does not
  expose the loop thread.
- The rank of a module among handlers of the same priority is the order of its
  first subscription: a module that loads first but subscribes after another
  module has subscribed runs after it.
- The capacity of the queue of an event type is not configurable.
- Events live in the assembly of the module that emits them; there is no
  separate contract assembly yet.
