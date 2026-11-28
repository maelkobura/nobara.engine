// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Motion.Tests.Support;
using Nobara.Physics;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Motion.Tests;

/// <summary>Grids against the terrain and against other grids: surface cells, the two modes, the budget, and the impacts.</summary>
[Collection(Serial.Name)]
public sealed class GridCollisionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_000_000_000, -1_000_000_000)]
    public void AGridFallsOnTheTerrainAndRestsWithoutJitter(int x, int z)
    {
        using var scene = new MotionScene(center: WorldPosition.FromBlocks(x + 16, 16, z + 16));
        Grid grid = scene.Grid(scene.At(16, 3, 16), scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 1, 2));
        scene.Motion.Add(grid);

        scene.Tick(90);
        WorldPosition settled = grid.Transform.Position;
        scene.Tick(30);

        Assert.InRange(scene.Height(grid), -0.06, 0.02);   // its bottom on the top of the ground, at y = 0
        Assert.True(grid.Transform.Position.RelativeTo(settled).Length() < 0.005f, "The grid still moves once at rest.");
        Assert.True(Vector3.Transform(Vector3.UnitY, grid.Transform.Rotation).Y > 0.999f);
        Assert.True(scene.Motion.GetState(grid).IsAsleep);
        Assert.Equal(VoxelContactMode.Blocks, ((MotionSystem)scene.Motion).CollisionOf(grid).Mode);
    }

    [Fact]
    public void AGridFallsOnAnotherGridAndRestsOnIt()
    {
        using var scene = new MotionScene();
        uint wood = scene.State("wood");
        Grid lower = scene.Grid(scene.At(16, 0, 16), wood, MotionScene.Box(-3, 0, -3, 3, 1, 3));
        scene.Motion.Add(lower);
        Grid upper = scene.Grid(scene.At(16.5, 6, 16.5), wood, MotionScene.Box(-1, 0, -1, 1, 1, 1));
        scene.Motion.Add(upper);

        scene.Tick(150);

        Assert.InRange(scene.Height(lower), -0.06, 0.02);
        Assert.InRange(scene.Height(upper), 1.94, 2.02);   // on the lower grid, two blocks thick
        Assert.True(scene.Motion.GetState(upper).IsAsleep);
    }

    [Fact]
    public void AGridTurnedByThirtyDegreesSettlesOnTheTerrain()
    {
        using var scene = new MotionScene();
        var transform = new WorldTransform(scene.At(16, 3, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6), Vector3.One);
        Grid grid = scene.Grid(transform, scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 1, 2));
        scene.Motion.Add(grid);

        scene.Tick(180);

        Assert.True(Vector3.Transform(Vector3.UnitY, grid.Transform.Rotation).Y > 0.99f, "The grid did not settle on a face.");
        Assert.InRange(scene.Height(grid), -0.1, 0.1);
    }

    [Fact]
    public void BlocksAndCornersEndTheSameFallCloseToEachOther()
    {
        Vector3 Fall(string cornerMass)
        {
            using var scene = new MotionScene(settings: new Dictionary<string, string> { [MotionSettings.CornerMass] = cornerMass });
            var transform = new WorldTransform(scene.At(16, 4, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f), Vector3.One);
            Grid grid = scene.Grid(transform, scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 1, 2));
            scene.Motion.Add(grid);
            scene.Tick(150);
            Assert.Equal(cornerMass == "1" ? VoxelContactMode.Corners : VoxelContactMode.Blocks, ((MotionSystem)scene.Motion).CollisionOf(grid).Mode);
            return grid.Transform.Position.RelativeTo(scene.At(16, 0, 16));
        }

        Vector3 blocks = Fall("0");
        Vector3 corners = Fall("1");

        Assert.True((blocks - corners).Length() < 0.5f, $"blocks {blocks}, corners {corners}");   // the corners let an edge sink in: the grid slides a little further
    }

    [Fact]
    public void AHeavyGridTestsCornersAndComesBackPastTheHysteresis()
    {
        // 50 blocks of wood weigh 50,000 kilograms; corners from 40,000, blocks again under 32,000.
        using var scene = new MotionScene(settings: new Dictionary<string, string> { [MotionSettings.CornerMass] = "40000" });
        var motion = (MotionSystem)scene.Motion;
        Grid grid = scene.Grid(scene.At(16, 0, 16), scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 1, 2));
        scene.Motion.Add(grid);
        scene.Tick();
        Assert.Equal(VoxelContactMode.Corners, motion.CollisionOf(grid).Mode);

        scene.Break(grid, MotionScene.Box(-2, 1, -2, 2, 1, -1));   // 10 blocks: 40,000 kilograms
        scene.Tick(2);
        scene.Break(grid, MotionScene.Box(-2, 1, 0, 2, 1, 0));     // 5 more: 35,000, still over 32,000
        scene.Tick(2);
        Assert.Equal(VoxelContactMode.Corners, motion.CollisionOf(grid).Mode);
        Assert.Equal(0, motion.Switches);

        scene.Break(grid, MotionScene.Box(-2, 1, 1, 2, 1, 1));     // 5 more: 30,000
        scene.Tick(2);
        Assert.Equal(VoxelContactMode.Blocks, motion.CollisionOf(grid).Mode);
        scene.Place(grid, scene.State("wood"), MotionScene.Box(-2, 1, 1, 2, 1, 1));   // 35,000: still under 40,000
        scene.Tick(2);
        Assert.Equal(VoxelContactMode.Blocks, motion.CollisionOf(grid).Mode);
        Assert.Equal(1, motion.Switches);
    }

    [Fact]
    public void AnOverloadedTickSwitchesEveryGridToCornersUntilTheLoadFalls()
    {
        using var scene = new MotionScene(settings: new Dictionary<string, string> { [MotionSettings.LoadShare] = "0.5" });
        var motion = (MotionSystem)scene.Motion;
        Grid grid = scene.Grid(scene.At(16, 0, 16), scene.State("wood"), MotionScene.Box(-1, 0, -1, 1, 0, 1));
        scene.Motion.Add(grid);
        scene.Tick();

        motion.SimulateLoad(0.8);
        motion.SimulateLoad(0.8);
        Assert.False(motion.Overloaded);   // one tick too few
        motion.SimulateLoad(0.8);
        Assert.True(motion.Overloaded);
        scene.Tick();
        Assert.Equal(VoxelContactMode.Corners, motion.CollisionOf(grid).Mode);

        motion.SimulateLoad(0.4);   // under the share, but not under 70 % of it
        for (int i = 0; i < MotionSystem.RecoverTicks - 2; i++)
        {
            motion.SimulateLoad(0.1);
        }

        Assert.True(motion.Overloaded);
        motion.SimulateLoad(0.1);
        motion.SimulateLoad(0.1);
        Assert.False(motion.Overloaded);
        scene.Tick();
        Assert.Equal(VoxelContactMode.Blocks, motion.CollisionOf(grid).Mode);
    }

    [Fact]
    public void ABudgetDefersTestsAndTheGridStillRests()
    {
        using var scene = new MotionScene(settings: new Dictionary<string, string> { [MotionSettings.PairBudget] = "24" });
        var motion = (MotionSystem)scene.Motion;
        Grid grid = scene.Grid(scene.At(16, 2, 16), scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 1, 2));
        scene.Motion.Add(grid);
        long deferred = 0;
        for (int i = 0; i < 40; i++)
        {
            scene.Tick();
            deferred += motion.CollisionOf(grid).Counts.Deferred;
        }

        Assert.True(deferred > 0, "Nothing was deferred.");
        scene.Tick(80);
        Assert.InRange(scene.Height(grid), -0.06, 0.02);
    }

    [Fact]
    public void TheSurfaceFollowsABlockBrokenInTheMiddleOfAGrid()
    {
        using var scene = new MotionScene();
        var motion = (MotionSystem)scene.Motion;
        Grid grid = scene.Grid(scene.At(16, 8, 16), scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 4, 2));
        scene.Motion.Add(grid);
        scene.Motion.Freeze(grid);
        GridVoxelCollision surface = motion.SurfaceOf(grid);
        Assert.Equal(125 - 27, surface.SurfaceCount);   // every cell but the 3 × 3 × 3 inside
        Assert.False(surface.IsSurface(new BlockCoordinate(0, 3, 0)));

        scene.Break(grid, new BlockCoordinate(0, 4, 0));
        scene.Tick();

        Assert.False(surface.IsSurface(new BlockCoordinate(0, 4, 0)));   // gone
        Assert.True(surface.IsSurface(new BlockCoordinate(0, 3, 0)));    // open from above
        Assert.Equal(125 - 27, surface.SurfaceCount);                     // one cell less, one more

        scene.Place(grid, scene.State("grass"), new BlockCoordinate(0, 4, 0));   // no collision: still open
        scene.Tick();
        Assert.True(surface.IsSurface(new BlockCoordinate(0, 3, 0)));
        Assert.False(surface.IsSurface(new BlockCoordinate(0, 4, 0)));

        scene.Break(grid, new BlockCoordinate(0, 4, 0));
        scene.Tick();
        scene.Place(grid, scene.State("wood"), new BlockCoordinate(0, 4, 0));
        scene.Tick();
        Assert.False(surface.IsSurface(new BlockCoordinate(0, 3, 0)));
        Assert.True(surface.IsSurface(new BlockCoordinate(0, 4, 0)));
        Assert.Equal(125 - 27, surface.SurfaceCount);
    }

    [Fact]
    public void AGridBrokenInTheMiddleFallsOntoTheTerrainThroughItsNewSurface()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 0.2, 16), scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 1, 2));
        scene.Motion.Add(grid);
        scene.Tick(60);

        // A pillar of the terrain rises into the hole left in the middle of the grid: the cells around it are its new surface.
        scene.Place(scene.Terrain, scene.State("stone"), new BlockCoordinate(16, 0, 16), new BlockCoordinate(16, 1, 16));
        scene.Break(grid, new BlockCoordinate(0, 0, 0), new BlockCoordinate(0, 1, 0));
        scene.Tick(60);

        Assert.InRange(scene.Height(grid), -0.06, 0.02);
        Assert.True(Vector3.Transform(Vector3.UnitY, grid.Transform.Rotation).Y > 0.999f);
    }

    [Fact]
    public void AnImpactOfAGridIsPublishedWithTheBlockItHit()
    {
        using var scene = new MotionScene();
        var impacts = new List<ContactEvent>();
        scene.Physics.SubscribeStepEnd(scene.Probe.Context, new Log(impacts));
        Grid grid = scene.Grid(scene.At(16, 3, 16), scene.State("wood"), MotionScene.Box(-2, 0, -2, 2, 1, 2));
        BodyHandle body = scene.Motion.Add(grid);

        scene.Tick(60);

        ContactEvent impact = impacts.First(e => e.Body == body);
        Assert.Same(scene.Terrain, impact.Grid);
        Assert.Equal(-1, impact.Block.Y);
        Assert.True(impact.Speed >= 2, $"speed {impact.Speed}");
        Assert.DoesNotContain(impacts, e => e.Body == body && e.Speed < 2);
    }

    [Fact]
    public void FiveHundredGridsFallOnTheTerrainAndSleep()
    {
        using var scene = new MotionScene(new ChunkArea(6, 1));
        var motion = (MotionSystem)scene.Motion;
        uint wood = scene.State("wood");
        var random = new Random(9);
        var grids = new List<(Grid Grid, double Floor)>();
        for (int i = 0; i < 500; i++)
        {
            // A quarter of two blocks (small grids), then 3 × 3, 4 × 4, and 6 × 6 cells, two cells high.
            int size = i % 4;
            BlockCoordinate[] blocks = size == 0
                ? [new BlockCoordinate(0, 0, 0), new BlockCoordinate(1, 0, 0)]
                : MotionScene.Box(0, 0, 0, size == 1 ? 2 : size == 2 ? 3 : 5, 1, size == 1 ? 2 : size == 2 ? 3 : 5);
            Quaternion turn = Quaternion.CreateFromYawPitchRoll(random.NextSingle() * 6.3f, (random.NextSingle() - 0.5f) * 0.6f, (random.NextSingle() - 0.5f) * 0.6f);
            var transform = new WorldTransform(scene.At(-84 + (7 * (i % 25)), 3 + (random.NextDouble() * 4), -64 + (7 * (i / 25))), turn, Vector3.One);
            Grid grid = scene.Grid(transform, wood, blocks);
            scene.Motion.Add(grid);
            grids.Add((grid, 0));
        }

        var falling = Stopwatch.StartNew();
        scene.Tick(60);
        falling.Stop();
        int ticks = 60;
        int asleep = 0;
        while (ticks < 1800 && asleep < grids.Count)
        {
            scene.Tick(30);
            ticks += 30;
            asleep = grids.Count(g => scene.Motion.GetState(g.Grid).IsAsleep);
        }

        var resting = Stopwatch.StartNew();
        scene.Tick(30);
        resting.Stop();
        double lowest = grids.Min(g => scene.Height(g.Grid));
        int small = grids.Count(g => scene.Motion.GetState(g.Grid).IsSmall);
        float tilt = grids.Max(g => 1 - Vector3.Transform(Vector3.UnitY, g.Grid.Transform.Rotation).Y);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"500 grids ({small} small): {falling.Elapsed.TotalMilliseconds / 60:F2} ms per tick over the first 2 s; {asleep} asleep after {ticks / 30.0:F1} s; {resting.Elapsed.TotalMilliseconds / 30:F2} ms per tick at rest; lowest {lowest:F3}; largest tilt {tilt:F4}; {motion.Switches} switches."));

        foreach ((Grid awake, double _) in grids.Where(g => !scene.Motion.GetState(g.Grid).IsAsleep).Take(12))
        {
            GridMotionState state = scene.Motion.GetState(awake);
            var collision = motion.CollisionOf(awake);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  awake #{awake.Identifier}: {state.Blocks} blocks, height {scene.Height(awake):F3}, up {Vector3.Transform(Vector3.UnitY, awake.Transform.Rotation).Y:F3}, v {state.LinearVelocity.Length():F4}, w {state.AngularVelocity.Length():F4}, {collision.Counts.Pairs} pairs, {collision.Counts.Tests} tests, {collision.Counts.Deferred} deferred, frozen {state.IsFrozen}"));
        }

        Assert.True(lowest > -0.1, $"A grid went into the terrain: {lowest}.");
        Assert.Equal(grids.Count, asleep);
        Assert.Equal(0, scene.Physics.Statistics.FrozenBodies);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        scene.Tick(30);
        Assert.Equal(0, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    /// <summary>Keeps the impacts of the steps.</summary>
    private sealed class Log(List<ContactEvent> impacts) : IPhysicsStepObserver
    {
        public void OnStepEnd(in PhysicsStepEnd step) => impacts.AddRange(step.Impacts);
    }
}
