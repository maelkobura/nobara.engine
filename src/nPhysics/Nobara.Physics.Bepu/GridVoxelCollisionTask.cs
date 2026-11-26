// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu;

/// <summary>
/// The collision of the voxels of a grid body with other voxels: a chunk of a
/// grid that is not a body, or another grid body. The surface cells of the
/// tested grid (<see cref="GridVoxelCollision"/>) that lie where the other
/// voxels are, widened by the speculative margin of the pair, are tested
/// against the boxes of the other voxels: each collision box of a cell
/// against each box it may touch, by the box tests of BEPUphysics, in
/// <see cref="VoxelContactMode.Blocks"/>; each corner of the box of a cell
/// against each box it may touch, computed here, in
/// <see cref="VoxelContactMode.Corners"/>. The non-convex reduction of
/// BEPUphysics merges the contacts of the pair.
/// </summary>
/// <remarks>
/// <para>
/// A pair spends at most the budget of its tested grid. Cells whose middle
/// lies in a box of the other voxels, the deepest, go first; the others
/// follow, from the cursor of the grid, which moves past the cells tested
/// each time the budget runs out, so that the cells left are tested first at
/// a later step.
/// </para>
/// <para>
/// A corner is a child of index <c>~(chunk slot × stride + cell × 16 + corner)</c>,
/// negative, so that the callbacks tell it from a box.
/// </para>
/// </remarks>
/// <typeparam name="TOther">The shape of the other voxels.</typeparam>
internal sealed unsafe class GridVoxelCollisionTask<TOther> : CollisionTask
    where TOther : unmanaged, IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
{
    private const int Stride = VoxelChunkCells.MaxBoxesPerCell;
    private const int Size = VoxelCoordinates.ChunkSize;

    /// <summary>The smallest margin around the voxels, in blocks, so that a grid at rest finds the voxels it rests on.</summary>
    private const float MinimumMargin = 0.02f;

    /// <summary>Creates the task.</summary>
    public GridVoxelCollisionTask()
    {
        BatchSize = 16;
        ShapeTypeIndexA = VoxelGridShape.Id;
        ShapeTypeIndexB = TOther.TypeId;
        SubtaskGenerator = true;
        PairType = CollisionTaskPairType.BoundsTestedPair;
    }

    /// <inheritdoc/>
    public override void ExecuteBatch<TCallbacks>(ref UntypedList batch, ref CollisionBatcher<TCallbacks> batcher)
    {
        Buffer<BoundsTestedPair> pairs = batch.Buffer.As<BoundsTestedPair>();
        for (int i = 0; i < batch.Count; i++)
        {
            Collide(ref pairs[i], ref batcher);
        }
    }

    /// <summary>Tests one pair; the grid of the first shape, and the other voxels, of the second.</summary>
    [HotPath]
    private static void Collide<TCallbacks>(ref BoundsTestedPair pair, ref CollisionBatcher<TCallbacks> batcher)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref VoxelGridShape a = ref Unsafe.AsRef<VoxelGridShape>(pair.A);
        ref TOther b = ref Unsafe.AsRef<TOther>(pair.B);
        GridVoxelCollision? first = VoxelGridShape.CellsOf(a.Cells).Collision;
        GridVoxelCollision? second = typeof(TOther) == typeof(VoxelGridShape)
            ? VoxelGridShape.CellsOf(Unsafe.As<TOther, VoxelGridShape>(ref b).Cells).Collision
            : null;
        if (first is null && second is null)
        {
            batcher.ProcessEmptyResult(ref pair.Continuation);
            return;
        }

        bool testA = second is null || (first is not null && first.SurfaceCount <= second.SurfaceCount);
        VoxelContactMode mode = first?.Mode == VoxelContactMode.Corners || second?.Mode == VoxelContactMode.Corners ? VoxelContactMode.Corners : VoxelContactMode.Blocks;
        int budget = Smaller(first?.PairBudget ?? 0, second?.PairBudget ?? 0);
        if (testA)
        {
            Collide(ref pair, ref batcher, ref a, ref b, first!, testA: true, mode, budget);
        }
        else
        {
            Collide(ref pair, ref batcher, ref Unsafe.As<TOther, VoxelGridShape>(ref b), ref a, second!, testA: false, mode, budget);
        }
    }

    /// <summary>Tests the surface cells of grid <paramref name="tested"/> against the voxels of <paramref name="other"/>.</summary>
    [HotPath]
    private static void Collide<TCallbacks, TO>(ref BoundsTestedPair pair, ref CollisionBatcher<TCallbacks> batcher, ref VoxelGridShape tested, ref TO other, GridVoxelCollision collision, bool testA, VoxelContactMode mode, int budget)
        where TCallbacks : struct, ICollisionCallbacks
        where TO : unmanaged, IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
    {
        // Poses in the frame of the pair: the first shape at the origin.
        Vector3 positionT = testA ? Vector3.Zero : pair.OffsetB;
        Vector3 positionO = testA ? pair.OffsetB : Vector3.Zero;
        Quaternion rotationT = testA ? pair.OrientationA : pair.OrientationB;
        Quaternion rotationO = testA ? pair.OrientationB : pair.OrientationA;
        var frame = new Frame(rotationT, rotationO, positionT - positionO);
        float margin = MathF.Max(pair.SpeculativeMargin, MinimumMargin);
        VoxelGridCells cells = VoxelGridShape.CellsOf(tested.Cells);

        // Where the other voxels are, in the frame of the tested grid.
        other.GetLocalBounds(out Vector3 otherMin, out Vector3 otherMax);
        frame.BoundsInTested(otherMin, otherMax, out Vector3 low, out Vector3 high);
        low = Vector3.Max(low + tested.Origin - new Vector3(margin), cells.Minimum);
        high = Vector3.Min(high + tested.Origin + new Vector3(margin), cells.Maximum);
        if (cells.IsEmpty || low.X > high.X || low.Y > high.Y || low.Z > high.Z)
        {
            collision.Count(0, 0);
            batcher.ProcessEmptyResult(ref pair.Continuation);
            return;
        }

        BufferPool pool = batcher.Pool;
        var keys = new QuickList<int>(16, pool);
        var ranks = new QuickList<int>(16, pool);
        FindCells(collision, cells, low, high, ref keys, pool);
        if (budget > 0)
        {
            // Many cells: the deepest go first. Few: one rank. Either way spread from the cursor, so that the cells a budget left come first.
            bool rank = keys.Count * RankedShare > budget;
            for (int i = 0; i < keys.Count; i++)
            {
                int slot = keys[i] / Chunk.Volume;
                Vector3 corner = cells.CornerOf(slot) + VoxelChunkShape.CornerOf(keys[i] - (slot * Chunk.Volume)) - tested.Origin;
                ranks.Allocate(pool) = rank ? Rank(ref other, frame, corner, margin) : 0;
            }

            Order(ref keys, ref ranks, collision.Cursor, pool);
        }

        var subpairs = new QuickList<Subpair>(Math.Max(16, keys.Count * 4), pool);
        var found = new QuickList<int>(32, pool);
        int tests = 0;
        int deferred = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            if (budget > 0 && tests >= budget)
            {
                deferred = keys.Count - i;
                collision.Cursor += i;   // the cells left go first next time
                break;
            }

            tests += mode == VoxelContactMode.Blocks
                ? TestBoxes(keys[i], cells, tested.Origin, ref other, frame, margin, ref subpairs, ref found, ref batcher)
                : TestCorners(keys[i], cells, tested.Origin, ref other, frame, margin, ref subpairs, ref found, ref batcher);
        }

        collision.Count(tests, deferred);
        Emit(ref pair, ref batcher, ref tested, ref other, frame, positionT, positionO, testA, ref subpairs);
        found.Dispose(pool);
        subpairs.Dispose(pool);
        keys.Dispose(pool);
        ranks.Dispose(pool);
    }

    /// <summary>Lists the surface cells of the tested grid with a collision box in the given bounds of the grid.</summary>
    [HotPath]
    private static void FindCells(GridVoxelCollision collision, VoxelGridCells cells, Vector3 low, Vector3 high, ref QuickList<int> keys, BufferPool pool)
    {
        int x0 = (int)MathF.Floor(low.X);
        int y0 = (int)MathF.Floor(low.Y);
        int z0 = (int)MathF.Floor(low.Z);
        int x1 = (int)MathF.Floor(high.X);
        int y1 = (int)MathF.Floor(high.Y);
        int z1 = (int)MathF.Floor(high.Z);
        for (int cy = FloorDiv(y0); cy <= FloorDiv(y1); cy++)
        {
            for (int cz = FloorDiv(z0); cz <= FloorDiv(z1); cz++)
            {
                for (int cx = FloorDiv(x0); cx <= FloorDiv(x1); cx++)
                {
                    var chunk = new ChunkCoordinate(cx, cy, cz);
                    ReadOnlySpan<ulong> surface = collision.GetSurface(chunk);
                    int slot = surface.IsEmpty ? -1 : cells.SlotOf(chunk);
                    if (slot < 0 || cells.GetChunk(slot) is not { } chunkCells)
                    {
                        continue;
                    }

                    int bx = cx * Size;
                    int by = cy * Size;
                    int bz = cz * Size;
                    int lx0 = Math.Max(x0 - bx, 0);
                    int ly0 = Math.Max(y0 - by, 0);
                    int lz0 = Math.Max(z0 - bz, 0);
                    int lx1 = Math.Min(x1 - bx, Size - 1);
                    int ly1 = Math.Min(y1 - by, Size - 1);
                    int lz1 = Math.Min(z1 - bz, Size - 1);
                    for (int y = ly0; y <= ly1; y++)
                    {
                        for (int z = lz0; z <= lz1; z++)
                        {
                            for (int x = lx0; x <= lx1; x++)
                            {
                                int cell = VoxelChunkShape.IndexOf(x, y, z);
                                if ((surface[cell >> 6] & (1UL << (cell & 63))) == 0 || chunkCells.GetBoxes(cell).IsEmpty)
                                {
                                    continue;
                                }

                                keys.Allocate(pool) = (slot * Chunk.Volume) + cell;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>The number of ranks a cell can have.</summary>
    private const int Ranks = 10;

    /// <summary>A pair ranks its cells once they number more than its budget divided by this: about the tests that a cell makes.</summary>
    private const int RankedShare = 4;

    /// <summary>Counts the corners of a cell, pushed out by the margin, and its middle, that lie in the other voxels.</summary>
    [HotPath]
    private static int Rank<TO>(ref TO other, in Frame frame, Vector3 corner, float margin)
        where TO : unmanaged, IVoxelShape
    {
        int rank = other.IsSolidAt(frame.ToOther(corner + new Vector3(0.5f))) ? 1 : 0;
        for (int c = 0; c < 8; c++)
        {
            var point = new Vector3((c & 1) != 0 ? 1 + margin : -margin, (c & 2) != 0 ? 1 + margin : -margin, (c & 4) != 0 ? 1 + margin : -margin);
            if (other.IsSolidAt(frame.ToOther(corner + point)))
            {
                rank++;
            }
        }

        return rank;
    }

    /// <summary>Orders the cells by rank, the highest first; within a rank, spread from the cursor, so that the cells a budget left come first at a later step.</summary>
    [HotPath]
    private static void Order(ref QuickList<int> keys, ref QuickList<int> ranks, int cursor, BufferPool pool)
    {
        Span<int> starts = stackalloc int[Ranks + 1];
        Span<int> next = stackalloc int[Ranks];
        starts.Clear();
        for (int i = 0; i < ranks.Count; i++)
        {
            starts[Ranks - 1 - ranks[i]]++;   // the highest rank first
        }

        int at = 0;
        for (int r = 0; r < Ranks; r++)
        {
            int count = starts[r];
            starts[r] = at;
            next[r] = at;
            at += count;
        }

        starts[Ranks] = at;
        var ordered = new QuickList<int>(Math.Max(keys.Count, 1), pool);
        ordered.Count = keys.Count;
        for (int i = 0; i < keys.Count; i++)
        {
            ordered[next[Ranks - 1 - ranks[i]]++] = keys[i];
        }

        // Within a rank, the cells go by a step prime with their number, from the cursor: a budget that keeps a few spreads them over the whole area.
        int written = 0;
        for (int r = 0; r < Ranks; r++)
        {
            int length = starts[r + 1] - starts[r];
            int step = Spread(length);
            int first = length > 0 ? (int)((uint)cursor % (uint)length) : 0;
            for (int k = 0; k < length; k++)
            {
                keys[written++] = ordered[starts[r] + (int)(((long)first + ((long)k * step)) % length)];
            }
        }

        ordered.Dispose(pool);
    }

    /// <summary>Gets a step prime with a number of cells, near a fraction of it that is not small, so that the first cells are far apart.</summary>
    [HotPath]
    private static int Spread(int length)
    {
        if (length <= 2)
        {
            return 1;
        }

        int step = Math.Max((int)(length * 0.618f), 1);
        while (Gcd(step, length) != 1)
        {
            step++;
        }

        return step;
    }

    [HotPath]
    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    /// <summary>Lists each collision box of a surface cell against each box of the other voxels it may touch.</summary>
    /// <returns>The tests listed.</returns>
    [HotPath]
    private static int TestBoxes<TCallbacks, TO>(int key, VoxelGridCells cells, Vector3 origin, ref TO other, in Frame frame, float margin, ref QuickList<Subpair> subpairs, ref QuickList<int> found, ref CollisionBatcher<TCallbacks> batcher)
        where TCallbacks : struct, ICollisionCallbacks
        where TO : unmanaged, IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
    {
        int slot = key / Chunk.Volume;
        int cell = key - (slot * Chunk.Volume);
        ReadOnlySpan<CollisionBox> boxes = cells.GetChunk(slot)!.GetBoxes(cell);
        Vector3 corner = cells.CornerOf(slot) + VoxelChunkShape.CornerOf(cell) - origin;
        int tests = 0;
        for (int box = 0; box < boxes.Length; box++)
        {
            Vector3 center = frame.ToOther(corner + ((boxes[box].Minimum + boxes[box].Maximum) * 0.5f));
            Vector3 half = frame.HalfInOther((boxes[box].Maximum - boxes[box].Minimum) * 0.5f) + new Vector3(margin);
            found.Count = 0;
            var collector = new Collector(ref found, batcher.Pool);
            other.FindLocalOverlaps(center - half, center + half, batcher.Pool, batcher.Shapes, ref collector);
            int child = (slot * VoxelGridCells.ChunkStride) + (cell * Stride) + box;
            for (int i = 0; i < found.Count; i++)
            {
                subpairs.Allocate(batcher.Pool) = new Subpair { Tested = child, Other = found[i] };
            }

            tests += found.Count;
        }

        return tests;
    }

    /// <summary>Tests each corner of the box of a surface cell against each box of the other voxels it may touch.</summary>
    /// <returns>The tests made.</returns>
    [HotPath]
    private static int TestCorners<TCallbacks, TO>(int key, VoxelGridCells cells, Vector3 origin, ref TO other, in Frame frame, float margin, ref QuickList<Subpair> subpairs, ref QuickList<int> found, ref CollisionBatcher<TCallbacks> batcher)
        where TCallbacks : struct, ICollisionCallbacks
        where TO : unmanaged, IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
    {
        int slot = key / Chunk.Volume;
        int cell = key - (slot * Chunk.Volume);
        ReadOnlySpan<CollisionBox> boxes = cells.GetChunk(slot)!.GetBoxes(cell);
        Vector3 low = boxes[0].Minimum;
        Vector3 high = boxes[0].Maximum;
        for (int i = 1; i < boxes.Length; i++)
        {
            low = Vector3.Min(low, boxes[i].Minimum);
            high = Vector3.Max(high, boxes[i].Maximum);
        }

        Vector3 cellCorner = cells.CornerOf(slot) + VoxelChunkShape.CornerOf(cell) - origin;
        var reach = new Vector3(margin);
        int tests = 0;
        for (int c = 0; c < 8; c++)
        {
            Vector3 local = cellCorner + new Vector3((c & 1) != 0 ? high.X : low.X, (c & 2) != 0 ? high.Y : low.Y, (c & 4) != 0 ? high.Z : low.Z);
            Vector3 point = frame.ToOther(local);
            found.Count = 0;
            var collector = new Collector(ref found, batcher.Pool);
            other.FindLocalOverlaps(point - reach, point + reach, batcher.Pool, batcher.Shapes, ref collector);
            tests += found.Count;
            for (int i = 0; i < found.Count; i++)
            {
                if (PointContact(ref other, found[i], point, margin, out Vector3 normal, out float depth))
                {
                    subpairs.Allocate(batcher.Pool) = new Subpair
                    {
                        Tested = ~((slot * VoxelGridCells.ChunkStride) + (cell * Stride) + c),
                        Other = found[i],
                        Corner = local,
                        Normal = frame.NormalToWorld(normal),
                        Depth = depth,
                        Feature = c,
                    };
                }
            }
        }

        return tests;
    }

    /// <summary>
    /// The contact of a point with a box of the other voxels, in the frame of
    /// the other voxels: inside, out of the nearest face that is not covered
    /// by the neighbor cell; outside but within the margin, from the nearest
    /// point of the box.
    /// </summary>
    /// <param name="other">The other voxels.</param>
    /// <param name="child">The box of the other voxels.</param>
    /// <param name="point">The point, in the frame of the other voxels.</param>
    /// <param name="margin">How far apart a contact is still made.</param>
    /// <param name="normal">The normal, from the box toward the point.</param>
    /// <param name="depth">The depth; negative while apart.</param>
    /// <returns><see langword="true"/> for a contact.</returns>
    [HotPath]
    private static bool PointContact<TO>(ref TO other, int child, Vector3 point, float margin, out Vector3 normal, out float depth)
        where TO : unmanaged, IVoxelShape
    {
        other.GetChild(child, out Box box, out Vector3 center);
        var half = new Vector3(box.HalfWidth, box.HalfHeight, box.HalfLength);
        Vector3 offset = point - center;
        Vector3 nearest = Vector3.Clamp(offset, -half, half);
        Vector3 apart = offset - nearest;
        float distance = apart.Length();
        if (distance > 0)
        {
            normal = apart / distance;
            depth = -distance;
            return distance <= margin && !other.IsInnerFace(child, normal);
        }

        normal = default;
        depth = float.MaxValue;
        Vector3 inside = half - Vector3.Abs(offset);
        for (int axis = 0; axis < 3; axis++)
        {
            float penetration = axis == 0 ? inside.X : axis == 1 ? inside.Y : inside.Z;
            float side = (axis == 0 ? offset.X : axis == 1 ? offset.Y : offset.Z) >= 0 ? 1 : -1;
            Vector3 face = axis == 0 ? new Vector3(side, 0, 0) : axis == 1 ? new Vector3(0, side, 0) : new Vector3(0, 0, side);
            if (penetration < depth && !other.IsInnerFace(child, face))
            {
                depth = penetration;
                normal = face;
            }
        }

        return depth != float.MaxValue;
    }

    /// <summary>Hands every subpair of a pair to the batcher, as the children of one non-convex reduction, in the order of the pair BEPUphysics was given.</summary>
    [HotPath]
    private static void Emit<TCallbacks, TO>(ref BoundsTestedPair pair, ref CollisionBatcher<TCallbacks> batcher, ref VoxelGridShape tested, ref TO other, in Frame frame, Vector3 positionT, Vector3 positionO, bool testA, ref QuickList<Subpair> subpairs)
        where TCallbacks : struct, ICollisionCallbacks
        where TO : unmanaged, IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
    {
        int count = Math.Min(subpairs.Count, PairContinuation.ExclusiveMaximumChildIndex - 1);
        if (count == 0)
        {
            batcher.ProcessEmptyResult(ref pair.Continuation);
            return;
        }

        bool flip = pair.FlipMask < 0;
        bool testedIsFirst = testA != flip;   // the tested grid is collidable A of the pair as it was given
        ref NonconvexReduction continuation = ref batcher.NonconvexReductions.CreateContinuation(count, batcher.Pool, out int continuationIndex);
        for (int k = 0; k < count; k++)
        {
            ref Subpair subpair = ref subpairs[k];
            other.GetChild(subpair.Other, out Box otherBox, out Vector3 otherCenter);
            Vector3 otherOffset = Vector3.Transform(otherCenter, frame.RotationO);   // from the center of the other collidable
            Vector3 testedOffset;
            Box testedBox = default;
            if (subpair.Tested >= 0)
            {
                tested.GetChild(subpair.Tested, out testedBox, out Vector3 testedCenter);
                testedOffset = Vector3.Transform(testedCenter, frame.RotationT);
            }
            else
            {
                testedOffset = Vector3.Transform(subpair.Corner, frame.RotationT);
            }

            int childA = testedIsFirst ? subpair.Tested : subpair.Other;
            int childB = testedIsFirst ? subpair.Other : subpair.Tested;
            ref NonconvexReductionChild child = ref continuation.Children[k];
            child.ChildIndexA = childA;
            child.ChildIndexB = childB;
            child.OffsetA = testedIsFirst ? testedOffset : otherOffset;
            child.OffsetB = testedIsFirst ? otherOffset : testedOffset;
            var report = new PairContinuation(pair.Continuation.PairId, childA, childB, CollisionContinuationType.NonconvexReduction, continuationIndex, k);
            if (!batcher.Callbacks.AllowCollisionTesting(pair.Continuation.PairId, childA, childB))
            {
                batcher.ProcessUntestedSubpairConvexResult(ref report);
                continue;
            }

            // The centers of the two children, in the frame of the pair as BEPUphysics holds it.
            Vector3 testedAt = positionT + testedOffset;
            Vector3 otherAt = positionO + otherOffset;
            if (subpair.Tested >= 0)
            {
                // Each pair of boxes goes to the box tests of BEPUphysics, A first as the pair was given.
                batcher.CacheShapeB(Box.Id, Box.Id, Unsafe.AsPointer(ref testedBox), Unsafe.SizeOf<Box>(), out void* testedData);
                batcher.CacheShapeB(Box.Id, Box.Id, Unsafe.AsPointer(ref otherBox), Unsafe.SizeOf<Box>(), out void* otherData);
                if (testedIsFirst)
                {
                    batcher.AddDirectly(Box.Id, Box.Id, testedData, otherData, otherAt - testedAt, frame.RotationT, frame.RotationO, pair.SpeculativeMargin, report);
                }
                else
                {
                    batcher.AddDirectly(Box.Id, Box.Id, otherData, testedData, testedAt - otherAt, frame.RotationO, frame.RotationT, pair.SpeculativeMargin, report);
                }
            }
            else
            {
                // A corner: one contact, at the corner, whose normal goes from collidable B to collidable A.
                Vector3 firstAt = testedIsFirst ? testedAt : otherAt;
                Vector3 secondAt = testedIsFirst ? otherAt : testedAt;
                ConvexContactManifold manifold = default;
                manifold.OffsetB = secondAt - firstAt;
                manifold.Count = 1;
                manifold.Normal = testedIsFirst ? subpair.Normal : -subpair.Normal;
                manifold.Contact0.Offset = testedAt - firstAt;
                manifold.Contact0.Depth = subpair.Depth;
                manifold.Contact0.FeatureId = subpair.Feature;
                batcher.ProcessConvexResult(ref manifold, ref report);
            }
        }
    }

    [HotPath]
    private static int Smaller(int first, int second) => first == 0 ? second : second == 0 ? first : Math.Min(first, second);

    [HotPath]
    private static int FloorDiv(int value) => value >= 0 ? value / Size : ((value + 1) / Size) - 1;

    /// <summary>A test: a box or a corner of the tested grid, against a box of the other voxels.</summary>
    private struct Subpair
    {
        /// <summary>The child of the tested grid: a box, or the bitwise complement of a corner.</summary>
        public int Tested;

        /// <summary>The child of the other voxels.</summary>
        public int Other;

        /// <summary>For a corner: where it lies in the frame of the tested shape.</summary>
        public Vector3 Corner;

        /// <summary>For a corner: the normal along the axes of the world, from the other voxels toward the tested grid.</summary>
        public Vector3 Normal;

        /// <summary>For a corner: the depth.</summary>
        public float Depth;

        /// <summary>For a corner: its number, 0 to 7.</summary>
        public int Feature;
    }

    /// <summary>The change from the frame of the tested shape to the frame of the other voxels.</summary>
    private readonly struct Frame
    {
        public readonly Quaternion RotationT;
        public readonly Quaternion RotationO;
        private readonly Quaternion _toOther;
        private readonly Vector3 _offset;
        private readonly Matrix4x4 _absolute;

        /// <summary>Creates the change of frame.</summary>
        /// <param name="rotationT">The orientation of the tested shape.</param>
        /// <param name="rotationO">The orientation of the other voxels.</param>
        /// <param name="offset">The position of the tested shape from the other voxels, along the axes of the world.</param>
        [HotPath]
        public Frame(Quaternion rotationT, Quaternion rotationO, Vector3 offset)
        {
            RotationT = rotationT;
            RotationO = rotationO;
            Quaternion inverse = Quaternion.Conjugate(rotationO);
            _toOther = Quaternion.Normalize(Quaternion.Concatenate(rotationT, inverse));
            _offset = Vector3.Transform(offset, inverse);
            Matrix4x4 m = Matrix4x4.CreateFromQuaternion(_toOther);
            _absolute = new Matrix4x4(
                MathF.Abs(m.M11), MathF.Abs(m.M12), MathF.Abs(m.M13), 0,
                MathF.Abs(m.M21), MathF.Abs(m.M22), MathF.Abs(m.M23), 0,
                MathF.Abs(m.M31), MathF.Abs(m.M32), MathF.Abs(m.M33), 0,
                0, 0, 0, 1);
        }

        /// <summary>Takes a point of the tested shape to the frame of the other voxels.</summary>
        [HotPath]
        public Vector3 ToOther(Vector3 point) => Vector3.Transform(point, _toOther) + _offset;

        /// <summary>Gets the half widths, in the frame of the other voxels, of the box that holds a box of the tested shape.</summary>
        [HotPath]
        public Vector3 HalfInOther(Vector3 half) => Vector3.TransformNormal(half, _absolute);

        /// <summary>Takes a normal of the frame of the other voxels to the axes of the world.</summary>
        [HotPath]
        public Vector3 NormalToWorld(Vector3 normal) => Vector3.Transform(normal, RotationO);

        /// <summary>Gets the bounds, in the frame of the tested shape, of a box of the frame of the other voxels.</summary>
        [HotPath]
        public void BoundsInTested(Vector3 min, Vector3 max, out Vector3 low, out Vector3 high)
        {
            Quaternion back = Quaternion.Conjugate(_toOther);
            Vector3 center = Vector3.Transform(((min + max) * 0.5f) - _offset, back);
            Matrix4x4 m = Matrix4x4.CreateFromQuaternion(back);
            var absolute = new Matrix4x4(
                MathF.Abs(m.M11), MathF.Abs(m.M12), MathF.Abs(m.M13), 0,
                MathF.Abs(m.M21), MathF.Abs(m.M22), MathF.Abs(m.M23), 0,
                MathF.Abs(m.M31), MathF.Abs(m.M32), MathF.Abs(m.M33), 0,
                0, 0, 0, 1);
            Vector3 half = Vector3.TransformNormal((max - min) * 0.5f, absolute);
            low = center - half;
            high = center + half;
        }
    }

    /// <summary>Adds each child found to a list.</summary>
    private struct Collector : IBreakableForEach<int>
    {
        private readonly QuickList<int>* _list;
        private readonly BufferPool _pool;

        [HotPath]
        public Collector(ref QuickList<int> list, BufferPool pool)
        {
            _list = (QuickList<int>*)Unsafe.AsPointer(ref list);
            _pool = pool;
        }

        /// <inheritdoc/>
        [HotPath]
        public readonly bool LoopBody(int child)
        {
            _list->Allocate(_pool) = child;
            return true;
        }
    }
}
