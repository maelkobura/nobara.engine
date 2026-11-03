# nEntity

This page is the module sheet of nEntity, the entity component system of the
engine. It describes what the module provides to other modules and the rules
it enforces. Terms are defined in [the glossary](../../docs/explanation/glossary.md), and
the reasons behind the worlds and systems in [entity
worlds](../../docs/explanation/entity-worlds.md).

## Purpose

nEntity holds the game world as entities and components: one entity world
per dimension, component types registered under qualified names, entity
systems that run as tick systems of nGame, change detection, a parent and
child hierarchy with relative transforms, and entity definitions written in
`.nof` files, from which it creates entities.

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
| `nobara.context` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
<!-- nobara:generated:end -->

## Entry points

| Member | Use |
| --- | --- |
| `IEntityWorlds` | The worlds of the context, the component types, the entity systems, and the built-in components. |
| `EntityWorld`, `EntityId` | One world: entities, their components, archetypes, queries, and the hierarchy. |
| `ComponentType`, `ComponentType<T>`, `ComponentOptions`, `ComponentInfo` | The handles and the description of a component type. |
| `EntityArchetype`, `EntityQuery`, `QueryDescription`, `ChunkView` | Storage by archetype, and iteration block by block. |
| `IEntitySystem`, `EntitySystemRun`, `EntitySystemOptions`, `EntitySystemHandle`, `IChunkJob` | Entity systems, and the work they spread over worker threads. |
| `EntityCommandBuffer` | Structural changes recorded while a system runs. |
| `ChangeVersions` | Comparing the change versions of tracked components. |
| `LocalTransform`, `ParentLink`, `OrphanPolicy` | The hierarchy of entities. |
| `EntityDefinition`, `EntityLoadException`, `EntityProblem` | Entity definitions of `.nof` files, and why one cannot be loaded. |
| `ComponentReader<T>`, `DefinitionValues`, `DefinitionValueException` | How a module reads the values that definitions write for its component. |
| `EntityConverter`, `EntityConversion`, `EntityConverterHandle` | Free properties of definitions converted into components. |

The service is resolved with `context.Resolve<IEntityWorlds>()`. There is one
per context, client and server, and nothing is shared between contexts.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Worlds

Each dimension has its own entity world. The module that owns the dimensions
creates the world of a dimension once the dimension has a clock in nGame, and
destroys it:

```csharp
IEntityWorlds worlds = context.Resolve<IEntityWorlds>();
sessions.SubscribeStarting(context, _ =>
{
    time.AddDimension(Nether);
    worlds.Create(context, Nether);
});
sessions.SubscribeEnded(context, _ => worlds.Destroy(context, Nether));
```

- A world belongs to a world session: it is created in `SessionStarting` at
  the earliest, and destroyed in `SessionEnded` at the latest. Creating a
  world without a session that starts or runs throws.
- Only the module that created a world destroys it.
- The client holds one world at a time, the world of the dimension the
  player sees: creating a second one throws.
- Worlds are kept in the order of their dimensions, the order in which
  systems go through them.
- Worlds are created and destroyed outside entity systems.

In development mode, nEntity registers two world state counters for each
module that creates worlds, `entity worlds` and `entities`. A world left after
`SessionEnded` is reported as a leak of that module, under its error policy.
When the next session starts, nEntity destroys the worlds left behind, with a
warning.

### Components

A component is an unmanaged struct, registered once per context under a
qualified name that starts with the identifier of the module:

```csharp
ComponentType<Growth> growth = worlds.RegisterComponent<Growth>(context, "example.farm.growth");
ComponentType<Moisture> moisture = worlds.RegisterComponent(context, "example.farm.moisture",
    new ComponentOptions { TrackChanges = true }, defaultValue: new Moisture { Value = 0.5f });
```

- A name that does not start with the identifier of the module is refused.
  A name or a C# type is registered once.
- Registration works at any time, including for a type found in an assembly
  loaded after the start: `RegisterComponent(context, name, type)` takes the
  `Type`, and `TryGetComponent<T>` gives the typed handle back.
- `TryGetComponent(name, out ...)` finds a component by its qualified name.
- A new entity starts with the default value of each component.
- A handle is valid in every world of the context that registered it.

### Entities and archetypes

