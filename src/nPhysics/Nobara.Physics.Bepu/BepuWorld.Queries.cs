// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using Nobara.Core;

namespace Nobara.Physics.Bepu;

using BodyVelocity = BepuPhysics.BodyVelocity;

/// <summary>
/// The queries of a world: rays, sweeps, and overlaps among its bodies, by
/// the queries of BEPUphysics. The chunks of voxels are left out: nPhysics
/// tests the voxels itself.
/// </summary>
/// <remarks>
/// Each thread that queries at once has its own pool of unmanaged memory and
/// its own table of hits, made with the world: thread 0 uses the
/// pool of the world, which no step uses while queries run.
/// </remarks>
internal sealed partial class BepuWorld
{
    /// <summary>How far a shape moves to test what it overlaps: an overlap is a sweep whose hits at distance 0 count.</summary>
    private const float OverlapProbe = 1e-4f;

    private BufferPool?[] _queryPools = [];
    private BackendQueryHit[][] _queryHits = [];

    /// <inheritdoc/>
    public override int RayCast(in BackendQuery query, int thread, Span<BackendQueryHit> hits)
    {
        if (hits.Length == 0)
        {
            return 0;
        }

        var handler = new BodyHits(_surfaces, query, Scratch(thread, hits.Length), hits.Length, overlap: false);
        Simulation.RayCast(query.Origin, query.Direction, query.MaxDistance, PoolOf(thread), ref handler);
        return handler.CopyTo(hits);
    }

    /// <inheritdoc/>
    public override int Sweep(in BackendQuery query, int thread, Span<BackendQueryHit> hits)
    {
        if (hits.Length == 0)
        {
            return 0;
        }

        var handler = new BodyHits(_surfaces, query, Scratch(thread, hits.Length), hits.Length, overlap: false);
        SweepShape(query, query.Direction, query.MaxDistance, thread, ref handler);
        return handler.CopyTo(hits);
    }

    /// <inheritdoc/>
    public override int Overlap(in BackendQuery query, int thread, Span<BackendQueryHit> hits)
    {
        if (hits.Length == 0)
        {
            return 0;
        }

        var handler = new BodyHits(_surfaces, query, Scratch(thread, hits.Length), hits.Length, overlap: true);
        SweepShape(query, Vector3.UnitY, OverlapProbe, thread, ref handler);
        return handler.CopyTo(hits);
    }

    /// <summary>Frees the pools of the threads that queried the world.</summary>
    [HotPath]
    private void DisposeQueries()
    {
        for (int i = 1; i < _queryPools.Length; i++)
        {
            _queryPools[i]?.Clear();
        }

        _queryPools = [];
        _queryHits = [];
    }

    [HotPath]
    private void SweepShape(in BackendQuery query, Vector3 direction, float distance, int thread, ref BodyHits handler)
    {
        var pose = new RigidPose(query.Origin, Quaternion.Normalize(query.Rotation));
        var velocity = new BodyVelocity(direction, Vector3.Zero);
        BufferPool pool = PoolOf(thread);
        QueryShape shape = query.Shape;
        switch (shape.Kind)
        {
            case ShapeKind.Sphere:
                Simulation.Sweep(new Sphere(shape.Radius), pose, velocity, distance, pool, ref handler);
                break;
            case ShapeKind.Capsule:
                Simulation.Sweep(new Capsule(shape.Radius, shape.Length), pose, velocity, distance, pool, ref handler);
                break;
            default:
                Simulation.Sweep(new Box(shape.Size.X, shape.Size.Y, shape.Size.Z), pose, velocity, distance, pool, ref handler);
                break;
        }
    }

    /// <summary>Gets the pool of a thread that queries: the pool of the world for thread 0, a pool of its own for the others.</summary>
    [HotPath]
    private BufferPool PoolOf(int thread)
    {
        if (thread == 0)
        {
            return _pool;
        }

        RequireThread(thread);
        return _queryPools[thread]!;
    }

    /// <summary>Gets the table of hits of a thread, with room for a number of hits.</summary>
    [HotPath]
    private BackendQueryHit[] Scratch(int thread, int capacity)
    {
        RequireThread(thread);
        if (_queryHits[thread].Length < capacity)
        {
            Array.Resize(ref _queryHits[thread], capacity);   // grown by its own thread, to the largest query
        }

        return _queryHits[thread];
    }

