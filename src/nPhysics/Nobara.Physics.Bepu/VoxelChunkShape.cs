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
/// The collidable of one chunk of voxels: a compound of unit boxes whose
/// children are the cells of the chunk that hold a block, found by reading the
/// cells that a query covers straight from the chunk, with no tree and no copy.
/// A block placed or broken is seen at the next step, at no cost.
/// </summary>
/// <remarks>
/// <para>
/// The index of a child is the local index of its cell in the chunk. The shape
/// sits at the corner of the chunk: cell (x, y, z) spans from (x, y, z) to
/// (x + 1, y + 1, z + 1) in the frame of the shape.
/// </para>
/// <para>
/// A shape of BEPUphysics is an unmanaged value, so the shape holds a handle to
/// the chunk, which the world that adds the shape owns and frees. The threads
/// of the solver read the palette of the chunk while the world steps: the chunk
/// does not change during a step.
/// </para>
/// </remarks>
internal unsafe struct VoxelChunkShape : IHomogeneousCompoundShape<Box, BoxWide>
{
    /// <summary>The type identifier of the shape, outside the range of the shapes of BEPUphysics.</summary>
    public const int Id = 24;

    private const int Size = VoxelCoordinates.ChunkSize;

    /// <summary>The handle of the chunk, from <see cref="GCHandle.ToIntPtr"/>.</summary>
    public nint Chunk;

    /// <summary>Creates the shape of a chunk.</summary>
    /// <param name="chunk">The handle of the chunk.</param>
    [HotPath]
    public VoxelChunkShape(nint chunk) => Chunk = chunk;

    /// <inheritdoc/>
    public static int TypeId => Id;

    /// <inheritdoc/>
    public readonly int ChildCount => Nobara.Voxel.Chunk.Volume;

    /// <inheritdoc/>
    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches) =>
        new HomogeneousCompoundShapeBatch<VoxelChunkShape, Box, BoxWide>(pool, initialCapacity);

    /// <summary>Gets the local index of a cell.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf(int x, int y, int z) => x + (Size * (z + (Size * y)));

    /// <summary>Gets the center of a cell, in the frame of the shape.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 CenterOf(int index) => new((index & (Size - 1)) + 0.5f, (index >> 10) + 0.5f, ((index >> 5) & (Size - 1)) + 0.5f);

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
    public readonly void GetLocalChild(int childIndex, out Box childShape) => childShape = new Box(1, 1, 1);

    /// <inheritdoc/>
    public readonly void GetPosedLocalChild(int childIndex, out Box childShape, out RigidPose childPose)
    {
        childShape = new Box(1, 1, 1);
        childPose = new RigidPose(CenterOf(childIndex));
    }

    /// <inheritdoc/>
    public readonly void GetLocalChild(int childIndex, ref BoxWide shapeWide)
    {
        GatherScatter.GetFirst(ref shapeWide.HalfWidth) = 0.5f;
        GatherScatter.GetFirst(ref shapeWide.HalfHeight) = 0.5f;
        GatherScatter.GetFirst(ref shapeWide.HalfLength) = 0.5f;
    }

    /// <inheritdoc/>
    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
    {
        for (int i = 0; i < pairs.Length; i++)
        {
            ref OverlapQueryForPair pair = ref pairs[i];
            Palette cells = PaletteOf(Unsafe.AsRef<VoxelChunkShape>(pair.Container).Chunk);
            ref TSubpairOverlaps target = ref overlaps.GetOverlapsForPair(i);
            Range(pair.Min, pair.Max, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int index = IndexOf(x, y, z);
                        if (cells.Get(index) != 0)
                        {
                            target.Allocate(pool) = index;
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
        // Every cell with a block in the bounds swept over the whole interval.
        Vector3 end = sweep * maximumT;
        Range(Vector3.Min(min, min + end), Vector3.Max(max, max + end), out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
        Palette cells = PaletteOf(Chunk);
        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int index = IndexOf(x, y, z);
                    if (cells.Get(index) != 0)
                    {
                        Unsafe.AsRef<TOverlaps>(overlaps).Allocate(pool) = index;
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
        Palette cells = PaletteOf(Chunk);
        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int index = IndexOf(x, y, z);
                    if (cells.Get(index) != 0 && !enumerator.LoopBody(index))
                    {
                        return;
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
        if (RayLocal(PaletteOf(Chunk), origin, direction, maximumT, out float t, out Vector3 normal, out int child))
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
        // The chunk belongs to nVoxel, and its handle to the world.
    }

    /// <summary>Gets the palette of the chunk behind a handle.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Palette PaletteOf(nint chunk) => Unsafe.As<Chunk>(GCHandle.FromIntPtr(chunk).Target!).Palette;

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

    /// <summary>Walks the cells along a ray (Amanatides and Woo), in the frame of the shape.</summary>
    [HotPath]
    private static bool RayLocal(Palette cells, Vector3 origin, Vector3 direction, float maximumT, out float t, out Vector3 normal, out int child)
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
        Vector3 face = Vector3.Zero;
        float at = enter;
        while (at <= exit)
        {
            int index = IndexOf(x, y, z);
            if (cells.Get(index) != 0)
            {
                t = at;
                normal = face;
                child = index;
                return true;
            }

            if (nextX < nextY && nextX < nextZ)
            {
                x += stepX;
                at = nextX;
                nextX += deltaX;
                face = new Vector3(-stepX, 0, 0);
            }
            else if (nextY < nextZ)
            {
                y += stepY;
                at = nextY;
                nextY += deltaY;
                face = new Vector3(0, -stepY, 0);
            }
            else
            {
                z += stepZ;
                at = nextZ;
                nextZ += deltaZ;
                face = new Vector3(0, 0, -stepZ);
            }

            if ((uint)x >= Size || (uint)y >= Size || (uint)z >= Size)
            {
                return false;
            }
        }

        return false;
    }

    [HotPath]
    private static float Next(float position, int cell, float direction) =>
        direction > 0 ? (cell + 1 - position) / direction : direction < 0 ? (cell - position) / direction : float.MaxValue;
}
