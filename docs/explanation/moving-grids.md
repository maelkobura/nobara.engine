# Grids that move

A grid is a set of voxels with a transform of its own: the terrain, a ship, a
lift, a block that fell off a cliff. nVoxel stores its blocks, nPhysics
writes its transform, and Motion decides that it moves. This page explains
how the three share the work. The modules are described in [the Motion module
sheet](../../src/Motion/README.md) and [the nPhysics module
sheet](../../src/nPhysics/README.md); terms are defined in [the
glossary](glossary.md).

## Who does what

| Module | Holds | Does |
| --- | --- | --- |
| nVoxel | the blocks of each grid, its entity, its transform component | places and breaks blocks, splits and merges grids, notifies every change |
| nPhysics | the bodies and their worlds | simulates, and alone writes the transform of a grid after each step |
| Motion | the mass of each grid body | makes a grid a body, follows its blocks, and gives the commands that move it |

Without Motion, grids stay still: nPhysics simulates only bodies, and no
module makes a grid one. Bodies and characters already collide with every
grid through the voxel layer of nPhysics, which reads the blocks where nVoxel
keeps them.

## A body made of blocks

When a module gives a grid to Motion, Motion counts its blocks: each weighs
the `Weight` of its type, and lies at the middle of its cell. From those
masses it computes the mass of the grid, its [center of
mass](glossary.md#center-of-mass), and its inertia, then makes the grid a
body of nPhysics.

- **Two points.** The transform of a grid is the origin of its blocks, which
  every module uses to find a block. A body turns about its center of mass.
  Motion keeps the second in the frame of the grid and gives it to nPhysics as
  the center of the body; nPhysics turns the pose of the body into the
  transform of the grid after each step.
- **Blocks that change.** Motion keeps sums of the masses and of their
  moments, and adjusts them from the notifications of nVoxel: placing or
  breaking a block costs that block alone, whatever the size of the grid.
  The sums are in double precision, so that ten thousand changes give what
  counting the blocks again would.
- **A center that moves.** Adding blocks on one side moves the center of
  mass. Motion gives the body a shape centered on the new point; nPhysics
  moves the body so that the grid stays where it is, and Motion gives the
  body the velocity that its new center had. Nothing jumps on the screen.

## Two shapes

A grid body collides through one of two shapes:

- **The voxel shape** of nPhysics: the collision boxes of its blocks, read in
  place while the world steps, with nothing to build when a block changes. It
  suits every grid, however large.
- **A compound of boxes**, for a [small grid](glossary.md#small-grid): the
  collision boxes of its few blocks, built again at each change. An explosion
  can throw hundreds of loose blocks; as small compounds, the backend handles
  them as ordinary bodies, and they sleep as soon as they rest.

A grid switches between the two with a margin: it becomes small at the
threshold, and stops being small at twice it, so that a grid that gains and
loses one block does not rebuild its shape each time.

## Dynamic and kinematic

A dynamic grid falls, is pushed, and carries what rests on it by friction. A
kinematic grid follows the velocities or the targets that its module gives,
pushes dynamic bodies, and nothing pushes it back: a lift, a platform on a
path. A grid changes mode at once, for instance when the mechanism that held
a platform breaks.

A character that stands on a grid becomes a child of the grid entity, and the
character controller of nPhysics moves it in the frame of the grid: it turns
and moves with the grid, kinematic or dynamic, with no drift.

## What is not there yet

A grid body collides with bodies and characters, but not with the terrain nor
with another grid: two sets of voxels never meet in the backend, and the
collision of a grid with voxels needs its own means, with a budget. Until
then, a grid that falls goes through the ground.
