// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Tests.Support;

namespace Nobara.Physics.Tests;

/// <summary>The step: commands, then the step of each world, then the transforms and the end of the step.</summary>
public sealed class StepTests : IDisposable
{
    private const float Delta = 1f / 30;
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AppliesCommandsBeforeTheStepAndWritesTransformsAfterIt()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(1, 10, 1), BodyKind.Kinematic);
        var observer = new RecordingObserver(probe);
        using PhysicsStepSubscription subscription = probe.Physics.SubscribeStepEnd(probe.Context, observer);

        probe.Physics.SetVelocity(body, new Vector3(3, 0, 0), Vector3.Zero);
        Assert.Equal(1f, probe.TransformOf(body).Position.Local.X);
        Assert.DoesNotContain(Assert.Single(_harness.Backend.Worlds).Calls, c => c.StartsWith("velocity", StringComparison.Ordinal));

        _harness.Tick(engine);

        List<string> calls = Assert.Single(_harness.Backend.Worlds).Calls;
        Assert.True(calls.FindIndex(c => c.StartsWith("velocity", StringComparison.Ordinal)) < calls.IndexOf("step"));
        Assert.Equal(1 + (3 * Delta), probe.TransformOf(body).Position.Local.X, 5);
        Assert.Equal(new Vector3(3, 0, 0), probe.Physics.GetState(body).LinearVelocity);

        BodyPose pose = Assert.Single(observer.Poses);
        Assert.Equal(body, pose.Body);
        Assert.Equal(probe.TransformOf(body), pose.Transform);
        Assert.Equal(probe.TransformOf(body), observer.TransformSeen);   // written before the end of the step is published
    }

    [Fact]
    public void TeleportsAtOnceAndKeepsTheVelocities()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0), BodyKind.Kinematic);
        probe.Physics.SetVelocity(body, new Vector3(0, 0, 6), Vector3.Zero);
        _harness.Tick(engine);

        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        probe.Physics.Teleport(body, WorldPosition.FromBlocks(100, 50, 100), turned);
        _harness.Tick(engine);

        WorldTransform transform = probe.TransformOf(body);
        Assert.Equal(WorldPosition.FromBlocks(100, 50, 100 + (6 * Delta)).ChunkX, transform.Position.ChunkX);
        Assert.Equal((100 % 32) + 0f, transform.Position.Local.X, 4);
        Assert.Equal((100 % 32) + (6 * Delta), transform.Position.Local.Z, 4);
        Assert.True(Quaternion.Dot(turned, transform.Rotation) > 0.99999f);
    }

    [Fact]
    public void TeleportsAStaticBody()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle wall = probe.CreateBody(WorldPosition.FromBlocks(0, 0, 0), BodyKind.Static);

        probe.Physics.Teleport(wall, WorldPosition.FromBlocks(5, 6, 7), Quaternion.Identity);
        _harness.Tick(engine);

        Assert.Equal(WorldPosition.FromBlocks(5, 6, 7), probe.TransformOf(wall).Position);
    }

    [Fact]
    public void StepsOncePerTickWithTheSubstepsOfTheDistribution()
    {
        _harness.Settings[PhysicsSettings.Substeps] = "3";
        _harness.Settings[PhysicsSettings.VelocityIterations] = "2";
        using Engine engine = _harness.Start();
        _harness.Probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0));

        _harness.Tick(engine, 2);

        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        Assert.Equal(2, world.StepCount);
        Assert.Equal(6, world.SubstepCount);
        PhysicsStepSettings settings = world.Steps[0];
        Assert.Equal(Delta, settings.Delta, 6);
        Assert.Equal(3, settings.Substeps);
        Assert.Equal(2, settings.VelocityIterations);
        Assert.Equal(3, _harness.Physics.Substeps);
        Assert.Equal(2UL, _harness.Physics.Statistics.Steps);
    }

    [Fact]
    public void DoesNotStepWhileTheGameIsPausedAndKeepsTheStepUnderATimeScale()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0));
        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);

        probe.Time.Paused = true;
        _harness.Tick(engine, 3);
        Assert.Equal(0, world.StepCount);

        probe.Time.Paused = false;
        probe.Time.TimeScale = 2;
        _harness.Tick(engine, 3);
        Assert.Equal(6, world.StepCount);
        Assert.All(world.Steps, s => Assert.Equal(Delta, s.Delta, 6));
    }

    [Fact]
    public void BringsAKinematicBodyToItsTargetThenStopsIt()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle lift = probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0), BodyKind.Kinematic);
        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);

        probe.Physics.SetKinematicTarget(lift, WorldPosition.FromBlocks(0, 12, 1), turned);
        _harness.Tick(engine);

        WorldTransform reached = probe.TransformOf(lift);
        Assert.Equal(12f, reached.Position.Local.Y, 3);
        Assert.Equal(1f, reached.Position.Local.Z, 3);
        Assert.True(MathF.Abs(Quaternion.Dot(turned, reached.Rotation)) > 0.9999f);

        _harness.Tick(engine, 2);
        Assert.Equal(12f, probe.TransformOf(lift).Position.Local.Y, 3);
        Assert.Equal(Vector3.Zero, probe.Physics.GetState(lift).LinearVelocity);
        Assert.Throws<ArgumentException>(() => probe.Physics.SetKinematicTarget(probe.CreateBody(WorldPosition.FromBlocks(0, 0, 0)), WorldPosition.FromBlocks(0, 1, 0), Quaternion.Identity));
    }

    [Fact]
    public void TurnsForcesIntoImpulsesOverTheStepAndPlacesThemOnTheBody()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(10, 10, 10), mass: 2);

        probe.Physics.ApplyForce(body, new Vector3(60, 0, 0));
        probe.Physics.ApplyImpulseAt(body, new Vector3(0, 0, 4), WorldPosition.FromBlocks(10, 11, 10));
        _harness.Tick(engine);

        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        Assert.Equal(2, world.Calls.Count(c => c.StartsWith("impulse 0 ", StringComparison.Ordinal)));
        Assert.Equal(new Vector3(0, 1, 0), world.Body(0).LastOffset);
        Vector3 velocity = probe.Physics.GetState(body).LinearVelocity;
        Assert.Equal(60 * Delta / 2, velocity.X, 4);
        Assert.Equal(2f, velocity.Z, 4);
        Assert.Throws<ArgumentException>(() => probe.Physics.ApplyForce(probe.CreateBody(WorldPosition.FromBlocks(0, 0, 0), BodyKind.Kinematic), Vector3.UnitX));
    }

    [Fact]
    public void ReportsSleepingBodiesAndStopsWritingThem()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0));
        var observer = new RecordingObserver(probe);
        using PhysicsStepSubscription subscription = probe.Physics.SubscribeStepEnd(probe.Context, observer);
        _harness.Tick(engine);
        Assert.Single(observer.Poses);

        Assert.Single(_harness.Backend.Worlds).Sleep(0);
        _harness.Tick(engine);
        Assert.True(probe.Physics.GetState(body).IsAsleep);
        Assert.Equal(1, probe.Physics.Statistics.SleepingBodies);
        Assert.Single(observer.Poses);   // the pose where it fell asleep

        _harness.Tick(engine);
        Assert.Empty(observer.Poses);

        probe.Physics.Wake(body);
        _harness.Tick(engine);
        Assert.False(probe.Physics.GetState(body).IsAsleep);
        Assert.Single(observer.Poses);
    }

    [Fact]
    public void WakesTheBodiesOfAnAreaInTheFrameOfTheirWorld()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.CreateBody(WorldPosition.FromBlocks(40, 10, 0));

        probe.Physics.WakeArea(DimensionId.Default, Voxel.WorldBox.Around(WorldPosition.FromBlocks(41, 10, 0), Vector3.One));
        _harness.Tick(engine);

        Assert.Contains($"wake area {new Vector3(40, 9, -1)} {new Vector3(42, 11, 1)}", Assert.Single(_harness.Backend.Worlds).Calls);
    }

    private sealed class RecordingObserver(PhysicsProbe probe) : IPhysicsStepObserver
    {
        public List<BodyPose> Poses { get; } = [];

        public WorldTransform TransformSeen { get; private set; }

        public void OnStepEnd(in PhysicsStepEnd step)
        {
            Poses.Clear();
            foreach (BodyPose pose in step.Bodies)
            {
                Poses.Add(pose);
                TransformSeen = probe.TransformOf(pose.Body);
            }
        }
    }
}
