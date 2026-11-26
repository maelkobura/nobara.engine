// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Physics.Bepu.Tests.Samples;

/// <summary>What the client of a fictional farming game samples for a farmer at each tick.</summary>
public struct FarmerInput
{
    /// <summary>The wanted movement, from -1 to 1: X to the right, Y forward.</summary>
    public Vector2 Move;

    /// <summary>Where the farmer looks, in radians around the vertical; 0 faces -Z.</summary>
    public float Yaw;

    /// <summary>Whether the jump button is held.</summary>
    public bool Jump;

    /// <summary>Whether the run button is held.</summary>
    public bool Run;
}

/// <summary>
/// The movement rules of the farming game: at the stage <c>Intents</c>, on
/// both sides, they turn the input of each farmer into its movement intent,
/// which the character controller of nPhysics follows.
/// </summary>
public sealed class FarmerMovement : ITickSystem
{
    public const float WalkSpeed = 4.3f;
    public const float RunSpeed = 5.6f;
    public const float JumpSpeed = 7f;

    private readonly IPhysicsSystem _physics;
    private readonly List<EntityId> _farmers = [];
    private readonly ComponentType<WorldTransform> _transform;
    private readonly ComponentType<FarmerInput> _input;
    private readonly ComponentType<MovementIntent> _intent;
    private readonly ComponentType<CharacterState> _state;
    private EntityWorld? _world;

    public FarmerMovement(ModuleContext context)
    {
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
    }

    /// <summary>Creates a farmer, whose feet are at a position, and gives it to the character controller.</summary>
    public EntityId Spawn(EntityWorld world, in WorldPosition feet)
    {
        _world = world;
        EntityId farmer = world.Create(world.GetArchetype(_transform, _input));
        world.Set(farmer, _transform, WorldTransform.At(feet));
        CharacterBody body = CharacterBody.Default;
        body.StepHeight = 0.6f;
        _physics.AddCharacter(world.Dimension, farmer, body);
        _farmers.Add(farmer);
        return farmer;
    }

    /// <summary>Turns the input of each farmer into its movement intent.</summary>
    public void Run(in TickTime time)
    {
        if (_world is null)
        {
            return;
        }

        foreach (EntityId farmer in _farmers)
        {
            FarmerInput input = _world.Read(farmer, _input);
            var forward = new Vector3(-MathF.Sin(input.Yaw), 0, -MathF.Cos(input.Yaw));
            var right = new Vector3(MathF.Cos(input.Yaw), 0, -MathF.Sin(input.Yaw));
            float speed = input.Run ? RunSpeed : WalkSpeed;
            Vector3 velocity = ((right * input.Move.X) + (forward * input.Move.Y)) * speed;
            _world.Set(farmer, _intent, new MovementIntent { Velocity = velocity, JumpSpeed = input.Jump ? JumpSpeed : 0 });
        }
    }

    /// <summary>Tells whether a farmer stands on the ground.</summary>
    public bool IsOnGround(EntityId farmer) => _world!.Read(farmer, _state).IsGrounded;

    /// <summary>Takes a farmer from the character controller: it flies where its module puts it.</summary>
    public void Spectate(EntityId farmer) => _physics.RemoveCharacter(_world!.Dimension, farmer);

    /// <summary>Gives a farmer back to the character controller, from where it is.</summary>
    public void Resume(EntityId farmer) => _physics.AddCharacter(_world!.Dimension, farmer, CharacterBody.Default);
}

/// <summary>Hurts the farmers that land too fast, from the landings of each step.</summary>
public sealed class FallDamage : IPhysicsStepObserver
{
    /// <summary>The fastest landing that does no harm, in blocks per second: a fall of about 5 blocks.</summary>
    public const float SafeSpeed = 10f;

    public float Damage { get; private set; }

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
}
