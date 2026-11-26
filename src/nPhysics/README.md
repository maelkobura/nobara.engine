# nPhysics

This page is the module sheet of nPhysics, the physics module. It describes
what it provides to other modules and the rules it enforces. Terms are
defined in [the glossary](../../docs/explanation/glossary.md).

## Purpose

nPhysics simulates the [bodies](../../docs/explanation/glossary.md#body) of a
context behind a neutral API, on a physics
[backend](../../docs/explanation/glossary.md#backend) that an extension
registers, and is the only module that writes the position of a simulated
entity or grid: the [movement authority](../../docs/explanation/glossary.md#movement-authority).

A body is an entity of nEntity that carries the components of nPhysics. Other
modules create shapes, make entities bodies, join bodies with joints, and
move them through commands: forces, impulses, velocities, kinematic targets,
and teleports. nPhysics simulates each
[physics region](../../docs/explanation/glossary.md#physics-region), an island
of active chunks, in a [physics world](../../docs/explanation/glossary.md#physics-world)
of its own: it applies the commands before each step, moves the bodies to the
world of their region, steps every world, then writes the transforms of the
bodies that moved, grids included. Bodies collide with the voxels of every
grid, read where nVoxel keeps them, with no geometry to rebuild. Its
[character controller](../../docs/explanation/glossary.md#character-controller)
moves characters, players and mobs alike, after the
[movement intent](../../docs/explanation/glossary.md#movement-intent) that a
game writes for them, and carries them on their
[support grid](../../docs/explanation/glossary.md#support-grid). Its
[queries](../../docs/explanation/glossary.md#physics-query) meet the voxels of
every grid and the bodies of every region as one world, and the bodies that
ask for them get [contact events](../../docs/explanation/glossary.md#contact-event)
at the end of each step. The layers and the
authority are described in [the physics of nPhysics](../../docs/explanation/physics.md).

## Status

`experimental`

## Level

nPhysics:

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server. Requires an active backend on the client and on the server.
<!-- nobara:generated:end -->

nPhysics.Bepu:

<!-- nobara:generated:begin level nobara.physics.bepu -->
Platform module, on the client and on the server. Backend of `nobara.physics`.
<!-- nobara:generated:end -->

## Dependencies

nPhysics:

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.blocks` | `^1.0` | Required |
| `nobara.context` | `^1.0` | Required |
| `nobara.entity` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |
| `nobara.debug` | `^1.0` | Optional |
| `nobara.graphics` | `^1.0` | Optional |
| `nobara.renderer` | `^1.0` | Optional |
<!-- nobara:generated:end -->

nPhysics.Bepu:

<!-- nobara:generated:begin dependencies nobara.physics.bepu -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.physics` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |

Third-party libraries: `BepuPhysics` 2.5.0-beta.33, `BepuUtilities` 2.5.0-beta.33.
<!-- nobara:generated:end -->

nPhysics holds no physics library: the backend extension brings one.
nPhysics.Bepu uses BEPUphysics v2, `BepuPhysics` and `BepuUtilities`
2.5.0-beta.33 (Apache 2.0, without a `NOTICE` file), pure C# with no native
library. The version is a beta, chosen on purpose and given exactly: a newer
version is taken by an explicit change, after running the tests of the
extension again. No other module sees the types of BEPUphysics.

The optional dependencies of nPhysics on nDebug, nRenderer, and nGraphics
are used by one isolation class, `PhysicsDebugWindow`: the "Physics" tool
window, which shows the backend, the duration of the last step, the queries
of the last tick and the contact events of the last step, the bodies by
kind, awake, asleep, and frozen, the counts of transfers, merges, splits,
and recenters, and the regions with their box, their origin, and their
bodies, ghosts, and joints. In development mode, it adjusts the substeps and
the gravity of the context until it stops, without saving them. When
nRenderer is active and draws in development mode, it draws in the world,
through `IDebugDraw`, the shapes of the bodies (colored by kind, sleep, and
freezing) and the boxes of the characters, the contacts of the bodies that
report them, and the boxes of the regions.

## Entry points

| Member | Use |
| --- | --- |
| `IPhysicsSystem` | Shapes (`CreateSphere`, `CreateCapsule`, `CreateBox`, `CreateCylinder`, `CreateConvexHull`, `CreateCompound`, `CreateMesh`, `GetShape`, `DestroyShape`), bodies (`AddBody`, `RemoveBody`, `IsAlive`, `TryGetBody`, `GetState`), commands, joints (`CreateJoint`, `SetMotor`, `DestroyJoint`), characters (`AddCharacter`, `RemoveCharacter`, `IsCharacter`), queries (`RayCast`, `RayCastAll`, `Sweep`, `SweepAll`, `Overlap`, and the batches `RayCast` and `Sweep`), providers of medium, the end of the steps (`SubscribeStepEnd`), and the counts (`Regions`, `Worlds`, `Statistics`, `BackendName`, `Substeps`, `Gravity`). |
| `QueryShape`, `QueryFilter`, `PhysicsHit`, `PhysicsHitKind`, `RayQuery`, `SweepQuery` | The shapes, filters, and results of the queries. |
| `ContactReporting`, `ContactEvent`, `ContactEvents` | How a body reports its contacts, the events, and their fixed rules. |
| `BodyDescription`, `BodyKind`, `BodyHandle`, `PhysicsMaterial`, `CollisionFilter`, `BodyInertia`, `BodyState` | What a body is made of, its handle, and its state. |
| `PhysicsBody`, `BodyShape`, `BodyMaterial`, `BodyMass`, `BodyVelocity`, `BodyFlags` | The components of a body, written by nPhysics only. |
| `PhysicsShape`, `ShapeKind`, `ShapeDescription`, `CompoundChild` | Shapes, shared by the bodies that use them. |
| `JointDescription`, `JointKind`, `JointHandle`, `JointMotor` | Hinges, sliders, and fixed joints, with an optional motor. |
| `CharacterBody`, `MovementIntent`, `CharacterState`, `CharacterGround`, `CharacterGroundKind`, `CharacterController` | The components of a character, what it stands on, and the fixed rules of the character controller. |
| `IPhysicsStepObserver`, `PhysicsStepEnd`, `BodyPose`, `CharacterStep`, `CharacterLanding`, `PhysicsStepSubscription` | The end of each step, with the bodies whose transform it wrote, the contact events, and the steps and landings of the characters. |
| `IMediumProvider`, `MediumSample`, `MediumProviderHandle` | What fills a point, as a fluid module describes it. |
| `PhysicsRegionInfo`, `PhysicsWorldInfo`, `PhysicsStatistics` | The regions, the physics worlds, and the counts after the last step. |
| `PhysicsFrame` | The conversions between positions in the world and positions in a physics world. |
| `IPhysicsBackends`, `IPhysicsBackend`, `IPhysicsShapeSource`, `PhysicsBackendWorld`, `PhysicsWorldOptions`, `PhysicsStepSettings`, `BackendBody`, `BackendBodyState`, `BackendJoint`, `VoxelChunkCollider`, `VoxelChunkCells`, `BackendQuery`, `BackendQueryHit`, `BackendContact` | The interface of a backend, for the extensions that provide one. |
| `PhysicsSettings` | The names of the configuration keys. |
| `PhysicsModule` | The identifier of the module and the name of its tick system. |

Resolve the services with `context.Resolve<IPhysicsSystem>()` and
`context.Resolve<IPhysicsBackends>()`, from the initialization of a module
that depends on `nobara.physics`, and call them on the loop thread of the
context.

The tick system `nobara.physics.step` runs in the stage `Physics`. Other
systems can be ordered against it. nPhysics reserves `voxel.grid.transform`:
it holds the writer of grid transforms of nVoxel.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Bodies

```csharp
IPhysicsSystem physics = context.Resolve<IPhysicsSystem>();
PhysicsShape crate = physics.CreateBox(new Vector3(1, 1, 1));   // once, shared

EntityId entity = world.Create(world.GetArchetype(entities.WorldTransform));
world.Set(entity, entities.WorldTransform, WorldTransform.At(WorldPosition.FromBlocks(10, 64, 10)));
BodyHandle body = physics.AddBody(DimensionId.Default, entity, new BodyDescription
{
    Shape = crate,
    Mass = 20,
    Material = new PhysicsMaterial(0.8f, 0.1f),
});
```

- The entity has a world transform, no parent, and is neither a body nor a
  character.
  The body starts where the entity is; `Center` places the center of the
  body, the origin of its shape and its center of mass, in the frame of the
  entity.
- `AddBody` adds five components to the entity: `nobara.physics.body`,
  `nobara.physics.shape`, `nobara.physics.material`, `nobara.physics.mass`,
  and `nobara.physics.velocity`. Read them, never write them: change a body
  through `IPhysicsSystem`.
- Bodies are added and removed outside entity systems, since both change the
  structure of the world. A system that adds bodies declares the structure of
  the worlds and the components of nPhysics.
- From then on, nPhysics writes the world transform of the entity after each
  step in which the body moved. When the entity is a grid, it writes the
  transform of the grid through nVoxel, which updates the spatial index of
  the grids. A transform written to a body entity by another module is lost
  at the next step: move a body with `Teleport`.
- The scale of the entity is kept and does not change the shape.
- `RemoveBody` removes the components and the joints of the body; the entity
  keeps its last transform, and its module writes it again. A body whose
  entity is destroyed is removed at the next step.
- An entity without a body is not simulated: its module writes its world
  transform.

A static body never moves on its own; a kinematic body moves by its
velocities or its targets, pushes dynamic bodies and is never pushed; a
dynamic body is simulated. A mesh is the shape of a static body only.

### Shapes

A shape is created once and shared by every body that uses it. A compound
holds convex shapes: spheres, capsules, boxes, cylinders, and convex hulls.
`DestroyShape` refuses a shape that a body or a compound still uses. A
backend reads the `ShapeDescription` of a shape the first time a body of a
world uses it.

### Commands

Every change of a body is queued, and applied before the next step, in the
order the commands were given:

| Command | Bodies | Effect |
| --- | --- | --- |
| `Teleport` | all | Moves the body at once, without sweeping the way; velocities are kept. |
| `SetVelocity` | kinematic, dynamic | Sets the linear and angular velocities. |
| `SetKinematicTarget` | kinematic | Gives the place reached at the end of the next step; the body stops there unless it gets another target. |
| `ApplyForce`, `ApplyForceAt`, `ApplyTorque` | dynamic | Applies a force or a torque during the next step. |
| `ApplyImpulse`, `ApplyImpulseAt`, `ApplyAngularImpulse` | dynamic | Applies an impulse before the next step. |
| `Wake`, `WakeArea` | all | Wakes a body, or the bodies of a dimension that overlap a box. |
| `SetKind`, `SetShape`, `SetMass`, `SetMaterial`, `SetFilter`, `SetContinuous` | all | Changes what the body is made of. |
| `SetContactReporting` | all | Turns its [contact events](#contact-events) on or off. |

A command on a body that is not alive throws `ArgumentException`; a body
removed between the command and the step skips it. Commands are hot path
code and allocate nothing once the queue has grown to the largest tick.

### Collision layers

`CollisionFilter` gives a body the layers it belongs to and the layers it
collides with, as two 32-bit masks. Two bodies collide when each belongs to
a layer the other collides with:

```csharp
var player = new CollisionFilter(Layers: 1 << 1, Mask: ~(1u << 1));   // players do not collide with players
```

The voxels of every grid belong to `CollisionFilter.VoxelLayer`, the last
layer (bit 31), and collide with every layer. Keep that bit out of the layers
of bodies; a body whose mask leaves it out goes through the voxels:

```csharp
var ghost = new CollisionFilter(Layers: 1 << 2, Mask: ~CollisionFilter.VoxelLayer);   // through walls and floors
```

### Joints

```csharp
JointHandle hinge = physics.CreateJoint(frame, door, new JointDescription(
    JointKind.Hinge,
    AnchorA: new Vector3(0.5f, 0, 0),
    AnchorB: new Vector3(-0.5f, 0, 0),
    AxisA: Vector3.UnitY,
    AxisB: Vector3.UnitY,
    Motor: new JointMotor(TargetVelocity: 1, MaxForce: 200)));
physics.SetMotor(hinge, null);
```

A joint joins two bodies of the same dimension. Joined bodies stay in one
physics world, the one of the region of the first body of their group: a
joint between bodies of two regions takes effect at the next step, once the
second body has moved there. Anchors and axes are in the frame of each body,
from its center. A fixed joint keeps the place and
the rotation the bodies had relative to each other when it was made, and has
no motor. Removing a body destroys its joints.

### The step

Each tick, in the stage `Physics`, `nobara.physics.step`:

1. removes the bodies whose entity, entity world, or grid is gone;
2. applies the queued commands, in order;
3. stops the kinematic bodies that reached their target at the last step;
4. derives the regions from the tickets, moves the bodies to the world of
   their region, and places the ghosts;
5. steps each physics world once, with `physics.substeps` substeps, the
   worlds of independent regions at the same time;
6. writes the world transform and the velocities of each body that moved,
   and the flag `Asleep` when it changes;
7. reads the contacts of the bodies that report them (see
   [Contact events](#contact-events));
8. moves the characters, on the grids where the step has just put them (see
   [Characters](#characters));
9. publishes the end of the step to the observers.

The duration of a step is the duration of the tick. While the game is
paused, the phase `Simulation` does not run, and nothing steps; under a time
scale, more or fewer ticks run per second, each with the same step.

### Physics worlds and positions

A physics world is a simulation of the backend with an origin, a whole
chunk. Positions in it are offsets from the corner of that chunk, in blocks:
`PhysicsFrame` converts them from and to `WorldPosition`, and no absolute
coordinate in single precision reaches a backend. A body a billion blocks
from the origin keeps the precision it has at the origin.

### Regions

nPhysics simulates only the active areas: the area tickets of level `Active`
on the terrain of each dimension, such as the simulation area of a viewer
(`IChunkLoading.AddViewer`). It derives the regions from them before each
step, and when a body is added:

- a region is the box of chunks of one ticket, or of several tickets whose
  boxes share a chunk; its origin is the chunk at its center;
- a region gets a physics world when a body enters it, and loses it when the
  last one leaves;
- a body belongs to the region that holds its center, and stays in it while
  its center is less than 1 block out of it. When it leaves, it moves to the
  world of the region it enters, with its pose and its velocities: the move
  costs about 5 µs per body. It arrives awake;
- a body that no region holds is frozen: it keeps its position and its
  velocities, carries the flag `Frozen`, and moves again once a region holds
  it. Commands change what it is made of, its pose, and its velocities;
  forces, impulses, and kinematic targets given to it are lost;
- two regions that touch see each other's bodies near their border as ghosts:
  a body less than 2 blocks plus its velocity over a step away from the other
  region, beyond its bounds, has a double there, of the same kind, placed
  again where the body is before each step. A dynamic ghost carries the mass
  of its body, so that what it rests on bears its weight; the ghost of a
  sleeping body is kinematic and still;
- regions whose boxes share a chunk merge: the bodies move to the world of the
  region with the most bodies. A merged region splits once two empty chunks
  lie between its islands;
- when the center of a region drifts more than 8 chunks from its origin, its
  bodies move to a new world around its center;
- the world with the most awake bodies steps on the loop thread, where its
  backend may spread it over the workers; the others step at the same time,
  each in one index of a parallel job of the job system.

`IPhysicsSystem.Regions` lists the regions, and `Statistics` counts the
frozen bodies, the ghosts, the transfers, the merges, the splits, and the
recenters. The regions and their worlds belong to the world session. In
development mode, nPhysics registers the world state counters `bodies`,
`joints`, `regions`, `physics worlds`, `ghosts`, `voxel chunks`, and
`characters`. The
values are described in [the physics of nPhysics](../../docs/explanation/physics.md#regions-at-local-origins).

### Voxels

Bodies collide with the voxels of the terrain and of every other grid, with
no geometry built from them: the backend reads the blocks where nVoxel keeps
them, while the world steps.

- **Chunks in a world.** Before each step, each dynamic body (and each
  dynamic ghost) holds the chunks its bounds reach, grown by its velocity
  over the step and 1 block, in the frame of each grid the spatial index of
  nVoxel finds there. A world adds a chunk to its backend
  (`AddVoxelChunk`) when a body first holds it, and removes it 30 steps after
  the last one lets it go, or before the chunk leaves memory. A sleeping body
  keeps its chunks without computing them again. Static and kinematic bodies
  hold none: they do not collide with voxels.
- **Shapes.** Each cell gives its collision boxes, in the frame of the cell:
  a cube is one box; a model, or a part of a multi-block, the boxes of its
  shape, turned by its orientation, as nVoxel gives them; a shape that
  depends on the instance, the boxes it gives for the block data of the cell,
  computed again when that cell changes. Air and blocks with
  `<Collision>none` have none. nPhysics reads the shape of every block state
  once, when it starts, and keeps at most 16 boxes per cell: the boxes past
  the 16th are merged into one.
- **Inner faces.** A contact that leaves a box through a face of its cell,
  when the cell next to it covers that face whole, is dropped: a body cannot
  reach that face without going through the neighbor. A box that slides over
  a flat floor then does not catch on the joints between its cells.
- **Turned grids.** A grid that is turned collides in its own frame: its
  chunks enter the world with the rotation of the grid.
- **Changed blocks.** A block placed or broken is seen at the next step, at
  no cost to the physics. The notifications of nVoxel wake the bodies that
  overlap the changed blocks, grown by one block, at the next step: a crate
  asleep on a block that breaks falls. Blocks filled as a chunk is generated
  wake nobody.
- **Freezing.** A dynamic or kinematic body that reaches a chunk of the
  terrain that is not loaded or not active, within its bounds, its velocity
  over a step, and 1 block, is frozen as a body that no region holds is
  (see [Regions](#regions)). It moves again, with the velocities it had,
  once those chunks are active: a body is never lost in an empty chunk.

`Statistics.VoxelChunks` counts the chunks the worlds hold, and
`Statistics.VoxelWakes` the times changed blocks woke the bodies of a world.

### Characters

A character is an entity that the character controller moves: an upright
box, at the feet of the entity, that follows its `MovementIntent`. The
controller is part of nPhysics, needs no backend, and runs the same code on
both sides; it reads no input and knows no walking speed. The guide
[Make a character walk](../../docs/guides/make-a-character-walk.md) shows
where the player controller of a game plugs in.

```csharp
CharacterBody body = CharacterBody.Default;   // 0.6 × 1.8 × 0.6 blocks, steps of 0.6 block
physics.AddCharacter(DimensionId.Default, farmer, body);
world.Set(farmer, intent, new MovementIntent { Velocity = new Vector3(4.3f, 0, 0), JumpSpeed = jumping ? 7f : 0 });
```

- **Components.** `AddCharacter` adds `CharacterBody` (`nobara.physics.character`),
  `CharacterState` (`nobara.physics.character_state`), and, when the entity
  lacks them, `MovementIntent` (`nobara.physics.movement_intent`) and
  `BodyVelocity`. The module of the character writes `CharacterBody` when it
  wants; any module writes `MovementIntent` at the stage `Intents`; nPhysics
  alone writes `CharacterState`, `BodyVelocity`, and the position. The entity
  has a world transform, no parent, and no body.
- **Movement.** On the ground, the character takes the wanted horizontal
  velocity at once, relative to the ground; while `JumpSpeed` is above 0, it
  jumps each time it stands on the ground. In the air, gravity times
  `GravityScale` pulls it, and its horizontal velocity turns toward the wanted
  one at `AirAcceleration`, or stays when the wanted one is zero.
- **Collision.** The box moves along the vertical, then X, then Z, and stops
  at each box it meets on the way: it slides along walls and out of inner
  corners. It meets the collision boxes of the voxels of every grid, read as
  the voxel layer reads them, and the static bodies, by the box that holds
  each shape or each child of a compound. A step no higher than `StepHeight`
  is walked up when the way ahead is blocked and the character stood on the
  ground.
- **Ground.** The character stands on the ground when its downward motion
  was stopped. `CharacterState.Ground` gives what it stands on: a block, its
  grid, and its state, or a static body; of several, the one under most of
  its feet. A jump leaves the ground at once.
- **Support grid.** When the ground is a block of a grid other than the
  terrain, that grid becomes the support grid of the character: the character
  becomes a child of the grid entity (`ParentLink`), its local transform is
  where it stands on the grid, and the controller resolves it in the frame of
  the grid. It moves and turns with the grid with no drift, since it does not
  move in that frame. When the ground belongs to another grid, or to the
  terrain, the character passes to that frame at once, with its velocity in
  the world kept. After `CharacterController.SupportGraceSteps` steps in the
  air, 3, it leaves the grid with the velocity the grid had at its feet. A
  grid turned more than 45 degrees from the vertical, or scaled, is never a
  support grid.
- **Dynamic bodies.** The character goes through dynamic bodies, and pushes
  those its box touches, by the sphere that holds their shape: each step, an
  impulse along the way from the character to the body, of the collision
  that would leave both at one speed. A crate of 2 kilograms takes almost all
  the speed of a character of 80; a block of 2 tonnes, almost none. Dynamic
  bodies never push the character back.
- **Freezing.** A character that reaches a chunk of the terrain that is not
  loaded or not active does not move, and keeps its velocity:
  `CharacterState.IsFrozen` tells so.
- **Removal.** `RemoveCharacter`, or removing `CharacterBody` from the entity
  (seen at the next step), takes the character off its support grid where it
  is in the world, removes `CharacterBody` and `CharacterState`, and keeps
  `MovementIntent` and `BodyVelocity`, with the last velocity. nPhysics writes
  its transform no more: its module moves it, or makes it a body.
  `AddCharacter` gives it back to the controller from where it is, with the
  velocity of its `BodyVelocity`.
- **Position.** nPhysics keeps no position of its own for a character: each
  step starts from its transform, its local transform on a support grid. It
  writes the position only: the module of the character turns it, through its
  local transform while it has a parent.

The end of each step lists in `CharacterSteps` the characters that walked on
the ground, with the distance and the ground, and in `CharacterLandings`
those that landed at `CharacterController.LandingSpeed` (1 block per second)
or faster, with their speed, their steps in the air, and the ground.
`Statistics.Characters` counts the characters.

### Queries

```csharp
if (physics.RayCast(DimensionId.Default, eye, look, 5, out PhysicsHit hit, new QueryFilter(CrateLayer | CollisionFilter.VoxelLayer)))
{
    // hit.Kind: Block (hit.Grid, hit.Block, hit.State), Body (hit.Body), or NotLoaded.
}
```

The guide [Query the physics](../../docs/guides/query-the-physics.md) goes
through each kind of query.

- **Kinds.** `RayCast` gives the closest hit along a ray, `RayCastAll` the
  closest ones into a span, closest first. `Sweep` and `SweepAll` move a
  `QueryShape` (a sphere, a capsule along Y, or a box) along a line without
  turning it; a shape that starts inside something meets it at distance 0,
  with the opposite of the direction as normal. `Overlap` gives what a shape
  touches where it is, in no particular order. All take positions as
  `WorldPosition`, and a direction of any length.
- **Voxels and bodies.** A query meets the blocks of every grid of the
  dimension, terrain included, by their collision boxes as the voxel layer
  reads them, and the bodies of every region it may reach. nPhysics tests the
  voxels itself, in the frame of each grid, whether a physics world holds
  their chunk or not; the backend tests the bodies, in the frame of each
  world. A body and its ghost are met once.
- **Filter.** `QueryFilter` gives the layers a query meets, the voxels when
  its mask holds `CollisionFilter.VoxelLayer`, and one body it goes through.
  A body is met when it belongs to a layer of the mask. The default filter
  meets everything.
- **Chunks that are not loaded.** A chunk of a grid loaded by tickets that is
  not in memory is reported as a hit of kind `NotLoaded`, at the distance
  where the query reaches it, and nothing past it is reported. A grid that
  keeps its chunks has no such chunk.
- **Results.** `PhysicsHit` gives the distance along the direction, the
  point, the normal toward the query, and the body, or the grid, the block,
  and its state. A ray reports the point it reaches; a sweep a point of the
  surface it touches; an overlap the center of its shape and no normal.
- **Batches.** `RayCast` and `Sweep` take a batch, `ReadOnlySpan<RayQuery>`
  or `ReadOnlySpan<SweepQuery>`, and give the closest hit of each at its
  index. A batch of more than 32 queries runs in slices on the workers of
  the job system, each with its own scratch, and returns once all are done.
  Call it on the loop thread, outside a job. Its jobs declare the reads of
  `IVoxelWorlds.ChunkAccess` and of the data of `WorldTransform`: a tick
  system that runs a batch declares them too.
- **Cost.** Queries are hot path code, and allocate nothing once their
  tables have grown (checked with a batch of 1,000 rays), except a single
  query that meets a block whose shape depends on its instance in a chunk
  that no physics world holds, which evaluates its shape.

`Statistics.Queries` counts the queries asked between the last two steps.

### Contact events

A body reports its contacts when its description, or `SetContactReporting`,
gives it a `ContactReporting`; it then carries the flag `ReportsContacts`.
Other bodies cost nothing.

```csharp
BodyHandle crate = physics.AddBody(DimensionId.Default, entity, new BodyDescription
{
    Shape = shape,
    Contacts = new ContactReporting(ImpactSpeed: 6),   // ContactReporting.Default: 1 block per second
});
```

- **Pairs.** A contact is a pair: a body that reports and another body, or a
  body and a grid. A body touches a grid once, however many of its blocks
  it touches; the event gives the block under the deepest contact. Two
  bodies that both report give one event, whose `Body` has the lower slot.
- **Begun.** A pair begins when its parts come within
  `ContactEvents.BeginReach` (0.01 block) of each other, their approach over
  the step counted, and is listed in `PhysicsStepEnd.ContactsBegun`.
- **Ended.** A pair ends once the backend has not reported it for
  `ContactEvents.GraceSteps` (3) steps in a row, and is listed in
  `ContactsEnded` with what it last was. A pair whose body sleeps or is
  frozen goes on. A pair of a removed body ends at once, with its dead
  handle. A pair whose bodies stop reporting is dropped without an event.
- **Impacts.** A pair whose parts approached, along the normal and before
  the step, at the impact speed of its body or faster (the lower one when
  both report) is listed in `Impacts`, with `Speed` and an estimate of the
  `Impulse`: the speed times the reduced mass of the pair, a body that is not
  dynamic and a block counting as of infinite mass, times one plus the
  restitution of the pair.
- **Where.** `Point` is the deepest contact, and `Normal` goes from the other
  part toward `Body`.

The events are published by batches in the end of the step, on the loop
thread, to the observers of `SubscribeStepEnd`. `Statistics.ContactEvents`
counts the events of the last step. The steps and landings of the
characters stay apart, in `CharacterSteps` and `CharacterLandings`.

### End of the step

```csharp
public sealed class History : IPhysicsStepObserver
{
    public void OnStepEnd(in PhysicsStepEnd step)
    {
        foreach (BodyPose pose in step.Bodies)
        {
            // Keep pose.Transform for step.Tick.
        }
    }
}

physics.SubscribeStepEnd(context, new History());
```

The observers are called on the loop thread, once the transforms are
written and the characters moved, with the bodies whose transform the step
wrote, the contact events, and the steps and landings of the characters.
`OnStepEnd` is hot path code. An exception is charged to the module of the
observer.

### Providers of medium

A module that knows what fills the world, such as a fluid module, registers
an `IMediumProvider` with `AddMediumProvider`; `TrySampleMedium` asks the
providers in the order they were added, and the first that describes the
point answers with its density, viscosity, and current.

### Backends

A backend extension declares `<BackendOf module="nobara.physics" />`, depends
on `nobara.physics`, and registers its backend while it initializes:

```csharp
public void Initialize(ModuleContext context) =>
    context.Resolve<IPhysicsBackends>().Register(context, new ExampleBackend());
```

- nPhysics requires a backend on both sides: a distribution that activates
  it without an active backend fails to build (`NRC211`), and a context in
  which no backend registers fails to start.
- When it starts, nPhysics takes the backend that `physics.backend` names,
  or the first one registered. Registering a backend later, or two backends
  of the same name, throws.
- `IPhysicsBackend.CreateWorld` creates a `PhysicsBackendWorld` for each
  physics world. Only nPhysics calls it, never from two threads at once on
  the same world: the members of `PhysicsBackendWorld` are hot path code. Bodies
  and joints are numbered by the world; `BackendBody.Slot` is the slot of the
  body in nPhysics, for tables that a backend indexes by body.
- `PhysicsStepSettings.MaySpread` tells a world whether its step may use the
  worker threads of the job system: nPhysics sets it when it steps the world
  on the loop thread. A world stepped from a job must not wait for other jobs.
- `ComputeInertia` gives the inertia of a shape when a description gives
  none.
- nPhysics calls `AddVoxelChunk` and `RemoveVoxelChunk` for the chunks
  around the dynamic bodies of a world (see [Voxels](#voxels)). A backend
  reads the collision boxes of each cell from `VoxelChunkCollider.Cells`
  while the world steps, and drops the contacts that
  `VoxelChunkCells.IsInnerContact` sets apart.
- `RayCast`, `Sweep`, and `Overlap` meet the bodies of the world only, never
  its chunks of voxels: nPhysics tests the voxels itself. They give hits by
  `BackendBody.Slot`, which a ghost shares with its body, and keep the
  closest ones that fit the span. They are the only members called from
  several threads at once, each with its index below
  `PhysicsWorldOptions.QueryThreads`, while no other member runs.
- After each step, `Contacts` lists the pairs that hold a body whose
  `BackendBody.ReportsContacts` is set (`SetReportsContacts`), once per pair
  of bodies or of a body and a chunk, at their deepest contact, while they
  touch or will touch during the step within `BackendContact.Reach`.
- `SetGravity` changes the gravity of a world; the debug window uses it in
  development mode.

| Extension | Backend name | Library |
| --- | --- | --- |
| nPhysics.Bepu (`nobara.physics.bepu`) | `bepu` | BEPUphysics v2, 2.5.0-beta.33 |

### nPhysics.Bepu

nPhysics.Bepu runs on both sides. Each physics world is a simulation of
BEPUphysics of its own, with its own pools of unmanaged memory, freed with
the world; two worlds share nothing and can step at the same time.

- **Bodies.** A dynamic or kinematic body is a body of BEPUphysics; a static
  body is a static of BEPUphysics, except while a joint holds it: BEPUphysics
  joins bodies only, so a static body with joints is a kinematic body that
  never moves, and a static again once its last joint is gone. A body that
  may sleep falls asleep after about a second at rest.
- **Shapes.** A world builds the shape of BEPUphysics of a neutral shape the
  first time one of its bodies uses it, and frees it once no body and no
  compound of the world uses it. A convex hull keeps the origin its points
  give it. A triangle of a mesh collides on one side only, its front face,
  from which its vertices turn counterclockwise.
- **Inertia.** `ComputeInertia` gives the tensor about the origin of the
  shape, for a uniform density; a compound shares its mass among its children
  by volume.
- **Materials.** The friction of a pair is the geometric mean of the
  frictions of its two bodies; its restitution, the larger of the two. Contacts
  are springs of 30 hertz. BEPUphysics has no restitution of its own: when a
  pair that bounces is about to touch, the world notes its speed of approach,
  and after the step gives the pair back the share of that speed its
  restitution asks for, along the normal of the contact. A pair that
  approaches slower than 1 block per second does not bounce, so that a body
  can come to rest.
- **Layers.** Two bodies collide when each belongs to a layer the other
  collides with; contacts are computed only for pairs with at least one
  dynamic body.
- **Continuous detection.** Every body has an unbounded speculative margin: a
  contact is found before the bodies touch, however fast they go. A body with
  continuous detection is also swept along its path, and its contacts are as
  stiff as the substeps allow, half their rate: a fast body then stops at a
  thin wall instead of going through.
- **Joints.** A hinge is a hinge constraint, plus an angular motor; a slider
  is a point on a line and a lock of the rotation the bodies had when it was
  made, plus a linear motor; a fixed joint is a weld. A motor drives the
  second body relative to the first: about the axis of the first body for a
  hinge, along it for a slider. A slider uses the axis of the first body only.
- **Voxels.** `AddVoxelChunk` adds a static whose shape is a compound of the
  collision boxes of the cells, read where they are while the world steps,
  with no tree and no copy: a block placed or broken is seen at the next
  step, with no structure to rebuild. Each cell has up to 16 children, one per
  box. The callback that configures the contacts drops those that go through
  an inner face. The voxels have the default material. The chunk does not
  change during a step. Without `VoxelChunkCollider.Cells`, every cell that
  holds a block collides as a whole cube.
- **Queries.** Rays, sweeps, and overlaps are the queries of BEPUphysics,
  among the bodies and statics of the world, its chunks of voxels left out.
  Each thread that queries at once has a pool of memory of its own. An
  overlap is a sweep over a ten-thousandth of a block whose hits at distance
  0 count.
- **Contacts.** The callback that configures the contacts of a pair notes,
  for a pair with a body that reports, its deepest contact and the speed at
  which the pair approaches, in a list per worker of the step; the world
  gathers the lists after the step. BEPUphysics finds no contact for a pair
  that parts slowly a few millimeters apart, hence the grace steps of
  nPhysics before a contact ends.
- **Threads.** A world steps on the thread that calls it. When
  `MaySpread` is set and the world has at least 1,000 awake bodies, the step
  spreads over the job system: the calling thread is a worker, and the other
  workers run as one parallel job, which the calling thread waits for. The
  extension creates no thread, and a step in a steady state allocates no
  managed memory.

## Events

None. The end of each step, with its contact events, is published to the
observers of `SubscribeStepEnd`, synchronously, on the loop thread.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `physics.backend` | distribution | string | `auto` |  | both |
| `physics.substeps` | distribution | int | `8` | 1 to 32 | both |
| `physics.velocityIterations` | distribution | int | `1` | 1 to 16 | both |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `physics.backend` | The name of the physics backend, or `auto` for the first one registered. An unknown name takes the first one, with a warning. Read at start. |
| `physics.substeps` | The number of substeps of each step. More substeps keep stacks and joints stable, at a cost in time. Read at start. |
| `physics.velocityIterations` | The number of velocity iterations of the solver in each substep. Read at start. |

## Resources

None.

## Constraints

- Call the services and the handles on the loop thread of the context. Shapes
  can be created from the initialization of nPhysics on; bodies, once it has
  started and a world session runs.
- nPhysics is the only module that writes the position of a body or the
  transform of a grid. nVoxel gives its writer of grid transforms only to the
  module that reserves `voxel.grid.transform`.
- Once the queue of commands and the tables of bodies have grown, a step
  allocates no managed memory, commands, observers, and the writing of the
  transforms included (checked by a test with a test backend). The placing of
  the bodies, which creates the regions, their worlds, and the records of the
  ghosts when they appear, is not hot path code as a whole; it allocates
  nothing once the regions are stable (checked by a test with three regions
  and ghosts). Moving characters allocates nothing once they walk on voxels,
  on a grid, and along static bodies (checked by a test with the backend of
  nPhysics.Bepu); putting a character on a grid for the first time gives it
  the components of the hierarchy, a change of structure. The voxel layer allocates when a chunk enters a world, and
  nothing once the bodies rest on chunks the worlds hold (checked by a test
  with bodies on the voxels).
- Queries read the blocks and the transforms of the grids: a tick system
  that queries declares `IVoxelWorlds.ChunkAccess` and the data of
  `WorldTransform` among its reads. Call them outside the step of nPhysics,
  or from its observers.
- The step reads the blocks of every grid: its tick system declares
  `IVoxelWorlds.ChunkAccess` as a read. It writes the structure of the entity
  worlds, since characters change parents and lose their components there. Blocks change at the stage `World`, before the
  step, and not during it.
- Bodies, joints, characters, and physics worlds belong to the world
  session: they are forgotten when it ends. Shapes belong to the context.
- nPhysics keeps no static state.

## Known limitations

- No backend ships in this module: a distribution needs a backend extension
  of `nobara.physics`.
- Only area tickets of level `Active` on a terrain grid make regions: a
  ticket on a single chunk, a ticket of level `ReducedActive`, and a ticket on
  another grid do not. A body there is frozen.
- A region is the box of its tickets, so it covers the chunks between two
  tickets that share a chunk, even when no ticket holds them.
- Only the chunks of the terrain freeze a body: a body that reaches a chunk
  of another grid that is not in memory meets no voxel there.
- A body frozen by a chunk is looked at again when a chunk enters or leaves
  memory, when the regions change, or when a command touches it: a chunk
  already in memory that becomes active through a ticket that makes no
  region does not free it by itself.
- A body that leaves the world of a region loses its sleep, and the backend
  loses its contacts: it can move by a few hundredths of a block per second
  at the next step. A transferred joint is made again: a fixed joint keeps
  the pose the bodies have then, and a slider the rotation they have then.
- Across a border, a body and the ghost of another do not push each other as
  two bodies of one world do: each world solves its own bodies, and the
  ghost of a dynamic body follows its body again at the next step.
- Joined bodies are simulated in the region of the first body of their group,
  however far from it the others are.
- Queries meet neither characters nor frozen bodies, nor the voxels of a
  scaled grid.
- A query goes through one body at most (`QueryFilter.Ignored`).
- A hit on a part of a multi-block gives its cell, not the origin of the
  multi-block: ask nVoxel for it from `Grid` and `Block`.
- A query meets the bodies of the regions whose box, grown by one chunk and
  the reach of the query, it comes near: a body larger than a chunk can be
  missed by a query from outside that margin.
- Call the queries on the loop thread: the queries that are not batches
  share one scratch.
- A batch takes a block whose shape depends on its instance, in a chunk
  that no physics world holds, as a whole cell: it does not call the shapes
  of other modules from its jobs.
- A sweep of a capsule or a sphere stops a ten-thousandth of a block before
  it touches; one that only grazes the corner of a box after many steps of
  its advancement is not reported.
- A contact event names the block under the deepest contact of a body
  against a grid, not every block the body touches.
- Static and kinematic bodies give no contact events with each other nor
  with the voxels, since the backend computes no contact there.
- The impulse of an impact is an estimate from the speed and the masses,
  not the impulse the solver applied.
- A contact ends three steps after its parts part; a body that bounces off
  and comes back within them keeps it.
- The debug drawing shows a cylinder, a convex hull, and a mesh by the box
  that holds them, at most 2,000 bodies, and the contacts of the bodies that
  report them only.
- Static and kinematic bodies do not collide with voxels, and grids do not
  collide with anything yet: the voxels of a grid that is a body, and of a
  scaled grid, are left out of the voxel layer.
- The voxels have the default material, whatever the block.
- A cell keeps at most 16 collision boxes: the boxes of a shape past the 16th
  are merged into their bounds.
- Across the border of a chunk, a cell whose shape depends on its instance
  is never taken as covering a face: a body can catch on the joint there.
- A body woken by a changed block is one that overlaps the block grown by
  one block: a body that rests on a body that rests on the block wakes when
  the one below starts to move.
- A character collides with static bodies by the box that holds each shape,
  along the axes of its frame: a sphere, a turned box, or a capsule stops it
  before its surface. It goes through meshes and kinematic bodies.
- A grid that moves into a character that does not stand on it does not push
  it: the character can end up inside the grid. The voxels of a grid turned
  relative to the frame of the character collide by the box that holds each
  of their boxes in that frame.
- A character pushes a dynamic body by the sphere that holds its shape,
  starting before their surfaces touch for a box.
- Each character looks at every body of the context for static bodies to
  meet and dynamic bodies to push: the cost of a step grows with the number
  of characters times the number of bodies.
- A character on a support grid whose position in the grid is thousands of
  blocks from its origin loses precision as single precision does there.
- A shape of a block that depends on its instance, in a chunk that no physics
  world holds, is evaluated at each step for the characters near it, which
  allocates.
- The providers of medium are asked only through `TrySampleMedium`: nPhysics
  applies no buoyancy nor drag.
- Bodies are added and removed outside entity systems only.
- A transform written to a body entity by another module is not detected; the
  next step overwrites it.
- The gravity is 9.81 blocks per square second downward, and no setting
  changes it: only the debug window does, in development mode.
- nPhysics.Bepu bounces bodies along the normal of the contact, on their
  linear velocities only: a spinning ball bounces as one that does not spin.
- nPhysics.Bepu notes at most 256 bouncing pairs per worker in a step; the
  pairs past that limit do not bounce during that step.
- In nPhysics.Bepu, a body that reaches the back face of a mesh is not held,
  and can be thrown out of it.
- nPhysics.Bepu depends on a beta of BEPUphysics, whose interface changes
  between betas.
