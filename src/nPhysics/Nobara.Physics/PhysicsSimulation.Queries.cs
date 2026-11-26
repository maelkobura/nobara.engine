// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The queries: rays, sweeps, and overlaps in the world, against the voxels
/// of every grid, which nPhysics tests itself where nVoxel keeps them, and
/// against the bodies of every region, which their backend worlds test.
/// </summary>
/// <remarks>
/// <para>
/// Each grid is tested in its own frame, from a cell near the start of the
/// query, so that the arithmetic stays small a billion blocks away. A ray
/// walks the cells it crosses; a sweep tests the cells along its way, piece
/// by piece, and stops once a piece holds a hit; an overlap tests the cells
/// under the shape.
/// </para>
/// <para>
/// A query runs on one thread with its own scratch (<see cref="QueryScratch"/>):
/// thread 0 for the queries of the loop thread, one per index of the job of
/// a batch.
/// </para>
/// </remarks>
internal sealed partial class PhysicsSimulation
{
    /// <summary>The fewest queries of a batch that one index of its job takes.</summary>
    private const int BatchSlice = 32;

    private readonly CollisionBox[] _wholeCell = [new CollisionBox(Vector3.Zero, Vector3.One)];

    private QueryScratch[] _queryScratch = [new QueryScratch()];
    private RayQuery[] _rayBatch = [];
    private SweepQuery[] _sweepBatch = [];
    private PhysicsHit[] _batchHits = [];
    private Exception?[] _batchFailures = [];
    private JobData? _transformData;
    private int _queries;
    private int _lastQueries;

    private enum QueryKind : byte
    {
        Ray,
        Sweep,
        Overlap,
    }

    [HotPath]
    public bool RayCast(DimensionId dimension, in WorldPosition origin, Vector3 direction, float maxDistance, out PhysicsHit hit, QueryFilter filter = default)
    {
        Vector3 unit = RequireLine(origin, direction, maxDistance);
        _queries++;
        Span<PhysicsHit> one = _queryScratch[0].One();
        int count = Query(QueryKind.Ray, dimension, origin, Quaternion.Identity, unit, maxDistance, default, filter, one, 0, evaluate: true);
        hit = count > 0 ? one[0] : default;
        return count > 0;
    }

    [HotPath]
    public int RayCastAll(DimensionId dimension, in WorldPosition origin, Vector3 direction, float maxDistance, Span<PhysicsHit> hits, QueryFilter filter = default)
    {
        Vector3 unit = RequireLine(origin, direction, maxDistance);
        _queries++;
        return Query(QueryKind.Ray, dimension, origin, Quaternion.Identity, unit, maxDistance, default, filter, hits, 0, evaluate: true);
    }

    [HotPath]
    public bool Sweep(DimensionId dimension, in QueryShape shape, in WorldPosition origin, Quaternion rotation, Vector3 direction, float maxDistance, out PhysicsHit hit, QueryFilter filter = default)
    {
        RequireShape(shape, rotation);
        Vector3 unit = RequireLine(origin, direction, maxDistance);
        _queries++;
        Span<PhysicsHit> one = _queryScratch[0].One();
        int count = Query(QueryKind.Sweep, dimension, origin, rotation, unit, maxDistance, shape, filter, one, 0, evaluate: true);
        hit = count > 0 ? one[0] : default;
        return count > 0;
    }

    [HotPath]
    public int SweepAll(DimensionId dimension, in QueryShape shape, in WorldPosition origin, Quaternion rotation, Vector3 direction, float maxDistance, Span<PhysicsHit> hits, QueryFilter filter = default)
    {
        RequireShape(shape, rotation);
        Vector3 unit = RequireLine(origin, direction, maxDistance);
        _queries++;
        return Query(QueryKind.Sweep, dimension, origin, rotation, unit, maxDistance, shape, filter, hits, 0, evaluate: true);
    }

    [HotPath]
    public int Overlap(DimensionId dimension, in QueryShape shape, in WorldPosition position, Quaternion rotation, Span<PhysicsHit> hits, QueryFilter filter = default)
    {
        RequireShape(shape, rotation);
        if (!IsFinite(position.Local))
        {
            throw new ArgumentException("The position of an overlap is finite.", nameof(position));
        }

        _queries++;
        return Query(QueryKind.Overlap, dimension, position, rotation, Vector3.UnitY, 0, shape, filter, hits, 0, evaluate: true);
    }

