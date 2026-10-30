# nItems

This page is the module sheet of nItems, the item registry. It describes
what the module provides to other modules and the rules it enforces. Terms
are defined in [the glossary](../../docs/explanation/glossary.md).

## Purpose

nItems keeps the types of items of a game: the resource type `Item`, the
item types defined by every module, their tags, the declared look of each
item, the types of item components, and the usage of items (kinds of
interaction, handlers, prolonged uses, and the usage service). It never
holds instances of items: stacks belong to the module that stores them. It
knows nothing of blocks or entities.

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
| `nobara.game` | `^1.0` | Required |
<!-- nobara:generated:end -->

nGame times the prolonged uses: they follow the dimension clock of their
actor, so the pause and a frozen dimension suspend them.

## Entry points

| Member | Use |
| --- | --- |
| `IItemRegistry` | The items (`Get`, `TryGet`, `GetByIndex`, `Items`), the tags (`TryGetTag`, `Tags`), the default stack size, and the component types (`RegisterComponent`, `TryGetComponentType`). |
| `ItemType`, `ItemVisual`, `ItemTag` | An item type with its native fields, its look, its resource, and its default components; a tag of the registry. |
| `ItemComponentType<T>`, `ItemComponents`, `ComponentValues` | A type of item component; an immutable set of components and values; the values of a `.nof` file given to the reader of a type. |
| `IItemUsage` | The kinds of interaction (`Use`, `UseOnBlock`, `UseOnEntity`, `Attack`, `DeclareInteraction`), the handlers (`AddHandler`), and the usage service (`UseItem`, `Interrupt`, `TryGetProlongedUse`). |
| `IItemHandler<T>`, `ItemUse<T>`, `UseResult` | The code that runs when an item with a component is used, what it receives, and what it answers. |
| `ItemActor`, `UseTarget`, `ItemStackView` | Who uses the item, on what, and the item with the components of its stack, in a neutral form. |
| `ItemLoadException`, `ItemProblem` | Why the items cannot be loaded. |
| `Nobara.Items.Build.DerivedItem` | Builds the content of an item that the plugin of another type derives from its resources. |

The services are resolved with `context.Resolve<IItemRegistry>()` and
`context.Resolve<IItemUsage>()`; there is one of each per context. Call them
on the loop thread of the context.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### The registry

Items are resources of type `Item`, described in [the `Item`
reference](../../docs/reference/formats/item.md). The core decodes them once
per process; each context builds its own item types from them.

1. While modules initialize, they register their component types, declare
   their interaction kinds, and attach their handlers.
2. When nItems starts, after every module has initialized, the registry
   converts the components of every item, resolves the tags, computes the
   handler tables, and freezes. A problem in an item stops the context with
   an `ItemLoadException`.
3. From then on, the items are read, and registering a component type or
   declaring a kind throws. Reading the items before throws too.

Each item has an index, from 0: the position of its identifier among the
identifiers of every item, sorted. The same items give the same indices on
every side. Use the index in tables and hot paths, never on disk.

An item derived from the resource of another type, such as the item of a
block, gives the identifier of its source in `ItemType.DerivedFrom`. nItems
does not know the source type.

### Item components

A component holds data that a stack may change for itself, such as the
nourishment of a food or the durability of a tool. Its type is defined in
code and registered by a module, under a name that starts with the
identifier of that module. Items declare their default values in their
`.nof` file, and the registry converts them once, when it freezes:

```csharp
public sealed record Edible(int Food, long Duration);

public void Initialize(ModuleContext context)
{
    IItemRegistry items = context.Resolve<IItemRegistry>();
    _edible = items.RegisterComponent(
        context,
        "example.food.edible",
        values => new Edible(values.GetInt("Food"), values.GetTicks("Duration", fallbackSeconds: 1.6)),
        EqualityComparer<Edible>.Default);
}
```

- The reader receives `ComponentValues`: typed reads by name (`GetInt`,
  `GetFloat`, `GetBool`, `GetString`, `GetObjectId`, `GetVector`,
  `GetElement`), with or without a fallback. A value that the reader never
  reads, of the wrong type, missing, or written twice fails the load with the
  position of the value. `Error` builds the exception of a rule of the
  component.
- `GetTicks` reads a duration in seconds and converts it at the tick rate of
  the distribution: definitions do not depend on it.
- The equality decides whether two stacks merge. Values are immutable:
  `ItemComponents.With` and `Without` return a new set.
