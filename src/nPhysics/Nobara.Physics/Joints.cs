// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Physics;

/// <summary>The kinds of joints of the neutral API.</summary>
public enum JointKind : byte
{
    /// <summary>The bodies turn about a shared axis, and nothing else.</summary>
    Hinge,

    /// <summary>The bodies slide along a shared axis, and nothing else.</summary>
    Slider,

    /// <summary>The bodies keep the place and the rotation they had relative to each other when the joint was made.</summary>
    Fixed,
}

/// <summary>Identifies a joint. It names no joint any more once the joint is destroyed, or one of its bodies removed.</summary>
public readonly struct JointHandle : IEquatable<JointHandle>
{
    [HotPath]
    internal JointHandle(int index, int generation) => (Index, Generation) = (index, generation);

    /// <summary>Gets the slot of the joint.</summary>
    public int Index { get; }

    /// <summary>Gets the generation of the slot, from 1; 0 for the default handle, which names no joint.</summary>
    public int Generation { get; }

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name the same joint.</returns>
    [HotPath]
    public static bool operator ==(JointHandle left, JointHandle right) => left.Equals(right);

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name different joints.</returns>
    [HotPath]
    public static bool operator !=(JointHandle left, JointHandle right) => !left.Equals(right);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(JointHandle other) => Index == other.Index && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JointHandle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <inheritdoc/>
    public override string ToString() => $"joint {Index}#{Generation}";
}

/// <summary>The motor of a hinge or a slider: it drives the joint toward a speed, with a bounded effort.</summary>
/// <param name="TargetVelocity">The speed it aims at: radians per second for a hinge, blocks per second for a slider.</param>
/// <param name="MaxForce">The most effort it uses, from 0: a torque for a hinge, a force for a slider.</param>
[HotPath]
public readonly record struct JointMotor(float TargetVelocity, float MaxForce);

/// <summary>What <see cref="IPhysicsSystem.CreateJoint"/> makes between two bodies.</summary>
/// <remarks>
/// Anchors and axes are given in the frame of each body, from its center. A
/// hinge turns about its axis, a slider slides along it; a fixed joint uses
/// neither the axes nor a motor.
/// </remarks>
/// <param name="Kind">The kind.</param>
/// <param name="AnchorA">The point of the joint on the first body.</param>
/// <param name="AnchorB">The point of the joint on the second body.</param>
/// <param name="AxisA">The axis on the first body, of length 1.</param>
/// <param name="AxisB">The axis on the second body, of length 1.</param>
/// <param name="Motor">The motor, or <see langword="null"/>.</param>
public readonly record struct JointDescription(JointKind Kind, Vector3 AnchorA, Vector3 AnchorB, Vector3 AxisA, Vector3 AxisB, JointMotor? Motor = null);
