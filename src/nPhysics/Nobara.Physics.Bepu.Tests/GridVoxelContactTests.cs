// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>The voxels of grid bodies against the voxels of the terrain and of other grids.</summary>
[Collection(Serial.Name)]
public sealed class GridVoxelContactTests
{
    private static readonly ChunkArea Small = new(1, 1);

    /// <summary>The center of mass of a <see cref="Slab"/>, in the frame of its grid.</summary>
    private static readonly Vector3 SlabCenter = new(0.5f, 1, 0.5f);

    [Theory]
    [InlineData(VoxelContactMode.Blocks, 0, 0)]
    [InlineData(VoxelContactMode.Corners, 0, 0)]
    [InlineData(VoxelContactMode.Blocks, 1_000_000_000, -1_000_000_000)]
    public void AGridFallsOnTheTerrainAndRestsWithoutJitter(VoxelContactMode mode, double x, double z)
    {
        using var scene = new VoxelScene(Small, WorldPosition.FromBlocks(x + 16, 16, z + 16));
        Grid grid = Slab(scene, scene.At(16, 3, 16));
        BodyHandle body = Body(scene, grid, new GridVoxelCollision { Mode = mode });

        scene.Tick(90);
        double settled = Bottom(scene, body);
        scene.Tick(30);

        Assert.InRange(Bottom(scene, body), -0.06, 0.02);   // on the ground, its top at y = 0
        Assert.True(Math.Abs(Bottom(scene, body) - settled) < 0.005, "The grid still moves once at rest.");
        Assert.True(Vector3.Transform(Vector3.UnitY, scene.State(body).Transform.Rotation).Y > 0.999f);
        Assert.True(scene.State(body).IsAsleep);
    }

    [Fact]
    public void AGridTurnedAboutTheVerticalRestsFlatOnTheTerrain()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Slab(scene, scene.At(16, 2, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6));
        BodyHandle body = Body(scene, grid, new GridVoxelCollision());

        scene.Tick(120);

