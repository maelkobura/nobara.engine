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
and teleports. nPhysics applies the commands before each step, steps every
[physics world](../../docs/explanation/glossary.md#physics-world), then writes
the transforms of the bodies that moved, grids included. The layers and the
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
| `nobara.context` | `^1.0` | Required |
| `nobara.entity` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |
| `nobara.debug` | `^1.0` | Optional |
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

The optional dependency of nPhysics on nDebug is used by one isolation class,
`PhysicsDebugWindow`: the "Physics" tool window, which shows the backend, the
duration of the last step, the bodies by kind, awake and asleep, and the
physics worlds with their origin.

## Entry points

| Member | Use |
| --- | --- |
| `IPhysicsSystem` | Shapes (`CreateSphere`, `CreateCapsule`, `CreateBox`, `CreateCylinder`, `CreateConvexHull`, `CreateCompound`, `CreateMesh`, `GetShape`, `DestroyShape`), bodies (`AddBody`, `RemoveBody`, `IsAlive`, `TryGetBody`, `GetState`), commands, joints (`CreateJoint`, `SetMotor`, `DestroyJoint`), providers of medium, the end of the steps (`SubscribeStepEnd`), and the counts (`Worlds`, `Statistics`, `BackendName`, `Substeps`, `Gravity`). |
| `BodyDescription`, `BodyKind`, `BodyHandle`, `PhysicsMaterial`, `CollisionFilter`, `BodyInertia`, `BodyState` | What a body is made of, its handle, and its state. |
| `PhysicsBody`, `BodyShape`, `BodyMaterial`, `BodyMass`, `BodyVelocity`, `BodyFlags` | The components of a body, written by nPhysics only. |
| `PhysicsShape`, `ShapeKind`, `ShapeDescription`, `CompoundChild` | Shapes, shared by the bodies that use them. |
| `JointDescription`, `JointKind`, `JointHandle`, `JointMotor` | Hinges, sliders, and fixed joints, with an optional motor. |
| `IPhysicsStepObserver`, `PhysicsStepEnd`, `BodyPose`, `PhysicsStepSubscription` | The end of each step, with the bodies whose transform it wrote. |
| `IMediumProvider`, `MediumSample`, `MediumProviderHandle` | What fills a point, as a fluid module describes it. |
| `PhysicsFrame` | The conversions between positions in the world and positions in a physics world. |
| `IPhysicsBackends`, `IPhysicsBackend`, `IPhysicsShapeSource`, `PhysicsBackendWorld`, `PhysicsWorldOptions`, `PhysicsStepSettings`, `BackendBody`, `BackendBodyState`, `BackendJoint`, `VoxelChunkCollider`, `BackendRayHit` | The interface of a backend, for the extensions that provide one. |
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

- The entity has a world transform, no parent, and is not a body already.
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

A joint joins two bodies of the same physics world. Anchors and axes are in
the frame of each body, from its center. A fixed joint keeps the place and
the rotation the bodies had relative to each other when it was made, and has
no motor. Removing a body destroys its joints.

### The step

Each tick, in the stage `Physics`, `nobara.physics.step`:

1. removes the bodies whose entity, entity world, or grid is gone;
2. applies the queued commands, in order;
3. stops the kinematic bodies that reached their target at the last step;
4. steps each physics world once, with `physics.substeps` substeps;
5. writes the world transform and the velocities of each body that moved,
   and the flag `Asleep` when it changes;
6. publishes the end of the step to the observers.

The duration of a step is the duration of the tick. While the game is
paused, the phase `Simulation` does not run, and nothing steps; under a time
scale, more or fewer ticks run per second, each with the same step.

### Physics worlds and positions

A physics world is a simulation of the backend with an origin, a whole
chunk. Positions in it are offsets from the corner of that chunk, in blocks:
`PhysicsFrame` converts them from and to `WorldPosition`, and no absolute
coordinate in single precision reaches a backend. A body a billion blocks
from the origin keeps the precision it has at the origin.

nPhysics creates the world of a dimension when its first body is added, with
the chunk of that body as its origin, and disposes it when its last body is
removed. Several worlds can exist in one context.

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
written, with the bodies whose transform the step wrote. `OnStepEnd` is hot
path code. An exception is charged to the module of the observer.

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
- `AddVoxelChunk`, `RemoveVoxelChunk`, and `RayCast` are part of the
  interface; nPhysics does not call them in this version.

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
- **Voxels.** `AddVoxelChunk` adds a static whose shape reads the cells of
  the chunk where they are, while the world steps: a cell that holds a block
  collides as a whole cube, and a block placed or broken is seen at the next
  step, with no structure to rebuild. The chunk does not change during a
  step.
- **Threads.** A world steps on the thread that calls it. When
  `MaySpread` is set and the world has at least 1,000 awake bodies, the step
  spreads over the job system: the calling thread is a worker, and the other
  workers run as one parallel job, which the calling thread waits for. The
  extension creates no thread, and a step in a steady state allocates no
  managed memory.

## Events

None. The end of each step is published to the observers of
`SubscribeStepEnd`, synchronously, on the loop thread.

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
  transforms included (checked by a test with a test backend).
- Bodies, joints, and physics worlds belong to the world session: they are
  forgotten when it ends. Shapes belong to the context.
- nPhysics keeps no static state.

## Known limitations

- No backend ships in this module: a distribution needs a backend extension
  of `nobara.physics`.
- One physics world per dimension, whose origin is the chunk of its first
  body: bodies far from that origin lose precision, without a warning.
- Bodies do not collide with voxels yet, and there are no queries nor contact
  events: only the backend collides bodies with each other.
- No character controller.
- The providers of medium are asked only through `TrySampleMedium`: nPhysics
  applies no buoyancy nor drag.
- Bodies are added and removed outside entity systems only.
- A transform written to a body entity by another module is not detected; the
  next step overwrites it.
- The gravity is fixed: 9.81 blocks per square second downward.
- nPhysics.Bepu bounces bodies along the normal of the contact, on their
  linear velocities only: a spinning ball bounces as one that does not spin.
- In nPhysics.Bepu, every cell of a chunk that holds a block collides as a
  whole cube, whatever the shape of the block; a box that slides on the
  voxels can catch on the joints between cells.
- In nPhysics.Bepu, a body that reaches the back face of a mesh is not held,
  and can be thrown out of it.
- nPhysics.Bepu depends on a beta of BEPUphysics, whose interface changes
  between betas.
