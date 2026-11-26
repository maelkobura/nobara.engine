// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The contact events: after each step, nPhysics reads the pairs that each
/// world reports for the bodies that report their contacts, merges those of
/// a body and its ghosts, and compares them with the pairs of the step
/// before. A pair that appears has begun, one that disappears has ended, and
/// one whose parts approached at the impact speed or faster is an impact.
/// </summary>
/// <remarks>
/// A pair of a body that sleeps or is frozen is kept as it was, since the
/// backend no longer looks at it: a crate that falls asleep on the ground
/// does not end its contact. A pair ends once the backend has not reported
/// it for <see cref="ContactEvents.GraceSteps"/> steps in a row: a body that
/// hops a few millimeters as it settles, where the backend finds no contact,
/// keeps its contact. The pairs live in two tables, swapped at each step,
/// which allocate nothing once they have grown.
/// </remarks>
internal sealed partial class PhysicsSimulation
{
    /// <summary>How far into a block, from the deepest contact against the normal, the block of a contact is looked for.</summary>
    private static ReadOnlySpan<float> BlockProbes
    {
        [HotPath]
        get => [0.01f, 0.1f, 0.5f];
    }

    private Dictionary<ContactKey, ContactRecord> _contactsNow = [];
    private Dictionary<ContactKey, ContactRecord> _contactsBefore = [];
    private ContactEvent[] _begun = new ContactEvent[16];
    private ContactEvent[] _ended = new ContactEvent[16];
    private ContactEvent[] _impacts = new ContactEvent[16];
    private int _begunCount;
    private int _endedCount;
    private int _impactCount;
    private ulong _contactTick;
    private float _contactDelta;

    /// <summary>Gets the contacts that the bodies that report them hold after the last step, for the debug drawing.</summary>
    internal Dictionary<ContactKey, ContactRecord>.ValueCollection Contacts => _contactsBefore.Values;

    /// <summary>Reads the contacts of every world after the step, and lists the contacts begun, ended, and the impacts.</summary>
    [HotPath]
    private void GatherContacts(ulong tick, float delta)
    {
        _begunCount = _endedCount = _impactCount = 0;
        _contactTick = tick;
        _contactDelta = delta;
        for (int w = 0; w < _worlds.Count; w++)
        {
            WorldEntry world = _worlds[w];
            ReadOnlySpan<BackendContact> contacts = world.Backend.Contacts;
            for (int i = 0; i < contacts.Length; i++)
            {
                Note(world, contacts[i]);
            }
        }

        foreach (KeyValuePair<ContactKey, ContactRecord> pair in _contactsBefore)
        {
            if (_contactsNow.ContainsKey(pair.Key))
            {
                continue;
            }

            if (!Reports(pair.Value.Body) && !Reports(pair.Value.Other))
            {
                continue;   // its bodies stopped reporting their contacts: it is dropped without an event
            }

            if (IsResting(pair.Value))
            {
                CollectionsMarshal.GetValueRefOrAddDefault(_contactsNow, pair.Key, out _) = pair.Value with { Speed = 0, Missing = 0 };   // the backend no longer looks at it
            }
            else if (IsAlive(pair.Value.Body) && (pair.Value.Grid is not null || IsAlive(pair.Value.Other)) && pair.Value.Missing + 1 < ContactEvents.GraceSteps)
            {
                CollectionsMarshal.GetValueRefOrAddDefault(_contactsNow, pair.Key, out _) = pair.Value with { Speed = 0, Missing = pair.Value.Missing + 1 };   // a hop as it settles
            }
            else
            {
                Append(ref _ended, ref _endedCount, Event(pair.Value));
            }
        }

        foreach (KeyValuePair<ContactKey, ContactRecord> pair in _contactsNow)
        {
            ContactRecord record = pair.Value;
            if (record.Tick != tick)
            {
                continue;   // kept from a resting pair: neither new nor an impact
            }

            if (!_contactsBefore.ContainsKey(pair.Key))
            {
                Append(ref _begun, ref _begunCount, Event(record));
            }

            if (record.Speed >= record.ImpactSpeed)
            {
                Append(ref _impacts, ref _impactCount, Event(record));
            }
        }

        (_contactsBefore, _contactsNow) = (_contactsNow, _contactsBefore);
        _contactsNow.Clear();
    }