    [HotPath]
    public void RayCast(DimensionId dimension, ReadOnlySpan<RayQuery> queries, Span<PhysicsHit> hits)
    {
        if (hits.Length < queries.Length)
        {
            throw new ArgumentException("There is one hit per query.", nameof(hits));
        }

        for (int i = 0; i < queries.Length; i++)
        {
            RequireLine(queries[i].Origin, queries[i].Direction, queries[i].MaxDistance);
        }

        _queries += queries.Length;
        if (_rayBatch.Length < queries.Length)
        {
            Array.Resize(ref _rayBatch, queries.Length * 2);   // grows to the largest batch, then stays
        }

        queries.CopyTo(_rayBatch);
        RunBatch(dimension, queries.Length, sweeps: false, hits);
    }

    [HotPath]
    public void Sweep(DimensionId dimension, ReadOnlySpan<SweepQuery> queries, Span<PhysicsHit> hits)
    {
        if (hits.Length < queries.Length)
        {
            throw new ArgumentException("There is one hit per query.", nameof(hits));
        }

        for (int i = 0; i < queries.Length; i++)
        {
            RequireShape(queries[i].Shape, queries[i].Rotation);
            RequireLine(queries[i].Origin, queries[i].Direction, queries[i].MaxDistance);
        }

        _queries += queries.Length;
        if (_sweepBatch.Length < queries.Length)
        {
            Array.Resize(ref _sweepBatch, queries.Length * 2);
        }

        queries.CopyTo(_sweepBatch);
        RunBatch(dimension, queries.Length, sweeps: true, hits);
    }

    /// <summary>Prepares one scratch per thread that may query at once: the loop thread and each worker.</summary>
    private void PrepareQueries()
    {
        _transformData = Data(_transform);
        int threads = _context.Jobs.WorkerCount + 1;
        _queryScratch = new QueryScratch[threads];
        for (int i = 0; i < threads; i++)
        {
            _queryScratch[i] = new QueryScratch();
        }
    }

    /// <summary>Runs a batch, from <see cref="_rayBatch"/> or <see cref="_sweepBatch"/>: inline when it is small, otherwise one slice per index of a parallel job, each on its own scratch.</summary>
    [HotPath]
    private void RunBatch(DimensionId dimension, int count, bool sweeps, Span<PhysicsHit> hits)
    {
        if (_batchHits.Length < count)
        {
            Array.Resize(ref _batchHits, count * 2);
        }

        int slices = Math.Min(_queryScratch.Length, Math.Max(1, (count + BatchSlice - 1) / BatchSlice));
        int slice = (count + slices - 1) / Math.Max(1, slices);
        var job = new BatchJob(this, dimension, count, slice, sweeps);
        if (slices <= 1)
        {
            job.Execute(0);
        }
        else
        {
            if (_batchFailures.Length < slices)
            {
                Array.Resize(ref _batchFailures, _queryScratch.Length);
            }

            // Each index reads the blocks, the transforms of the grids, and the backend worlds, which nothing writes until the job completes, below.
            JobHandle handle = _context.Jobs.ScheduleFor(job, slices, 1, new JobAccess([_voxels!.ChunkAccess, _transformData!], []));
            handle.Complete();
            for (int i = 0; i < slices; i++)
            {
                if (_batchFailures[i] is { } failure)
                {
                    Array.Clear(_batchFailures);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                }
            }
        }

        _batchHits.AsSpan(0, count).CopyTo(hits);
        Array.Clear(_batchHits, 0, count);   // lets the grids they name go
    }

    /// <summary>Runs the queries of one slice of a batch.</summary>
    [HotPath]
    private void RunSlice(DimensionId dimension, int start, int end, int thread, bool sweeps)
    {
        Span<PhysicsHit> one = _queryScratch[thread].One();
        for (int i = start; i < end; i++)
        {
            int count;
            if (sweeps)
            {
                ref SweepQuery query = ref _sweepBatch[i];
                count = Query(QueryKind.Sweep, dimension, query.Origin, query.Rotation, Vector3.Normalize(query.Direction), query.MaxDistance, query.Shape, query.Filter, one, thread, evaluate: false);
            }
            else
            {
                ref RayQuery query = ref _rayBatch[i];
                count = Query(QueryKind.Ray, dimension, query.Origin, Quaternion.Identity, Vector3.Normalize(query.Direction), query.MaxDistance, default, query.Filter, one, thread, evaluate: false);
            }

            _batchHits[i] = count > 0 ? one[0] : default;
        }
    }

