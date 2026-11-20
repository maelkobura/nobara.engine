// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Physics.Bepu.Tests.Support;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>Hinges, sliders, and fixed joints, with and without a motor, between bodies and static bodies.</summary>
public sealed class JointTests
{
    [Fact]
    public void AMotorizedHingeHoldsItsTargetVelocityWithoutDriftingOverAThousandSteps()
    {
        using var scene = new BepuScene();
        BodyHandle post = scene.Add(WorldPosition.FromBlocks(16, 10, 16), scene.Physics.CreateBox(new Vector3(0.2f, 2, 0.2f)), BodyKind.Static);
        BodyHandle door = scene.Add(WorldPosition.FromBlocks(17, 10, 16), scene.Physics.CreateBox(new Vector3(1.6f, 2, 0.1f)), mass: 20);   // clear of the post
        scene.Physics.CreateJoint(post, door, new JointDescription(
            JointKind.Hinge,
            AnchorA: Vector3.Zero,
            AnchorB: new Vector3(-1, 0, 0),
            AxisA: Vector3.UnitY,
            AxisB: Vector3.UnitY,
            Motor: new JointMotor(TargetVelocity: 1.5f, MaxForce: 2000)));
        scene.Tick(30);

        float slowest = float.MaxValue;
        float fastest = float.MinValue;
        float drift = 0;
        for (int i = 0; i < 1000; i++)
        {
            scene.Tick();
            BodyState state = scene.State(door);
            slowest = MathF.Min(slowest, state.AngularVelocity.Y);
            fastest = MathF.Max(fastest, state.AngularVelocity.Y);

            // The anchor of the door stays on the axis of the post.
            Vector3 anchor = state.Transform.Position.Local + Vector3.Transform(new Vector3(-1, 0, 0), state.Transform.Rotation);
            drift = MathF.Max(drift, new Vector2(anchor.X - 16, anchor.Z - 16).Length() + MathF.Abs(anchor.Y - 10));
        }

        Assert.InRange(slowest, 1.45f, 1.55f);
        Assert.InRange(fastest, 1.45f, 1.55f);
        Assert.True(drift < 0.02f, $"The hinge drifted by {drift} blocks.");
        Assert.Equal(16, scene.Local(post).X, 1e-4f);   // the static post never moves
    }

    [Fact]
    public void AMotorizedSliderMovesAtItsTargetVelocityAlongTheAxis()
    {
        using var scene = new BepuScene();
        BodyHandle rail = scene.Add(WorldPosition.FromBlocks(8, 10, 16), scene.Physics.CreateBox(new Vector3(0.5f)), BodyKind.Static);
        BodyHandle carriage = scene.Add(WorldPosition.FromBlocks(10, 10, 16), scene.Physics.CreateBox(new Vector3(0.5f)), mass: 5);
        scene.Physics.CreateJoint(rail, carriage, new JointDescription(
            JointKind.Slider,
            AnchorA: Vector3.Zero,
            AnchorB: Vector3.Zero,
            AxisA: Vector3.UnitX,
            AxisB: Vector3.UnitX,
            Motor: new JointMotor(TargetVelocity: 2, MaxForce: 1000)));

        scene.Tick(60);

        BodyState state = scene.State(carriage);
        Assert.Equal(2f, state.LinearVelocity.X, 0.05f);
        Assert.Equal(0f, state.LinearVelocity.Y, 0.05f);
        Assert.Equal(10f, scene.Local(carriage).Y, 0.02f);   // held against the gravity
        Assert.InRange(scene.Local(carriage).X, 13.5f, 14.2f);
        Assert.True(Quaternion.Dot(Quaternion.Identity, state.Transform.Rotation) > 0.9999f);
    }

    [Fact]
    public void RemovingTheMotorOfAHingeLetsItSwingFreely()
    {
        using var scene = new BepuScene();
        BodyHandle post = scene.Add(WorldPosition.FromBlocks(16, 10, 16), scene.Physics.CreateSphere(0.1f), BodyKind.Static);
        BodyHandle arm = scene.Add(WorldPosition.FromBlocks(17.2, 10, 16), scene.Physics.CreateBox(new Vector3(2, 0.2f, 0.2f)), mass: 2);
        JointHandle hinge = scene.Physics.CreateJoint(post, arm, new JointDescription(
            JointKind.Hinge, Vector3.Zero, new Vector3(-1.2f, 0, 0), Vector3.UnitZ, Vector3.UnitZ, new JointMotor(0, 1000)));
        scene.Tick(30);
        Assert.Equal(10f, scene.Local(arm).Y, 0.05f);   // a motor at zero speed holds it level

        scene.Physics.SetMotor(hinge, null);
        scene.Tick(15);

        Assert.True(scene.Local(arm).Y < 9.5f);   // it swings down
    }

    [Fact]
    public void AFixedJointKeepsTheBodiesTogether()
    {
        using var scene = new BepuScene();
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        BodyHandle first = scene.Add(WorldPosition.FromBlocks(16, 3, 16), crate);
        BodyHandle second = scene.Add(WorldPosition.FromBlocks(17.5, 3.5, 16), crate, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f));
        scene.Physics.CreateJoint(first, second, new JointDescription(JointKind.Fixed, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero));
        scene.Ground();

        scene.Tick(90);

        BodyState a = scene.State(first);
        BodyState b = scene.State(second);
        Vector3 offset = Vector3.Transform(b.Transform.Position.Local - a.Transform.Position.Local, Quaternion.Conjugate(a.Transform.Rotation));
        Assert.Equal(1.5f, offset.X, 0.02f);
        Assert.Equal(0.5f, offset.Y, 0.02f);
        Assert.Equal(0f, offset.Z, 0.02f);
        Quaternion relative = Quaternion.Conjugate(a.Transform.Rotation) * b.Transform.Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(relative, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f))) > 0.9999f);
    }

    [Fact]
    public void AStaticBodyKeepsItsCollisionsWhileAJointHoldsItAndAfter()
    {
        using var scene = new BepuScene();
        PhysicsShape slab = scene.Physics.CreateBox(new Vector3(6, 1, 6));
        BodyHandle floor = scene.Add(WorldPosition.FromBlocks(16, 4, 16), slab, BodyKind.Static);
        BodyHandle crate = scene.Add(WorldPosition.FromBlocks(16, 6, 16), scene.Physics.CreateBox(Vector3.One));
        BodyHandle pendulum = scene.Add(WorldPosition.FromBlocks(22, 4, 16), scene.Physics.CreateSphere(0.25f));
        JointHandle joint = scene.Physics.CreateJoint(floor, pendulum, new JointDescription(
            JointKind.Hinge, new Vector3(3, 0, 0), new Vector3(-3, 0, 0), Vector3.UnitZ, Vector3.UnitZ));
        scene.Tick(60);

        Assert.Equal(5f, scene.Local(crate).Y, 0.05f);   // on the slab, held by a joint
        Assert.Equal(16f, scene.Local(floor).X, 1e-4f);
        Assert.Equal(4f, scene.Local(floor).Y, 1e-4f);

        scene.Physics.DestroyJoint(joint);
        scene.Tick(30);

        Assert.Equal(5f, scene.Local(crate).Y, 0.05f);   // on the slab, a static of the backend again
        Assert.True(scene.Height(pendulum) < 0);        // falls, free, beside the slab
    }
}
