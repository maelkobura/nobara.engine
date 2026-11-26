// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Motion.Tests.Support;
using Nobara.Physics;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Motion.Tests;

/// <summary>Grids that Motion moves, simulated by nPhysics and BEPUphysics.</summary>
[Collection(Serial.Name)]
public sealed class GridBodyTests(ITestOutputHelper output)
{
    [Fact]
    public void ADynamicGridFallsOnAStaticBodyAndRestsOnIt()
    {
        using var scene = new MotionScene();
        scene.StaticBox(scene.At(16, 4.5, 16), new Vector3(20, 1, 20));   // its top at y = 5
        Grid grid = scene.Grid(scene.At(14, 12, 14), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        scene.Motion.Add(grid);
        Assert.False(scene.Motion.GetState(grid).IsSmall);

        scene.Tick(150);

        Assert.InRange(scene.Height(grid), 4.95, 5.05);
        GridMotionState state = scene.Motion.GetState(grid);
        Assert.True(state.IsAsleep || state.LinearVelocity.Length() < 0.01f);
        Assert.True(Vector3.Transform(Vector3.UnitY, state.Transform.Rotation).Y > 0.999f);
    }

    [Fact]
    public void ACharacterStaysOnAKinematicGridThatMotionTurns()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 4, 16), scene.State("stone"), MotionScene.Box(-3, 0, -3, 3, 0, 3));
        scene.Motion.Add(grid, GridMotionMode.Kinematic);
        Assert.True(scene.Probe.Entities.TryGetComponent(out ComponentType<CharacterState> characterState));
        EntityId character = scene.Probe.CreateEntity(WorldTransform.At(grid.Transform.TransformPoint(new Vector3(2.5f, 1.2f, 0.5f))));
        scene.Physics.AddCharacter(DimensionId.Default, character, CharacterBody.Default);
        scene.Tick(10);
        Assert.Equal(grid.Entity, scene.Probe.World.Read(character, characterState).Support);
        Vector3 start = scene.Probe.World.Read(character, scene.Probe.Entities.LocalTransform).Position;

        scene.Motion.SetVelocity(grid, Vector3.Zero, new Vector3(0, 1, 0));   // one radian per second, about the center of mass
        float drift = 0;
        int grounded = 0;
        for (int tick = 0; tick < 300; tick++)
        {
            scene.Tick();
            CharacterState state = scene.Probe.World.Read(character, characterState);
            grounded += state.IsGrounded ? 1 : 0;
            drift = MathF.Max(drift, Vector3.Distance(scene.Probe.World.Read(character, scene.Probe.Entities.LocalTransform).Position, start));
        }

        output.WriteLine($"Drift on the grid: {drift} blocks; turn: {2 * MathF.Acos(MathF.Abs(grid.Transform.Rotation.W))} rad.");
        Assert.Equal(300, grounded);
        Assert.True(drift < 1e-3f, $"The character drifted by {drift} blocks on the grid.");
        Assert.Equal(grid.Entity, scene.Probe.World.Read(character, characterState).Support);
        Assert.True(MathF.Abs(grid.Transform.Rotation.Y) > 0.5f, "The grid did not turn.");
    }

    [Fact]
    public void AKinematicGridBecomesDynamicAndFalls()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(14, 12, 14), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        scene.Motion.Add(grid, GridMotionMode.Kinematic);
        scene.Tick(20);
        Assert.InRange(scene.Height(grid), 11.99, 12.01);   // a kinematic grid without velocity stays

        scene.Motion.SetMode(grid, GridMotionMode.Dynamic);
        scene.Tick(15);

        Assert.True(scene.Height(grid) < 11, $"The grid is at {scene.Height(grid)}.");
        Assert.Equal(GridMotionMode.Dynamic, scene.Motion.GetState(grid).Mode);
    }

    [Fact]
    public void ASmallGridBecomesAVoxelShapePastTwiceTheThresholdAndComesBack()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 10, 16), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 0));   // 4 blocks
        BodyHandle body = scene.Motion.Add(grid, GridMotionMode.Kinematic);
        Assert.True(scene.Motion.GetState(grid).IsSmall);
        Assert.Equal(ShapeKind.Compound, Shape(scene, body).Kind);

        scene.Place(grid, scene.State("stone"), MotionScene.Box(0, 1, 0, 4, 1, 1));   // 14 blocks: still small
        scene.Tick(2);
        Assert.True(scene.Motion.GetState(grid).IsSmall);
        Assert.Equal(14, Assert.IsType<int>(scene.Motion.GetState(grid).Blocks));

        scene.Place(grid, scene.State("stone"), MotionScene.Box(0, 2, 0, 3, 2, 0));   // 18 blocks: past 16
        scene.Tick(2);
        Assert.False(scene.Motion.GetState(grid).IsSmall);
        Assert.Equal(ShapeKind.Voxels, Shape(scene, body).Kind);

        scene.Break(grid, MotionScene.Box(0, 2, 0, 3, 2, 0).Concat(MotionScene.Box(0, 1, 0, 4, 1, 1)[..5]).ToArray());   // 9 blocks: still a voxel shape
        scene.Tick(2);
        Assert.False(scene.Motion.GetState(grid).IsSmall);

        scene.Break(grid, MotionScene.Box(0, 1, 0, 4, 1, 1)[5]);   // 8 blocks: small again
        scene.Tick(2);
        Assert.True(scene.Motion.GetState(grid).IsSmall);
        Assert.Equal(ShapeKind.Compound, Shape(scene, body).Kind);
    }

    [Fact]
    public void TheCenterOfMassMovesWithoutMovingTheGrid()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(14, 12, 14), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        scene.Motion.Add(grid);
        scene.Motion.SetGravityScale(grid, 0);
        scene.Tick(2);
        WorldPosition before = grid.Transform.Position;
        Vector3 center = scene.Motion.GetState(grid).CenterOfMass;

        scene.Place(grid, scene.State("lead"), MotionScene.Box(3, 1, 0, 3, 1, 3));
        scene.Tick(2);

        Assert.NotEqual(center, scene.Motion.GetState(grid).CenterOfMass);
        Assert.True(grid.Transform.Position.RelativeTo(before).Length() < 1e-3f, $"The grid moved by {grid.Transform.Position.RelativeTo(before)}.");
    }

    [Fact]
    public void AGravityScaleOfZeroHoldsAGridAndAFrozenGridStays()
    {
        using var scene = new MotionScene();
        Grid floating = scene.Grid(scene.At(10, 12, 10), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        Grid frozen = scene.Grid(scene.At(20, 12, 20), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        scene.Motion.Add(floating);
        scene.Motion.Add(frozen);
        scene.Motion.SetGravityScale(floating, 0);
        scene.Motion.Freeze(frozen);

        scene.Tick(30);

        Assert.InRange(scene.Height(floating), 11.99, 12.01);
        Assert.InRange(scene.Height(frozen), 11.99, 12.01);
        Assert.True(scene.Motion.GetState(frozen).IsFrozen);

        scene.Motion.Release(frozen);
        scene.Tick(15);
        Assert.True(scene.Height(frozen) < 11);
    }

    [Fact]
    public void AGridSplitFromAMovingGridKeepsItsVelocity()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(8, 12, 16), scene.State("stone"), MotionScene.Box(0, 0, 0, 7, 0, 1));
        scene.Motion.Add(grid);
        scene.Motion.SetGravityScale(grid, 0);
        scene.Motion.SetVelocity(grid, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(5);

        Grid split = scene.Motion.Split(scene.Probe.Context, grid, MotionScene.Box(4, 0, 0, 7, 0, 1));
        scene.Motion.SetGravityScale(split, 0);
        scene.Tick(3);

        Assert.Equal(8, scene.Motion.GetState(split).Blocks);
        Assert.Equal(8, scene.Motion.GetState(grid).Blocks);
        Assert.Equal(2f, scene.Motion.GetState(split).LinearVelocity.X, 1);
        Assert.Equal(2f, scene.Motion.GetState(grid).LinearVelocity.X, 1);
    }

    [Fact]
    public void AGridAbsorbedByAMergeStopsBeingABody()
    {
        using var scene = new MotionScene();
        Grid target = scene.Grid(scene.At(10, 12, 16), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        Grid absorbed = scene.Grid(scene.At(20, 12, 16), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        scene.Motion.Add(target, GridMotionMode.Kinematic);
        BodyHandle body = scene.Motion.Add(absorbed, GridMotionMode.Kinematic);
        int shapes = scene.Physics.Statistics.Shapes;

        scene.Probe.Voxels.Merge(scene.Probe.Context, target, absorbed, 0, new BlockCoordinate(0, 1, 0));
        scene.Tick(5);

        Assert.False(scene.Motion.Contains(absorbed));
        Assert.Single(scene.Motion.Grids);
        Assert.False(scene.Physics.IsAlive(body));
        Assert.Equal(32, scene.Motion.GetState(target).Blocks);
        Assert.True(scene.Physics.Statistics.Shapes <= shapes, $"{scene.Physics.Statistics.Shapes - shapes} shapes leaked.");
    }

    [Fact]
    public void RefusesGridsThatCannotMove()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(10, 12, 16), scene.State("stone"), new BlockCoordinate(0, 0, 0));
        Grid scaled = scene.Grid(new WorldTransform(scene.At(20, 12, 16), Quaternion.Identity, new Vector3(2)), scene.State("stone"), new BlockCoordinate(0, 0, 0));
        Assert.True(scene.Probe.Voxels.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        scene.Motion.Add(grid);

        Assert.Throws<ArgumentException>(() => scene.Motion.Add(grid));
        Assert.Throws<ArgumentException>(() => scene.Motion.Add(terrain!));
        Assert.Throws<ArgumentException>(() => scene.Motion.Add(scaled));
        Assert.Throws<InvalidOperationException>(() => scene.Motion.SetTarget(grid, scene.At(0, 0, 0), Quaternion.Identity));
        scene.Motion.Remove(grid);
        Assert.False(scene.Motion.Contains(grid));
        Assert.Throws<ArgumentException>(() => scene.Motion.GetState(grid));
    }

    [Theory]
    [InlineData(1_000_000_000, -1_000_000_000)]
    [InlineData(-1_000_000_000, 1_000_000_000)]
    public void FiveHundredSmallGridsFallOnAFloorOfBoxesAndSleepWithoutAllocating(int x, int z)
    {
        using var scene = new MotionScene(new ChunkArea(3, 1), WorldPosition.FromBlocks(x + 16, 16, z + 16));
        scene.StaticBox(scene.At(16, 2, 16), new Vector3(90, 1, 90));   // its top at y = 2.5
        uint wood = scene.State("wood");
        for (int i = 0; i < 500; i++)
        {
            Grid grid = scene.Grid(scene.At(-20 + (3 * (i % 25)), 4, -14 + (3 * (i / 25))), wood, new BlockCoordinate(0, 0, 0), new BlockCoordinate(1, 0, 0));
            scene.Motion.Add(grid);
        }

        int asleep = 0;
        for (int tick = 0; tick < 400 && asleep < 500; tick += 10)
        {
            scene.Tick(10);
            asleep = scene.Motion.Grids.Count(grid => scene.Motion.GetState(grid).IsAsleep);
        }

        Assert.Equal(0, scene.Physics.Statistics.FrozenBodies);
        Assert.Equal(500, asleep);
        Assert.All(scene.Motion.Grids, grid => Assert.True(scene.Motion.GetState(grid).IsSmall));
        double lowest = scene.Motion.Grids.Min(scene.Height);
        Assert.True(lowest > 2.4, $"A grid fell to {lowest}.");
        scene.Tick(10);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        scene.Tick(30);
        Assert.Equal(0, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    private static ShapeDescription Shape(MotionScene scene, BodyHandle body)
    {
        Assert.True(scene.Probe.Entities.TryGetComponent(out ComponentType<BodyShape> shape));
        return scene.Physics.GetShape(scene.Probe.World.Read(scene.Physics.GetState(body).Entity, shape).Shape);
    }
}