    /// <summary>Runs a query against the voxels and the bodies of a dimension.</summary>
    /// <param name="kind">A ray, a sweep, or an overlap.</param>
    /// <param name="dimension">The dimension.</param>
    /// <param name="origin">The start of the ray or the sweep, or the place of the overlap.</param>
    /// <param name="rotation">The rotation of the shape.</param>
    /// <param name="direction">The direction, of length 1.</param>
    /// <param name="distance">How far the ray or the sweep goes.</param>
    /// <param name="shape">The shape of a sweep or an overlap.</param>
    /// <param name="filter">What the query meets.</param>
    /// <param name="hits">Receives the hits; its length is the most the query keeps.</param>
    /// <param name="thread">The scratch to use.</param>
    /// <param name="evaluate">Whether a block whose shape depends on its instance, in a chunk no world holds, is evaluated; otherwise it counts as a whole cell.</param>
    /// <returns>The number of hits, closest first for a ray and a sweep.</returns>
    [HotPath]
    private int Query(QueryKind kind, DimensionId dimension, in WorldPosition origin, Quaternion rotation, Vector3 direction, float distance, in QueryShape shape, QueryFilter filter, Span<PhysicsHit> hits, int thread, bool evaluate)
    {
        if (hits.Length == 0)
        {
            return 0;
        }

        var results = new HitList(hits, kind != QueryKind.Overlap);
        float reach = kind == QueryKind.Ray ? 0 : shape.BoundingRadius;
        if (filter.MeetsVoxels && _voxels is not null)
        {
            QueryVoxels(kind, dimension, origin, rotation, direction, distance, shape, reach, ref results, thread, evaluate);
        }

        QueryBodies(kind, dimension, origin, rotation, direction, distance, shape, reach, filter, ref results, thread);
        return results.Finish();
    }

    [HotPath]
    private void QueryBodies(QueryKind kind, DimensionId dimension, in WorldPosition origin, Quaternion rotation, Vector3 direction, float distance, in QueryShape shape, float reach, QueryFilter filter, ref HitList results, int thread)
    {
        if (_worlds.Count == 0)
        {
            return;
        }

        QueryScratch scratch = _queryScratch[thread];
        Span<BackendQueryHit> found = scratch.Backend(results.Capacity);
        WorldPosition middle = origin.Offset(direction * (distance / 2));
        float radius = (distance / 2) + reach;
        int ignored = !filter.Ignored.IsNone && IsAlive(filter.Ignored) ? filter.Ignored.Index : -1;
        for (int w = 0; w < _worlds.Count; w++)
        {
            WorldEntry world = _worlds[w];
            if (world.Region.Dimension != dimension || !Near(world.Region, middle, radius))
            {
                continue;
            }

            var query = new BackendQuery
            {
                Origin = PhysicsFrame.ToLocal(world.Origin, origin),
                Rotation = Quaternion.Normalize(rotation),
                Direction = direction,
                MaxDistance = distance,
                Shape = shape,
                Mask = filter.Mask,
                IgnoredSlot = ignored,
            };
            int count = kind switch
            {
                QueryKind.Ray => world.Backend.RayCast(query, thread, found),
                QueryKind.Sweep => world.Backend.Sweep(query, thread, found),
                _ => world.Backend.Overlap(query, thread, found),
            };
            for (int i = 0; i < count; i++)
            {
                BackendQueryHit hit = found[i];
                if ((uint)hit.Slot >= (uint)_bodyHigh || !_bodies[hit.Slot].Alive)
                {
                    continue;
                }

                var handle = new BodyHandle(hit.Slot, _bodies[hit.Slot].Generation);
                WorldPosition point = kind == QueryKind.Overlap ? origin : PhysicsFrame.ToWorld(world.Origin, hit.Point);
                results.Add(new PhysicsHit(PhysicsHitKind.Body, hit.Distance, point, hit.Normal, handle, null, default, 0));
            }
        }
    }

