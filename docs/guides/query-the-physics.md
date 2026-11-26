# Query the physics

This guide makes the crates of a fictional farming game answer to its
players: a farmer picks the crate or the block in sight, a scarecrow scares
the crows near it, the sprinklers find the ground under them all at once,
and a crate that falls too hard breaks. It uses the
[physics queries](../explanation/glossary.md#physics-query) and the
[contact events](../explanation/glossary.md#contact-event) of nPhysics. The
code excerpts come from
`src/nPhysics/Nobara.Physics.Bepu.Tests/Samples/CratePhysics.cs`, which the
tests of nPhysics.Bepu compile and run.

Prerequisites:

- a module with a dependency on `nobara.physics`;
- a distribution with a backend extension of nPhysics.

## 1. Give the crates their layer and their contacts

A query meets what its filter lets through: the layers of the bodies, and
the voxels when its mask holds `CollisionFilter.VoxelLayer`. Give each kind
of body a layer of its own. A crate also reports its contacts, with the
speed at which it breaks as its impact speed:

```csharp
public BodyHandle AddCrate(EntityId entity) => _physics.AddBody(DimensionId.Default, entity, new BodyDescription
{
    Shape = _crate,
    Mass = 15,
    Filter = new CollisionFilter(CrateLayer, uint.MaxValue),
    Contacts = new ContactReporting(ImpactSpeed: BreakingSpeed),
});
```

A body without `Contacts` reports nothing and costs nothing.
`SetContactReporting` changes it later.

## 2. Pick what a farmer looks at

A ray gives the closest block or body along its way, in every grid and
every region of the dimension:

```csharp
public bool TryPick(in WorldPosition eye, Vector3 look, out PhysicsHit target) =>
    _physics.RayCast(DimensionId.Default, eye, look, Reach, out target, new QueryFilter(CrateLayer | CollisionFilter.VoxelLayer))
    && target.Kind != PhysicsHitKind.NotLoaded;
```

- `target.Kind` tells a block (`Grid`, `Block`, `State`) from a body
  (`Body`); `Distance`, `Point`, and `Normal` say where.
- A chunk that is not loaded is not air: the ray reports it as
  `NotLoaded`, and nothing past it. The farmer picks nothing there.
- `QueryFilter` takes one body to go through, such as the body that casts
  the ray: `new QueryFilter(mask, ignored)`. The default filter meets
  everything.

`RayCastAll` gives every hit along the way, closest first, into a span the
caller owns.

## 3. Scare the crows around a scarecrow

An overlap gives what a shape touches where it stands: a sphere, a capsule,
or a box (`QueryShape`):

```csharp
public int Scare(in WorldPosition scarecrow)
{
    int crows = _physics.Overlap(DimensionId.Default, QueryShape.Sphere(3), scarecrow, Quaternion.Identity, _near, new QueryFilter(CrowLayer));
    for (int i = 0; i < crows; i++)
    {
        _physics.ApplyImpulse(_near[i].Body, new Vector3(0, 5, 0));   // off it flies
    }

    return crows;
}
```

A sweep (`Sweep`, `SweepAll`) moves the same shapes along a line, without
turning them: whether a farmer fits through a gap, where a thrown sack lands.

## 4. Water the fields in one batch

A hot path that casts many rays gives them in one batch. nPhysics spreads it
over the workers of the job system, returns once every ray is cast, and
allocates nothing once its tables have grown:

```csharp
public ReadOnlySpan<PhysicsHit> Water(ReadOnlySpan<WorldPosition> sprinklers)
{
    for (int i = 0; i < sprinklers.Length; i++)
    {
        _sprinklers[i] = new RayQuery(sprinklers[i], -Vector3.UnitY, 16, new QueryFilter(CollisionFilter.VoxelLayer));
    }

    _physics.RayCast(DimensionId.Default, _sprinklers.AsSpan(0, sprinklers.Length), _wetted);
    return _wetted.AsSpan(0, sprinklers.Length);
}
```

Call a batch on the loop thread, outside a job. Every query reads the
blocks and the transforms of the grids, and those of a batch from its jobs:
a tick system that queries declares `IVoxelWorlds.ChunkAccess` and the data
of `WorldTransform` among its reads.

## 5. Break the crates that land too hard

Contact events arrive with the end of each step, on the loop thread, by
batches: `ContactsBegun`, `ContactsEnded`, and `Impacts`. Subscribe once:

```csharp
_physics.SubscribeStepEnd(context, this);
```

Then read the impacts, the contacts whose parts approached at the impact
speed of their body or faster:

```csharp
public void OnStepEnd(in PhysicsStepEnd step)
{
    foreach (ContactEvent impact in step.Impacts)
    {
        if (!Broken.Contains(impact.Body))
        {
            Broken.Add(impact.Body);   // a crate landed at the breaking speed or faster
        }
    }
}
```

- `impact.Body` is the crate; the other part is a block (`Grid`, `Block`,
  `State`) or a body (`Other`).
- `Speed` is the speed of approach along the normal, and `Impulse` an
  estimate of the blow: the speed times the reduced mass of the pair.
- A crate that rests on the ground keeps its contact while it sleeps; it ends
  three steps after the crate leaves the ground.

## Related pages

- [The physics of nPhysics](../explanation/physics.md#queries): how queries
  and contact events work.
- [The nPhysics module sheet](../../src/nPhysics/README.md#queries): the
  whole API.
- [Make a character walk](make-a-character-walk.md): characters, which
  queries do not meet.
