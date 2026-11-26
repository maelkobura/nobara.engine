// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.CollisionDetection.SweepTasks;
using BepuUtilities;
using BepuUtilities.Memory;

namespace Nobara.Physics.Bepu;

/// <summary>A shape of voxels whose children are boxes: a chunk, or the voxels of a grid.</summary>
internal interface IVoxelShape
{
    /// <summary>Gets the box of a child: its full widths and its center, in the frame of the shape.</summary>
    /// <param name="childIndex">The index of the child.</param>
    /// <param name="box">The box.</param>
    /// <param name="center">Its center.</param>
    void GetChild(int childIndex, out Box box, out Vector3 center);
}

/// <summary>
/// The collision and sweep tasks that teach the narrow phase of BEPUphysics to
/// collide every shape of the neutral API with <see cref="VoxelChunkShape"/>
/// and <see cref="VoxelGridShape"/>: the point where the contacts of the
/// voxels enter the solver. No task joins two shapes of voxels: the voxels of
/// two grids do not collide in the backend.
/// </summary>
internal static class VoxelCollisionTasks
{
    /// <summary>Registers the tasks in a simulation, once, right after it is created.</summary>
    /// <param name="simulation">The simulation.</param>
    public static void Register(Simulation simulation)
    {
        Register<VoxelChunkShape>(simulation);
        Register<VoxelGridShape>(simulation);
    }

    /// <summary>Gives a box of a shape of voxels, posed in the frame of the pair; the voxels are the second shape of the pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void GetCell<TCallbacks, TVoxel>(ref CollisionBatcher<TCallbacks> batcher, in BoundsTestedPair pair, int shapeTypeA, int child, out RigidPose pose, out int type, out void* data)
        where TCallbacks : struct, ICollisionCallbacks
        where TVoxel : unmanaged, IVoxelShape
    {
        Unsafe.AsRef<TVoxel>(pair.B).GetChild(child, out Box box, out Vector3 center);
        QuaternionEx.TransformWithoutOverlap(center, pair.OrientationB, out pose.Position);
        pose.Orientation = pair.OrientationB;
        type = Box.Id;
        batcher.CacheShapeB(shapeTypeA, type, Unsafe.AsPointer(ref box), Unsafe.SizeOf<Box>(), out data);
    }

    private static void Register<TVoxel>(Simulation simulation)
        where TVoxel : unmanaged, IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
    {
        CollisionTaskRegistry tasks = simulation.NarrowPhase.CollisionTaskRegistry;
        tasks.Register(new ConvexCompoundCollisionTask<Sphere, TVoxel, ConvexCompoundOverlapFinder<Sphere, SphereWide, TVoxel>, ConvexVoxelContinuations<TVoxel>, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<Capsule, TVoxel, ConvexCompoundOverlapFinder<Capsule, CapsuleWide, TVoxel>, ConvexVoxelContinuations<TVoxel>, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<Box, TVoxel, ConvexCompoundOverlapFinder<Box, BoxWide, TVoxel>, ConvexVoxelContinuations<TVoxel>, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<Cylinder, TVoxel, ConvexCompoundOverlapFinder<Cylinder, CylinderWide, TVoxel>, ConvexVoxelContinuations<TVoxel>, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<ConvexHull, TVoxel, ConvexCompoundOverlapFinder<ConvexHull, ConvexHullWide, TVoxel>, ConvexVoxelContinuations<TVoxel>, NonconvexReduction>());
        tasks.Register(new CompoundPairCollisionTask<Compound, TVoxel, CompoundPairOverlapFinder<Compound, TVoxel>, CompoundVoxelContinuations<Compound, TVoxel>, NonconvexReduction>());
        tasks.Register(new CompoundPairCollisionTask<BigCompound, TVoxel, CompoundPairOverlapFinder<BigCompound, TVoxel>, CompoundVoxelContinuations<BigCompound, TVoxel>, NonconvexReduction>());

        SweepTaskRegistry sweeps = simulation.NarrowPhase.SweepTaskRegistry;
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Sphere, SphereWide, TVoxel, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Sphere, TVoxel>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Capsule, CapsuleWide, TVoxel, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Capsule, TVoxel>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Box, BoxWide, TVoxel, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Box, TVoxel>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Cylinder, CylinderWide, TVoxel, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Cylinder, TVoxel>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<ConvexHull, ConvexHullWide, TVoxel, Box, BoxWide, ConvexCompoundSweepOverlapFinder<ConvexHull, TVoxel>>());
        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<Compound, TVoxel, Box, BoxWide, CompoundPairSweepOverlapFinder<Compound, TVoxel>>());
        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<BigCompound, TVoxel, Box, BoxWide, CompoundPairSweepOverlapFinder<BigCompound, TVoxel>>());
    }
}

