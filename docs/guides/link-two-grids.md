# Link two grids

This guide joins the grids of the harbor of a fictional sky game with
Motion: the sails of a windmill turn on the terrain, the boom of a crane
turns on a moving ferry, and a gate slides open and shut. It ends with a rule
for the characters that the ferry catches against a wall. The code excerpts
come from `src/Motion/Nobara.Motion.Tests/Samples/HarborJoints.cs`, which the
tests of Motion compile and run.

Prerequisites:

- a module with a dependency on `nobara.motion`;
- a distribution with nPhysics and a backend extension of nPhysics;
- the guide [Move a grid](move-a-grid.md), which makes grids bodies.

## 1. Choose the joint

| Joint | The grid | Example |
| --- | --- | --- |
| `JointKind.Hinge` | turns about an axis of its carrier | sails, a door, a wheel |
| `JointKind.Slider` | slides along an axis of its carrier, without turning | a gate, a piston, a drawer |
| `JointKind.Fixed` | stays where it is on its carrier | a cargo welded to a ship |

The carrier is another grid that Motion moves, or the terrain. A hinge or a
slider can carry a motor: a target speed, and the most torque or force it
uses to keep it. Joints have no limits: a slider goes on until something
stops its grid.

## 2. Turn a grid on the terrain

Make the grid a body, then link it to the terrain. The anchor and the axis
are in the frame of the grid, the frame of its blocks; the joint holds them
where they are when it is made:

```csharp
_motion.Add(sails);
_motion.Link(sails, Terrain(), new GridJointDescription(
    JointKind.Hinge,
    Anchor: new Vector3(0.5f, 0.5f, 0.5f),   // the middle of the hub block, in the frame of the sails
    Axis: Vector3.UnitZ,
    Motor: new JointMotor(TargetVelocity: SailSpeed, MaxForce: 5e6f)));
```

The motor turns the grid counterclockwise about the axis, seen from its tip,
at the target speed in radians per second. Grids are heavy, a thousand
kilograms per block by default: give the motor enough torque to reach its
speed.

## 3. Turn a grid on another grid

A carrier that moves takes the joint along. The ferry is a kinematic grid,
moved by its module; the boom of its crane is a dynamic grid that turns on
it:

```csharp
_motion.Add(_ferry, GridMotionMode.Kinematic);
Grid boom = Build(position.Offset(new Vector3(1, 1.1f, 1)), plank, Cells(0, 0, 0, 3, 0, 0));
_motion.Add(boom);
_motion.Link(boom, _ferry, new GridJointDescription(
    JointKind.Hinge,
    Anchor: new Vector3(0.5f, 0, 0.5f),   // the foot of the boom, just above the deck
    Axis: Vector3.UnitY,
    Motor: new JointMotor(BoomSpeed, 1e6f)));
```

The boom is a little above the deck: two grids that touch rub on each other,
and the friction works against the motor.

## 4. Drive a slider

A slider without a motor slides freely along its axis, and holds its grid
against the rest: the gate does not fall. `SetMotor` changes the motor at
the next step:

```csharp
_gate = _motion.Link(gate, Terrain(), new GridJointDescription(JointKind.Slider, new Vector3(0.5f, 1.5f, 2), Vector3.UnitZ));
```

```csharp
public void OpenGate() => _motion.SetMotor(_gate, new JointMotor(GateSpeed, 1e6f));
```

```csharp
public void CloseGate() => _motion.SetMotor(_gate, new JointMotor(-GateSpeed, 1e6f));
```

## 5. Decide what happens to a crushed character

A kinematic grid pushes the characters in its way. When one has nowhere to
go, it is crushed, and by default the grid stops until the way is free. A
crush handler changes that for the grids it knows: this one puts the
characters that the ferry catches on its deck, and leaves the other crushes
to the next handler:

```csharp
_crushes = _motion.AddCrushHandler(context, this);
```

```csharp
public bool TryResolve(in CharacterCrush crush, out CrushResponse response)
{
    if (crush.Grid != _ferry)
    {
        response = default;
        return false;   // not ours: the next handler, or the default, decides
    }

    response = CrushResponse.MoveTo(_ferry.Transform.TransformPoint(Deck));
    return true;
}
```

Handlers are asked from the highest priority down. Dispose the handle to
remove the handler.

## Next steps

- `Unlink` removes a joint; a joint goes with its grid when the grid is
  destroyed or stops being a body.
- `GetJoint` gives the grids, the description, and the motor of a joint.
- The "Motion" window of nDebug lists the joints and the crushes of the last
  step.
