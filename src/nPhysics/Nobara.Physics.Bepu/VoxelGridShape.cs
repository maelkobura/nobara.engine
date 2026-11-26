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
/// The shape of the voxels of a grid, for the body of that grid: a compound of
/// boxes whose children are the collision boxes of the cells of every chunk of
/// the grid in memory, found by reading the cells that a query covers straight
/// from the chunks, through the <see cref="VoxelGridCells"/> of nPhysics, with
/// no tree and no copy. A block placed or broken is seen at the next step.
/// </summary>
/// <remarks>
/// <para>
/// The index of a child is the slot of its chunk times
/// <see cref="VoxelGridCells.ChunkStride"/>, plus the local index of its cell
/// times <see cref="VoxelChunkCells.MaxBoxesPerCell"/>, plus the index of the
/// box in the cell. The origin of the shape is <see cref="Origin"/> in the
/// frame of the grid: cell (x, y, z) of the grid spans from (x, y, z) − origin
/// to (x + 1, y + 1, z + 1) − origin in the frame of the shape.
/// </para>
/// <para>
/// Rays do not hit the shape: nPhysics tests the voxels of every grid itself,
/// and the queries of the world leave such bodies out.
/// </para>
/// </remarks>
internal unsafe struct VoxelGridShape : IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
{
    /// <summary>The type identifier of the shape, outside the range of the shapes of BEPUphysics.</summary>
    public const int Id = 25;

    private const int Size = VoxelCoordinates.ChunkSize;
    private const int Stride = VoxelChunkCells.MaxBoxesPerCell;

    /// <summary>The handle of the cells of the grid, from <see cref="GCHandle.ToIntPtr"/>.</summary>
    public nint Cells;

    /// <summary>Where the origin of the shape lies in the frame of the grid.</summary>
    public Vector3 Origin;

    /// <summary>Creates the shape of the voxels of a grid.</summary>
    /// <param name="cells">The handle of the cells of the grid.</param>
    /// <param name="origin">Where the origin of the shape lies in the frame of the grid.</param>
    [HotPath]
    public VoxelGridShape(nint cells, Vector3 origin) => (Cells, Origin) = (cells, origin);

    /// <inheritdoc/>
    public static int TypeId => Id;

    /// <inheritdoc/>
    public readonly int ChildCount => CellsOf(Cells).ChunkSlots * VoxelGridCells.ChunkStride;