    /// <summary>Tells whether a sphere of the world may reach the bodies of a region: its box of chunks, grown by a chunk for the bodies near its border.</summary>
    [HotPath]
    private static bool Near(RegionEntry region, in WorldPosition center, float radius)
    {
        long margin = 1 + (long)MathF.Ceiling(radius / WorldPosition.ChunkSize);
        return InRange(center.ChunkX, region.Minimum.X, region.Maximum.X, margin)
            && InRange(center.ChunkY, region.Minimum.Y, region.Maximum.Y, margin)
            && InRange(center.ChunkZ, region.Minimum.Z, region.Maximum.Z, margin);

        static bool InRange(int chunk, int minimum, int maximum, long margin) => chunk >= minimum - margin && chunk <= maximum + margin;
    }

    [HotPath]
    private void QueryVoxels(QueryKind kind, DimensionId dimension, in WorldPosition origin, Quaternion rotation, Vector3 direction, float distance, in QueryShape shape, float reach, ref HitList results, int thread, bool evaluate)
    {
        QueryScratch scratch = _queryScratch[thread];
        List<Grid> grids = scratch.Grids;
        grids.Clear();
        Vector3 end = direction * distance;
        var padding = new Vector3(reach + 1);
        _voxels!.SpatialIndex.Query(new WorldBox(origin, Vector3.Min(Vector3.Zero, end) - padding, Vector3.Max(Vector3.Zero, end) + padding), grids);
        for (int g = 0; g < grids.Count; g++)
        {
            Grid grid = grids[g];
            if (grid.IsDestroyed || grid.Dimension != dimension || grid.Transform.Scale != Vector3.One)
            {
                continue;
            }

            var frame = new QueryFrame(grid, origin, rotation, direction);
            if (!frame.Fits)
            {
                continue;
            }

            switch (kind)
            {
                case QueryKind.Ray:
                    RayInGrid(grid, frame, origin, direction, distance, ref results, evaluate);
                    break;
                case QueryKind.Sweep:
                    SweepInGrid(grid, frame, origin, direction, distance, shape, ref results, evaluate);
                    break;
                default:
                    OverlapInGrid(grid, frame, origin, shape, ref results, evaluate);
                    break;
            }
        }

        grids.Clear();
    }

    /// <summary>Walks the cells of a grid that a ray crosses, nearest first, and adds the blocks it meets.</summary>
    [HotPath]
    private void RayInGrid(Grid grid, in QueryFrame frame, in WorldPosition origin, Vector3 direction, float distance, ref HitList results, bool evaluate)
    {
        Vector3 start = frame.Start;
        Vector3 d = frame.Direction;
        int x = 0;
        int y = 0;
        int z = 0;
        int sx = Math.Sign(d.X);
        int sy = Math.Sign(d.Y);
        int sz = Math.Sign(d.Z);
        float dx = sx == 0 ? float.PositiveInfinity : MathF.Abs(1 / d.X);
        float dy = sy == 0 ? float.PositiveInfinity : MathF.Abs(1 / d.Y);
        float dz = sz == 0 ? float.PositiveInfinity : MathF.Abs(1 / d.Z);
        float tx = sx == 0 ? float.PositiveInfinity : (sx > 0 ? 1 - start.X : start.X) * dx;
        float ty = sy == 0 ? float.PositiveInfinity : (sy > 0 ? 1 - start.Y : start.Y) * dy;
        float tz = sz == 0 ? float.PositiveInfinity : (sz > 0 ? 1 - start.Z : start.Z) * dz;
        float t = 0;
        var cells = new CellReader(this, grid, evaluate);
        int limit = (3 * (int)MathF.Ceiling(distance)) + 4;
        for (int step = 0; step < limit && t <= distance; step++)
        {
            if (!results.WantsBeyond(t))
            {
                return;
            }

            CellRead read = cells.Read(frame, x, y, z, out BlockCoordinate block);
            if (read == CellRead.NotLoaded)
            {
                results.Add(new PhysicsHit(PhysicsHitKind.NotLoaded, t, origin.Offset(direction * t), -direction, default, grid, block, 0));
                return;   // nothing past it is known
            }

            if (read == CellRead.Block)
            {
                Vector3 local = start - new Vector3(x, y, z);
                float best = float.PositiveInfinity;
                Vector3 normal = Vector3.Zero;
                for (int b = 0; b < cells.Count; b++)
                {
                    float near = 0;
                    CollisionBox box = cells.Box(b);
                    if (QueryGeometry.Ray(local, d, box.Minimum, box.Maximum, ref near, distance, out Vector3 face) && near < best)
                    {
                        best = near;
                        normal = face;
                    }
                }

                if (best <= distance)
                {
                    results.Add(new PhysicsHit(PhysicsHitKind.Block, best, origin.Offset(direction * best), frame.ToWorld(normal), default, grid, block, cells.State));
                }
            }

            if (tx <= ty && tx <= tz)
            {
                t = tx;
                tx += dx;
                x += sx;
            }
            else if (ty <= tz)
            {
                t = ty;
                ty += dy;
                y += sy;
            }
            else
            {
                t = tz;
                tz += dz;
                z += sz;
            }
        }
    }