    /// <summary>Merges one pair that a world reports into the pairs of the step: once per pair, at its deepest contact and its fastest approach.</summary>
    [HotPath]
    private void Note(WorldEntry world, in BackendContact contact)
    {
        if ((uint)contact.SlotA >= (uint)_bodyHigh || !_bodies[contact.SlotA].Alive)
        {
            return;
        }

        ref BodyRecord first = ref _bodies[contact.SlotA];
        var record = new ContactRecord
        {
            Tick = _contactTick,
            Dimension = first.Dimension,
            Point = PhysicsFrame.ToWorld(world.Origin, contact.Point),
            Normal = contact.Normal,
            Depth = contact.Depth,
            Speed = contact.Speed,
        };
        ContactKey key;
        if (contact.SlotB < 0)
        {
            if (!ResolveBlock(world, contact, ref record))
            {
                return;
            }

            record.Body = new BodyHandle(contact.SlotA, first.Generation);
            record.Entity = first.Entity;
            record.ImpactSpeed = first.ImpactSpeed;
            record.Impulse = Impulse(contact.Speed, 1f / ReducedInverse(first, default, other: false), first.Material.Restitution);
            key = new ContactKey(record.Body, default, record.Grid);
        }
        else
        {
            if ((uint)contact.SlotB >= (uint)_bodyHigh || !_bodies[contact.SlotB].Alive)
            {
                return;
            }

            ref BodyRecord second = ref _bodies[contact.SlotB];
            bool swap = second.ReportsContacts && contact.SlotB < contact.SlotA;   // both report: the lower slot is the body of the event
            int bodySlot = swap ? contact.SlotB : contact.SlotA;
            int otherSlot = swap ? contact.SlotA : contact.SlotB;
            ref BodyRecord body = ref _bodies[bodySlot];
            ref BodyRecord other = ref _bodies[otherSlot];
            record.Body = new BodyHandle(bodySlot, body.Generation);
            record.Entity = body.Entity;
            record.Other = new BodyHandle(otherSlot, other.Generation);
            record.OtherEntity = other.Entity;
            record.Normal = swap ? -contact.Normal : contact.Normal;
            record.ImpactSpeed = other.ReportsContacts ? MathF.Min(body.ImpactSpeed, other.ImpactSpeed) : body.ImpactSpeed;
            float inverse = ReducedInverse(body, other, other: true);
            record.Impulse = inverse > 0 ? Impulse(contact.Speed, 1f / inverse, MathF.Max(body.Material.Restitution, other.Material.Restitution)) : 0;
            key = contact.SlotA < contact.SlotB
                ? new ContactKey(new BodyHandle(contact.SlotA, first.Generation), new BodyHandle(contact.SlotB, second.Generation), null)
                : new ContactKey(new BodyHandle(contact.SlotB, second.Generation), new BodyHandle(contact.SlotA, first.Generation), null);
        }

        if (!_contactsBefore.ContainsKey(key) && contact.Depth + (MathF.Max(contact.Speed, 0) * _contactDelta) < -BackendContact.BeginReach)
        {
            return;   // near, but not close enough to begin
        }

        ref ContactRecord kept = ref CollectionsMarshal.GetValueRefOrAddDefault(_contactsNow, key, out bool exists);
        if (!exists)
        {
            kept = record;
            return;
        }

        // The same pair from a ghost, or a body against two chunks of a grid.
        float speed = MathF.Max(kept.Speed, record.Speed);
        float impulse = MathF.Max(kept.Impulse, record.Impulse);
        if (record.Depth > kept.Depth)
        {
            kept = record;
        }

        kept.Speed = speed;
        kept.Impulse = impulse;
    }

    /// <summary>Finds the grid and the block of a contact with a chunk of voxels: the first block met from the contact into the voxels.</summary>
    [HotPath]
    private static bool ResolveBlock(WorldEntry world, in BackendContact contact, ref ContactRecord record)
    {
        if (world.Voxels is not { } table)
        {
            return false;
        }

        int slot = table.SlotOf(contact.VoxelChunk);
        if (slot < 0)
        {
            return false;
        }

        ref VoxelSlot entry = ref table.Slots[slot];
        Grid grid = entry.Key.Grid;
        ChunkCoordinate chunk = entry.Key.Chunk;
        ChunkCorner(grid, chunk, out WorldPosition corner);
        Quaternion inverse = Quaternion.Conjugate(entry.Cells!.Rotation);
        Vector3 local = Vector3.Transform(contact.Point - PhysicsFrame.ToLocal(world.Origin, corner), inverse);
        Vector3 into = Vector3.Transform(-contact.Normal, inverse);   // from the body into the voxels
        record.Grid = grid;
        for (int i = 0; i < BlockProbes.Length; i++)
        {
            float probe = BlockProbes[i] + MathF.Max(-contact.Depth, 0);
            Vector3 cell = local + (into * probe);
            var block = new BlockCoordinate(
                (chunk.X * VoxelCoordinates.ChunkSize) + (int)MathF.Floor(cell.X),
                (chunk.Y * VoxelCoordinates.ChunkSize) + (int)MathF.Floor(cell.Y),
                (chunk.Z * VoxelCoordinates.ChunkSize) + (int)MathF.Floor(cell.Z));
            uint state = StateAt(grid, block);
            if (state != 0 || i == BlockProbes.Length - 1)
            {
                record.Block = block;
                record.State = state;
                break;
            }
        }

        return true;
    }

