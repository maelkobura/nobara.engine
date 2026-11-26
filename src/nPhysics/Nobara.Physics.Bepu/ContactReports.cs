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
/// The contacts that a world reports to nPhysics: the narrow phase notes,
/// for each pair that holds a body that reports its contacts, its deepest
/// contact when the pair touches or will touch during the step; after the
/// step, the world gathers the lists of the workers into one.
/// </summary>
/// <remarks>
/// Each worker writes its own list, which it grows itself, so the lists need
/// no lock; once they have grown to the largest step, noting allocates
/// nothing.
/// </remarks>
internal sealed class ContactReports
{
    private readonly BackendContact[][] _records;
    private readonly int[] _counts;
    private BackendContact[] _gathered = new BackendContact[16];
    private int _gatheredCount;
    private float _delta;

    /// <summary>Creates the lists of a world.</summary>
    /// <param name="workers">The number of workers that can run the narrow phase.</param>
    public ContactReports(int workers)
    {
        _records = new BackendContact[workers][];
        _counts = new int[workers];
        for (int i = 0; i < workers; i++)
        {
            _records[i] = new BackendContact[16];
        }
    }

    /// <summary>Gets or sets the simulation, set when the narrow phase is created.</summary>
    public Simulation? Simulation { get; set; }

    /// <summary>Gets the contacts of the last step.</summary>
    public ReadOnlySpan<BackendContact> Gathered
    {
        [HotPath]
        get => new(_gathered, 0, _gatheredCount);
    }

    /// <summary>Prepares the step that runs.</summary>
    /// <param name="delta">Its duration.</param>
    [HotPath]
    public void Prepare(float delta) => _delta = delta;

    /// <summary>Notes the deepest contact of a pair, from a worker of the narrow phase, when the pair touches or will touch during the step.</summary>
    /// <typeparam name="TManifold">The type of the manifold.</typeparam>
    /// <param name="worker">The index of the worker.</param>
    /// <param name="pair">The pair.</param>
    /// <param name="first">The surface of its first collidable.</param>
    /// <param name="second">The surface of its second collidable.</param>
    /// <param name="manifold">Its contacts, whose normals go from the second collidable to the first.</param>
    [HotPath]
    public void Note<TManifold>(int worker, CollidablePair pair, in Surface first, in Surface second, ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        int deepest = 0;
        for (int i = 1; i < manifold.Count; i++)
        {
            if (manifold.GetDepth(i) > manifold.GetDepth(deepest))
            {
                deepest = i;
            }
        }

        Simulation simulation = Simulation!;
        Vector3 normal = manifold.GetNormal(deepest);
        float depth = manifold.GetDepth(deepest);
        Vector3 relative = Velocity(simulation.Bodies, pair.A) - Velocity(simulation.Bodies, pair.B);
        float approach = -Vector3.Dot(relative, normal);
        if (depth + (MathF.Max(approach, 0) * _delta) < -BackendContact.Reach)
        {
            return;   // apart, and stays apart during the step
        }

        Vector3 point = Position(simulation, pair.A) + manifold.GetOffset(deepest);
        BackendContact contact;
        if (first.Cells != 0)
        {
            contact = new BackendContact(second.Slot, -1, first.Chunk, point, -normal, depth, approach);
        }
        else if (second.Cells != 0)
        {
            contact = new BackendContact(first.Slot, -1, second.Chunk, point, normal, depth, approach);
        }
        else if (first.Reports)
        {
            contact = new BackendContact(first.Slot, second.Slot, -1, point, normal, depth, approach);
        }
        else
        {
            contact = new BackendContact(second.Slot, first.Slot, -1, point, -normal, depth, approach);
        }

        int count = _counts[worker];
        BackendContact[] records = _records[worker];
        if (count == records.Length)
        {
            Array.Resize(ref _records[worker], count * 2);   // grown by its own worker, to the largest step
            records = _records[worker];
        }

        records[count] = contact;
        _counts[worker] = count + 1;
    }

    /// <summary>Gathers the contacts that the workers noted during the step, and empties their lists.</summary>
    [HotPath]
    public void Gather()
    {
        int total = 0;
        for (int worker = 0; worker < _counts.Length; worker++)
        {
            total += _counts[worker];
        }

        if (_gathered.Length < total)
        {
            Array.Resize(ref _gathered, Math.Max(total, _gathered.Length * 2));
        }

        int at = 0;
        for (int worker = 0; worker < _counts.Length; worker++)
        {
            new ReadOnlySpan<BackendContact>(_records[worker], 0, _counts[worker]).CopyTo(_gathered.AsSpan(at));
            at += _counts[worker];
            _counts[worker] = 0;
        }

        _gatheredCount = total;
    }

    /// <summary>Forgets the contacts of the last step.</summary>
    [HotPath]
    public void Clear() => _gatheredCount = 0;

    [HotPath]
    private static Vector3 Velocity(Bodies bodies, CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static ? Vector3.Zero : bodies[collidable.BodyHandle].Velocity.Linear;

    [HotPath]
    private static Vector3 Position(Simulation simulation, CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static
            ? simulation.Statics[collidable.StaticHandle].Pose.Position
            : simulation.Bodies[collidable.BodyHandle].Pose.Position;
}
