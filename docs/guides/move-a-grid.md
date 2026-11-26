# Move a grid

This guide moves the airships and the lifts of a fictional sky game with
Motion: an airship is a grid that floats and that its engine pushes, a lift
is a grid that rises along a path. The code excerpts come from
`src/Motion/Nobara.Motion.Tests/Samples/AirshipMotion.cs`, which the tests of
Motion compile and run.

Prerequisites:

- a module with a dependency on `nobara.motion`;
- a distribution with nPhysics and a backend extension of nPhysics.

How Motion, nPhysics, and nVoxel share the work is explained in [Grids that
move](../explanation/moving-grids.md).

## 1. Give the blocks a weight

Motion reads the mass of each block from the free property `Weight` of its
type, in kilograms. A block without it weighs 1,000 kilograms:

```xml
<Block>
  <Name>Plank</Name>
  <Content><Shape>shape:nobara.cube</Shape></Content>
  <Properties><Weight>400</Weight></Properties>
</Block>
```

## 2. Resolve Motion and add a system

The system that moves the grids runs in the stage `Physics`, before the tick
system of Motion, which hands the commands to nPhysics. Points given in the
frame of a grid read its transform, so the system declares the data of
`WorldTransform`:

```csharp
_motion = context.Resolve<IMotionSystem>();
_voxels = context.Resolve<IVoxelWorlds>();
IEntityWorlds entities = context.Resolve<IEntityWorlds>();
context.Resolve<ITickSchedule>().AddSystem(context, "example.game.airships", TickStages.Physics, this, new TickSystemOptions
{
    Before = [MotionModule.TickSystem],
    Reads = [entities.GetComponent(entities.WorldTransform).Data],   // the transforms of the grids, for the points in their frame
});
```

## 3. Make a grid a body

Build the grid, then give it to Motion. It becomes a dynamic body where it
is, whose mass and center of mass come from its blocks. A gravity scale of 0
makes it float:

```csharp
_motion.Add(airship);                    // dynamic: its mass comes from its planks
_motion.SetGravityScale(airship, 0);     // its balloon holds it up
```

A lift is kinematic: nothing pushes it, and it pushes what it meets.

```csharp
_motion.Add(_lift, GridMotionMode.Kinematic);
```

## 4. Push it

Each tick, the engine pushes the airship along its own axis. Forces are along
the axes of the world, points in the frame of the grid: an engine on one side
of the stern turns the airship as it pushes it.

```csharp
Vector3 forward = Vector3.Transform(Vector3.UnitX, airship.Transform.Rotation);
_motion.ApplyForceAt(airship, forward * Thrust, new Vector3(0, 0.5f, 0));   // the engine, at the stern on one side, in the frame of the grid: it also turns the airship
```

## 5. Move a lift along a path

A kinematic grid follows a target: the transform it reaches at the end of the
next step. Give one each tick, and the lift moves along the path, carrying the
characters that stand on it:

```csharp
_liftHeight += LiftSpeed * (float)time.Delta.TotalSeconds;
_motion.SetTarget(_lift, _liftStart.Offset(new Vector3(0, _liftHeight, 0)), Quaternion.Identity);
```

## Next steps

- `GetState` gives the velocities, the mass, and the center of mass of a grid,
  for a speed indicator or the sound of an engine.
- `SetMode` turns a lift whose cable breaks into a dynamic grid that falls.
- `Split` cuts blocks out of a moving grid into a new grid that keeps its
  velocity.
