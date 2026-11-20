// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using Nobara.Core;

namespace Nobara.Physics.Bepu;

/// <summary>
/// The restitution of a world. BEPUphysics has no coefficient of restitution:
/// its contacts are springs, and its speculative contacts stop bodies before
/// they touch, so a spring alone cannot give back a share of the speed. The
/// narrow phase notes each pair that bounces and will touch during the step,
/// with its speed of approach; after the step, the world gives the pair back
/// the share of that speed its restitution asks for, along the normal.
/// </summary>
/// <remarks>
/// Each worker of the step writes its own list, so the lists need no lock.
/// A pair that approaches slower than <see cref="MinimumSpeed"/> does not
/// bounce, so that resting bodies fall asleep. The rotation of the bodies is
/// not taken into account: the bounce acts on their linear velocities.
/// </remarks>
internal sealed class Bounces
{
    /// <summary>The slowest approach that bounces, in blocks per second.</summary>
    public const float MinimumSpeed = 1f;

    /// <summary>The most pairs one worker notes in a step; the next ones do not bounce.</summary>
    public const int Capacity = 256;

    private readonly Bounce[][] _records;
    private readonly int[] _counts;

    /// <summary>Creates the lists of a world.</summary>
    /// <param name="workers">The number of workers that can run the narrow phase.</param>
    public Bounces(int workers)
    {
        _records = new Bounce[workers][];
        _counts = new int[workers];
        for (int i = 0; i < workers; i++)
        {
            _records[i] = new Bounce[Capacity];
        }
    }

    /// <summary>Gets the duration of the step that runs.</summary>
    public float Delta { get; private set; }

    /// <summary>Gets the frequency of the contacts of the bodies with continuous detection: half the rate of the substeps, and no less than the other contacts.</summary>
    public float StiffFrequency { get; private set; } = NarrowPhaseCallbacks.ContactFrequency;

    /// <summary>Prepares the step that runs.</summary>
    /// <param name="delta">Its duration.</param>
    /// <param name="substeps">Its number of substeps.</param>
    [HotPath]
    public void Prepare(float delta, int substeps)
    {
        Delta = delta;
        StiffFrequency = MathF.Max(NarrowPhaseCallbacks.ContactFrequency, substeps / (2f * delta));
    }

    /// <summary>Gets the simulation, set when the narrow phase is created.</summary>
    public Simulation? Simulation { get; set; }

    /// <summary>Notes a pair that may bounce, from a worker of the narrow phase.</summary>
    /// <param name="worker">The index of the worker.</param>
    /// <param name="pair">The pair.</param>
    /// <param name="normal">The normal of the contact, from the second collidable to the first.</param>
    /// <param name="depth">The depth of the deepest contact; negative while the shapes are apart.</param>
    /// <param name="restitution">The restitution of the pair.</param>
    [HotPath]
    public void Note(int worker, CollidablePair pair, Vector3 normal, float depth, float restitution)
    {
        Bodies bodies = Simulation!.Bodies;
        Vector3 relative = Velocity(bodies, pair.A) - Velocity(bodies, pair.B);
        float approach = -Vector3.Dot(relative, normal);
        if (approach < MinimumSpeed || depth < -approach * Delta)
        {
            return;   // too slow, or does not touch during this step
        }

        int count = _counts[worker];
        if (count < Capacity)
        {
            _records[worker][count] = new Bounce(pair, normal, approach * restitution);
            _counts[worker] = count + 1;
        }
    }

    /// <summary>Gives each pair noted during the step the speed of its bounce, then forgets them.</summary>
    [HotPath]
    public void Apply()
    {
        Bodies bodies = Simulation!.Bodies;
        for (int worker = 0; worker < _counts.Length; worker++)
        {
            Bounce[] records = _records[worker];
            for (int i = 0; i < _counts[worker]; i++)
            {
                Apply(bodies, records[i]);
            }

            _counts[worker] = 0;
        }
    }

    [HotPath]
    private static void Apply(Bodies bodies, in Bounce bounce)
    {
        float inverseA = InverseMass(bodies, bounce.Pair.A);
        float inverseB = InverseMass(bodies, bounce.Pair.B);
        if (inverseA + inverseB <= 0)
        {
            return;
        }

        Vector3 relative = Velocity(bodies, bounce.Pair.A) - Velocity(bodies, bounce.Pair.B);
        float missing = bounce.Speed - Vector3.Dot(relative, bounce.Normal);
        if (missing <= 0)
        {
            return;
        }

        float impulse = missing / (inverseA + inverseB);
        if (inverseA > 0)
        {
            bodies[bounce.Pair.A.BodyHandle].Velocity.Linear += bounce.Normal * (impulse * inverseA);
        }

        if (inverseB > 0)
        {
            bodies[bounce.Pair.B.BodyHandle].Velocity.Linear -= bounce.Normal * (impulse * inverseB);
        }
    }

    [HotPath]
    private static Vector3 Velocity(Bodies bodies, CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static ? Vector3.Zero : bodies[collidable.BodyHandle].Velocity.Linear;

    [HotPath]
    private static float InverseMass(Bodies bodies, CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Dynamic ? bodies[collidable.BodyHandle].LocalInertia.InverseMass : 0;

    /// <summary>A pair that bounces: its normal, from the second collidable to the first, and the speed at which they part.</summary>
    [HotPath]
    private readonly record struct Bounce(CollidablePair Pair, Vector3 Normal, float Speed);
}
