// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Motion.Tests.Support;
using Nobara.Physics;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Motion.Tests;

/// <summary>Hinges, sliders, and fixed joints between grids, and between a grid and the terrain.</summary>
[Collection(Serial.Name)]
public sealed class JointTests(ITestOutputHelper output)
{
    [Fact]
    public void AMotorizedHingeTurnsAGridOnTheAxisOfItsCarrierForTenThousandSteps()
    {
        using var scene = new MotionScene();
        uint stone = scene.State("stone");
        Grid carrier = scene.Grid(scene.At(16, 11.2, 16), stone, new BlockCoordinate(0, 0, 0));
        Grid turntable = scene.Grid(scene.At(15, 10, 15), stone, MotionScene.Box(0, 0, 0, 2, 0, 2));
        scene.Motion.Add(carrier, GridMotionMode.Kinematic);
        scene.Motion.Add(turntable);
        var anchor = new Vector3(1.5f, 1, 1.5f);   // the middle of the top of the turntable, under the carrier
        GridJointHandle joint = scene.Motion.Link(turntable, carrier, new GridJointDescription(JointKind.Hinge, anchor, Vector3.UnitY, new JointMotor(TargetVelocity: 1, MaxForce: 1e6f)));
        WorldPosition axis = carrier.Transform.TransformPoint(new Vector3(0.5f, -0.2f, 0.5f));
        scene.Tick(30);

        float slowest = float.MaxValue;
        float fastest = float.MinValue;
        float drift = 0;
        for (int tick = 0; tick < 10_000; tick++)
        {
            scene.Tick();
            GridMotionState state = scene.Motion.GetState(turntable);
            slowest = MathF.Min(slowest, state.AngularVelocity.Y);
            fastest = MathF.Max(fastest, state.AngularVelocity.Y);
            drift = MathF.Max(drift, turntable.Transform.TransformPoint(anchor).RelativeTo(axis).Length());
        }

        output.WriteLine($"Hinge over 10,000 steps: {slowest} to {fastest} rad/s, the anchor at most {drift} blocks off the axis.");
        Assert.InRange(slowest, 0.97f, 1.03f);
        Assert.InRange(fastest, 0.97f, 1.03f);
        Assert.True(drift < 0.05f, $"The hinge drifted by {drift} blocks.");
        Assert.True(scene.Motion.IsLinked(joint));
    }

    [Fact]
    public void AGridTurnsOnTheAxisOfACarrierThatMoves()
    {
        using var scene = new MotionScene();
        uint stone = scene.State("stone");
        Grid carrier = scene.Grid(scene.At(10, 11.2, 16), stone, new BlockCoordinate(0, 0, 0));
        Grid turntable = scene.Grid(scene.At(9, 10, 15), stone, MotionScene.Box(0, 0, 0, 2, 0, 2));
        scene.Motion.Add(carrier, GridMotionMode.Kinematic);
        scene.Motion.Add(turntable);
        var anchor = new Vector3(1.5f, 1, 1.5f);
        scene.Motion.Link(turntable, carrier, new GridJointDescription(JointKind.Hinge, anchor, Vector3.UnitY, new JointMotor(TargetVelocity: 2, MaxForce: 1e6f)));
        scene.Tick(10);

        scene.Motion.SetVelocity(carrier, new Vector3(2, 0, 0), Vector3.Zero);
        float drift = 0;
        for (int tick = 0; tick < 90; tick++)
        {
            scene.Tick();
            WorldPosition axis = carrier.Transform.TransformPoint(new Vector3(0.5f, -0.2f, 0.5f));
            drift = MathF.Max(drift, turntable.Transform.TransformPoint(anchor).RelativeTo(axis).Length());
        }

        GridMotionState state = scene.Motion.GetState(turntable);
        Assert.Equal(2f, state.AngularVelocity.Y, 0.1f);
        Assert.Equal(2f, state.LinearVelocity.X, 0.1f);   // carried along
        Assert.True(drift < 0.05f, $"The hinge drifted by {drift} blocks.");
    }