    /// <summary>Tests the cells of a grid along the way of a moving shape, piece by piece, and adds the blocks it meets.</summary>
    [HotPath]
    private void SweepInGrid(Grid grid, in QueryFrame frame, in WorldPosition origin, Vector3 direction, float distance, in QueryShape shape, ref HitList results, bool evaluate)
    {
        var placed = new PlacedShape(shape, frame.Start, frame.Rotation);
        Vector3 d = frame.Direction;
        float piece = MathF.Max(1, 2 * shape.BoundingRadius);
        var cells = new CellReader(this, grid, evaluate);
        Vector3 grow = placed.Extent + new Vector3(QueryGeometry.Contact);
        for (float from = 0; from <= distance; from += piece)
        {
            if (!results.WantsBeyond(from))
            {
                return;
            }

            float to = MathF.Min(distance, from + piece);
            Vector3 a = placed.Center + (d * from);
            Vector3 b = placed.Center + (d * to);
            Vector3 low = Vector3.Min(a, b) - grow;
            Vector3 high = Vector3.Max(a, b) + grow;
            bool stop = false;
            for (int y = (int)MathF.Floor(low.Y); y <= (int)MathF.Floor(high.Y) && !stop; y++)
            {
                for (int z = (int)MathF.Floor(low.Z); z <= (int)MathF.Floor(high.Z) && !stop; z++)
                {
                    for (int x = (int)MathF.Floor(low.X); x <= (int)MathF.Floor(high.X) && !stop; x++)
                    {
                        CellRead read = cells.Read(frame, x, y, z, out BlockCoordinate block);
                        if (read == CellRead.Air)
                        {
                            continue;
                        }

                        var corner = new Vector3(x, y, z);
                        int count = read == CellRead.NotLoaded ? 1 : cells.Count;
                        float best = float.PositiveInfinity;
                        Vector3 normal = Vector3.Zero;
                        Vector3 point = Vector3.Zero;
                        for (int i = 0; i < count; i++)
                        {
                            CollisionBox box = read == CellRead.NotLoaded ? _wholeCell[0] : cells.Box(i);
                            if (QueryGeometry.Sweep(placed, d, distance, corner + box.Minimum, corner + box.Maximum, out float at, out Vector3 face, out Vector3 touch) && at < best)
                            {
                                best = at;
                                normal = face;
                                point = touch;
                            }
                        }

                        if (best <= distance)
                        {
                            PhysicsHitKind hitKind = read == CellRead.NotLoaded ? PhysicsHitKind.NotLoaded : PhysicsHitKind.Block;
                            results.Add(new PhysicsHit(hitKind, best, frame.ToWorld(origin, point), frame.ToWorld(normal), default, grid, block, read == CellRead.NotLoaded ? 0 : cells.State));
                        }
                    }
                }
            }

            if (to >= distance)
            {
                return;
            }
        }
    }

    /// <summary>Tests the cells of a grid under a shape, and adds the blocks it overlaps and the chunks that are not loaded.</summary>
    [HotPath]
    private void OverlapInGrid(Grid grid, in QueryFrame frame, in WorldPosition origin, in QueryShape shape, ref HitList results, bool evaluate)
    {
        var placed = new PlacedShape(shape, frame.Start, frame.Rotation);
        var cells = new CellReader(this, grid, evaluate);
        Vector3 low = placed.Center - placed.Extent;
        Vector3 high = placed.Center + placed.Extent;
        for (int y = (int)MathF.Floor(low.Y); y <= (int)MathF.Floor(high.Y); y++)
        {
            for (int z = (int)MathF.Floor(low.Z); z <= (int)MathF.Floor(high.Z); z++)
            {
                for (int x = (int)MathF.Floor(low.X); x <= (int)MathF.Floor(high.X); x++)
                {
                    CellRead read = cells.Read(frame, x, y, z, out BlockCoordinate block);
                    if (read == CellRead.Air)
                    {
                        continue;
                    }

                    var corner = new Vector3(x, y, z);
                    if (read == CellRead.NotLoaded)
                    {
                        if (QueryGeometry.Overlaps(placed, corner, corner + Vector3.One) && !results.HoldsChunk(grid, VoxelCoordinates.ToChunk(block)))
                        {
                            results.Add(new PhysicsHit(PhysicsHitKind.NotLoaded, 0, origin, Vector3.Zero, default, grid, block, 0));
                        }

                        continue;
                    }

                    for (int i = 0; i < cells.Count; i++)
                    {
                        CollisionBox box = cells.Box(i);
                        if (QueryGeometry.Overlaps(placed, corner + box.Minimum, corner + box.Maximum))
                        {
                            results.Add(new PhysicsHit(PhysicsHitKind.Block, 0, origin, Vector3.Zero, default, grid, block, cells.State));
                            break;
                        }
                    }
                }
            }
        }
    }

