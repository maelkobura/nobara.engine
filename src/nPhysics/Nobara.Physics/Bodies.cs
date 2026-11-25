// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Physics;

/// <summary>How a body moves.</summary>
public enum BodyKind : byte
{
    /// <summary>Never moves on its own: terrain features, walls. Other bodies collide with it.</summary>
    Static,

    /// <summary>Moved by code, through velocities or targets: it pushes dynamic bodies and is never pushed.</summary>
    Kinematic,

    /// <summary>Simulated: gravity, forces, and collisions move it.</summary>
    Dynamic,
}

/// <summary>Flags of a body, as its component shows them.</summary>
[Flags]
public enum BodyFlags : byte
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>Continuous collision detection is on, for fast bodies such as projectiles.</summary>
    Continuous = 1,

    /// <summary>The body may fall asleep when it rests.</summary>
    CanSleep = 2,

    /// <summary>The body sleeps: the backend does not simulate it until something wakes it.</summary>
    Asleep = 4,

    /// <summary>The body is out of every region, or reaches a chunk of the terrain that is not loaded or not active: nPhysics does not simulate it until a region holds it again and those chunks are active.</summary>
    Frozen = 8,
}

/// <summary>Identifies a body. It names no body any more once the body is removed.</summary>
public readonly struct BodyHandle : IEquatable<BodyHandle>
{
    [HotPath]
    internal BodyHandle(int index, int generation) => (Index, Generation) = (index, generation);

    /// <summary>Gets the slot of the body.</summary>
    public int Index { get; }

    /// <summary>Gets the generation of the slot, from 1; 0 for the default handle, which names no body.</summary>
    public int Generation { get; }

    /// <summary>Gets whether this is the default handle, which names no body.</summary>
    public bool IsNone
    {
        [HotPath]
        get => Generation == 0;
    }

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name the same body.</returns>
    [HotPath]
    public static bool operator ==(BodyHandle left, BodyHandle right) => left.Equals(right);

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name different bodies.</returns>
    [HotPath]
    public static bool operator !=(BodyHandle left, BodyHandle right) => !left.Equals(right);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(BodyHandle other) => Index == other.Index && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BodyHandle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <inheritdoc/>
    public override string ToString() => $"body {Index}#{Generation}";
}

/// <summary>How a surface slides and bounces.</summary>
/// <param name="Friction">The friction coefficient, from 0 (ice).</param>
/// <param name="Restitution">The share of the speed kept in a bounce, from 0 (no bounce) to 1.</param>
[HotPath]
public readonly record struct PhysicsMaterial(float Friction, float Restitution)
{
    /// <summary>Gets the default material: friction 0.6, no bounce.</summary>
    public static PhysicsMaterial Default => new(0.6f, 0f);
}

/// <summary>
/// The collision layers of a body: the layers it belongs to, and the layers
/// it collides with. Queries use the same masks. The voxels of every grid
/// belong to <see cref="VoxelLayer"/> and collide with every layer: a body
/// whose mask leaves that layer out goes through the voxels.
/// </summary>
/// <param name="Layers">The layers the body belongs to, one bit each.</param>
/// <param name="Mask">The layers the body collides with.</param>
[HotPath]
public readonly record struct CollisionFilter(uint Layers, uint Mask)
{
    /// <summary>The layer of the voxels, the last one (bit 31): keep it for them, and out of the layers of bodies.</summary>
    public const uint VoxelLayer = 1u << 31;

    /// <summary>Gets the default filter: layer 0, colliding with every layer.</summary>
    public static CollisionFilter Default => new(1u, uint.MaxValue);

    /// <summary>Gets whether two bodies collide: each belongs to a layer the other collides with.</summary>
    /// <param name="other">The filter of the other body.</param>
    /// <returns><see langword="true"/> when they collide.</returns>
    [HotPath]
    public bool CollidesWith(in CollisionFilter other) => (Layers & other.Mask) != 0 && (other.Layers & Mask) != 0;
}

/// <summary>
/// The inertia tensor of a body, in its own frame, in kilograms times square
/// blocks: a symmetric matrix, given by its lower triangle.
/// </summary>
/// <param name="XX">The moment about X.</param>
/// <param name="YX">The product of Y and X.</param>
/// <param name="YY">The moment about Y.</param>
/// <param name="ZX">The product of Z and X.</param>
/// <param name="ZY">The product of Z and Y.</param>
/// <param name="ZZ">The moment about Z.</param>
[HotPath]
public readonly record struct BodyInertia(float XX, float YX, float YY, float ZX, float ZY, float ZZ)
{
    /// <summary>Gets a diagonal tensor.</summary>
    /// <param name="moments">The moments about X, Y, and Z.</param>
    /// <returns>The tensor.</returns>
    [HotPath]
    public static BodyInertia Diagonal(Vector3 moments) => new(moments.X, 0, moments.Y, 0, 0, moments.Z);
}