        Assert.InRange(Bottom(scene, body), -0.06, 0.02);
        Assert.True(Vector3.Transform(Vector3.UnitY, scene.State(body).Transform.Rotation).Y > 0.999f);
    }

    [Fact]
    public void AGridTiltedByThirtyDegreesFallsFlatOnTheTerrain()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Slab(scene, scene.At(16, 3, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6));
        BodyHandle body = Body(scene, grid, new GridVoxelCollision());

        scene.Tick(180);

        Assert.True(Vector3.Transform(Vector3.UnitY, scene.State(body).Transform.Rotation).Y > 0.99f, "The grid did not settle on a face.");
        Assert.InRange(Bottom(scene, body), -0.1, 0.1);
    }

    [Fact]
    public void BlocksAndCornersEndTheSameFallCloseToEachOther()
    {
        double Fall(VoxelContactMode mode)
        {
            using var scene = new VoxelScene(Small);
            Grid grid = Slab(scene, scene.At(16, 4, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f));
            BodyHandle body = Body(scene, grid, new GridVoxelCollision { Mode = mode });
            scene.Tick(150);
            return scene.State(body).Transform.Position.RelativeTo(scene.At(16, 0, 16)).Length();
        }

        Assert.True(Math.Abs(Fall(VoxelContactMode.Blocks) - Fall(VoxelContactMode.Corners)) < 0.25);
    }

    [Fact]
    public void AGridFallsOnAnotherGridBodyAndRestsOnIt()
    {
        using var scene = new VoxelScene(Small);
        Grid lower = Slab(scene, scene.At(16, 0, 16));
        BodyHandle below = Body(scene, lower, new GridVoxelCollision(), BodyKind.Static);
        Grid upper = Slab(scene, scene.At(16.5, 5, 16.5));
        BodyHandle above = Body(scene, upper, new GridVoxelCollision());

        scene.Tick(120);

        Assert.InRange(Bottom(scene, above), 1.94, 2.02);   // on top of the lower slab, two blocks thick
        Assert.True(scene.State(above).IsAsleep);
        Assert.Equal(BodyKind.Static, scene.State(below).Kind);
    }

    [Fact]
    public void ABudgetDefersTestsAndTheGridStillRests()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Slab(scene, scene.At(16, 2, 16));
        var collision = new GridVoxelCollision { PairBudget = 24 };
        BodyHandle body = Body(scene, grid, collision);

        scene.Tick(30);
        VoxelContactCounts counts = collision.TakeCounts();

        Assert.True(counts.Deferred > 0, "Nothing was deferred.");
        Assert.True(counts.Tests <= counts.Pairs * (24 + 16), $"{counts.Tests} tests for {counts.Pairs} pairs.");   // a cell may pass the budget by its own tests
        scene.Tick(90);
        Assert.InRange(Bottom(scene, body), -0.06, 0.02);
        Assert.True(scene.State(body).LinearVelocity.Length() < 0.05f, $"The grid moves at {scene.State(body).LinearVelocity}.");   // its contacts change at each step: it may not sleep
    }

    [Fact]
    public void AFewCellsWithManyTestsEachShareTheBudgetAndTheGridRestsFlat()
    {
        // 25 cells under the slab make about 9 tests each: a budget of 120 leaves some at each step without ranking them.
        using var scene = new VoxelScene(Small);
        Grid grid = Slab(scene, scene.At(16, 0.5, 16));
        var collision = new GridVoxelCollision { PairBudget = 120 };
        BodyHandle body = Body(scene, grid, collision);
        for (int y = 0; y <= 1; y++)
        {
            for (int z = -2; z <= 2; z++)
            {
                for (int x = -2; x <= 2; x++)
                {
                    collision.SetSurface(new BlockCoordinate(x, y, z), y == 0);   // the bottom only
                }
            }
        }

        scene.Tick(30);
        Assert.True(collision.TakeCounts().Deferred > 0, "Nothing was deferred.");
        scene.Tick(90);

        Assert.InRange(Bottom(scene, body), -0.06, 0.02);
        Assert.True(Vector3.Transform(Vector3.UnitY, scene.State(body).Transform.Rotation).Y > 0.999f, "A side of the slab is never held.");
    }

    [Fact]
    public void AGridWithoutSurfaceGoesThroughTheTerrain()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Slab(scene, scene.At(16, 3, 16));
        var collision = new GridVoxelCollision();
        BodyHandle body = Body(scene, grid, collision);
        collision.ClearSurface();

        scene.Tick(40);

        Assert.True(Bottom(scene, body) < -1, "The grid stopped without a surface cell.");
    }

    [Fact]
    public void AGridBrokenInTheMiddleIsTestedThroughItsNewSurface()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Slab(scene, scene.At(16, 0.2, 16));
        var collision = new GridVoxelCollision();
        BodyHandle body = Body(scene, grid, collision);
        scene.Tick(60);

        // The pillar under the slab enters it from below once the bottom cells it meets are gone.
        scene.Place((scene.Block(16, 0, 16), scene.State("stone")));
        scene.Probe.Batches.Break(grid, BlockActor.OfCommand("test"), BlockCause.Damage, [new BlockCoordinate(0, 0, 0)]);
        collision.SetSurface(new BlockCoordinate(0, 1, 0), true);
        scene.Tick(30);

        Assert.InRange(Bottom(scene, body), -0.06, 0.05);   // held by the ground around the pillar, not by the pillar
        Assert.True(collision.TakeCounts().Tests > 0);
    }

    [Fact]
    public void AnImpactOfAGridNamesTheBlockOfTheTerrain()
    {
        using var scene = new VoxelScene(Small);
        var impacts = new List<ContactEvent>();
        scene.Physics.SubscribeStepEnd(scene.Probe.Context, new Log(impacts));
        Grid grid = Slab(scene, scene.At(16, 3, 16));
        BodyHandle body = Body(scene, grid, new GridVoxelCollision(), contacts: new ContactReporting(2));

        scene.Tick(60);

        ContactEvent impact = impacts.First(e => e.Body == body);
        Assert.Same(scene.Ground, impact.Grid);
        Assert.Equal(-1, impact.Block.Y);
        Assert.Equal(scene.State("stone"), impact.State);
        Assert.True(impact.Speed > 5, $"speed {impact.Speed}");
        Assert.True(Vector3.Dot(impact.Normal, Vector3.UnitY) > 0.9f);
    }

    [Fact]
    public void AContactWithAGridBodyNamesItsBlock()
    {
        using var scene = new VoxelScene(Small);
        var impacts = new List<ContactEvent>();
        scene.Physics.SubscribeStepEnd(scene.Probe.Context, new Log(impacts));
        Grid grid = Slab(scene, scene.At(16, 5, 16));
        BodyHandle platform = Body(scene, grid, null, BodyKind.Static);
        EntityId entity = scene.Probe.CreateEntity(WorldTransform.At(scene.At(16.5, 10, 16.5)));
        BodyHandle box = scene.Physics.AddBody(DimensionId.Default, entity, new BodyDescription
        {
            Shape = scene.Physics.CreateBox(new Vector3(0.5f)),
            Contacts = ContactReporting.Default,
        });

        scene.Tick(60);

        ContactEvent impact = Assert.Single(impacts, e => e.Body == box);
        Assert.Equal(platform, impact.Other);
        Assert.Same(grid, impact.Grid);
        Assert.Equal(new BlockCoordinate(0, 1, 0), impact.Block);   // the top layer of the slab
        Assert.Equal(scene.State("stone"), impact.State);
    }

    [Fact]
    public void AContactWithAGridBodyEndsAtOnceWhenTheBodyIsRemoved()
    {
        using var scene = new VoxelScene(Small);
        var ended = new List<ContactEvent>();
        scene.Physics.SubscribeStepEnd(scene.Probe.Context, new Log(ended, endings: true));
        Grid grid = Slab(scene, scene.At(16, 5, 16));
        BodyHandle platform = Body(scene, grid, null, BodyKind.Static);
        EntityId entity = scene.Probe.CreateEntity(WorldTransform.At(scene.At(16.5, 7.4, 16.5)));
        BodyHandle box = scene.Physics.AddBody(DimensionId.Default, entity, new BodyDescription
        {
            Shape = scene.Physics.CreateBox(new Vector3(0.5f)),
            Contacts = ContactReporting.Default,
        });
        scene.Tick(90);
        Assert.True(scene.State(box).IsAsleep);
        Assert.Empty(ended);

        scene.Physics.RemoveBody(platform);   // the grid stays, its body goes
        scene.Tick();

        ContactEvent end = Assert.Single(ended);
        Assert.Equal(box, end.Body);
        Assert.Equal(platform, end.Other);
        Assert.False(scene.Physics.IsAlive(end.Other));
    }

    [Fact]
    public void AGridRestingOnTheTerrainAllocatesNothing()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Slab(scene, scene.At(16, 0.3, 16));
        BodyHandle body = Body(scene, grid, new GridVoxelCollision());
        scene.Tick(30);
        scene.Physics.SetVelocity(body, new Vector3(0, 0, 0), new Vector3(0, 0.01f, 0));   // awake: the pairs are tested at each step
        scene.Tick(5);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < 10; i++)
        {
            scene.Physics.Wake(body);
            scene.Tick();
        }

        Assert.Equal(0, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    /// <summary>
    /// Creates a grid with a slab of stone, 5 by 5 cells around its origin and
    /// 2 cells high above it: its bottom is the origin of the grid.
    /// </summary>
    private static Grid Slab(VoxelScene scene, in WorldPosition origin, Quaternion? rotation = null)
    {
        Grid grid = scene.Probe.Voxels.CreateGrid(scene.Probe.Context, DimensionId.Default, new GridOptions { Transform = new WorldTransform(origin, rotation ?? Quaternion.Identity, Vector3.One) });
        uint stone = scene.State("stone");
        for (int y = 0; y <= 1; y++)
        {
            for (int z = -2; z <= 2; z++)
            {
                for (int x = -2; x <= 2; x++)
                {
                    var block = new BlockCoordinate(x, y, z);
                    grid.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(block)).Palette.Set(VoxelCoordinates.ToIndex(block), stone);
                }
            }
        }

        return grid;
    }

    /// <summary>Makes a slab a body whose shape is its voxels, every cell a surface cell, colliding with other voxels when a collision is given.</summary>
    private static BodyHandle Body(VoxelScene scene, Grid grid, GridVoxelCollision? collision, BodyKind kind = BodyKind.Dynamic, ContactReporting? contacts = null)
    {
        BodyHandle body = scene.Physics.AddBody(DimensionId.Default, grid.Entity, new BodyDescription
        {
            Kind = kind,
            Shape = scene.Physics.CreateVoxels(grid, SlabCenter),
            Center = SlabCenter,
            Mass = 50,
            Contacts = contacts,
        });
        if (collision is not null)
        {
            for (int y = 0; y <= 1; y++)
            {
                for (int z = -2; z <= 2; z++)
                {
                    for (int x = -2; x <= 2; x++)
                    {
                        collision.SetSurface(new BlockCoordinate(x, y, z), true);
                    }
                }
            }

            scene.Physics.SetVoxelCollision(grid, collision);
        }

        return body;
    }

    /// <summary>Gets the height of the origin of a slab, its bottom, from the top of the ground.</summary>
    private static double Bottom(VoxelScene scene, BodyHandle body) => scene.State(body).Transform.Position.RelativeTo(scene.At(0, 0, 0)).Y;

    /// <summary>Keeps the impacts of the steps.</summary>
    private sealed class Log(List<ContactEvent> events, bool endings = false) : IPhysicsStepObserver
    {
        public void OnStepEnd(in PhysicsStepEnd step) => events.AddRange(endings ? step.ContactsEnded : step.Impacts);
    }
}
