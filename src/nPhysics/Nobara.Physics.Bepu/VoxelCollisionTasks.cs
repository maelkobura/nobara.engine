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

/// <summary>
/// The collision and sweep tasks that teach the narrow phase of BEPUphysics to
/// collide every shape of the neutral API with <see cref="VoxelChunkShape"/>:
/// the point where the contacts of the voxels enter the solver.
/// </summary>
internal static class VoxelCollisionTasks
{
    /// <summary>Registers the tasks in a simulation, once, right after it is created.</summary>
    /// <param name="simulation">The simulation.</param>
    public static void Register(Simulation simulation)
    {
        CollisionTaskRegistry tasks = simulation.NarrowPhase.CollisionTaskRegistry;
        tasks.Register(new ConvexCompoundCollisionTask<Sphere, VoxelChunkShape, ConvexCompoundOverlapFinder<Sphere, SphereWide, VoxelChunkShape>, ConvexVoxelContinuations, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<Capsule, VoxelChunkShape, ConvexCompoundOverlapFinder<Capsule, CapsuleWide, VoxelChunkShape>, ConvexVoxelContinuations, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<Box, VoxelChunkShape, ConvexCompoundOverlapFinder<Box, BoxWide, VoxelChunkShape>, ConvexVoxelContinuations, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<Cylinder, VoxelChunkShape, ConvexCompoundOverlapFinder<Cylinder, CylinderWide, VoxelChunkShape>, ConvexVoxelContinuations, NonconvexReduction>());
        tasks.Register(new ConvexCompoundCollisionTask<ConvexHull, VoxelChunkShape, ConvexCompoundOverlapFinder<ConvexHull, ConvexHullWide, VoxelChunkShape>, ConvexVoxelContinuations, NonconvexReduction>());
        tasks.Register(new CompoundPairCollisionTask<Compound, VoxelChunkShape, CompoundPairOverlapFinder<Compound, VoxelChunkShape>, CompoundVoxelContinuations<Compound>, NonconvexReduction>());
        tasks.Register(new CompoundPairCollisionTask<BigCompound, VoxelChunkShape, CompoundPairOverlapFinder<BigCompound, VoxelChunkShape>, CompoundVoxelContinuations<BigCompound>, NonconvexReduction>());

        SweepTaskRegistry sweeps = simulation.NarrowPhase.SweepTaskRegistry;
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Sphere, SphereWide, VoxelChunkShape, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Sphere, VoxelChunkShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Capsule, CapsuleWide, VoxelChunkShape, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Capsule, VoxelChunkShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Box, BoxWide, VoxelChunkShape, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Box, VoxelChunkShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Cylinder, CylinderWide, VoxelChunkShape, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Cylinder, VoxelChunkShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<ConvexHull, ConvexHullWide, VoxelChunkShape, Box, BoxWide, ConvexCompoundSweepOverlapFinder<ConvexHull, VoxelChunkShape>>());
        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<Compound, VoxelChunkShape, Box, BoxWide, CompoundPairSweepOverlapFinder<Compound, VoxelChunkShape>>());
        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<BigCompound, VoxelChunkShape, Box, BoxWide, CompoundPairSweepOverlapFinder<BigCompound, VoxelChunkShape>>());
    }

    /// <summary>Gives a box of a cell of a chunk, posed in the frame of the pair; the chunk is the second shape of the pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void GetCell<TCallbacks>(ref CollisionBatcher<TCallbacks> batcher, in BoundsTestedPair pair, int shapeTypeA, int child, out RigidPose pose, out int type, out void* data)
        where TCallbacks : struct, ICollisionCallbacks
    {
        Unsafe.AsRef<VoxelChunkShape>(pair.B).GetChild(child, out Box box, out Vector3 center);
        QuaternionEx.TransformWithoutOverlap(center, pair.OrientationB, out pose.Position);
        pose.Orientation = pair.OrientationB;
        type = Box.Id;
        batcher.CacheShapeB(shapeTypeA, type, Unsafe.AsPointer(ref box), Unsafe.SizeOf<Box>(), out data);
    }
}

/// <summary>A convex shape against a chunk: each cell it overlaps is a box, and the non-convex reduction merges their contacts.</summary>
internal unsafe struct ConvexVoxelContinuations : IConvexCompoundContinuationHandler<NonconvexReduction>
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
        VoxelCollisionTasks.GetCell(ref collisionBatcher, pair, shapeTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
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

/// <summary>A compound against a chunk: each pair of a child and a cell is a pair of convex shapes.</summary>
/// <typeparam name="TCompoundA">The type of the compound.</typeparam>
internal unsafe struct CompoundVoxelContinuations<TCompoundA> : ICompoundPairContinuationHandler<NonconvexReduction>
    where TCompoundA : ICompoundShape
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
        VoxelCollisionTasks.GetCell(ref collisionBatcher, pair, childTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
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
