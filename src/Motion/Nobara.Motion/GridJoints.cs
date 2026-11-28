// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>Identifies a joint that Motion made between two grids, or a grid and the terrain. It names no joint once the joint is removed, or one of its grids stops being a body.</summary>
public readonly struct GridJointHandle : IEquatable<GridJointHandle>
{
    [HotPath]
    internal GridJointHandle(int index, int generation) => (Index, Generation) = (index, generation);

    /// <summary>Gets the slot of the joint.</summary>
    public int Index { get; }

    /// <summary>Gets the generation of the slot, from 1; 0 for the default handle, which names no joint.</summary>
    public int Generation { get; }

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name the same joint.</returns>
    [HotPath]
    public static bool operator ==(GridJointHandle left, GridJointHandle right) => left.Equals(right);

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name different joints.</returns>
    [HotPath]
    public static bool operator !=(GridJointHandle left, GridJointHandle right) => !left.Equals(right);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(GridJointHandle other) => Index == other.Index && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GridJointHandle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <inheritdoc/>
    public override string ToString() => $"grid joint {Index}#{Generation}";
}

/// <summary>What <see cref="IMotionSystem.Link"/> makes between a grid and its carrier.</summary>
/// <remarks>
/// The anchor and the axis are in the frame of the grid, the frame of its
/// blocks: the corner of the cell (0, 0, 0) is its origin. The joint holds
/// that point and that axis where they are on the carrier when it is made. A
/// hinge turns the grid about the axis, a slider slides it along the axis; a
/// fixed joint keeps the grid where it is on the carrier, and uses neither
/// the axis nor a motor.
/// </remarks>
/// <param name="Kind">Whether the joint is a hinge, a slider, or fixed.</param>
/// <param name="Anchor">The point of the joint, in the frame of the grid.</param>
/// <param name="Axis">The axis of a hinge or a slider, in the frame of the grid; of any length but 0.</param>
/// <param name="Motor">
/// The speed and effort that drive the grid relative to its carrier, or
/// <see langword="null"/> for a free joint: radians per second about the axis
/// (counterclockwise seen from its tip) and a torque for a hinge, blocks per
/// second along the axis and a force for a slider.
/// </param>
public readonly record struct GridJointDescription(JointKind Kind, Vector3 Anchor, Vector3 Axis, JointMotor? Motor = null);

/// <summary>A joint that Motion made, as <see cref="IMotionSystem.GetJoint"/> gives it.</summary>
/// <param name="Grid">The grid the joint moves.</param>
/// <param name="Carrier">The grid that carries it, or the terrain.</param>
/// <param name="Description">The joint as it was made, with its motor of now.</param>
/// <param name="Joint">The joint of nPhysics.</param>
public readonly record struct GridJointState(Grid Grid, Grid Carrier, GridJointDescription Description, JointHandle Joint);

/// <summary>
/// Decides what happens when a kinematic grid crushes a character: a module
/// adds one through <see cref="IMotionSystem.AddCrushHandler"/>. Motion asks
/// the handlers on the loop thread, during the step of nPhysics.
/// </summary>
/// <remarks>
/// A handler may read the entities and call the commands of Motion and of
/// nPhysics, which apply at the next step; it does not change the structure
/// of an entity world.
/// </remarks>
public interface ICrushHandler
{
    /// <summary>Decides what a crush does, or leaves it to the next handler.</summary>
    /// <param name="crush">The grid, the character, and the way the grid pushes.</param>
    /// <param name="response">What happens, when the handler decides.</param>
    /// <returns><see langword="true"/> when the handler decides; the handlers after it are not asked.</returns>
    [HotPath]
    bool TryResolve(in CharacterCrush crush, out CrushResponse response);
}

/// <summary>The registration of a crush handler; dispose it to remove the handler.</summary>
public sealed class CrushHandlerHandle : IDisposable
{
    private Action<CrushHandlerHandle>? _remove;

    internal CrushHandlerHandle(ModuleContext module, ICrushHandler handler, int priority, long order, Action<CrushHandlerHandle> remove) =>
        (Module, Handler, Priority, Order, _remove) = (module, handler, priority, order, remove);

    internal ModuleContext Module { get; }

    internal ICrushHandler Handler { get; }

    internal int Priority { get; }

    internal long Order { get; }

    /// <summary>Removes the handler.</summary>
    public void Dispose()
    {
        _remove?.Invoke(this);
        _remove = null;
    }
}