- A component marked `optional="true"` is ignored when its module is
  declared by the distribution and not active in the context.

### Usage

A module attaches a handler to a component type and a kind of interaction.
The handler may come from another module than the one that registered the
type, if it depends on it:

```csharp
usage.AddHandler(context, _edible, usage.Use, new EatHandler(), priority: 0);

public sealed class EatHandler : IItemHandler<Edible>
{
    public UseResult Use(in ItemUse<Edible> use) => UseResult.Prolong(use.Value.Duration);

    public void UseFinished(in ItemUse<Edible> use)
    {
        // Feed the actor; consume the item through the module that stores it.
    }
}
```

The controller of the player, in stage `Intents` of nGame, or an artificial
intelligence calls the service on the server:

```csharp
UseOutcome outcome = usage.UseItem(new ItemActor(entity, dimension), new ItemStackView(item, components), usage.Use, UseTarget.None);
```

- The handlers of the components of the stack for that kind run by
  decreasing priority; at equal priority, in the load order of their
  modules, then by the name of their component, then in the order they were
  added. The first that answers `Handled` or `Prolong` stops the others;
  `Pass` lets the next one run.
- A stack with the default components of its item takes the handlers that
  the registry computed for that item. A stack with its own components goes
  through every handler of the kind, which is slower.
- The service never changes the stack. Which input triggers which kind is
  the business of the game.
- A handler added after the registry froze rebuilds the tables at once.
  Disposing its `ItemHandlerHandle` removes it.
- A handler that throws is reported with `ModuleContext.ReportFault` on its
  module: its error policy applies, and the next handler runs.

The four standard kinds are `Use`, `UseOnBlock`, `UseOnEntity`, and `Attack`.
A module declares more, such as `example.bows.reload`, with
`DeclareInteraction` while it initializes.

### Prolonged uses

A handler that answers `UseResult.Prolong(duration, everyTick)` starts a
prolonged use, such as eating or drawing a bow. The tick system
`nobara.items.prolonged_uses`, in stage `Schedule` after the scheduler,
times it on the dimension clock of the actor, and calls the handler:

| Method | When |
| --- | --- |
| `UseTick` | At each tick of the use, when the handler asked for it. |
| `UseFinished` | Once the duration has elapsed on the clock. A jump of the clock ends the use at the next tick. |
| `UseInterrupted` | When `Interrupt` is called, when the actor starts another prolonged use (`Replaced`), when the dimension of the actor loses its clock, or when the handler is removed. |

An actor has one prolonged use at a time. The pause and a frozen dimension
suspend it: its clock does not move.

### Tags

Item tags are resolved by the core and turned into sets of indices.
`TryGetTag` takes `#example.meat` or `#item:example.meat`;
`ItemType.HasTag` and `ItemTag.Contains` allocate nothing.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `items.defaultMaxStack` | distribution | int | `64` | 1 to … | both |
<!-- nobara:generated:end -->

## Resources

The resource type `Item`, described in [its
reference](../../docs/reference/formats/item.md). Its plugin,
`Nobara.Items.Build`, checks what the schema cannot: a `<Visual>` without
model or sprite (`NITM001`), and a component declared twice (`NITM002`). The
registry of the type accepts tags.

In development mode, nItems applies reloaded items in place, at the next
frame: native fields, component values, look, and tags. The `ItemType` and
`ItemTag` objects stay the same, and the handler tables are rebuilt before
the next tick. An item added or removed, or a value that fails its checks, is
refused with a warning and needs a restart of the context.

## Constraints

- Use the services on the loop thread of the context.
- `UseItem`, `Interrupt`, `TryGetProlongedUse`, `ItemComponents.TryGet`,
  `ItemType.HasTag`, and the timing of the prolonged uses allocate nothing.
  The methods of `IItemHandler<T>` are hot path code, checked by the engine
  analyzers.
- Registering, converting, and rebuilding tables allocate; they happen while
  modules initialize, when nItems starts, and when resources reload.
- nItems keeps no static state: each context has its own registry.

## Known limitations

- Item stacks, their storage, and their transactions are not part of nItems.
- Icons and the rendering of items are not provided.
- Component types provide their equality only: no network serialization and
  no persistence yet.
- Uses are not predicted on the client, and the service sends no event on the
  event bus.
- An item added or removed by a reload needs a restart of the context.
