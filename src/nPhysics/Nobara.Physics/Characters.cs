// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The component <c>nobara.physics.character</c>: makes an entity a
/// character that the character controller of nPhysics moves, as an upright
/// box, after its <see cref="MovementIntent"/>. The module of the character
/// writes it; nPhysics reads it at each step.
/// </summary>
/// <remarks>
/// The position of the entity is the middle of the bottom of the box, at its
/// feet. The controller reads no input and knows no walking speed: the speed
/// and the jumps come from <see cref="MovementIntent"/>.
/// </remarks>
public struct CharacterBody
{
    /// <summary>The size of the box along X, Y (its height), and Z, in blocks, each above 0.</summary>
    public Vector3 Size;

    /// <summary>The highest step the character walks up without jumping, in blocks, from 0.</summary>
    public float StepHeight;

    /// <summary>The share of the gravity of the world that pulls the character, from 0.</summary>
    public float GravityScale;

    /// <summary>How fast the character changes its horizontal velocity toward the wanted one in the air, in blocks per square second, from 0.</summary>
    public float AirAcceleration;

    /// <summary>The mass of the character, in kilograms, above 0: it sets how hard it pushes dynamic bodies.</summary>
    public float Mass;

    /// <summary>The collision layers of the character, against voxels (<see cref="CollisionFilter.VoxelLayer"/>), static bodies, and the dynamic bodies it pushes.</summary>
    public CollisionFilter Filter;

    /// <summary>Gets the default character: 0.6 by 1.8 by 0.6 blocks, steps of 0.6 block, the whole gravity, an air acceleration of 2 blocks per square second, 80 kilograms, the default layers.</summary>
    public static CharacterBody Default
    {
        [HotPath]
        get => new()
        {
            Size = new Vector3(0.6f, 1.8f, 0.6f),
            StepHeight = 0.6f,
            GravityScale = 1f,
            AirAcceleration = 2f,
            Mass = 80f,
            Filter = CollisionFilter.Default,
        };
    }
}

/// <summary>
/// The component <c>nobara.physics.movement_intent</c>: how a character wants
/// to move at the next step. Any module writes it, at the stage
/// <c>Intents</c>: the movement rules of a game for its players, the
/// artificial intelligence of a mob.
/// </summary>
public struct MovementIntent
{
    /// <summary>
    /// The wanted horizontal velocity, in blocks per second, along the axes of
    /// the world, relative to the ground the character stands on; its vertical
    /// part is ignored. On the ground, the character takes it at once; in the
    /// air, it turns toward it at <see cref="CharacterBody.AirAcceleration"/>,
    /// and keeps its velocity when this is zero.
    /// </summary>
    public Vector3 Velocity;

    /// <summary>The upward speed of a jump, in blocks per second: while it is above 0, the character jumps each time it stands on the ground.</summary>
    public float JumpSpeed;
}

/// <summary>The fixed rules of the character controller of nPhysics.</summary>
public static class CharacterController
{
    /// <summary>The slowest downward speed, in blocks per second, at which a character that touches the ground is reported as landing.</summary>
    public const float LandingSpeed = 1f;

    /// <summary>The number of steps a character spends in the air before it leaves its support grid.</summary>
    public const int SupportGraceSteps = 3;

    /// <summary>The steepest a grid can be turned, in degrees from the vertical of the world, to be a support grid.</summary>
    public const float MaxSupportTilt = 45f;
}

/// <summary>What a character stands on, or touched when it landed.</summary>
public enum CharacterGroundKind : byte
{
    /// <summary>Nothing: the character is in the air.</summary>
    None,

    /// <summary>A block of a grid.</summary>
    Block,

    /// <summary>A static body.</summary>
    Body,
}

/// <summary>What a character stands on, or touched when it landed.</summary>
/// <param name="Kind">A block, a static body, or nothing.</param>
/// <param name="Grid">The grid of the block, by its handle.</param>
/// <param name="Block">The block, in the coordinates of its grid.</param>
/// <param name="State">The state of the block, as nBlocks numbers it; 0 for a body or nothing.</param>
/// <param name="Body">The static body.</param>
[HotPath]
public readonly record struct CharacterGround(CharacterGroundKind Kind, GridHandle Grid, BlockCoordinate Block, uint State, BodyHandle Body);

/// <summary>The component <c>nobara.physics.character_state</c>: the state of a character after the last step, written by nPhysics only.</summary>
/// <remarks>Its velocity is in <see cref="BodyVelocity"/>, in the world, the motion of its support grid included.</remarks>
public struct CharacterState
{
    /// <summary>Whether the character stands on the ground.</summary>
    public bool IsGrounded;

    /// <summary>Whether the character is frozen: it reaches a chunk of the terrain that is not loaded or not active, and does not move.</summary>
    public bool IsFrozen;

    /// <summary>The entity of its support grid, its parent in the hierarchy of nEntity; <see cref="EntityId.Null"/> when it is on no grid.</summary>
    public EntityId Support;

    /// <summary>What it stands on.</summary>
    public CharacterGround Ground;

    /// <summary>The number of steps since it last stood on the ground; 0 on the ground.</summary>
    public int AirSteps;

    internal int Slot;
}

/// <summary>A character that walked on the ground during a step.</summary>
/// <param name="Dimension">The dimension of the character.</param>
/// <param name="Entity">Its entity.</param>
/// <param name="Position">Where its feet are after the step.</param>
/// <param name="Distance">The distance it walked on the ground during the step, relative to the ground, in blocks, above 0.</param>
/// <param name="Ground">What it walks on.</param>
[HotPath]
public readonly record struct CharacterStep(DimensionId Dimension, EntityId Entity, WorldPosition Position, float Distance, CharacterGround Ground);

/// <summary>A character that landed on the ground during a step.</summary>
/// <param name="Dimension">The dimension of the character.</param>
/// <param name="Entity">Its entity.</param>
/// <param name="Position">Where its feet are after the step.</param>
/// <param name="Speed">Its downward speed relative to the ground when it touched it, in blocks per second, at least <see cref="CharacterController.LandingSpeed"/>.</param>
/// <param name="AirSteps">The number of steps it spent in the air.</param>
/// <param name="Ground">What it landed on.</param>
[HotPath]
public readonly record struct CharacterLanding(DimensionId Dimension, EntityId Entity, WorldPosition Position, float Speed, int AirSteps, CharacterGround Ground);
