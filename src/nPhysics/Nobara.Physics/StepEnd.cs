// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Physics;

/// <summary>Where a body is after a step.</summary>
/// <param name="Body">The body.</param>
/// <param name="Dimension">The dimension of its entity.</param>
/// <param name="Entity">Its entity.</param>
/// <param name="Transform">The world transform written to its entity.</param>
/// <param name="LinearVelocity">The linear velocity, in blocks per second.</param>
/// <param name="AngularVelocity">The angular velocity, in radians per second.</param>
[HotPath]
public readonly record struct BodyPose(BodyHandle Body, DimensionId Dimension, EntityId Entity, WorldTransform Transform, Vector3 LinearVelocity, Vector3 AngularVelocity);

/// <summary>The end of a step: the bodies whose transform it wrote, the contacts of the bodies that report them, and what the characters did.</summary>
public readonly ref struct PhysicsStepEnd
{
    [HotPath]
    internal PhysicsStepEnd(ulong tick, TimeSpan delta, ReadOnlySpan<BodyPose> bodies)
    {
        Tick = tick;
        Delta = delta;
        Bodies = bodies;
    }

    /// <summary>Gets the number of the tick.</summary>
    public ulong Tick { get; }

    /// <summary>Gets the duration of the step.</summary>
    public TimeSpan Delta { get; }

    /// <summary>Gets the bodies whose transform the step wrote: those that moved or are awake, and those moved by a command. Valid during the call only.</summary>
    public ReadOnlySpan<BodyPose> Bodies { get; }

    /// <summary>Gets the characters that walked on the ground during the step, with the distance and the ground: the start of footstep sounds. Valid during the call only.</summary>
    public ReadOnlySpan<CharacterStep> CharacterSteps { get; init; }

    /// <summary>Gets the characters that landed during the step, with their speed and the ground: the start of landing sounds and fall damage. Valid during the call only.</summary>
    public ReadOnlySpan<CharacterLanding> CharacterLandings { get; init; }

    /// <summary>Gets the contacts that began during the step, of the bodies that report their contacts. Valid during the call only.</summary>
    public ReadOnlySpan<ContactEvent> ContactsBegun { get; init; }

    /// <summary>Gets the contacts that ended during the step, those of removed bodies included. Valid during the call only.</summary>
    public ReadOnlySpan<ContactEvent> ContactsEnded { get; init; }

    /// <summary>Gets the contacts whose parts approached at the impact speed of their body or faster: the start of impact sounds, fall damage, and breaking. Valid during the call only.</summary>
    public ReadOnlySpan<ContactEvent> Impacts { get; init; }
}

/// <summary>Receives the end of each step, on the loop thread: a module that keeps a history of positions per tick starts here.</summary>
public interface IPhysicsStepObserver
{
    /// <summary>Called after each step, once the transforms are written.</summary>
    /// <param name="step">The step.</param>
    [HotPath]
    void OnStepEnd(in PhysicsStepEnd step);
}

/// <summary>A subscription to the end of the steps; dispose it to stop receiving them.</summary>
public sealed class PhysicsStepSubscription : IDisposable
{
    private Action<PhysicsStepSubscription>? _remove;

    internal PhysicsStepSubscription(ModuleContext module, IPhysicsStepObserver observer, Action<PhysicsStepSubscription> remove) => (Module, Observer, _remove) = (module, observer, remove);

    internal ModuleContext Module { get; }

    internal IPhysicsStepObserver Observer { get; }

    /// <summary>Stops the subscription from the next step on.</summary>
    public void Dispose()
    {
        _remove?.Invoke(this);
        _remove = null;
    }
}