    [Theory]
    [InlineData(16.0, 16.0)]
    [InlineData(1_000_000_016.0, -999_999_984.0)]
    public void AMotorizedSliderToTheTerrainPushesAGridToItsStop(double x, double z)
    {
        using var scene = new MotionScene(center: WorldPosition.FromBlocks(x, 16, z));
        uint stone = scene.State("stone");
        Grid wall = scene.Terrain;
        scene.Place(wall, stone, [.. MotionScene.Box(0, 0, 0, 0, 4, 4).Select(cell => ToTerrain(scene, cell, 22, 0, 14))]);
        Grid platform = scene.Grid(scene.At(14, 2, 15), stone, MotionScene.Box(0, 0, 0, 1, 0, 1));
        scene.Motion.Add(platform);
        GridJointHandle slider = scene.Motion.Link(platform, scene.Terrain, new GridJointDescription(JointKind.Slider, new Vector3(1, 0.5f, 1), Vector3.UnitX, new JointMotor(TargetVelocity: 2, MaxForce: 1e5f)));
        scene.Tick();

        float fastest = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            scene.Tick();
            fastest = MathF.Max(fastest, scene.Motion.GetState(platform).LinearVelocity.X);
        }

        Assert.InRange(fastest, 1.95f, 2.05f);
        scene.Tick(120);