```csharp
EntityArchetype crops = world.GetArchetype(growth, moisture, worlds.WorldTransform);
EntityId wheat = world.Create(crops);
world.Set(wheat, growth, new Growth { Stage = 2 });
ref readonly Moisture wet = ref world.Read(wheat, moisture);
world.Add(wheat, burning, new Burning { Remaining = 5 });
world.Destroy(wheat);
```

Entities with the same components share an archetype, stored in blocks of
64 KiB of unmanaged memory: one column per component. An `EntityId` stays the
same while the entity lives; once it is destroyed, its index is reused with a
new generation, so an old identifier names no entity.

`Create(archetype, span)` creates many entities at once, and
`Destroy(query)` destroys every entity a query matches.

### Transient states

Adding or removing a component moves the entity to another archetype, which
costs a copy of all its components. Keep that for changes that happen
rarely. A state that comes and goes often, such as burning or stunned, is a
value in a component the entity always has:

```csharp
public struct Status
{
    public float BurningRemaining;   // 0 when the entity does not burn
    public float StunnedRemaining;
}
```

### Queries and blocks

```csharp
EntityQuery query = world.Query(new QueryDescription { All = [growth, moisture], None = [frozen] });
foreach (ChunkView block in query)
{
    Span<Growth> stages = block.Write(growth);
    ReadOnlySpan<Moisture> water = block.Read(moisture);
    for (int i = 0; i < block.Count; i++)
    {
        stages[i].Progress += water[i].Value;
    }
}
```

A query follows the archetypes created after it: create it once and keep it.
Do not change the structure of the world while going through a query.

### Entity systems

An entity system is registered while the module initializes. nEntity
registers it with nGame as one tick system, under the same name; each tick,
it runs the system once per world, in the order of the dimensions, and skips
the worlds whose clock is frozen or removed.

```csharp
worlds.AddSystem(context, "example.farm.growth", new GrowthSystem(growth, moisture), new EntitySystemOptions
{
    Query = new QueryDescription { All = [growth, moisture] },
    Reads = [moisture],
    Writes = [growth],
});

public sealed class GrowthSystem(ComponentType<Growth> growth, ComponentType<Moisture> moisture) : IEntitySystem
{
    public void Run(in EntitySystemRun run) => run.RunChunks(new GrowJob { Growth = growth, Moisture = moisture });
}

public struct GrowJob : IChunkJob
{
    public ComponentType<Growth> Growth;
    public ComponentType<Moisture> Moisture;

    public readonly void Execute(in ChunkView chunk)
    {
        Span<Growth> stages = chunk.Write(Growth);
        ReadOnlySpan<Moisture> water = chunk.Read(Moisture);
        for (int i = 0; i < chunk.Count; i++)
        {
            stages[i].Progress += water[i].Value;
        }
    }
}
```

- The stage is `Entities` unless `Stage` says otherwise. `After` and `Before`
  order the system against other tick systems, as in nGame.
- `Reads` and `Writes` declare the components the system touches. Each
  component is one piece of data of the job system for the whole context;
  every entity system also reads the structure of the worlds
  (`IEntityWorlds.Structure`). nGame and the jobs of the system declare
  exactly these data. In development mode, reading a component that is not
  declared, or writing one declared as read only, throws a
  `JobAccessException` that names the system and the data. `ReadsData` and
  `WritesData` add data of other modules.
- `RunChunks` runs a job on each block of the query and returns when all are
  done. From `ParallelThreshold` blocks on (128 by default), it spreads the
  blocks over worker threads, as a `parallel for`; below, it runs them on the
  loop thread, which costs less for little work.
- `IEntitySystem.Run` and `IChunkJob.Execute` are hot path code.
- The profiler measures the system (`Tick system <name>`) and the system in
  each world (`Tick system <name> in dimension <n>`).
- An exception in one world follows the error policy charged by the core,
  and the system still runs in the other worlds unless its module was
  disabled.

### Structural changes during a system

While a system runs on a world, the structure of the world is locked:
creating, destroying, adding or removing components, or changing parents
throws. The system records these changes in `run.Commands`, an
`EntityCommandBuffer`, and declares `ChangesStructure`; the commands apply
in order once `Run` returns. Recording is safe from the jobs of the system. A
command on an entity that is no longer alive is skipped.

### Change detection

A component registered with `TrackChanges` records each write: a change
version per entity, and per block for the whole block. Versions are a 32-bit
counter of the world that goes up after each run of a system, and with
`AdvanceChangeVersion`.

