// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Tests.Support;

namespace Nobara.Physics.Tests;

/// <summary>Shapes, joints, providers of medium, and the absence of allocation once the step is stable.</summary>
public sealed class JointAndShapeTests : IDisposable
{
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ChecksAndSharesShapes()
    {
        using Engine engine = _harness.Start();
        IPhysicsSystem physics = _harness.Physics;

        Assert.Throws<ArgumentOutOfRangeException>(() => physics.CreateSphere(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.CreateBox(new Vector3(1, float.NaN, 1)));
        Assert.Throws<ArgumentException>(() => physics.CreateConvexHull([Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitX + Vector3.UnitY]));
        Assert.Throws<ArgumentException>(() => physics.CreateMesh([Vector3.Zero, Vector3.UnitX], [0, 1, 2]));

        PhysicsShape hull = physics.CreateConvexHull([Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]);
        PhysicsShape capsule = physics.CreateCapsule(0.3f, 1.2f);
        PhysicsShape compound = physics.CreateCompound([new CompoundChild(hull, Vector3.Zero, Quaternion.Identity), new CompoundChild(capsule, Vector3.UnitY, new Quaternion(0, 0, 0, 2))]);
        ShapeDescription description = physics.GetShape(compound);
        Assert.Equal(ShapeKind.Compound, description.Kind);
        Assert.Equal(Quaternion.Identity, description.Children.Span[1].Rotation);
        Assert.Throws<ArgumentException>(() => physics.CreateCompound([new CompoundChild(compound, Vector3.Zero, Quaternion.Identity)]));

        Assert.Throws<InvalidOperationException>(() => physics.DestroyShape(hull));   // the compound holds it
        physics.DestroyShape(compound);
        physics.DestroyShape(hull);
        Assert.Throws<ArgumentException>(() => physics.GetShape(hull));
        Assert.NotEqual(hull, physics.CreateSphere(1));
        Assert.Equal(2, physics.Statistics.Shapes);   // the capsule and the new sphere
    }

    [Fact]
    public void JoinsBodiesAndDrivesTheirMotorBeforeTheStep()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle frame = probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0), BodyKind.Static);
        BodyHandle wheel = probe.CreateBody(WorldPosition.FromBlocks(2, 10, 0));

        JointHandle hinge = probe.Physics.CreateJoint(frame, wheel, new JointDescription(JointKind.Hinge, new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 0, 3), new Vector3(0, 0, 3), new JointMotor(2, 50)));

        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        BackendJoint joint = Assert.Single(world.Joints)!.Value;
        Assert.Equal((0, 1), (joint.BodyA, joint.BodyB));
        Assert.Equal(Vector3.UnitZ, joint.AxisA);
        Assert.Equal(new JointMotor(2, 50), joint.Motor);
        Assert.Equal(1, probe.Physics.Statistics.Joints);

        probe.Physics.SetMotor(hinge, new JointMotor(-1, 10));
        Assert.Equal(new JointMotor(2, 50), world.Joints[0]!.Value.Motor);
        _harness.Tick(engine);
        Assert.Equal(new JointMotor(-1, 10), world.Joints[0]!.Value.Motor);

        Assert.Throws<ArgumentException>(() => probe.Physics.CreateJoint(frame, frame, new JointDescription(JointKind.Fixed, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero)));
        Assert.Throws<ArgumentException>(() => probe.Physics.CreateJoint(frame, wheel, new JointDescription(JointKind.Fixed, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, new JointMotor(1, 1))));
        Assert.Throws<ArgumentException>(() => probe.Physics.CreateJoint(frame, wheel, new JointDescription(JointKind.Slider, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.UnitX)));

        probe.Physics.RemoveBody(wheel);
        Assert.False(probe.Physics.IsAlive(hinge));
        Assert.Null(world.Joints[0]);
        Assert.Equal(0, probe.Physics.Statistics.Joints);
    }

    [Fact]
    public void AsksTheProvidersOfMediumInOrder()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        var water = new Medium(y => y < 10, new MediumSample(1000, 1, Vector3.UnitX));
        var air = new Medium(_ => true, new MediumSample(1.2f, 0, Vector3.Zero));
        MediumProviderHandle first = probe.Physics.AddMediumProvider(probe.Context, water);
        using MediumProviderHandle second = probe.Physics.AddMediumProvider(probe.Context, air);

        Assert.True(probe.Physics.TrySampleMedium(DimensionId.Default, WorldPosition.FromBlocks(0, 5, 0), out MediumSample sample));
        Assert.Equal(1000, sample.Density);
        Assert.True(probe.Physics.TrySampleMedium(DimensionId.Default, WorldPosition.FromBlocks(0, 50, 0), out sample));
        Assert.Equal(1.2f, sample.Density);

        first.Dispose();
        Assert.True(probe.Physics.TrySampleMedium(DimensionId.Default, WorldPosition.FromBlocks(0, 5, 0), out sample));
        Assert.Equal(1.2f, sample.Density);
    }

    [Fact]
    public void AllocatesNothingPerTickOnceStable()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        _harness.Backend.Records = false;
        var bodies = new BodyHandle[100];
        for (int i = 0; i < bodies.Length; i++)
        {
            bodies[i] = probe.CreateBody(WorldPosition.FromBlocks(i * 2, 10, 0), i % 4 == 0 ? BodyKind.Kinematic : BodyKind.Dynamic);
        }

        using PhysicsStepSubscription subscription = probe.Physics.SubscribeStepEnd(probe.Context, new CountingObserver());
        Commands(probe, bodies);
        _harness.Tick(engine, 10);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = 0; tick < 30; tick++)
        {
            Commands(probe, bodies);
            _harness.Tick(engine);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static void Commands(PhysicsProbe probe, BodyHandle[] bodies)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            if (i % 4 == 0)
            {
                probe.Physics.SetKinematicTarget(bodies[i], WorldPosition.FromBlocks(i * 2, 11, 0), Quaternion.Identity);
            }
            else
            {
                probe.Physics.ApplyForce(bodies[i], new Vector3(0, 9.81f, 0));
            }
        }
    }

    private sealed class Medium(Func<float, bool> covers, MediumSample sample) : IMediumProvider
    {
        public bool TrySample(DimensionId dimension, in WorldPosition position, out MediumSample result)
        {
            result = sample;
            return covers((position.ChunkY * 32) + position.Local.Y);
        }
    }

    private sealed class CountingObserver : IPhysicsStepObserver
    {
        public int Bodies { get; private set; }

        public void OnStepEnd(in PhysicsStepEnd step) => Bodies += step.Bodies.Length;
    }
}