    [HotPath]
    private static uint StateAt(Grid grid, BlockCoordinate block) =>
        grid.Storage.TryGetChunk(VoxelCoordinates.ToChunk(block), out Chunk? chunk) && chunk is not null ? chunk.Palette.Get(VoxelCoordinates.ToIndex(block)) : 0;

    /// <summary>Gets the sum of the inverse masses of a pair; a body that is not dynamic, and a block, count as of infinite mass.</summary>
    [HotPath]
    private static float ReducedInverse(in BodyRecord body, in BodyRecord second, bool other)
    {
        float inverse = body.Kind == BodyKind.Dynamic ? 1f / body.Mass : 0;
        if (other && second.Kind == BodyKind.Dynamic)
        {
            inverse += 1f / second.Mass;
        }

        return inverse;
    }

    [HotPath]
    private static float Impulse(float speed, float mass, float restitution) =>
        speed > 0 && float.IsFinite(mass) ? speed * mass * (1 + restitution) : 0;

    /// <summary>Tells whether a pair that the backend no longer reports rests: its body sleeps or is frozen, and so does the other part, or it is a block or a body that does not move.</summary>
    [HotPath]
    private bool IsResting(in ContactRecord record)
    {
        if (!IsAlive(record.Body) || !Still(_bodies[record.Body.Index]))
        {
            return false;
        }

        if (record.Grid is not null)
        {
            return !record.Grid.IsDestroyed;
        }

        return IsAlive(record.Other) && (_bodies[record.Other.Index].Kind == BodyKind.Static || Still(_bodies[record.Other.Index]));

        static bool Still(in BodyRecord body) => body.Asleep || body.World is null;
    }

    /// <summary>Tells whether a body is alive and reports its contacts; a removed body counts as reporting, so that its contacts end.</summary>
    [HotPath]
    private bool Reports(BodyHandle body) => !body.IsNone && (!IsAlive(body) || _bodies[body.Index].ReportsContacts);

    [HotPath]
    private static ContactEvent Event(in ContactRecord record) =>
        new(record.Dimension, record.Body, record.Entity, record.Other, record.OtherEntity, record.Grid, record.Block, record.State, record.Point, record.Normal, record.Speed, record.Impulse);

    [HotPath]
    private static void Append(ref ContactEvent[] events, ref int count, in ContactEvent value)
    {
        if (count == events.Length)
        {
            Array.Resize(ref events, events.Length * 2);   // grows to the most events of a step, then stays
        }

        events[count++] = value;
    }

    /// <summary>Forgets the pairs and the events, at the end of a world session.</summary>
    private void ResetContacts()
    {
        _contactsNow.Clear();
        _contactsBefore.Clear();
        Array.Clear(_begun);
        Array.Clear(_ended);
        Array.Clear(_impacts);
        _begunCount = _endedCount = _impactCount = 0;
    }

    /// <summary>A pair: two bodies, the lower slot first, or a body and a grid.</summary>
    internal readonly record struct ContactKey
    {
        [HotPath]
        public ContactKey(BodyHandle first, BodyHandle second, Grid? grid) => (First, Second, Grid) = (first, second, grid);

        public BodyHandle First { get; }

        public BodyHandle Second { get; }

        public Grid? Grid { get; }
    }

    /// <summary>What nPhysics keeps of a pair between two steps.</summary>
    internal struct ContactRecord
    {
        public ulong Tick;
        public DimensionId Dimension;
        public BodyHandle Body;
        public EntityId Entity;
        public BodyHandle Other;
        public EntityId OtherEntity;
        public Grid? Grid;
        public BlockCoordinate Block;
        public uint State;
        public WorldPosition Point;
        public Vector3 Normal;
        public float Depth;
        public float Speed;
        public float Impulse;
        public float ImpactSpeed;

        /// <summary>The steps in a row in which the backend did not report the pair.</summary>
        public int Missing;
    }
}