/// <summary>A convex shape against voxels: each box it overlaps is a child, and the non-convex reduction merges their contacts.</summary>
/// <typeparam name="TVoxel">The shape of voxels.</typeparam>
internal unsafe struct ConvexVoxelContinuations<TVoxel> : IConvexCompoundContinuationHandler<NonconvexReduction>
    where TVoxel : unmanaged, IVoxelShape
{
    /// <inheritdoc/>
    public readonly CollisionContinuationType CollisionContinuationType => CollisionContinuationType.NonconvexReduction;

    /// <inheritdoc/>
    public ref NonconvexReduction CreateContinuation<TCallbacks>(ref CollisionBatcher<TCallbacks> collisionBatcher, int childCount, in BoundsTestedPair pair, in OverlapQueryForPair pairQuery, out int continuationIndex)
        where TCallbacks : struct, ICollisionCallbacks =>
        ref collisionBatcher.NonconvexReductions.CreateContinuation(childCount, collisionBatcher.Pool, out continuationIndex);

    /// <inheritdoc/>
    public void ConfigureContinuationChild<TCallbacks>(ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, int continuationChildIndex, in BoundsTestedPair pair, int shapeTypeA, int childIndexB, out RigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        VoxelCollisionTasks.GetCell<TCallbacks, TVoxel>(ref collisionBatcher, pair, shapeTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
        ref NonconvexReductionChild child = ref continuation.Children[continuationChildIndex];
        if (pair.FlipMask < 0)
        {
            child.ChildIndexA = childIndexB;
            child.ChildIndexB = 0;
            child.OffsetA = childPoseB.Position;
            child.OffsetB = default;
        }
        else
        {
            child.ChildIndexA = 0;
            child.ChildIndexB = childIndexB;
            child.OffsetA = default;
            child.OffsetB = childPoseB.Position;
        }
    }
}

/// <summary>A compound against voxels: each pair of a child and a box is a pair of convex shapes.</summary>
/// <typeparam name="TCompoundA">The type of the compound.</typeparam>
/// <typeparam name="TVoxel">The shape of voxels.</typeparam>
internal unsafe struct CompoundVoxelContinuations<TCompoundA, TVoxel> : ICompoundPairContinuationHandler<NonconvexReduction>
    where TCompoundA : ICompoundShape
    where TVoxel : unmanaged, IVoxelShape
{
    /// <inheritdoc/>
    public readonly CollisionContinuationType CollisionContinuationType => CollisionContinuationType.NonconvexReduction;

    /// <inheritdoc/>
    public ref NonconvexReduction CreateContinuation<TCallbacks>(ref CollisionBatcher<TCallbacks> collisionBatcher, int totalChildCount, ref Buffer<ChildOverlapsCollection> pairOverlaps, ref Buffer<OverlapQueryForPair> pairQueries, in BoundsTestedPair pair, out int continuationIndex)
        where TCallbacks : struct, ICollisionCallbacks =>
        ref collisionBatcher.NonconvexReductions.CreateContinuation(totalChildCount, collisionBatcher.Pool, out continuationIndex);

    /// <inheritdoc/>
    public void GetChildAData<TCallbacks>(ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, in BoundsTestedPair pair, int childIndexA, out RigidPose childPoseA, out int childTypeA, out void* childShapeDataA)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref BepuPhysics.Collidables.CompoundChild child = ref Unsafe.AsRef<TCompoundA>(pair.A).GetChild(childIndexA);
        Compound.GetRotatedChildPose(child.AsPose(), pair.OrientationA, out childPoseA);
        childTypeA = child.ShapeIndex.Type;
        collisionBatcher.Shapes[childTypeA].GetShapeData(child.ShapeIndex.Index, out childShapeDataA, out _);
    }

    /// <inheritdoc/>
    public void ConfigureContinuationChild<TCallbacks>(ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, int continuationChildIndex, in BoundsTestedPair pair, int childIndexA, int childTypeA, int childIndexB, in RigidPose childPoseA, out RigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        VoxelCollisionTasks.GetCell<TCallbacks, TVoxel>(ref collisionBatcher, pair, childTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
        ref NonconvexReductionChild child = ref continuation.Children[continuationChildIndex];
        if (pair.FlipMask < 0)
        {
            child.ChildIndexA = childIndexB;
            child.ChildIndexB = childIndexA;
            child.OffsetA = childPoseB.Position;
            child.OffsetB = childPoseA.Position;
        }
        else
        {
            child.ChildIndexA = childIndexA;
            child.ChildIndexB = childIndexB;
            child.OffsetA = childPoseA.Position;
            child.OffsetB = childPoseB.Position;
        }
    }
}