```csharp
uint end = world.AdvanceChangeVersion();
foreach (ChunkView block in query)
{
    if (!block.HasChangedSince(moisture, _lastRead))
    {
        continue;   // nothing changed in this block
    }

    for (int i = 0; i < block.Count; i++)
    {
        if (block.HasChangedSince(moisture, i, _lastRead))
        {
            // block.Entities[i] changed
        }
    }
}

_lastRead = end;
```

- `ChunkView.Write` and `EntityWorld.Write` or `Set` record the change. A
  whole column taken with `Write` records every entity of the block; take it
  with `WriteUntracked`, then call `MarkChanged` for the entities that
  change.
- Creating an entity or adding a component records the new values; moving an
  entity to another archetype keeps the versions of its values.
- In a system, `run.LastRunVersion` is the version of the previous run of the
  system on the world: it sees the changes made since, and not its own.
- Compare versions with `ChangeVersions`, which handles the wrap of the
  counter. The world brings versions older than 2^30 up to that age as the
  counter goes; a reader that did not read for that long sees every value as
  changed.

### Hierarchy

An entity can be the child of another, placed relative to it:

```csharp
world.SetParent(rider, horse);                                   // stays where it is
world.SetParent(saddle, horse, OrphanPolicy.Destroy, keepWorldTransform: false);
world.ClearParent(rider);                                        // keeps its place in the world
```

- `nobara.entity.transform` (`WorldTransform`) is where an entity is in the
  world; `nobara.entity.local_transform` (`LocalTransform`) is where a child
  is relative to its parent; `nobara.entity.parent` (`ParentLink`) links the
  child to its parent, with its depth.
- `SetParent` adds the components the child lacks. With
  `keepWorldTransform`, the default, the child stays where it is and its
  local transform is computed; otherwise it keeps its local transform.
- The link is a value: changing the parent of an entity that already has a
  link moves it to no other archetype. The link cannot be written or removed
  directly.
- An entity has one parent; a parent that is the child itself or one of its
  descendants is refused.
- Destroying a parent detaches each child, which keeps its place in the
  world, or destroys it, as its link says (`OrphanPolicy`).
- The system `nobara.entity.transforms`, in stage `Late`, computes the world
  transform of each child from its parent, depth by depth, with the blocks of
  one depth in parallel. A child's world transform is written by this system:
  move a child through its local transform.

### Entity definitions

An `Entity` resource of a `.nof` file defines the components of an entity
and their initial values; its format is in [the `Entity`
reference](../../docs/reference/formats/entity.md). nEntity loads each
definition once per context, then creates entities from it without reading
the resources again:

```csharp
EntityDefinition pig = worlds.GetDefinition("entity:example.animals.pig");   // once, outside hot path code
EntityId one = world.Instantiate(pig);
world.Instantiate(pig, herd);                                                 // a Span<EntityId>: as many as it holds
```

- In development mode, every definition is loaded when nEntity starts: an
  error stops the start of the context with an `EntityLoadException`, which
  lists every problem with its file, line, and column. Otherwise a
  definition is loaded the first time `GetDefinition` asks for it, and the
  exception is thrown there.
- Definitions are read once nEntity has started, after every module has
  initialized: register components and converters while initializing.
- The components of a definition are fixed when it is loaded: those the file
  writes, without the optional components of modules that are not active,
  plus those the converters add. A definition has one archetype per world,
  created the first time it is instantiated there; after that, instantiating
  allocates nothing.