/// <summary>What <see cref="IPhysicsSystem.AddBody"/> makes of an entity.</summary>
/// <remarks>
/// The body starts where the entity is: its world transform, with
/// <see cref="Center"/> applied. The scale of the entity does not change the
/// shape.
/// </remarks>
public sealed class BodyDescription
{
    /// <summary>Gets or initializes how the body moves.</summary>
    public BodyKind Kind { get; init; } = BodyKind.Dynamic;

    /// <summary>Gets or initializes the shape. A mesh is for a static body only.</summary>
    public required PhysicsShape Shape { get; init; }

    /// <summary>Gets or initializes the material.</summary>
    public PhysicsMaterial Material { get; init; } = PhysicsMaterial.Default;

    /// <summary>Gets or initializes the collision layers.</summary>
    public CollisionFilter Filter { get; init; } = CollisionFilter.Default;

    /// <summary>Gets or initializes the mass, in kilograms, above 0. Only a dynamic body uses it.</summary>
    public float Mass { get; init; } = 1f;

    /// <summary>Gets or initializes the inertia tensor; <see langword="null"/> lets the backend compute it from the shape and the mass.</summary>
    public BodyInertia? Inertia { get; init; }

    /// <summary>
    /// Gets or initializes where the center of the body, the origin of its
    /// shape and its center of mass, sits in the frame of the entity, in
    /// blocks. A grid whose center of mass is not its origin uses it.
    /// </summary>
    public Vector3 Center { get; init; }

    /// <summary>Gets or initializes whether continuous collision detection is on.</summary>
    public bool Continuous { get; init; }

    /// <summary>Gets or initializes whether the body may fall asleep when it rests.</summary>
    public bool CanSleep { get; init; } = true;

    /// <summary>Gets or initializes the linear velocity at the start, in blocks per second.</summary>
    public Vector3 LinearVelocity { get; init; }

    /// <summary>Gets or initializes the angular velocity at the start, in radians per second, along the axes of the world.</summary>
    public Vector3 AngularVelocity { get; init; }
}

/// <summary>The component <c>nobara.physics.body</c>: the body of an entity, written by nPhysics only.</summary>
public struct PhysicsBody
{
    /// <summary>The handle of the body.</summary>
    public BodyHandle Handle;

    /// <summary>How the body moves.</summary>
    public BodyKind Kind;

    /// <summary>Its flags, the state of sleep included.</summary>
    public BodyFlags Flags;

    /// <summary>Its collision layers.</summary>
    public CollisionFilter Filter;

    /// <summary>Where its center sits in the frame of the entity.</summary>
    public Vector3 Center;
}

/// <summary>The component <c>nobara.physics.shape</c>: the shape of a body, written by nPhysics only.</summary>
public struct BodyShape
{
    /// <summary>The shared shape the body collides with.</summary>
    public PhysicsShape Shape;
}

/// <summary>The component <c>nobara.physics.material</c>: the material of a body, written by nPhysics only.</summary>
public struct BodyMaterial
{
    /// <summary>The friction and restitution of its surface.</summary>
    public PhysicsMaterial Material;
}

/// <summary>The component <c>nobara.physics.mass</c>: the mass of a body, written by nPhysics only.</summary>
public struct BodyMass
{
    /// <summary>The mass, in kilograms; 0 for a body that is not dynamic.</summary>
    public float Mass;

    /// <summary>The inertia tensor, in the frame of the body.</summary>
    public BodyInertia Inertia;
}

/// <summary>The component <c>nobara.physics.velocity</c>: the velocities of a body after the last step, written by nPhysics only.</summary>
public struct BodyVelocity
{
    /// <summary>The linear velocity, in blocks per second.</summary>
    public Vector3 Linear;

    /// <summary>The angular velocity, in radians per second, along the axes of the world.</summary>
    public Vector3 Angular;
}

/// <summary>The state of a body, as <see cref="IPhysicsSystem.GetState"/> reads it.</summary>
/// <param name="Kind">How it moves.</param>
/// <param name="Dimension">The dimension of its entity.</param>
/// <param name="Entity">Its entity.</param>
/// <param name="Transform">The world transform of its entity, as the last step wrote it.</param>
/// <param name="LinearVelocity">The linear velocity, in blocks per second.</param>
/// <param name="AngularVelocity">The angular velocity, in radians per second.</param>
/// <param name="IsAsleep">Whether it sleeps.</param>
[HotPath]
public readonly record struct BodyState(
    BodyKind Kind,
    DimensionId Dimension,
    EntityId Entity,
    WorldTransform Transform,
    Vector3 LinearVelocity,
    Vector3 AngularVelocity,
    bool IsAsleep);
