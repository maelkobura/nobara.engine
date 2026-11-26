# Make a character walk

This guide makes the farmers of a fictional farming game walk, run, and jump
with the [character controller](../explanation/glossary.md#character-controller)
of nPhysics, and shows where the player controller of the game plugs in. The
code excerpts come from
`src/nPhysics/Nobara.Physics.Bepu.Tests/Samples/FarmerMovement.cs`, which the
tests of nPhysics.Bepu compile and run.

Prerequisites:

- a module with a dependency on `nobara.physics`;
- a distribution with a backend extension of nPhysics.

nPhysics has no player controller: it moves a character from what the game
wants, and knows neither players nor walking speeds. A game splits its player
controller into three parts:

| Part | Where | Does |
| --- | --- | --- |
| Input | client | samples the input of the player at each tick, into a component of the game |
| Movement rules | both sides | turn that input, or the decisions of a mob, into a [movement intent](../explanation/glossary.md#movement-intent) |
| Character controller | nPhysics, both sides | moves the character after its intent, against the voxels and the static bodies |

The same movement rules run on the client and on the server, so that the
client can predict the movement of its player later.

## 1. Declare the input and the rules

The input is a component of the game. Here, the wanted movement, where the
farmer looks, and the buttons held:

```csharp
public struct FarmerInput
{
    public Vector2 Move;
    public float Yaw;
    public bool Jump;
    public bool Run;
}
```

The movement rules are a tick system of the stage `Intents`, which runs
before the stage `Physics`. From the initialization of the module, register
the input, find the components of nPhysics, and add the system:

```csharp
_physics = context.Resolve<IPhysicsSystem>();
IEntityWorlds entities = context.Resolve<IEntityWorlds>();
_transform = entities.WorldTransform;
_input = entities.RegisterComponent<FarmerInput>(context, "example.game.farmer_input");
entities.TryGetComponent(out _intent);   // registered by nPhysics, a dependency
entities.TryGetComponent(out _state);
context.Resolve<ITickSchedule>().AddSystem(context, "example.game.farmer_movement", TickStages.Intents, this, new TickSystemOptions
{
    Reads = [entities.GetComponent(_input).Data],
    Writes = [entities.GetComponent(_intent).Data],
});
```

## 2. Make an entity a character

`AddCharacter` makes an entity a character, outside entity systems. The
position of the entity is the middle of the bottom of its box, at its feet:

```csharp
EntityId farmer = world.Create(world.GetArchetype(_transform, _input));
world.Set(farmer, _transform, WorldTransform.At(feet));
CharacterBody body = CharacterBody.Default;
body.StepHeight = 0.6f;
_physics.AddCharacter(world.Dimension, farmer, body);
```

- `CharacterBody` gives the size of the box, the highest step the character
  walks up, its share of the gravity, how fast it steers in the air, its
  mass, and its collision layers. The default is a box of 0.6 by 1.8 by 0.6
  blocks. Its module can change these values at any time.
- `AddCharacter` also adds `MovementIntent`, `CharacterState`, and
  `BodyVelocity` to the entity. From the next step on, nPhysics writes its
  transform.

## 3. Write the movement intent

At each tick, the rules turn the input into the velocity the farmer wants,
along the axes of the world, and into the speed of a jump:

```csharp
FarmerInput input = _world.Read(farmer, _input);
var forward = new Vector3(-MathF.Sin(input.Yaw), 0, -MathF.Cos(input.Yaw));
var right = new Vector3(MathF.Cos(input.Yaw), 0, -MathF.Sin(input.Yaw));
float speed = input.Run ? RunSpeed : WalkSpeed;
Vector3 velocity = ((right * input.Move.X) + (forward * input.Move.Y)) * speed;
_world.Set(farmer, _intent, new MovementIntent { Velocity = velocity, JumpSpeed = input.Jump ? JumpSpeed : 0 });
```

- On the ground, the character takes the wanted velocity at once, relative to
  the ground: on a moving grid, it walks on the deck.
- In the air, it turns toward the wanted velocity at
  `CharacterBody.AirAcceleration`, and keeps its velocity while the wanted one
  is zero.
- While `JumpSpeed` is above 0, the character jumps each time it stands on
  the ground. A game that wants one jump per press writes it once.
- A mob writes its intent the same way, from its artificial intelligence.

## 4. Read the state

`CharacterState` tells whether the character stands on the ground, what it
stands on (a block and its state, or a static body), and its
[support grid](../explanation/glossary.md#support-grid); `BodyVelocity` gives
its velocity in the world:

```csharp
public bool IsOnGround(EntityId farmer) => _world!.Read(farmer, _state).IsGrounded;
```

On a grid, the character is a child of the grid entity: it moves and turns
with the grid, and its local transform is where it stands on it. Turn a
character through its local transform while it has a parent, and through its
world transform otherwise; nPhysics writes its position only.

## 5. React to steps and landings

The end of each step lists the characters that walked on the ground, with
the distance and the block under their feet, and those that landed, with
their speed. Sounds of footsteps and fall damage start there:

```csharp
public void OnStepEnd(in PhysicsStepEnd step)
{
    foreach (CharacterLanding landing in step.CharacterLandings)
    {
        if (landing.Speed > SafeSpeed)
        {
            Damage += (landing.Speed - SafeSpeed) * 2;   // the game hurts landing.Entity
        }
    }
}
```

Subscribe the observer with `IPhysicsSystem.SubscribeStepEnd`. A landing
slower than `CharacterController.LandingSpeed`, 1 block per second, is not
listed.

## 6. Take the character away and back

A spectator, or a character that becomes a ragdoll, leaves the character
controller:

```csharp
public void Spectate(EntityId farmer) => _physics.RemoveCharacter(_world!.Dimension, farmer);

public void Resume(EntityId farmer) => _physics.AddCharacter(_world!.Dimension, farmer, CharacterBody.Default);
```

`RemoveCharacter` takes the character off its support grid where it is in
the world and keeps its last velocity in `BodyVelocity`; from then on, its
module writes its transform, or makes it a body. `AddCharacter` gives it back
to the character controller from where it is, with the velocity of its
`BodyVelocity`.