- A component not found is an error, unless it is optional and the module its
  name starts with is declared by the distribution but not active. The table
  of cases is in [the `Entity` reference](../../docs/reference/formats/entity.md#optional-components).
- Entities created from a definition are ordinary entities: nothing links
  them to the definition afterwards.

### Component values

By default, the values a definition writes for a component go to the public
fields of its struct, by name: numbers, booleans, enumerations by the name of
a member, and nested structs such as `Vector3` as child elements. A field
that is not written keeps the default value of the component.

A value that a field cannot hold as written, such as an object identifier, or
a rule of the component, such as a bound, is read by a reader that the module
registers with the component:

```csharp
worlds.RegisterComponent<Voice>(context, "example.animals.voice", (values, fallback) =>
{
    double volume = values.GetFloat("Volume", fallback.Volume);
    return volume is >= 0 and <= 1
        ? new Voice { Volume = (float)volume }
        : throw values.Error($"<Volume> is {volume}, expected 0 to 1.", values.GetElement("Volume"));
});
```

- A reader runs once per definition and per context, when definitions load
  or reload; it may allocate.
- A value written twice, a value of the wrong type, and a child element that
  the reader never reads are errors, with the position of the value.
- `nobara.entity.transform` and `nobara.entity.local_transform` read
  `<Position>`, `<Rotation>` (a quaternion), and `<Scale>` as vectors.
  `nobara.entity.parent` is never written in a definition.

### Converters

A module reads the free properties of the definitions it cares about, and
turns them into components of its own. The definition does not need to know
the module:

```csharp
public void Register(ModuleContext context) =>
    _weight = context.Resources.DeclareProperty("Weight", PropertyType.Float);

public void Initialize(ModuleContext context)
{
    IEntityWorlds worlds = context.Resolve<IEntityWorlds>();
    _body = worlds.RegisterComponent<Body>(context, "example.motion.body");
    worlds.AddConverter(context, conversion =>
    {
        if (_weight.TryGetFloat(conversion.Resource, out double mass))
        {
            conversion.Set(_body, new Body { Mass = (float)mass });
        }
    });
}
```

- A converter runs once per definition and per context, when the definition
  is loaded or reloaded. Nothing of the free properties is kept afterwards.
- The components of the file come first; then converters run in the load
  order of their modules, and in the order they were added within a module.
  A converter reads every component of the definition (`Has`, `TryGet`,
  `Components`) and sets the components of its own module only (`Set`).
- `conversion.Error` reports a property that breaks a rule of the module: the
  definition fails to load, as for a wrong value. An exception thrown by the
  converter follows the error policy of its module, and what the converter
  set is discarded.
- Converters are added while modules initialize. Disposing the handle
  removes the converter for the definitions loaded later.

### Hot reload of definitions

In a development build, a changed `.nof` file applies at the next frame:

- new values apply to the entities created from then on; entities created
  before keep theirs;
- a change of the components of a definition is reported in the logs, and
  applies once no entity world exists, when the world is reloaded: until
  then, the definition keeps its previous components and values;
- a definition added is loaded at once; a definition removed stays until the
  context restarts, and a value that fails its checks is reported, with the
  definition kept as it was.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

## Resources

nEntity defines the resource type `Entity` (prefix `entity`), described in
[the `Entity` reference](../../docs/reference/formats/entity.md). Its plugin,
`Nobara.Entity.Build`, checks it when a module that depends on
`nobara.entity` is built.

## Constraints

- Use the service and the worlds on the loop thread of the context. Jobs of
  entity systems read and write the components their system declares.
- Components are unmanaged structs: the storage lives outside the managed
  heap.
- Running entity systems allocates nothing once the worlds and archetypes are
  stable, in development mode too; the tests measure it with 100,000
  entities. Creating a world, an archetype, or a query allocates.
- Instantiating a definition allocates nothing once its archetype exists in
  the world; the tests measure it with 10,000 entities. Getting a definition
  the first time loads it, which allocates.
- nEntity keeps no static state: each context has its own component types,
  worlds, and systems.

## Known limitations

- An error raised by nEntity because a system misuses it, such as an
  undeclared access in development mode or an entity that is not alive, is
  charged to nEntity, the module whose code threw, which stops the context.
  The message names the system and its module.
- Entities do not move between worlds, and have no lasting identifier.
- Nothing is replicated or saved; change detection only records the writes.
  Destroyed entities are not reported to readers of changes.
- Systems of different modules never run in parallel with each other, and a
  system cannot run once every few ticks.
- An entity created by a command buffer is not known to the system that
  records it.
- When a job of `RunChunks` fails, the system is not told: its commands are
  still applied, and the failure reaches the next synchronization point.
- A component type is never unregistered: it stays until the context ends.
- `GetArchetype` allocates, even for an archetype that exists: get archetypes
  once, outside hot path code.
- Scales of a hierarchy combine axis by axis: a non-uniform scale under a
  rotation gives an approximate transform.
- A command buffer cannot instantiate a definition, so an entity system
  cannot create entities from one while it runs.
- Definitions have no tags, and do not inherit from one another.
- The fields of a component are written by their C# names: renaming a field
  breaks the files that write it, unless the module reads the values with a
  reader.
- A whole number above the range of `long` cannot be written in a `ulong`
  field.
- A converter adds or fills components; it does not remove the components
  that the file writes.
