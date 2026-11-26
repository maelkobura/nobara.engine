// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu;

/// <summary>
/// The collidable of one chunk of voxels: a compound of boxes whose children
/// are the collision boxes of the cells of the chunk, found by reading the
/// cells that a query covers straight from the chunk, through the
/// <see cref="VoxelChunkCells"/> of nPhysics, with no tree and no copy. A
/// block placed or broken is seen at the next step, at no cost.
/// </summary>
/// <remarks>
/// <para>
/// The index of a child is the local index of its cell times
/// <see cref="VoxelChunkCells.MaxBoxesPerCell"/>, plus the index of the box in
/// the cell. The shape sits at the corner of the chunk: cell (x, y, z) spans
/// from (x, y, z) to (x + 1, y + 1, z + 1) in the frame of the shape.
/// </para>
/// <para>
/// A shape of BEPUphysics is an unmanaged value, so the shape holds a handle to
/// the cells, which the world that adds the shape owns and frees. The threads
/// of the solver read them while the world steps: the chunk does not change
/// during a step.
/// </para>
/// </remarks>
internal unsafe struct VoxelChunkShape : IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
{
    /// <summary>The type identifier of the shape, outside the range of the shapes of BEPUphysics.</summary>
    public const int Id = 24;

    /// <summary>The number of child indices of one cell.</summary>
    public const int Stride = VoxelChunkCells.MaxBoxesPerCell;

    private const int Size = VoxelCoordinates.ChunkSize;

    /// <summary>The handle of the cells, from <see cref="GCHandle.ToIntPtr"/>.</summary>
    public nint Cells;

    /// <summary>Creates the shape of a chunk.</summary>
    /// <param name="cells">The handle of the cells of the chunk.</param>
    [HotPath]
    public VoxelChunkShape(nint cells) => Cells = cells;

    /// <inheritdoc/>
    public static int TypeId => Id;

    /// <inheritdoc/>
    public readonly int ChildCount => Nobara.Voxel.Chunk.Volume * Stride;

