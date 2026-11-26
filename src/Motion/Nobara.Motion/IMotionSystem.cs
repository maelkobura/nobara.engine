// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>How a grid body moves.</summary>
public enum GridMotionMode : byte
{
    /// <summary>Gravity, forces, and collisions move the grid.</summary>
    Dynamic,

    /// <summary>The grid follows the velocities or the targets it is given, pushes dynamic bodies, and is never pushed.</summary>
    Kinematic,
}

/// <summary>
/// What Motion knows of a grid body after the last step.
/// </summary>
/// <param name="Mode">How the grid moves, as asked; a frozen grid keeps the mode it returns to.</param>
/// <param name="Transform">The transform of the grid: the position and the rotation of the origin of its blocks.</param>
/// <param name="LinearVelocity">The velocity of its center of mass, in blocks per second.</param>
/// <param name="AngularVelocity">Its angular velocity, in radians per second, along the axes of the world.</param>
/// <param name="Mass">Its mass, in kilograms, from its blocks.</param>
/// <param name="CenterOfMass">Its center of mass, in the frame of the grid, in blocks.</param>
/// <param name="Blocks">The number of cells of the grid in memory that hold a block.</param>
/// <param name="IsAsleep">Whether the grid sleeps.</param>
/// <param name="IsFrozen">Whether the grid is frozen by <see cref="IMotionSystem.Freeze"/>.</param>
/// <param name="IsSmall">Whether the grid is a small grid, simulated as a compound of boxes.</param>
public readonly record struct GridMotionState(
    GridMotionMode Mode,
    WorldTransform Transform,
    Vector3 LinearVelocity,
    Vector3 AngularVelocity,
    float Mass,
    Vector3 CenterOfMass,
    int Blocks,
    bool IsAsleep,
    bool IsFrozen,
    bool IsSmall);

/// <summary>
/// Makes grids physical bodies and moves them: the service of Motion. A grid
/// that Motion takes is a body of nPhysics whose mass, center of mass, and
/// inertia come from its blocks and follow them as they change; every
/// command goes through nPhysics, which applies it before the next step and
/// alone writes the transform of the grid.
/// </summary>
/// <remarks>
/// Call it on the loop thread of the context, outside entity systems for
/// <see cref="Add"/>, <see cref="Remove"/>, and <see cref="Split"/>, which
/// change the structure of the entity world. Forces and impulses are along
/// the axes of the world; points are in the frame of the grid, in blocks.
/// </remarks>
public interface IMotionSystem
{
    /// <summary>Gets the grids that Motion moves.</summary>
    IReadOnlyCollection<Grid> Grids { get; }

    /// <summary>Gets the number of blocks under which a grid is a small grid; from <c>motion.smallGridBlocks</c>.</summary>
    int SmallGridBlocks { get; }

    /// <summary>
    /// Makes a grid a body: its mass, center of mass, and inertia are read from
    /// its blocks, and the grid starts where it is, at rest.
    /// </summary>
    /// <param name="grid">A grid that is not the terrain, not destroyed, not a body yet, and not scaled.</param>
    /// <param name="mode">How it moves: dynamic by default.</param>
    /// <returns>The body of the grid in nPhysics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grid"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The grid is the terrain, destroyed, scaled, or already a body.</exception>
    BodyHandle Add(Grid grid, GridMotionMode mode = GridMotionMode.Dynamic);

    /// <summary>Stops moving a grid: its body is removed and the grid stays where it is, still.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void Remove(Grid grid);

    /// <summary>Tells whether Motion moves a grid.</summary>
    /// <param name="grid">The grid.</param>
    /// <returns><see langword="true"/> when the grid is a body of Motion.</returns>
    bool Contains(Grid grid);

    /// <summary>Gets the body of a grid in nPhysics.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <returns>The body.</returns>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    BodyHandle GetBody(Grid grid);

    /// <summary>Gets what Motion knows of a grid after the last step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <returns>Its state.</returns>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    GridMotionState GetState(Grid grid);

    /// <summary>Changes how a grid moves, at the next step, where it is and with its velocities.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="mode">The new mode.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void SetMode(Grid grid, GridMotionMode mode);

    /// <summary>Applies a force at the center of mass of a dynamic grid during the next step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="force">The force, in newtons (kilograms times blocks per square second).</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void ApplyForce(Grid grid, Vector3 force);

    /// <summary>Applies a force at a point of a dynamic grid during the next step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="force">The force.</param>
    /// <param name="point">Where it applies, in the frame of the grid.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void ApplyForceAt(Grid grid, Vector3 force, Vector3 point);

    /// <summary>Applies an impulse at the center of mass of a dynamic grid before the next step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="impulse">The impulse, in kilograms times blocks per second.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void ApplyImpulse(Grid grid, Vector3 impulse);

    /// <summary>Applies an impulse at a point of a dynamic grid before the next step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="impulse">The impulse.</param>
    /// <param name="point">Where it applies, in the frame of the grid.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void ApplyImpulseAt(Grid grid, Vector3 impulse, Vector3 point);

    /// <summary>Applies a torque to a dynamic grid during the next step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="torque">The torque, along the axes of the world.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void ApplyTorque(Grid grid, Vector3 torque);

    /// <summary>Applies an angular impulse to a dynamic grid before the next step.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="impulse">The angular impulse, along the axes of the world.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void ApplyAngularImpulse(Grid grid, Vector3 impulse);

    /// <summary>Sets how much of the gravity pulls a dynamic grid: 1 by default, 0 for none, below 0 to lift it.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <param name="scale">The share of the gravity, finite.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid, or <paramref name="scale"/> is not finite.</exception>
    void SetGravityScale(Grid grid, float scale);

    /// <summary>Sets the velocities of a grid, at its center of mass, before the next step.</summary>
    /// <param name="grid">A grid that Motion moves, not frozen.</param>
    /// <param name="linear">The velocity of its center of mass, in blocks per second.</param>
    /// <param name="angular">Its angular velocity, in radians per second, along the axes of the world.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void SetVelocity(Grid grid, Vector3 linear, Vector3 angular);

    /// <summary>Gives a kinematic grid the transform it reaches at the end of the next step; it stops there unless it gets another.</summary>
    /// <param name="grid">A kinematic grid that Motion moves, not frozen.</param>
    /// <param name="position">The position of the origin of its blocks.</param>
    /// <param name="rotation">Its rotation.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    /// <exception cref="InvalidOperationException">The grid is not kinematic.</exception>
    void SetTarget(Grid grid, in WorldPosition position, Quaternion rotation);

    /// <summary>Freezes a grid where it is: it stops, and neither gravity nor contacts move it until <see cref="Release"/>.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void Freeze(Grid grid);

    /// <summary>Releases a frozen grid: it moves again in its mode, from rest.</summary>
    /// <param name="grid">A grid that Motion moves.</param>
    /// <exception cref="ArgumentException">Motion does not move the grid.</exception>
    void Release(Grid grid);

    /// <summary>
    /// Splits blocks of a grid into a new grid, through nVoxel, and makes the
    /// new grid a body of the same mode, with the velocity that the source had
    /// at its center of mass. The new grid takes its blocks at the next tick.
    /// </summary>
    /// <param name="owner">The module that created the source grid, which owns the new one.</param>
    /// <param name="source">A grid that Motion moves.</param>
    /// <param name="selection">The blocks to move, in the frame of the source.</param>
    /// <returns>The new grid.</returns>
    /// <exception cref="ArgumentException">Motion does not move the source grid.</exception>
    Grid Split(ModuleContext owner, Grid source, IReadOnlyCollection<BlockCoordinate> selection);
}
