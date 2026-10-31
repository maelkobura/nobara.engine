# nBlocks

This page is the module sheet of nBlocks, the block registry. It describes
what the module provides to other modules and the rules it enforces. Terms
are defined in [the glossary](../../docs/explanation/glossary.md).

## Purpose

nBlocks keeps the types of blocks of a game: the resource type `Block`, the
block types defined by every module, their native fields, their state
properties and the index of each block state, their tags, and the block
hooks. It provides the air, `block:nobara.air`. It never holds blocks of a
world: grids and chunks belong to the module that stores them. It draws
nothing and knows nothing of entities.

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
| `nobara.items` | `^1.0` | Required |
<!-- nobara:generated:end -->

nItems keeps the item that `<CreateItem>` generates for a block, and nBlocks
registers with it the item component `nobara.blocks.placeable`.

## Entry points

| Member | Use |
| --- | --- |
| `IBlockRegistry` | The types (`Get`, `TryGet`, `Types`, `Air`), the tags (`TryGetTag`, `Tags`), the states (`StateCount`, `TypeOf`, `GetOpacity`, `GetCollision`, `GetHardness`, `GetValue`, `With`, `Rotate`, `HasTag`, `Describe`, `TryParseState`), the free properties of each state (`ResolveProperty`), and the item component `Placeable`. |
| `BlockType`, `StateProperty`, `BlockTag` | A type of block with its native fields, its range of states, and its properties; a state property and its values; a tag of the registry. |
| `BlockCollision`, `BlockOpacity`, `BlockOrientations`, `BlockCell`, `StatePropertyKind` | The values of the native fields, and the kinds of state properties. |
| `IBlockHooks` | The standard hooks (`Placing`, `Placed`, `Breaking`, `Broken`, `Interact`, `NeighborChanged`, `RandomTick`, `ScheduledTick`), the hooks declared by modules (`DeclareHook`), the subscriptions (`Subscribe`, `SubscribeAll`), and the firing (`HasSubscribers`, `Fire`). |
| `BlockBatch`, `BlockChange`, `BlockBatchView`, `IBlockBatchHandler` | A batch of blocks placed or broken, one block of it, what one subscriber sees of it, and the subscriber. |
| `BlockHook<TArgs>`, `IBlockHandler<TArgs>`, `BlockInteraction`, `NeighborChange`, `BlockTick` | A hook fired for one block, its subscriber, and the arguments of the standard ones. |
| `BlockActor`, `BlockCause`, `BlockPosition` | Who acts, why, and where, in a neutral form. |
| `PlaceableBlock` | The value of the item component `nobara.blocks.placeable`: the block and the state an item places. |
| `BlockLoadException`, `BlockProblem` | Why the blocks cannot be loaded. |

The services are resolved with `context.Resolve<IBlockRegistry>()` and
`context.Resolve<IBlockHooks>()`; there is one of each per context. Call them
on the loop thread of the context.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### The registry

Blocks are resources of type `Block`, described in [the `Block`
reference](../../docs/reference/formats/block.md). The core decodes them once
per process; each context builds its own block types from them.

1. While modules register, nBlocks publishes its two services.
2. When nBlocks initializes, after nItems, it registers the item component
   `nobara.blocks.placeable`, then reads every block, numbers the types and
   their states, resolves the tags, and freezes. A problem in a block stops
   the context with a `BlockLoadException`.
3. The modules that depend on nBlocks initialize next: they read the types,
   resolve free properties per state, and subscribe to hooks. Reading the
   registry earlier throws.

Each type has an index: 0 for the air, then the other types sorted by
identifier, in ordinal order. The states of a type are contiguous, and the
air is state 0. The same blocks give the same indices on every side and in
every load. Use indices in memory and on the network between sides that
loaded the same modules, never on disk.

### Block states

A state is one combination of the values of the state properties of a type.
The properties of a type are, in index order:

1. `orientation`, when the type has orientations;
2. `part`, when the block occupies several cells;
3. the `<Bool>` properties, then the `<Int>` ones, then the `<Enum>` ones,
   each kind in the order written.

The last property varies fastest. Resolve a property once with
`BlockType.GetProperty`, and keep it:

```csharp
BlockType lamp = blocks.Get("block:example.blocks.lamp");
StateProperty lit = lamp.GetProperty("lit");

uint on = blocks.With(lamp.DefaultState, lit, 1);
bool isLit = blocks.GetValue(on, lit) == 1;
string id = blocks.Describe(on);   // block:example.blocks.lamp[lit=true]
```

`GetValue`, `With`, `Rotate`, `TypeOf`, `GetOpacity`, `GetCollision`,
`GetHardness`, and `HasTag` take constant time and allocate nothing.

`Rotate` gives the state after a quarter turn, clockwise seen from above:
the orientation turns, a property with `rotatesTo` gives its value to its
target, and an `<Enum>` with `rotation` maps its values. Four turns give the
same state.