    [HotPath]
    private static Vector3 RequireLine(in WorldPosition origin, Vector3 direction, float maxDistance)
    {
        if (!IsFinite(origin.Local) || !IsFinite(direction) || direction.LengthSquared() < 1e-12f)
        {
            throw new ArgumentException("The origin and the direction of a query are finite, and the direction is not zero.", nameof(direction));
        }

        if (!float.IsFinite(maxDistance) || maxDistance < 0)
        {
            throw new ArgumentException($"The length of a query is finite and not negative; here {maxDistance}.", nameof(maxDistance));
        }

        return Vector3.Normalize(direction);
    }

    [HotPath]
    private static void RequireShape(in QueryShape shape, Quaternion rotation)
    {
        bool valid = shape.Kind switch
        {
            ShapeKind.Sphere or ShapeKind.Capsule => shape.Radius > 0,
            ShapeKind.Box => shape.Size.X > 0 && shape.Size.Y > 0 && shape.Size.Z > 0,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("The shape of a query is made by QueryShape.Sphere, Capsule, or Box.", nameof(shape));
        }

        if (!IsFinite(rotation) || rotation.LengthSquared() < 1e-6f)
        {
            throw new ArgumentException("The rotation of a query is finite and not zero.", nameof(rotation));
        }
    }

    /// <summary>What a cell holds, for a query.</summary>
    private enum CellRead : byte
    {
        Air,
        Block,
        NotLoaded,
    }

    /// <summary>The scratch of one thread that queries: the grids it finds, and the tables of hits.</summary>
    private sealed class QueryScratch
    {
        private readonly PhysicsHit[] _one = new PhysicsHit[1];
        private BackendQueryHit[] _backend = new BackendQueryHit[16];

        public List<Grid> Grids { get; } = [];

        [HotPath]
        public Span<PhysicsHit> One() => _one;

        [HotPath]
        public Span<BackendQueryHit> Backend(int capacity)
        {
            if (_backend.Length < capacity)
            {
                Array.Resize(ref _backend, capacity);   // grows to the largest query of the thread, then stays
            }

            return _backend.AsSpan(0, capacity);
        }
    }

