// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Physics.Bepu.Tests.Samples;

/// <summary>
/// The crates of a fictional farming game: a farmer picks the crate or the
/// block in sight, a scarecrow scares the crows near it, the sprinklers look
/// for the ground below them all at once, and a crate that falls too hard
/// breaks.
/// </summary>
public sealed class CratePhysics : IPhysicsStepObserver
{
    /// <summary>The layer of the crates.</summary>
    public const uint CrateLayer = 1u << 1;

    /// <summary>The layer of the crows.</summary>
    public const uint CrowLayer = 1u << 2;

    /// <summary>How far a farmer reaches, in blocks.</summary>
    public const float Reach = 5f;

    /// <summary>The slowest landing that breaks a crate, in blocks per second.</summary>
    public const float BreakingSpeed = 6f;

    private readonly IPhysicsSystem _physics;
    private readonly PhysicsShape _crate;
    private readonly RayQuery[] _sprinklers = new RayQuery[256];
    private readonly PhysicsHit[] _wetted = new PhysicsHit[256];
    private readonly PhysicsHit[] _near = new PhysicsHit[16];

    public CratePhysics(ModuleContext context)
    {
        _physics = context.Resolve<IPhysicsSystem>();
        _crate = _physics.CreateBox(new Vector3(0.8f));
        _physics.SubscribeStepEnd(context, this);
    }

    /// <summary>Gets the crates that broke, in the order they broke.</summary>
    public List<BodyHandle> Broken { get; } = [];

    /// <summary>Makes an entity a crate, which reports its landings.</summary>
    public BodyHandle AddCrate(EntityId entity) => _physics.AddBody(DimensionId.Default, entity, new BodyDescription
    {
        Shape = _crate,
        Mass = 15,
        Filter = new CollisionFilter(CrateLayer, uint.MaxValue),
        Contacts = new ContactReporting(ImpactSpeed: BreakingSpeed),
    });

    /// <summary>Gets what a farmer looks at, a block or a crate, within reach.</summary>
    public bool TryPick(in WorldPosition eye, Vector3 look, out PhysicsHit target) =>
        _physics.RayCast(DimensionId.Default, eye, look, Reach, out target, new QueryFilter(CrateLayer | CollisionFilter.VoxelLayer))
        && target.Kind != PhysicsHitKind.NotLoaded;

    /// <summary>Counts the crows that a scarecrow scares, within 3 blocks.</summary>
    public int Scare(in WorldPosition scarecrow)
    {
        int crows = _physics.Overlap(DimensionId.Default, QueryShape.Sphere(3), scarecrow, Quaternion.Identity, _near, new QueryFilter(CrowLayer));
        for (int i = 0; i < crows; i++)
        {
            _physics.ApplyImpulse(_near[i].Body, new Vector3(0, 5, 0));   // off it flies
        }

        return crows;
    }

    /// <summary>Finds the ground under each sprinkler, in one batch, on the workers.</summary>
    public ReadOnlySpan<PhysicsHit> Water(ReadOnlySpan<WorldPosition> sprinklers)
    {
        for (int i = 0; i < sprinklers.Length; i++)
        {
            _sprinklers[i] = new RayQuery(sprinklers[i], -Vector3.UnitY, 16, new QueryFilter(CollisionFilter.VoxelLayer));
        }

        _physics.RayCast(DimensionId.Default, _sprinklers.AsSpan(0, sprinklers.Length), _wetted);
        return _wetted.AsSpan(0, sprinklers.Length);
    }

    /// <inheritdoc/>
    public void OnStepEnd(in PhysicsStepEnd step)
    {
        foreach (ContactEvent impact in step.Impacts)
        {
            if (!Broken.Contains(impact.Body))
            {
                Broken.Add(impact.Body);   // a crate landed at the breaking speed or faster
            }
        }
    }
}
