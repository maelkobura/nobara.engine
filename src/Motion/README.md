# Motion

This page is the module sheet of Motion, the module that makes grids move.
It describes what it provides to other modules and the rules it enforces.
Terms are defined in [the glossary](../../docs/explanation/glossary.md).

## Purpose

Motion makes [grids](../../docs/explanation/glossary.md#grid) physical
bodies: a [grid body](../../docs/explanation/glossary.md#grid-body) is a body
of nPhysics whose mass, [center of mass](../../docs/explanation/glossary.md#center-of-mass),
and inertia come from its blocks and follow them as they are placed and
broken. A grid body is dynamic, moved by gravity, forces, and collisions, or
kinematic, moved along the velocities or the targets a module gives it, and
its mode changes at any time. Motion delegates the whole simulation to
nPhysics, which alone writes the transform of a grid; it is the API through
which modules move grids: a ship, a lift, a piston. A grid that no module
gives to Motion stays still. How a grid body fits with nPhysics is described
in [Grids that move](../../docs/explanation/moving-grids.md), and the guide
[Move a grid](../../docs/guides/move-a-grid.md) moves an airship and a lift.

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
| `nobara.blocks` | `^1.0` | Required |
| `nobara.context` | `^1.0` | Required |
| `nobara.entity` | `^1.0` | Required |
| `nobara.game` | `^1.0` | Required |
| `nobara.physics` | `^1.0` | Required |
| `nobara.voxel` | `^1.0` | Required |
| `nobara.debug` | `^1.0` | Optional |
<!-- nobara:generated:end -->

Motion moves the grids of the world of its context, on both sides, until a
network module makes the server the authority over the grids. Its code makes
no assumption about its side and keeps no global state. The optional
dependency on nDebug is used by one isolation class, `MotionDebugWindow`: the
"Motion" tool window, which lists the grids that Motion moves with their
mode, mass, center of mass, blocks, whether they are small grids, and whether
they sleep or are frozen; and, for their collision with other voxels, the
mode of their surface cells, the pairs, tests, and deferred cells of the last
step, the load of the tick, and the switches of mode.

## Entry points

| Member | Use |
| --- | --- |
| `IMotionSystem` | Grid bodies (`Add`, `Remove`, `Contains`, `GetBody`, `GetState`, `Grids`), their mode (`SetMode`, `Freeze`, `Release`), the commands (`ApplyForce`, `ApplyForceAt`, `ApplyImpulse`, `ApplyImpulseAt`, `ApplyTorque`, `ApplyAngularImpulse`, `SetGravityScale`, `SetVelocity`, `SetTarget`), and `Split`. |
| `GridMotionMode`, `GridMotionState` | How a grid moves, and what Motion knows of it after the last step. |
| `MotionSettings` | The names of the configuration keys. |
| `MotionModule` | The identifier of the module and the name of its tick system. |

Resolve the service with `context.Resolve<IMotionSystem>()` from the
initialization of a module that depends on `nobara.motion`, and call it on
the loop thread of the context.

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Grid bodies

```csharp
IMotionSystem motion = context.Resolve<IMotionSystem>();
BodyHandle body = motion.Add(airship);                       // dynamic by default
motion.SetMode(airship, GridMotionMode.Kinematic);           // at the next step
GridMotionState state = motion.GetState(airship);            // mass, center of mass, velocities
```

- `Add` makes a grid a body of nPhysics where it is, at rest. The grid is
  not the terrain, not scaled, and not a body yet. `Remove` removes the body:
  the grid stays where it is, still.
- The transform of a grid is the origin of its blocks; its body is centered
  on its center of mass, which Motion keeps in the frame of the grid. When a
  change of blocks moves the center of mass, the body gets a shape centered
  on the new one: the transform of the grid does not jump, and a dynamic grid
  goes on with the velocity that its new center had.
- A grid of `SmallGridBlocks` blocks or fewer is a
  [small grid](../../docs/explanation/glossary.md#small-grid): its shape is a
  compound of the collision boxes of its blocks, built again at each change.
  It stops being one past twice that number, and becomes one again at that
  number or fewer, so that a grid on the threshold does not switch at each
  block. A larger grid has the
  [voxel shape](../../docs/explanation/glossary.md#voxel-shape) of nPhysics.
- Bodies collide with a grid body, and characters stand on it, through the
  voxels of the grid, as with any grid: nothing is particular to Motion. A
  grid body collides with the terrain and with other grids through its
  surface cells (see [Collision with voxels](#collision-with-voxels)).
- A grid body sleeps at rest, and wakes on a contact, on a command, or when
  its blocks change.
- A grid destroyed, or absorbed by a merge, stops being a body; Motion
  forgets it at the next tick.
- `GetState` gives the transform, the velocities of the center of mass, the
  mass, the center of mass, the number of blocks, and whether the grid sleeps,
  is frozen, or is small.

### Mass

The mass of a grid is the sum of the masses of its blocks, read once when
Motion starts:

| Block | Mass | Center |
| --- | --- | --- |
| A block | `Weight` of its type, or `Motion.Weight` when it has both; 1,000 kilograms without either | The middle of its cell |
| A multi-block | The weight of its type, counted once, at its origin | The middle of its cells |
| A block whose shape depends on its instance | The mass its shape gives for the block data of the cell, or the weight of its type | The middle of the collision boxes of its shape, weighted by their volume |
| Air | None | |

```xml
<Block>
  <Name>Hull</Name>
  <Content><Shape>shape:nobara.cube</Shape></Content>
  <Properties><Weight>800</Weight></Properties>
</Block>
```

- `Weight` is a number of kilograms, 0 or more; a value that is not one
  gets the default, with a warning. A block without collision still weighs.
- Each block is a cube of its mass at its center: the inertia of the grid is
  the one of these cubes about the center of mass.
- Motion keeps sums of the masses and their moments, in double precision,
  and adjusts them from the notifications of nVoxel: placing or breaking a
  block, or changing the block data of a cell whose mass depends on it,
  costs the block alone. A chunk of the grid that enters or leaves memory
  counts the grid again. A grid whose blocks weigh nothing has a mass of
  1 kilogram.

### Collision with voxels

A dynamic grid body collides with the terrain, with the grids that are not
bodies, and with the other grid bodies; a kinematic one pushes the dynamic
grids it meets. How it works is explained in
[Grids that move](../../docs/explanation/moving-grids.md#grids-against-voxels).

- **Surface cells.** Motion keeps the
  [surface cells](../../docs/explanation/glossary.md#surface-cell) of each
  grid: the blocks that collide and have a neighbor that does not fill its
  cell (air, a block without collision, a slab, or a chunk not in memory). A
  placed or broken block updates its cell and its six neighbors; a chunk that
  enters or leaves memory, or is generated, has the grid counted again.
  Motion gives them to nPhysics as a `GridVoxelCollision`.
- **Blocks or corners.** A grid tests the collision boxes of its surface
  cells (`VoxelContactMode.Blocks`), or only their corners
  (`VoxelContactMode.Corners`) once its mass reaches `CornerMass`, or while
  the tick is over its `LoadShare`. It tests blocks again under 80 % of
  `CornerMass`, and when the load has stayed under 70 % of `LoadShare` for 30
  ticks in a row; the load goes over after 3 ticks in a row. The load is the
  time from the start of the tick system of Motion to the end of the step of
  nPhysics, as a share of the tick.
- **Budget.** A pair of a grid and a chunk of the terrain, or of two grids,
  makes about `PairBudget` tests in a step; the cells left wait for the next
  step.
- **Impacts.** A grid body reports its contacts with an impact speed of
  `ImpactSpeed`: the impacts of the grid are in `PhysicsStepEnd.Impacts` of
  nPhysics, with the block hit and the speed.
- A [small grid](../../docs/explanation/glossary.md#small-grid) collides with
  the voxels as a compound, through the voxel layer of nPhysics: neither
  mode, nor budget.

### Commands

| Command | Grids | Effect |
| --- | --- | --- |
| `ApplyForce`, `ApplyForceAt`, `ApplyTorque` | dynamic | A force or a torque during the next step, at the center of mass or at a point of the grid. |
| `ApplyImpulse`, `ApplyImpulseAt`, `ApplyAngularImpulse` | dynamic | An impulse before the next step. |
| `SetGravityScale` | dynamic | The share of the gravity that pulls the grid: 1 by default, 0 to float. |
| `SetVelocity` | all | The velocities of the center of mass. |
| `SetTarget` | kinematic | The transform the grid reaches at the end of the next step. |
| `SetMode` | all | Dynamic or kinematic, at the next step. |
| `Freeze`, `Release` | all | Holds the grid where it is, then lets it move again from rest. |

- Forces and impulses are along the axes of the world; points are in the
  frame of the grid. A point reads the transform of the grid: a tick system
  that gives one declares the data of `WorldTransform` among its reads.
- Every command goes through nPhysics, which applies it before the next step.
  Forces and impulses given to a kinematic or a frozen grid are ignored, and
  so are velocities and targets given to a frozen grid.
- The gravity scale is a force that Motion applies before each step, in its
  tick system, to the grids that do not sleep.

### Split

`Split` splits blocks of a grid into a new grid through nVoxel, and makes the
new grid a body of the same mode. The new grid takes its blocks at the next
tick; it then gets the velocity that the source had at its new center of
mass. A grid split through nVoxel directly is not a body: its module gives it
to Motion.

### The tick

The tick system `nobara.motion.grids` runs in the stage `Physics`, before
`nobara.physics.step`. For each grid whose blocks changed, it gives nPhysics
the new mass, inertia, and shape; it applies the forces of the gravity
scales; and it destroys the shapes that the last step stopped using. A
system that moves grids runs before it.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `motion.cornerMass` | distribution | float | `500000` | 0 to 1000000000000 | both |
| `motion.impactSpeed` | distribution | float | `2` | 0 to 1000 | both |
| `motion.loadShare` | distribution | float | `0.5` | 0 to 1 | both |
| `motion.pairBudget` | distribution | int | `1024` | 0 to 1000000 | both |
| `motion.smallGridBlocks` | distribution | int | `8` | 0 to 4096 | both |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `motion.smallGridBlocks` | The number of blocks of a grid at or under which it is a small grid; 0 for none. Read at start. |
| `motion.cornerMass` | The mass, in kilograms, from which a grid tests the corners of its surface cells instead of their boxes; 0 for never. Read at start. |
| `motion.loadShare` | The share of the tick that Motion and the step of nPhysics may take before every grid tests corners; 0 for never. Read at start. |
| `motion.pairBudget` | The most tests that a pair of a grid and other voxels makes in a step; 0 for no limit. Read at start. |
| `motion.impactSpeed` | The slowest approach, in blocks per second, that a grid reports as an impact; 0 for none. Read at start. |

## Resources

Motion reads the free property `Weight` of the blocks, and its qualified form
`Motion.Weight`, as a number.

## Constraints

- Call the service on the loop thread of the context, from its start on,
  once a world session runs. `Add`, `Remove`, and `Split` change the structure
  of the entity world: call them outside entity systems.
- Motion never writes the transform of a grid: nPhysics does, after each
  step, and updates the spatial index of the grids of nVoxel.
- Once the grids rest, a tick of Motion allocates no managed memory (checked
  by tests with 500 small grids, and with 500 grids of mixed sizes on the
  terrain and on each other). A change of blocks allocates when it builds a
  new shape, and the first surface cell of a chunk allocates its words.
- Grid bodies belong to the world session: Motion forgets them when it ends.
- Motion keeps no static state.

## Known limitations

- A grid body goes through meshes.
- A kinematic grid does not collide with the terrain nor with a grid that is
  not a body.
- In `Corners`, an edge of a block can sink into the voxels between two
  corners, and a block that does not fill its cell collides by the box of its
  cell.
- A grid whose pair runs out of budget while it rests changes its contacts
  at each step, and may not sleep.
- The load counts Motion and the step of nPhysics only, not the rest of the
  tick.
- A dynamic grid whose gravity scale is not 1 never sleeps: the backend sees
  the force that makes up for the gravity as a speed at the start of each
  step.
- Each block is a cube for the inertia, a multi-block or a block whose shape
  depends on its instance included.
- The center of mass of a block whose shape depends on its instance is the
  middle of its collision boxes: a shape cannot give one of its own.
- A small grid scans the chunks of its grid in memory each time it changes.
- Motion moves the grids on both sides, each context its own world: there is
  no replication, no prediction, and no API for the client alone yet.