    /// <summary>
    /// A grid as a query sees it: the frame of the grid, from a whole cell
    /// near the start of the query (the anchor), in which the start, the
    /// direction, and the rotation of the query are small.
    /// </summary>
    [HotPath]
    private readonly struct QueryFrame
    {
        public QueryFrame(Grid grid, in WorldPosition origin, Quaternion rotation, Vector3 direction)
        {
            WorldTransform transform = grid.Transform;
            Turn = Quaternion.Normalize(transform.Rotation);
            Quaternion inverse = Quaternion.Conjugate(Turn);
            Difference(origin, transform.Position, out double rx, out double ry, out double rz);
            Rotate(inverse, rx, ry, rz, out double gx, out double gy, out double gz);   // the start in the frame of the grid, in double precision a billion blocks away
            double ax = Math.Floor(gx);
            double ay = Math.Floor(gy);
            double az = Math.Floor(gz);
            Fits = Math.Abs(ax) < int.MaxValue - 1e6 && Math.Abs(ay) < int.MaxValue - 1e6 && Math.Abs(az) < int.MaxValue - 1e6;
            AnchorX = Fits ? (long)ax : 0;
            AnchorY = Fits ? (long)ay : 0;
            AnchorZ = Fits ? (long)az : 0;
            Start = new Vector3((float)(gx - ax), (float)(gy - ay), (float)(gz - az));
            Direction = Vector3.Transform(direction, inverse);
            Rotation = Quaternion.Normalize(inverse * rotation);
        }

        /// <summary>Gets whether the anchor fits the coordinates of blocks.</summary>
        public bool Fits { get; }

        public long AnchorX { get; }

        public long AnchorY { get; }

        public long AnchorZ { get; }

        /// <summary>Gets the start of the query, from the anchor.</summary>
        public Vector3 Start { get; }

        /// <summary>Gets the direction in the frame of the grid.</summary>
        public Vector3 Direction { get; }

        /// <summary>Gets the rotation of the shape in the frame of the grid.</summary>
        public Quaternion Rotation { get; }

        /// <summary>Gets the rotation of the grid.</summary>
        public Quaternion Turn { get; }

        /// <summary>Turns a vector of the frame of the grid into the world.</summary>
        [HotPath]
        public Vector3 ToWorld(Vector3 vector) => Vector3.Transform(vector, Turn);

        /// <summary>Gets the place in the world of a point of the frame, from the start of the query.</summary>
        [HotPath]
        public WorldPosition ToWorld(in WorldPosition origin, Vector3 point) => origin.Offset(Vector3.Transform(point - Start, Turn));
    }

    /// <summary>Reads the cells of one grid for a query, keeping the last chunk it read.</summary>
    [HotPath]
    private ref struct CellReader
    {
        private readonly PhysicsSimulation _simulation;
        private readonly Grid _grid;
        private readonly bool _evaluate;
        private Chunk? _chunk;
        private ChunkCoordinate _held;
        private bool _holds;
        private ReadOnlySpan<CollisionBox> _boxes;
        private CollisionShape? _evaluated;

        public CellReader(PhysicsSimulation simulation, Grid grid, bool evaluate)
        {
            _simulation = simulation;
            _grid = grid;
            _evaluate = evaluate;
        }

        /// <summary>Gets the state of the block that <see cref="Read"/> found.</summary>
        public uint State { get; private set; }

        /// <summary>Gets the number of collision boxes of the block that <see cref="Read"/> found.</summary>
        public readonly int Count => _evaluated?.Count ?? _boxes.Length;

        /// <summary>Gets a collision box of the block that <see cref="Read"/> found, in the frame of its cell.</summary>
        [HotPath]
        public readonly CollisionBox Box(int index) => _evaluated is { } shape ? shape[index] : _boxes[index];

        /// <summary>Reads a cell, given from the anchor of the frame.</summary>
        [HotPath]
        public CellRead Read(in QueryFrame frame, int x, int y, int z, out BlockCoordinate block)
        {
            long bx = frame.AnchorX + x;
            long by = frame.AnchorY + y;
            long bz = frame.AnchorZ + z;
            block = default;
            if (bx is < int.MinValue or > int.MaxValue || by is < int.MinValue or > int.MaxValue || bz is < int.MinValue or > int.MaxValue)
            {
                return CellRead.Air;
            }

            block = new BlockCoordinate((int)bx, (int)by, (int)bz);
            if (_grid.Bounds is { } bounds && !bounds.Contains(block))
            {
                return CellRead.Air;
            }

            ChunkCoordinate coordinate = VoxelCoordinates.ToChunk(block);
            if (!_holds || coordinate != _held)
            {
                _held = coordinate;
                _holds = true;
                if (!_grid.Storage.TryGetChunk(coordinate, out _chunk))
                {
                    _chunk = null;
                }
            }

            if (_chunk is null)
            {
                return _grid.Residency != GridResidency.Kept && _grid.Storage.Overlaps(coordinate) ? CellRead.NotLoaded : CellRead.Air;
            }

            int cell = VoxelCoordinates.ToIndex(block);
            uint state = _chunk.Palette.Get(cell);
            if (state == 0)
            {
                return CellRead.Air;
            }

            State = state;
            _evaluated = null;
            _boxes = default;
            ref readonly StateShape shape = ref _simulation._voxelShapes!.Get(state);
            if (!shape.Instance)
            {
                _boxes = _simulation._voxelShapes.Boxes(shape);
            }
            else if (_simulation._cells.TryGetValue(new VoxelChunkKey(_grid, coordinate), out VoxelChunkCells? cells))
            {
                _boxes = cells.GetBoxes(cell);
            }
            else if (_evaluate)
            {
                _evaluated = _simulation._shapeRegistry!.Get(state, _chunk.GetBlockDataView(cell)).Collision;   // no world holds the chunk: evaluated now
            }
            else
            {
                _boxes = _simulation._wholeCell;   // a batch does not call the shapes of other modules from its jobs
            }

            return Count > 0 ? CellRead.Block : CellRead.Air;
        }
    }