### Free properties per state

A module that reads a free property of blocks resolves it once for every
state, after it declared the property during registration:

```csharp
GenericElement?[] light = blocks.ResolveProperty(context, _light);
```

Each state takes the value of its resource, then of each variant that
matches it, in the order written: the last one wins. At each level, the
property qualified by the short name of the module wins over the plain one.
A value of a variant that does not have the type the module declared throws
a `BlockLoadException`.

### Block hooks

Hooks tell modules what happens to blocks of the types they subscribe to.
They are distinct from the event bus. nBlocks declares and stores them; the
module that acts on blocks fires them.

```csharp
hooks.Subscribe(context, hooks.Breaking, lamp, new KeepLitLamps(), priority: 0);

public sealed class KeepLitLamps : IBlockBatchHandler
{
    public void Handle(BlockBatchView batch)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            // Remove(i) keeps the block; Cancel() stops the whole batch.
        }
    }
}
```

| Hook | When | Subscribers chosen by |
| --- | --- | --- |
| `Placing`, `Breaking` | Before a batch of blocks applies. A subscriber may remove blocks or cancel the batch. | The new state (`Placing`) or the old state (`Breaking`). |
| `Placed`, `Broken` | Once the batch applied, with the blocks that remained. | The same. |
| `Interact` | An actor uses a block. A subscriber that answers `true` consumes it. | The state of the block. |
| `NeighborChanged` | A direct neighbor of a block changed. | The state of the block. |
| `RandomTick`, `ScheduledTick` | A random tick of a block, or a scheduled tick that falls due. | The state of the block. |

- A subscriber subscribes by type, by tag, or, for `Placing` and
  `Breaking`, to every type with `SubscribeAll`, such as a protection of
  areas.
- A batch is grouped by subscriber in one pass; each subscriber is called
  once, with the blocks that concern it and that no earlier subscriber
  removed. Removing one cell of a block of several cells removes all of
  them.
- Subscribers run by decreasing priority; at equal priority, in the load
  order of their modules, then by module identifier, then in the order they
  subscribed.
- When nBlocks starts, it builds one table per type and per hook, and one
  flag per state. A block whose type has no subscriber costs a test of
  `HasSubscribers`. Firing allocates nothing once the lists have grown.
- A subscription added or disposed after the start applies at the start of
  the next frame; a disposed subscriber is not called from the moment it is
  disposed.
- A subscriber that throws is reported with `ModuleContext.ReportFault` on
  its module, and the next one runs.
- A module declares more unit hooks, such as `example.physics.contact`, with
  `DeclareHook` while it initializes.

### Tags

Block tags are resolved by the core and turned into sets of type indices.
`TryGetTag` takes `#example.wood` or `#block:example.wood`;
`BlockType.HasTag`, `BlockTag.Contains`, and `IBlockRegistry.HasTag`
allocate nothing.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `blocks.maxStatesPerType` | distribution | int | `1024` | 1 to … | both |
<!-- nobara:generated:end -->

`blocks.maxStatesPerType` caps the number of states of one type. Building a
module warns about a type above the default; loading the blocks fails for a
type above the value of the distribution.

## Resources

The resource type `Block`, described in [its
reference](../../docs/reference/formats/block.md), and the air,
`block:nobara.air`. Its plugin, `Nobara.Blocks.Build`, checks what the schema
cannot (`NBLK001` to `NBLK011`) and generates the item of `<CreateItem>`. The
registry of the type accepts tags.

In development mode, nBlocks applies reloaded blocks in place, at the next
frame: native fields, variants, default values, and tags. The `BlockType`
and `BlockTag` objects stay the same, and the hook tables are rebuilt. A
module that resolved free properties per state resolves them again if it
needs the new values. A type added or removed, or a change of its properties or of their
values, changes the states: it is refused with a warning and needs a restart
of the context.

## Constraints

- Use the services on the loop thread of the context.
- The read methods of the states, `HasSubscribers`, and `Fire` allocate
  nothing. The methods of `IBlockBatchHandler` and `IBlockHandler<TArgs>`
  are hot path code, checked by the engine analyzers.
- Building the registry, resolving free properties, and rebuilding the hook
  tables allocate; they happen while modules initialize, when nBlocks
  starts, and when resources reload.
- nBlocks keeps no static state: each context has its own registry and its
  own hooks.

## Known limitations

- Worlds, grids, chunks, and the storage of blocks are not part of nBlocks:
  nothing fires the hooks yet but the modules that act on blocks.
- Shapes are kept as identifiers: no module checks them yet, and nothing is
  drawn.
- The tags of the generated item cannot be written in `<CreateItem>`.
- The cap of states is not checked when a distribution is built, only when
  the blocks load.
- The qualifiers of the free properties of variants are not checked against
  the short names of the modules.
- A type added or removed, or a change of its states, needs a restart of the
  context.
- Hooks are fired on the server only.