    /// <inheritdoc/>
    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches) =>
        new HomogeneousCompoundShapeBatch<VoxelChunkShape, Box, BoxWide>(pool, initialCapacity);

    /// <summary>Gets the local index of a cell.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf(int x, int y, int z) => x + (Size * (z + (Size * y)));

    /// <summary>Gets the corner of a cell, in the frame of the shape.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 CornerOf(int cell) => new(cell % Size, cell / (Size * Size), cell / Size % Size);

    /// <summary>Gets the cells behind a handle.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static VoxelChunkCells CellsOf(nint cells) => Unsafe.As<VoxelChunkCells>(GCHandle.FromIntPtr(cells).Target!);

    /// <summary>Gets the box of a child: its full widths and its center, in the frame of the shape.</summary>
    [HotPath]
    public readonly void GetChild(int childIndex, out Box box, out Vector3 center)
    {
        int cell = childIndex / Stride;
        ReadOnlySpan<CollisionBox> boxes = CellsOf(Cells).GetBoxes(cell);
        int index = childIndex - (cell * Stride);
        if ((uint)index >= (uint)boxes.Length)
        {
            box = new Box(1e-3f, 1e-3f, 1e-3f);   // no such box: a speck at the center of the cell
            center = CornerOf(cell) + new Vector3(0.5f);
            return;
        }

        CollisionBox bounds = boxes[index];
        Vector3 size = Vector3.Max(bounds.Maximum - bounds.Minimum, new Vector3(1e-3f));
        box = new Box(size.X, size.Y, size.Z);
        center = CornerOf(cell) + ((bounds.Minimum + bounds.Maximum) * 0.5f);
    }

    /// <inheritdoc/>
    public readonly void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? Size : 0, (i & 2) != 0 ? Size : 0, (i & 4) != 0 ? Size : 0);
            Vector3 turned = Vector3.Transform(corner, orientation);
            min = Vector3.Min(min, turned);
            max = Vector3.Max(max, turned);
        }
    }

    /// <inheritdoc/>
    public readonly void GetLocalChild(int childIndex, out Box childShape) => GetChild(childIndex, out childShape, out _);

    /// <inheritdoc/>
    public readonly void GetPosedLocalChild(int childIndex, out Box childShape, out RigidPose childPose)
    {
        GetChild(childIndex, out childShape, out Vector3 center);
        childPose = new RigidPose(center);
    }

    /// <inheritdoc/>
    public readonly void GetLocalChild(int childIndex, ref BoxWide shapeWide)
    {
        GetChild(childIndex, out Box box, out _);
        GatherScatter.GetFirst(ref shapeWide.HalfWidth) = box.HalfWidth;
        GatherScatter.GetFirst(ref shapeWide.HalfHeight) = box.HalfHeight;
        GatherScatter.GetFirst(ref shapeWide.HalfLength) = box.HalfLength;
    }

    /// <inheritdoc/>
    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
    {
        for (int i = 0; i < pairs.Length; i++)
        {
            ref OverlapQueryForPair pair = ref pairs[i];
            VoxelChunkCells cells = CellsOf(Unsafe.AsRef<VoxelChunkShape>(pair.Container).Cells);
            ref TSubpairOverlaps target = ref overlaps.GetOverlapsForPair(i);
            Range(pair.Min, pair.Max, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = IndexOf(x, y, z);
                        ReadOnlySpan<CollisionBox> boxes = cells.GetBoxes(cell);
                        var corner = new Vector3(x, y, z);
                        for (int b = 0; b < boxes.Length; b++)
                        {
                            if (Overlaps(boxes[b], corner, pair.Min, pair.Max))
                            {
                                target.Allocate(pool) = (cell * Stride) + b;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public readonly void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
    {
        // Every box in the bounds swept over the whole interval.
        Vector3 end = sweep * maximumT;
        Vector3 low = Vector3.Min(min, min + end);
        Vector3 high = Vector3.Max(max, max + end);
        Range(low, high, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
        VoxelChunkCells cells = CellsOf(Cells);
        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = IndexOf(x, y, z);
                    ReadOnlySpan<CollisionBox> boxes = cells.GetBoxes(cell);
                    var corner = new Vector3(x, y, z);
                    for (int b = 0; b < boxes.Length; b++)
                    {
                        if (Overlaps(boxes[b], corner, low, high))
                        {
                            Unsafe.AsRef<TOverlaps>(overlaps).Allocate(pool) = (cell * Stride) + b;
                        }
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public readonly void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
    {
        Range(min, max, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
        VoxelChunkCells cells = CellsOf(Cells);
        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = IndexOf(x, y, z);
                    ReadOnlySpan<CollisionBox> boxes = cells.GetBoxes(cell);
                    var corner = new Vector3(x, y, z);
                    for (int b = 0; b < boxes.Length; b++)
                    {
                        if (Overlaps(boxes[b], corner, min, max) && !enumerator.LoopBody((cell * Stride) + b))
                        {
                            return;
                        }
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public readonly void RayTest<TRayHitHandler>(in RigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        Quaternion inverse = Quaternion.Conjugate(pose.Orientation);
        Vector3 origin = Vector3.Transform(ray.Origin - pose.Position, inverse);
        Vector3 direction = Vector3.Transform(ray.Direction, inverse);
        if (RayLocal(CellsOf(Cells), origin, direction, maximumT, out float t, out Vector3 normal, out int child))
        {
            hitHandler.OnRayHit(ray, ref maximumT, t, Vector3.Transform(normal, pose.Orientation), child);
        }
    }

    /// <inheritdoc/>
    public readonly void RayTest<TRayHitHandler>(in RigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        for (int i = 0; i < rays.RayCount; i++)
        {
            rays.GetRay(i, out RayData* ray, out float* maximumT);
            RayTest(pose, *ray, ref *maximumT, pool, ref hitHandler);
        }
    }

    /// <inheritdoc/>
    public void Dispose(BufferPool pool)
    {
        // The cells belong to nPhysics, and their handle to the world.
    }

    /// <summary>Tells whether a box of a cell overlaps local bounds.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Overlaps(in CollisionBox box, Vector3 corner, Vector3 min, Vector3 max)
    {
        Vector3 low = corner + box.Minimum;
        Vector3 high = corner + box.Maximum;
        return low.X <= max.X && high.X >= min.X && low.Y <= max.Y && high.Y >= min.Y && low.Z <= max.Z && high.Z >= min.Z;
    }

    /// <summary>Clamps local bounds to the cells of the chunk.</summary>
    [HotPath]
    private static void Range(Vector3 min, Vector3 max, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
    {
        x0 = Clamp(min.X);
        y0 = Clamp(min.Y);
        z0 = Clamp(min.Z);
        x1 = Clamp(max.X);
        y1 = Clamp(max.Y);
        z1 = Clamp(max.Z);
        if (max.X < 0 || max.Y < 0 || max.Z < 0 || min.X >= Size || min.Y >= Size || min.Z >= Size)
        {
            x1 = x0 - 1;   // outside: no cell
        }
    }

    [HotPath]
    private static int Clamp(float value) => value <= 0 ? 0 : value >= Size ? Size - 1 : (int)value;

    /// <summary>
    /// Walks the cells along a ray (Amanatides and Woo), in the frame of the
    /// shape, and tests the ray against the boxes of each cell it crosses.
    /// </summary>
    [HotPath]
    private static bool RayLocal(VoxelChunkCells cells, Vector3 origin, Vector3 direction, float maximumT, out float t, out Vector3 normal, out int child)
    {
        t = 0;
        normal = default;
        child = -1;
        float enter = 0;
        float exit = maximumT;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < 0 || o > Size)
                {
                    return false;
                }

                continue;
            }

            float t0 = -o / d;
            float t1 = (Size - o) / d;
            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
            }

            enter = MathF.Max(enter, t0);
            exit = MathF.Min(exit, t1);
            if (enter > exit)
            {
                return false;
            }
        }

        Vector3 start = origin + (direction * enter);
        int x = Math.Clamp((int)MathF.Floor(start.X), 0, Size - 1);
        int y = Math.Clamp((int)MathF.Floor(start.Y), 0, Size - 1);
        int z = Math.Clamp((int)MathF.Floor(start.Z), 0, Size - 1);
        int stepX = direction.X > 0 ? 1 : -1;
        int stepY = direction.Y > 0 ? 1 : -1;
        int stepZ = direction.Z > 0 ? 1 : -1;
        float nextX = enter + Next(start.X, x, direction.X);
        float nextY = enter + Next(start.Y, y, direction.Y);
        float nextZ = enter + Next(start.Z, z, direction.Z);
        float deltaX = direction.X != 0 ? MathF.Abs(1 / direction.X) : float.MaxValue;
        float deltaY = direction.Y != 0 ? MathF.Abs(1 / direction.Y) : float.MaxValue;
        float deltaZ = direction.Z != 0 ? MathF.Abs(1 / direction.Z) : float.MaxValue;
        float at = enter;
        while (at <= exit)
        {
            int cell = IndexOf(x, y, z);
            ReadOnlySpan<CollisionBox> boxes = cells.GetBoxes(cell);
            if (boxes.Length > 0)
            {
                var corner = new Vector3(x, y, z);
                float best = float.MaxValue;
                for (int b = 0; b < boxes.Length; b++)
                {
                    if (RayBox(origin, direction, corner + boxes[b].Minimum, corner + boxes[b].Maximum, out float hit, out Vector3 face) && hit < best && hit <= maximumT)
                    {
                        best = hit;
                        normal = face;
                        child = (cell * Stride) + b;
                    }
                }

                if (child >= 0)
                {
                    t = best;
                    return true;
                }
            }

            if (nextX < nextY && nextX < nextZ)
            {
                x += stepX;
                at = nextX;
                nextX += deltaX;
            }
            else if (nextY < nextZ)
            {
                y += stepY;
                at = nextY;
                nextY += deltaY;
            }
            else
            {
                z += stepZ;
                at = nextZ;
                nextZ += deltaZ;
            }

            if ((uint)x >= Size || (uint)y >= Size || (uint)z >= Size)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Intersects a ray with a box (slab test); a ray that starts inside the box hits it at 0, with no normal.</summary>
    [HotPath]
    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 low, Vector3 high, out float t, out Vector3 normal)
    {
        t = 0;
        normal = default;
        float enter = 0;
        float exit = float.MaxValue;
        int entering = -1;
        float sign = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float d = direction[axis];
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < low[axis] || o > high[axis])
                {
                    return false;
                }

                continue;
            }

            float t0 = (low[axis] - o) / d;
            float t1 = (high[axis] - o) / d;
            float side = -1;
            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
                side = 1;
            }

            if (t0 > enter)
            {
                enter = t0;
                entering = axis;
                sign = side;
            }

            exit = MathF.Min(exit, t1);
            if (enter > exit)
            {
                return false;
            }
        }

        t = enter;
        normal = entering switch
        {
            0 => new Vector3(sign, 0, 0),
            1 => new Vector3(0, sign, 0),
            2 => new Vector3(0, 0, sign),
            _ => Vector3.Zero,
        };
        return true;
    }

    [HotPath]
    private static float Next(float position, int cell, float direction) =>
        direction > 0 ? (cell + 1 - position) / direction : direction < 0 ? (cell - position) / direction : float.MaxValue;
}