    /// <summary>
    /// The hits of a query: the closest ones up to its capacity, one per body
    /// and per block, cut after the first chunk that is not loaded.
    /// </summary>
    [HotPath]
    private ref struct HitList
    {
        private readonly Span<PhysicsHit> _hits;
        private readonly bool _ordered;
        private int _count;

        public HitList(Span<PhysicsHit> hits, bool ordered)
        {
            _hits = hits;
            _ordered = ordered;
            _count = 0;
        }

        public readonly int Capacity => _hits.Length;

        /// <summary>Tells whether a hit farther than a distance could still be kept.</summary>
        [HotPath]
        public readonly bool WantsBeyond(float distance)
        {
            if (!_ordered || _count < _hits.Length)
            {
                return true;
            }

            for (int i = 0; i < _count; i++)
            {
                if (_hits[i].Distance > distance)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Tells whether a chunk that is not loaded is already kept.</summary>
        [HotPath]
        public readonly bool HoldsChunk(Grid grid, ChunkCoordinate chunk)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_hits[i].Kind == PhysicsHitKind.NotLoaded && _hits[i].Grid == grid && _hits[i].Chunk == chunk)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Keeps a hit: once per body or block, at its closest, among the closest of the capacity.</summary>
        [HotPath]
        public void Add(in PhysicsHit hit)
        {
            for (int i = 0; i < _count; i++)
            {
                ref PhysicsHit kept = ref _hits[i];
                if (kept.Kind == hit.Kind && kept.Body == hit.Body && kept.Grid == hit.Grid && kept.Block == hit.Block)
                {
                    if (hit.Distance < kept.Distance)
                    {
                        kept = hit;
                    }

                    return;
                }
            }

            if (_count < _hits.Length)
            {
                _hits[_count++] = hit;
                return;
            }

            if (!_ordered)
            {
                return;
            }

            int farthest = 0;
            for (int i = 1; i < _count; i++)
            {
                if (_hits[i].Distance > _hits[farthest].Distance)
                {
                    farthest = i;
                }
            }

            if (hit.Distance < _hits[farthest].Distance)
            {
                _hits[farthest] = hit;
            }
        }

        /// <summary>Orders the hits, closest first, drops those past the first chunk that is not loaded, and gives their number.</summary>
        [HotPath]
        public int Finish()
        {
            if (!_ordered)
            {
                return _count;
            }

            for (int i = 1; i < _count; i++)
            {
                PhysicsHit hit = _hits[i];
                int j = i - 1;
                while (j >= 0 && Before(hit, _hits[j]))
                {
                    _hits[j + 1] = _hits[j];
                    j--;
                }

                _hits[j + 1] = hit;
            }

            for (int i = 0; i < _count; i++)
            {
                if (_hits[i].Kind == PhysicsHitKind.NotLoaded)
                {
                    _hits.Slice(i + 1, _count - i - 1).Clear();
                    _count = i + 1;
                    break;
                }
            }

            return _count;
        }

        /// <summary>Orders two hits: the closer first, and a chunk that is not loaded before what lies at its distance.</summary>
        [HotPath]
        private static bool Before(in PhysicsHit a, in PhysicsHit b) =>
            a.Distance < b.Distance || (a.Distance == b.Distance && a.Kind == PhysicsHitKind.NotLoaded && b.Kind != PhysicsHitKind.NotLoaded);
    }

    /// <summary>Runs one slice of a batch per index, on the scratch of that index; a failure is kept for the loop thread.</summary>
    [HotPath]
    private readonly struct BatchJob(PhysicsSimulation simulation, DimensionId dimension, int count, int slice, bool sweeps) : IJobFor
    {
        [HotPath]
        public void Execute(int index)
        {
            try
            {
                int start = index * slice;
                simulation.RunSlice(dimension, start, Math.Min(count, start + slice), index, sweeps);
            }
            catch (Exception exception)
            {
                simulation._batchFailures[index] = exception;
            }
        }
    }
}
