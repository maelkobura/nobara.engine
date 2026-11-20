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

    /// <summary>Whether the body uses continuous collision detection.</summary>
    public bool Continuous;

    /// <summary>Gets the surface of the chunks of voxels: the default material, in every layer, colliding with every layer.</summary>
    public static Surface Voxels
    {
        [HotPath]
        get => new()
    {
        Friction = PhysicsMaterial.Default.Friction,
        Restitution = PhysicsMaterial.Default.Restitution,
        Layers = uint.MaxValue,
        Mask = uint.MaxValue,
        Body = -1,
    };
    }
}

/// <summary>
/// The callbacks of the narrow phase: they filter pairs by collision layers,
/// give each pair its material, and note the pairs that bounce. Friction
/// combines as the geometric mean of the two bodies, restitution as the larger
/// of the two; <see cref="Bounces"/> applies the restitution after the step.
/// </summary>
/// <remarks>
/// The contacts are springs of <see cref="ContactFrequency"/>. A pair that
/// holds a body with continuous detection gets the stiffest spring the
/// substeps can integrate, half their rate: a softer contact stops a fast body
/// too late, and lets it through a thin wall.
/// </remarks>
internal struct NarrowPhaseCallbacks(CollidableProperty<Surface> surfaces, Bounces bounces) : INarrowPhaseCallbacks
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

        return true;
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

        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    /// <inheritdoc/>
    public readonly void Dispose()
    {
    }
}

/// <summary>The callbacks of the integration of poses: the gravity of the world, and no damping.</summary>
internal struct PoseIntegratorCallbacks(Vector3 gravity) : IPoseIntegratorCallbacks
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
    public void PrepareForIntegration(float dt) => _gravity = Vector3Wide.Broadcast(gravity);

    /// <inheritdoc/>
    public readonly void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity) =>
        velocity.Linear += _gravity * dt;
}