    /// <inheritdoc/>
    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches) =>
        new HomogeneousCompoundShapeBatch<VoxelGridShape, Box, BoxWide>(pool, initialCapacity);

    /// <summary>Gets the cells behind a handle.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static VoxelGridCells CellsOf(nint cells) => Unsafe.As<VoxelGridCells>(GCHandle.FromIntPtr(cells).Target!);

    /// <summary>Splits the index of a child into the slot of its chunk, its cell, and its box.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Split(int childIndex, out int slot, out int cell, out int box)
    {
        slot = childIndex / VoxelGridCells.ChunkStride;
        int rest = childIndex - (slot * VoxelGridCells.ChunkStride);
        cell = rest / Stride;
        box = rest - (cell * Stride);
    }

    /// <inheritdoc/>
    [HotPath]
    public readonly void GetChild(int childIndex, out Box box, out Vector3 center)
    {
        VoxelGridCells grid = CellsOf(Cells);
        Split(childIndex, out int slot, out int cell, out int index);
        VoxelChunkCells? chunk = grid.GetChunk(slot);
        ReadOnlySpan<CollisionBox> boxes = chunk is not null ? chunk.GetBoxes(cell) : default;
        Vector3 corner = chunk is not null ? grid.CornerOf(slot) + VoxelChunkShape.CornerOf(cell) - Origin : -Origin;
        if ((uint)index >= (uint)boxes.Length)
        {
            box = new Box(1e-3f, 1e-3f, 1e-3f);   // no such box: a speck at the center of the cell
            center = corner + new Vector3(0.5f);
            return;
        }

        CollisionBox bounds = boxes[index];
        Vector3 size = Vector3.Max(bounds.Maximum - bounds.Minimum, new Vector3(1e-3f));
        box = new Box(size.X, size.Y, size.Z);
        center = corner + ((bounds.Minimum + bounds.Maximum) * 0.5f);
    }

    /// <inheritdoc/>
    public readonly void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
    {
        VoxelGridCells grid = CellsOf(Cells);
        Vector3 low = grid.IsEmpty ? Vector3.Zero : grid.Minimum - Origin;
        Vector3 high = grid.IsEmpty ? Vector3.Zero : grid.Maximum - Origin;
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? high.X : low.X, (i & 2) != 0 ? high.Y : low.Y, (i & 4) != 0 ? high.Z : low.Z);
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
            ref VoxelGridShape shape = ref Unsafe.AsRef<VoxelGridShape>(pair.Container);
            var collector = new Collector<TSubpairOverlaps>(ref overlaps.GetOverlapsForPair(i), pool);
            Visit(CellsOf(shape.Cells), pair.Min + shape.Origin, pair.Max + shape.Origin, ref collector);
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
        var collector = new Collector<TOverlaps>(ref Unsafe.AsRef<TOverlaps>(overlaps), pool);
        Visit(CellsOf(Cells), low + Origin, high + Origin, ref collector);
    }

    /// <inheritdoc/>
    public readonly void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
    {
        var visitor = new Breakable<TEnumerator>(ref enumerator);
        Visit(CellsOf(Cells), min + Origin, max + Origin, ref visitor);
    }

    /// <inheritdoc/>
    public readonly void RayTest<TRayHitHandler>(in RigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        // nPhysics tests the voxels of every grid itself.
    }

    /// <inheritdoc/>
    public readonly void RayTest<TRayHitHandler>(in RigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        // nPhysics tests the voxels of every grid itself.
    }

    /// <inheritdoc/>
    public void Dispose(BufferPool pool)
    {
        // The cells belong to nPhysics, and their handle to the world.
    }

    /// <summary>Visits every box of the grid that overlaps bounds in the frame of the grid, by the index of its child.</summary>
    [HotPath]
    private static void Visit<TVisitor>(VoxelGridCells grid, Vector3 min, Vector3 max, ref TVisitor visitor)
        where TVisitor : struct, IChildVisitor, allows ref struct
    {
        if (grid.IsEmpty)
        {
            return;
        }

        min = Vector3.Max(min, grid.Minimum);
        max = Vector3.Min(max, grid.Maximum);
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            return;
        }

        int cx0 = ChunkOf(min.X);
        int cy0 = ChunkOf(min.Y);
        int cz0 = ChunkOf(min.Z);
        int cx1 = ChunkOf(max.X);
        int cy1 = ChunkOf(max.Y);
        int cz1 = ChunkOf(max.Z);
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cz = cz0; cz <= cz1; cz++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int slot = grid.SlotOf(new ChunkCoordinate(cx, cy, cz));
                    if (slot < 0 || grid.GetChunk(slot) is not { } cells)
                    {
                        continue;
                    }

                    Vector3 chunk = grid.CornerOf(slot);
                    Vector3 low = min - chunk;
                    Vector3 high = max - chunk;
                    int x0 = Clamp(low.X);
                    int y0 = Clamp(low.Y);
                    int z0 = Clamp(low.Z);
                    int x1 = Clamp(high.X);
                    int y1 = Clamp(high.Y);
                    int z1 = Clamp(high.Z);
                    int first = slot * VoxelGridCells.ChunkStride;
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int z = z0; z <= z1; z++)
                        {
                            for (int x = x0; x <= x1; x++)
                            {
                                int cell = VoxelChunkShape.IndexOf(x, y, z);
                                ReadOnlySpan<CollisionBox> boxes = cells.GetBoxes(cell);
                                var corner = new Vector3(x, y, z);
                                for (int b = 0; b < boxes.Length; b++)
                                {
                                    if (VoxelChunkShape.Overlaps(boxes[b], corner, low, high) && !visitor.Visit(first + (cell * Stride) + b))
                                    {
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [HotPath]
    private static int ChunkOf(float value) => (int)MathF.Floor(value / Size);

    [HotPath]
    private static int Clamp(float value) => value <= 0 ? 0 : value >= Size ? Size - 1 : (int)value;

    /// <summary>Receives the index of each child found.</summary>
    private interface IChildVisitor
    {
        /// <summary>Takes one child.</summary>
        /// <param name="child">The index of the child.</param>
        /// <returns><see langword="false"/> to stop.</returns>
        [HotPath]
        bool Visit(int child);
    }

    /// <summary>Adds each child to the overlaps of a pair.</summary>
    private readonly ref struct Collector<TOverlaps>(ref TOverlaps overlaps, BufferPool pool) : IChildVisitor
        where TOverlaps : ICollisionTaskSubpairOverlaps
    {
        private readonly ref TOverlaps _overlaps = ref overlaps;

        [HotPath]
        public bool Visit(int child)
        {
            _overlaps.Allocate(pool) = child;
            return true;
        }
    }

    /// <summary>Gives each child to an enumerator that can stop.</summary>
    private readonly ref struct Breakable<TEnumerator>(ref TEnumerator enumerator) : IChildVisitor
        where TEnumerator : IBreakableForEach<int>
    {
        private readonly ref TEnumerator _enumerator = ref enumerator;

        [HotPath]
        public bool Visit(int child) => _enumerator.LoopBody(child);
    }
}