    [HotPath]
    private void RequireThread(int thread)
    {
        if ((uint)thread >= (uint)_queryHits.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(thread), thread, $"The world takes queries from {_queryHits.Length} threads at once.");
        }
    }

    /// <summary>
    /// Keeps the closest bodies that a query meets, one hit per body, among
    /// those its mask lets through, without the chunks of voxels and the
    /// ignored body.
    /// </summary>
    [HotPath]
    private struct BodyHits : IRayHitHandler, ISweepHitHandler
    {
        private readonly CollidableProperty<Surface> _surfaces;
        private readonly BackendQueryHit[] _hits;
        private readonly int _capacity;
        private readonly uint _mask;
        private readonly int _ignored;
        private readonly Vector3 _origin;
        private readonly Vector3 _direction;
        private readonly bool _overlap;
        private int _count;

        public BodyHits(CollidableProperty<Surface> surfaces, in BackendQuery query, BackendQueryHit[] hits, int capacity, bool overlap)
        {
            _surfaces = surfaces;
            _hits = hits;
            _capacity = capacity;
            _mask = query.Mask;
            _ignored = query.IgnoredSlot;
            _origin = query.Origin;
            _direction = query.Direction;
            _overlap = overlap;
            _count = 0;
        }

        [HotPath]
        public readonly bool AllowTest(CollidableReference collidable)
        {
            ref Surface surface = ref _surfaces[collidable];
            return surface.Cells == 0 && surface.GridCells == 0 && (surface.Layers & _mask) != 0 && surface.Slot != _ignored;
        }

        [HotPath]
        public readonly bool AllowTest(CollidableReference collidable, int childIndex) => true;

        [HotPath]
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal, CollidableReference collidable, int childIndex) =>
            Add(ref maximumT, _surfaces[collidable].Slot, t, _origin + (_direction * t), Toward(normal));

        [HotPath]
        public void OnHit(ref float maximumT, float t, Vector3 hitLocation, Vector3 hitNormal, CollidableReference collidable)
        {
            if (!_overlap)
            {
                Add(ref maximumT, _surfaces[collidable].Slot, t, hitLocation, Toward(hitNormal));
            }
        }

        [HotPath]
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) =>
            Add(ref maximumT, _surfaces[collidable].Slot, 0, _origin, _overlap ? Vector3.Zero : -_direction);

        [HotPath]
        public readonly int CopyTo(Span<BackendQueryHit> hits)
        {
            new ReadOnlySpan<BackendQueryHit>(_hits, 0, _count).CopyTo(hits);
            return _count;
        }

        /// <summary>Turns a normal toward the query.</summary>
        [HotPath]
        private readonly Vector3 Toward(Vector3 normal)
        {
            float length = normal.Length();
            if (!(length > 1e-12f))
            {
                return Vector3.Zero;
            }

            normal /= length;
            return Vector3.Dot(normal, _direction) > 0 ? -normal : normal;
        }

        /// <summary>Keeps a hit: once per body, at its closest distance, among the closest <see cref="_capacity"/>.</summary>
        [HotPath]
        private void Add(ref float maximumT, int slot, float distance, Vector3 point, Vector3 normal)
        {
            var hit = new BackendQueryHit(slot, distance, point, normal);
            for (int i = 0; i < _count; i++)
            {
                if (_hits[i].Slot == slot)
                {
                    if (distance < _hits[i].Distance)
                    {
                        _hits[i] = hit;
                    }

                    return;
                }
            }

            if (_count < _capacity)
            {
                _hits[_count++] = hit;
            }
            else
            {
                int farthest = 0;
                for (int i = 1; i < _count; i++)
                {
                    if (_hits[i].Distance > _hits[farthest].Distance)
                    {
                        farthest = i;
                    }
                }

                if (distance >= _hits[farthest].Distance)
                {
                    return;
                }

                _hits[farthest] = hit;
            }

            if (_count == _capacity && !_overlap)
            {
                float far = 0;
                for (int i = 0; i < _count; i++)
                {
                    far = MathF.Max(far, _hits[i].Distance);
                }

                maximumT = far;   // nothing farther can enter the list
            }
        }
    }
}
