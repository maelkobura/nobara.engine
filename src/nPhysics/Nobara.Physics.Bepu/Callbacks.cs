// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu;

/// <summary>
/// What the narrow phase reads of a collidable, from the threads of the
/// solver: its material, its layers, and the number nPhysics knows it by.
/// </summary>
internal struct Surface
{
    /// <summary>The friction coefficient.</summary>
    public float Friction;

    /// <summary>The share of the speed kept in a bounce.</summary>
    public float Restitution;

    /// <summary>The layers the collidable belongs to.</summary>
    public uint Layers;

    /// <summary>The layers it collides with.</summary>
    public uint Mask;

    /// <summary>The number of the body in the world, or -1 for a chunk of voxels.</summary>
    public int Body;

    /// <summary>The slot of the body in nPhysics, or -1 for a chunk of voxels.</summary>
    public int Slot;

    /// <summary>The number of the chunk of voxels in the world, or -1 for a body.</summary>
    public int Chunk;

    /// <summary>Whether the contacts of the body are reported to nPhysics.</summary>
    public bool Reports;

    /// <summary>The handle of the cells of a chunk of voxels, from <see cref="System.Runtime.InteropServices.GCHandle.ToIntPtr"/>; 0 for a body.</summary>
    public nint Cells;

    /// <summary>Whether the body uses continuous collision detection.</summary>
    public bool Continuous;

    /// <summary>The handle of the cells of the grid of a body whose shape is the voxels of its grid, from <see cref="System.Runtime.InteropServices.GCHandle.ToIntPtr"/>; 0 for the others.</summary>
    public nint GridCells;

    /// <summary>Gets whether the collidable is made of voxels: a chunk, or the voxels of a grid.</summary>
    public readonly bool IsVoxels => Cells != 0 || GridCells != 0;

    /// <summary>Gets the surface of a chunk of voxels: the default material, in the layer of the voxels, colliding with every layer.</summary>
    /// <param name="cells">The handle of the cells of the chunk.</param>
    /// <param name="chunk">The number of the chunk in the world.</param>
    /// <returns>The surface.</returns>
    [HotPath]
    public static Surface Voxels(nint cells, int chunk) => new()
    {
        Friction = PhysicsMaterial.Default.Friction,
        Restitution = PhysicsMaterial.Default.Restitution,
        Layers = CollisionFilter.VoxelLayer,
        Mask = uint.MaxValue,
        Body = -1,
        Slot = -1,
        Chunk = chunk,
        Cells = cells,
    };
}

/// <summary>
/// The callbacks of the narrow phase: they filter pairs by collision layers,
/// give each pair its material, note the pairs that bounce, and note the
/// contacts of the bodies that report them (<see cref="ContactReports"/>). Friction
/// combines as the geometric mean of the two bodies, restitution as the larger
/// of the two; <see cref="Bounces"/> applies the restitution after the step.
/// </summary>
/// <remarks>
/// <para>
/// A contact between a body and a box of a cell of voxels that goes through an
/// inner face, one that the cell next to it covers whole, is dropped
/// (<see cref="VoxelChunkCells.IsInnerContact"/>): a body that slides over a
/// flat floor does not catch on the joints between its cells. The same holds
/// for the voxels of a grid that is a body.
/// </para>
/// <para>
/// Two collidables made of voxels never collide: a chunk and the voxels of a
/// grid, or the voxels of two grids. The collision of a grid with the voxels
/// of another is not the backend's.
/// </para>
/// <para>
/// The contacts are springs of <see cref="ContactFrequency"/>. A pair that
/// holds a body with continuous detection gets the stiffest spring the
/// substeps can integrate, half their rate: a softer contact stops a fast body
/// too late, and lets it through a thin wall.
/// </para>
/// </remarks>
internal struct NarrowPhaseCallbacks(CollidableProperty<Surface> surfaces, Bounces bounces, ContactReports reports) : INarrowPhaseCallbacks
{
    /// <summary>The frequency of the contact springs, in hertz.</summary>
    public const float ContactFrequency = 30f;

    /// <summary>The speed at which contacts that do not bounce push overlapping shapes apart, in blocks per second.</summary>
    public const float RecoveryVelocity = 2f;