        GridMotionState state = scene.Motion.GetState(platform);
        Vector3 offset = platform.Transform.Position.RelativeTo(scene.At(0, 0, 0));
        output.WriteLine($"Slider at its stop: origin {offset}, velocity {state.LinearVelocity}.");
        Assert.Equal(22f - 2, offset.X, 0.05f);   // its far face against the wall of the terrain
        Assert.Equal(2f, offset.Y, 0.05f);         // held up along the axis
        Assert.Equal(15f, offset.Z, 0.05f);
        Assert.True(MathF.Abs(state.LinearVelocity.X) < 0.05f);
        Assert.Equal(scene.Terrain, scene.Motion.GetJoint(slider).Carrier);
    }

    [Fact]
    public void AFixedJointHoldsTwoGridsThatFallTogether()
    {
        using var scene = new MotionScene();
        uint stone = scene.State("stone");
        Grid left = scene.Grid(scene.At(12, 6, 15), stone, MotionScene.Box(0, 0, 0, 1, 1, 1));
        Grid right = scene.Grid(new WorldTransform(scene.At(15, 6.5f, 15), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), Vector3.One), stone, MotionScene.Box(0, 0, 0, 1, 0, 1));
        scene.Motion.Add(left);
        scene.Motion.Add(right);
        scene.Motion.Link(right, left, new GridJointDescription(JointKind.Fixed, Vector3.Zero, Vector3.Zero));
        WorldTransform before = Relative(left, right);

        scene.Tick(120);

        WorldTransform after = Relative(left, right);
        Assert.True(scene.Height(left) < 1, $"The grids did not fall: {scene.Height(left)}.");   // on the ground, tilted by the higher grid
        Assert.True(after.Position.RelativeTo(before.Position).Length() < 0.03f, $"The grids moved {after.Position.RelativeTo(before.Position)} apart.");
        Assert.True(MathF.Abs(Quaternion.Dot(after.Rotation, before.Rotation)) > 0.9999f);
    }

    [Fact]
    public void AHingeStaysOnItsAxisWhenBlocksMoveTheCenterOfMass()
    {
        using var scene = new MotionScene();
        uint stone = scene.State("stone");
        Grid door = scene.Grid(scene.At(15, 4, 16), stone, MotionScene.Box(0, 0, 0, 1, 2, 0));
        scene.Motion.Add(door);
        var anchor = new Vector3(0, 1.5f, 0.5f);   // the edge of the door at X = 0
        scene.Motion.Link(door, scene.Terrain, new GridJointDescription(JointKind.Hinge, anchor, Vector3.UnitY, new JointMotor(TargetVelocity: 0.5f, MaxForce: 1e7f)));
        WorldPosition axis = door.Transform.TransformPoint(anchor);
        scene.Tick(10);

        scene.Place(door, stone, MotionScene.Box(2, 0, 0, 3, 2, 0));   // twice as wide: the center of mass moves away from the axis
        float drift = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            scene.Tick();
            drift = MathF.Max(drift, door.Transform.TransformPoint(anchor).RelativeTo(axis).Length());
        }

        Assert.Equal(12, scene.Motion.GetState(door).Blocks);
        Assert.True(drift < 0.05f, $"The hinge drifted by {drift} blocks.");
        Assert.Equal(0.5f, scene.Motion.GetState(door).AngularVelocity.Y, 0.05f);
    }

    [Fact]
    public void AJointGoesWithItsGridAndLeavesNothingBehind()
    {
        using var scene = new MotionScene();
        uint stone = scene.State("stone");
        Grid first = scene.Grid(scene.At(12, 6, 15), stone, MotionScene.Box(0, 0, 0, 1, 1, 1));
        Grid second = scene.Grid(scene.At(15, 6, 15), stone, MotionScene.Box(0, 0, 0, 1, 1, 1));
        scene.Motion.Add(first);
        scene.Motion.Add(second);
        int bodies = scene.Physics.Statistics.StaticBodies;
        GridJointHandle between = scene.Motion.Link(second, first, new GridJointDescription(JointKind.Fixed, Vector3.Zero, Vector3.Zero));
        GridJointHandle hanging = scene.Motion.Link(second, scene.Terrain, new GridJointDescription(JointKind.Hinge, new Vector3(1, 2, 1), Vector3.UnitZ));
        Assert.Equal(bodies + 1, scene.Physics.Statistics.StaticBodies);   // the anchor in the terrain
        scene.Tick(5);

        scene.Probe.Voxels.Destroy(scene.Probe.Context, second);
        scene.Tick(2);

        Assert.False(scene.Motion.IsLinked(between));
        Assert.False(scene.Motion.IsLinked(hanging));
        Assert.Throws<ArgumentException>(() => scene.Motion.GetJoint(hanging));
        Assert.Equal(bodies, scene.Physics.Statistics.StaticBodies);
        Assert.Equal(0, scene.Physics.Statistics.Joints);

        GridJointHandle again = scene.Motion.Link(first, scene.Terrain, new GridJointDescription(JointKind.Slider, Vector3.Zero, Vector3.UnitX));
        scene.Motion.Remove(first);
        Assert.False(scene.Motion.IsLinked(again));
        Assert.Equal(bodies, scene.Physics.Statistics.StaticBodies);
    }

    [Fact]
    public void LinkRefusesWhatCannotCarryAGrid()
    {
        using var scene = new MotionScene();
        uint stone = scene.State("stone");
        Grid grid = scene.Grid(scene.At(12, 6, 15), stone, MotionScene.Box(0, 0, 0, 1, 1, 1));
        Grid still = scene.Grid(scene.At(15, 6, 15), stone, MotionScene.Box(0, 0, 0, 1, 1, 1));
        scene.Motion.Add(grid);
        var hinge = new GridJointDescription(JointKind.Hinge, Vector3.Zero, Vector3.UnitY);

        Assert.Throws<ArgumentException>(() => scene.Motion.Link(grid, grid, hinge));
        Assert.Throws<ArgumentException>(() => scene.Motion.Link(grid, still, hinge));   // not a body of Motion
        Assert.Throws<ArgumentException>(() => scene.Motion.Link(still, scene.Terrain, hinge));
        Assert.Throws<ArgumentException>(() => scene.Motion.Link(grid, scene.Terrain, hinge with { Axis = Vector3.Zero }));
        Assert.Throws<ArgumentException>(() => scene.Motion.Link(grid, scene.Terrain, new GridJointDescription(JointKind.Fixed, Vector3.Zero, Vector3.Zero, new JointMotor(1, 1))));
        Assert.Equal(0, scene.Physics.Statistics.Joints);
        Assert.Equal(0, scene.Physics.Statistics.StaticBodies);   // no anchor left by a refused joint
    }

    [Fact]
    public void ChangingTheMotorOfAJointChangesItsSpeed()
    {
        using var scene = new MotionScene();
        Grid wheel = scene.Grid(scene.At(15, 10, 15), scene.State("stone"), MotionScene.Box(0, 0, 0, 2, 0, 2));
        scene.Motion.Add(wheel);
        GridJointHandle joint = scene.Motion.Link(wheel, scene.Terrain, new GridJointDescription(JointKind.Hinge, new Vector3(1.5f, 0.5f, 1.5f), Vector3.UnitY, new JointMotor(1, 1e6f)));
        scene.Tick(30);
        Assert.Equal(1f, scene.Motion.GetState(wheel).AngularVelocity.Y, 0.03f);

        scene.Motion.SetMotor(joint, new JointMotor(-2, 1e6f));
        scene.Tick(30);

        Assert.Equal(-2f, scene.Motion.GetState(wheel).AngularVelocity.Y, 0.03f);
        Assert.Equal(-2f, scene.Motion.GetJoint(joint).Description.Motor!.Value.TargetVelocity);
        scene.Motion.Unlink(joint);
        Assert.False(scene.Motion.IsLinked(joint));
        Assert.Throws<ArgumentException>(() => scene.Motion.SetMotor(joint, null));
    }

    [Fact]
    public void AKinematicGridLinkedToTheTerrainMovesAsItsModuleSays()
    {
        using var scene = new MotionScene();
        Grid lift = scene.Grid(scene.At(15, 3, 15), scene.State("stone"), MotionScene.Box(0, 0, 0, 1, 0, 1));
        scene.Motion.Add(lift, GridMotionMode.Kinematic);
        scene.Motion.Link(lift, scene.Terrain, new GridJointDescription(JointKind.Slider, Vector3.Zero, Vector3.UnitY, new JointMotor(1, 1e6f)));

        scene.Motion.SetVelocity(lift, new Vector3(1, 0, 0), Vector3.Zero);
        scene.Tick(30);

        GridMotionState state = scene.Motion.GetState(lift);
        Assert.Equal(16f, (float)lift.Transform.Position.RelativeTo(scene.At(0, 0, 0)).X, 0.05f);   // a kinematic grid ignores its joints
        Assert.True(float.IsFinite(state.LinearVelocity.X) && float.IsFinite(state.AngularVelocity.Y));
    }

    [Fact]
    public void MotorizedJointsAllocateNothingOnceRunning()
    {
        using var scene = new MotionScene();
        uint stone = scene.State("stone");
        Grid carrier = scene.Grid(scene.At(16, 11.2, 16), stone, new BlockCoordinate(0, 0, 0));
        Grid turntable = scene.Grid(scene.At(15, 10, 15), stone, MotionScene.Box(0, 0, 0, 2, 0, 2));
        Grid platform = scene.Grid(scene.At(6, 2, 6), stone, MotionScene.Box(0, 0, 0, 1, 0, 1));
        scene.Motion.Add(carrier, GridMotionMode.Kinematic);
        scene.Motion.Add(turntable);
        scene.Motion.Add(platform);
        scene.Motion.Link(turntable, carrier, new GridJointDescription(JointKind.Hinge, new Vector3(1.5f, 1, 1.5f), Vector3.UnitY, new JointMotor(1, 1e6f)));
        scene.Motion.Link(platform, scene.Terrain, new GridJointDescription(JointKind.Slider, Vector3.Zero, Vector3.UnitZ, new JointMotor(0.5f, 1e6f)));
        scene.Tick(60);

        long before = GC.GetAllocatedBytesForCurrentThread();
        scene.Tick(30);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Gets the transform of a grid in the frame of another.</summary>
    private static WorldTransform Relative(Grid frame, Grid grid)
    {
        WorldTransform a = frame.Transform;
        WorldTransform b = grid.Transform;
        Quaternion inverse = Quaternion.Conjugate(a.Rotation);
        return new WorldTransform(WorldPosition.FromBlocks(0, 0, 0).Offset(Vector3.Transform(b.Position.RelativeTo(a.Position), inverse)), inverse * b.Rotation, Vector3.One);
    }

    /// <summary>Gets a cell of the terrain from a cell of a box placed at a corner, from the corner of the chunk at the center of the scene.</summary>
    private static BlockCoordinate ToTerrain(MotionScene scene, BlockCoordinate cell, int x, int y, int z)
    {
        BlockCoordinate corner = VoxelCoordinates.ToBlock(scene.At(0, 0, 0));
        return new BlockCoordinate(corner.X + x + cell.X, corner.Y + y + cell.Y, corner.Z + z + cell.Z);
    }
}