    /// <inheritdoc/>
    public readonly void Initialize(Simulation simulation)
    {
        surfaces.Initialize(simulation);
        bounces.Simulation = simulation;
        reports.Simulation = simulation;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        if (a.Mobility != CollidableMobility.Dynamic && b.Mobility != CollidableMobility.Dynamic)
        {
            return false;
        }

        ref Surface first = ref surfaces[a];
        ref Surface second = ref surfaces[b];
        if ((first.Layers & second.Mask) == 0 || (second.Layers & first.Mask) == 0)
        {
            return false;
        }

        return !(first.IsVoxels && second.IsVoxels);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        ref Surface first = ref surfaces[pair.A];
        ref Surface second = ref surfaces[pair.B];
        pairMaterial.FrictionCoefficient = MathF.Sqrt(first.Friction * second.Friction);
        pairMaterial.MaximumRecoveryVelocity = RecoveryVelocity;
        pairMaterial.SpringSettings = new SpringSettings(first.Continuous || second.Continuous ? bounces.StiffFrequency : ContactFrequency, 1f);
        float restitution = MathF.Max(first.Restitution, second.Restitution);
        if (restitution > 0 && manifold.Count > 0)
        {
            int deepest = 0;
            for (int i = 1; i < manifold.Count; i++)
            {
                if (manifold.GetDepth(i) > manifold.GetDepth(deepest))
                {
                    deepest = i;
                }
            }

            bounces.Note(workerIndex, pair, manifold.GetNormal(deepest), manifold.GetDepth(deepest), restitution);
        }

        if ((first.Reports || second.Reports) && manifold.Count > 0)
        {
            reports.Note(workerIndex, pair, first, second, ref manifold);
        }

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Drops the contacts of a cell of voxels that go through an inner face. The normal points from B to A.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
    {
        if (manifold.Count == 0)
        {
            return true;
        }

        ref Surface first = ref surfaces[pair.A];
        if (first.Cells != 0)
        {
            return !IsInner(first.Cells, childIndexA, -manifold.Normal);
        }

        if (first.GridCells != 0)
        {
            return !IsInnerOfGrid(first.GridCells, OrientationOf(pair.A), childIndexA, -manifold.Normal);
        }

        ref Surface second = ref surfaces[pair.B];
        if (second.GridCells != 0)
        {
            return !IsInnerOfGrid(second.GridCells, OrientationOf(pair.B), childIndexB, manifold.Normal);
        }

        return second.Cells == 0 || !IsInner(second.Cells, childIndexB, manifold.Normal);
    }

    /// <inheritdoc/>
    public readonly void Dispose()
    {
    }

    /// <summary>Tells whether a contact with a child of the voxels of a grid goes through an inner face.</summary>
    /// <param name="cells">The handle of the cells of the grid.</param>
    /// <param name="orientation">The orientation of the body of the grid.</param>
    /// <param name="child">The child of the grid: its chunk, its cell, and its box.</param>
    /// <param name="normal">The normal, out of the box toward the other body, along the axes of the world.</param>
    [HotPath]
    private static bool IsInnerOfGrid(nint cells, Quaternion orientation, int child, Vector3 normal)
    {
        VoxelGridShape.Split(child, out int slot, out int cell, out int box);
        return VoxelGridShape.CellsOf(cells).GetChunk(slot) is { } chunk && chunk.IsInnerFace(cell, box, Vector3.Transform(normal, Quaternion.Conjugate(orientation)));
    }

    /// <summary>Gets the orientation of a collidable, read while the narrow phase runs and no pose changes.</summary>
    [HotPath]
    private readonly Quaternion OrientationOf(CollidableReference collidable) => collidable.Mobility == CollidableMobility.Static
        ? bounces.Simulation!.Statics[collidable.StaticHandle].Pose.Orientation
        : bounces.Simulation!.Bodies[collidable.BodyHandle].Pose.Orientation;

    /// <summary>Tells whether a contact with a child of a chunk of voxels goes through an inner face.</summary>
    /// <param name="cells">The handle of the cells of the chunk.</param>
    /// <param name="child">The child of the chunk: its cell and its box.</param>
    /// <param name="normal">The normal, out of the box toward the body.</param>
    [HotPath]
    private static bool IsInner(nint cells, int child, Vector3 normal)
    {
        int cell = child / VoxelChunkShape.Stride;
        return VoxelChunkShape.CellsOf(cells).IsInnerContact(cell, child - (cell * VoxelChunkShape.Stride), normal);
    }
}

/// <summary>The callbacks of the integration of poses: the gravity of the world, which it may change between steps, and no damping.</summary>
internal struct PoseIntegratorCallbacks(StrongBox<Vector3> gravity) : IPoseIntegratorCallbacks
{
    private Vector3Wide _gravity;

    /// <inheritdoc/>
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

    /// <inheritdoc/>
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;

    /// <inheritdoc/>
    public readonly bool IntegrateVelocityForKinematics => false;

    /// <inheritdoc/>
    public readonly void Initialize(Simulation simulation)
    {
    }

    /// <inheritdoc/>
    public void PrepareForIntegration(float dt) => _gravity = Vector3Wide.Broadcast(gravity.Value);

    /// <inheritdoc/>
    public readonly void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity) =>
        velocity.Linear += _gravity * dt;
}
