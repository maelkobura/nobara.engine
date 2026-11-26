// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>Grids whose body has the shape of their voxels, against the bodies of nPhysics.Bepu.</summary>
[Collection(Serial.Name)]
public sealed class GridVoxelTests
{
    private static readonly ChunkArea Small = new(1, 1);

    [Fact]
    public void ABoxRestsOnTheVoxelsOfAStaticGridBody()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Platform(scene, scene.At(16, 10, 16));
        Body(scene, grid, BodyKind.Static, Vector3.Zero);
        BodyHandle box = scene.Add(scene.At(16.5, 13, 16.5), scene.Physics.CreateBox(Vector3.One));

        scene.Tick(120);

        Assert.InRange(scene.Height(box), 11.45, 11.55);   // the top of the platform is 1 block above its origin
        Assert.True(scene.State(box).IsAsleep);
    }

    [Fact]
    public void ADynamicGridFallsOnAStaticBodyAndRestsOnIt()
    {
        using var scene = new VoxelScene(Small);
        scene.Scene.Add(scene.At(16, 5, 16), scene.Physics.CreateBox(new Vector3(20, 1, 20)), BodyKind.Static);
        Grid grid = Platform(scene, scene.At(16, 10, 16));
        BodyHandle body = Body(scene, grid, BodyKind.Dynamic, new Vector3(0.5f, 0.5f, 0.5f), mass: 49);

        scene.Tick(150);

        WorldTransform transform = scene.State(body).Transform;
        double bottom = transform.Position.RelativeTo(scene.At(0, 0, 0)).Y;   // the origin of the grid is the bottom of its blocks
        Assert.InRange(bottom, 5.45, 5.55);
        Assert.True(Vector3.Transform(Vector3.UnitY, transform.Rotation).Y > 0.999f);
        Assert.Equal(grid.Transform.Position, transform.Position);   // nPhysics wrote the transform of the grid
    }

    [Fact]
    public void ABoxOnAMovingKinematicGridIsCarriedByFriction()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Platform(scene, scene.At(12, 10, 16));
        BodyHandle platform = Body(scene, grid, BodyKind.Kinematic, Vector3.Zero);
        BodyHandle box = scene.Add(scene.At(12.5, 11.5, 16.5), scene.Physics.CreateBox(Vector3.One), material: new PhysicsMaterial(1, 0));
        scene.Tick(30);   // settled on the platform
        Vector3 start = scene.Offset(box);

        scene.Physics.SetVelocity(platform, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(60);   // two seconds at 2 blocks per second

        Vector3 moved = scene.Offset(box) - start;
        Assert.InRange(moved.X, 3.6, 4.2);
        Assert.InRange(scene.Height(box), 11.4, 11.6);
    }

    [Fact]
    public void ABlockPlacedOnAGridBodyIsMetAtTheNextStep()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Platform(scene, scene.At(16, 10, 16));
        Body(scene, grid, BodyKind.Static, Vector3.Zero);
        scene.Place(grid, (new BlockCoordinate(0, 1, 0), scene.State("stone")), (new BlockCoordinate(0, 2, 0), scene.State("stone")));
        scene.Tick();
        BodyHandle box = scene.Add(scene.At(16.5, 16, 16.5), scene.Physics.CreateBox(new Vector3(0.5f, 0.5f, 0.5f)));

        scene.Tick(120);

        Assert.InRange(scene.Height(box), 13.2, 13.3);   // on the pillar, two blocks above the platform
    }

    [Fact]
    public void TheCellsOfAGridFollowTheBlocksPlacedAndBroken()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Platform(scene, scene.At(16, 10, 16));
        VoxelGridCells cells = scene.Physics.GetShape(scene.Physics.CreateVoxels(grid, Vector3.Zero)).Cells!;
        int version = cells.BoundsVersion;
        Assert.Equal(new Vector3(-3, 0, -3), cells.Minimum);
        Assert.Equal(new Vector3(4, 1, 4), cells.Maximum);

        scene.Place(grid, (new BlockCoordinate(40, 2, 0), scene.State("stone")));
        scene.Tick();

        Assert.Equal(5, cells.ChunkCount);
        Assert.Equal(new Vector3(41, 3, 4), cells.Maximum);
        Assert.NotEqual(version, cells.BoundsVersion);

        scene.Probe.Batches.Break(grid, BlockActor.OfCommand("test"), BlockCause.Damage, [new BlockCoordinate(40, 2, 0)]);
        scene.Tick();

        Assert.Equal(4, cells.ChunkCount);   // a chunk without blocks is left out
        Assert.Equal(new Vector3(4, 1, 4), cells.Maximum);
    }

    [Fact]
    public void TheCellsOfAGridFollowItsChunksAsTheyEnterAndLeaveMemory()
    {
        using var scene = new VoxelScene(Small);
        VoxelGridCells cells = scene.Physics.GetShape(scene.Physics.CreateVoxels(scene.Ground, Vector3.Zero)).Cells!;
        int before = cells.ChunkCount;
        Assert.True(before > 0);

        AreaTicket ticket = scene.Probe.Activate(scene.At(16 + 320, 16, 16), Small);   // stone below y = 0, ten chunks away
        for (int i = 0; i < 200 && cells.ChunkCount == before; i++)
        {
            scene.Tick();
        }

        int loaded = cells.ChunkCount;
        Assert.True(loaded > before, "The chunks that entered memory were not followed.");
        Assert.True(cells.Maximum.X > 320);

        scene.Probe.Tickets.Remove(ticket);
        ticket.Dispose();
        for (int i = 0; i < 400 && cells.ChunkCount != before; i++)   // nVoxel keeps a chunk 300 ticks after its last ticket
        {
            scene.Tick();
        }

        Assert.Equal(before, cells.ChunkCount);   // taken out before they left memory
        Assert.True(cells.Maximum.X < 320);
    }

    [Fact]
    public void TheCellsOfADestroyedGridAreEmptied()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Platform(scene, scene.At(16, 10, 16));
        VoxelGridCells cells = scene.Physics.GetShape(scene.Physics.CreateVoxels(grid, Vector3.Zero)).Cells!;
        Assert.Equal(4, cells.ChunkCount);

        scene.Probe.Voxels.Destroy(scene.Probe.Context, grid);
        scene.Tick();

        Assert.Equal(0, cells.ChunkCount);
        Assert.True(cells.IsEmpty);
    }

    [Fact]
    public void ABoxThrownAtADynamicGridPushesIt()
    {
        using var scene = new VoxelScene(Small);
        scene.Scene.Add(scene.At(16, 5, 16), scene.Physics.CreateBox(new Vector3(30, 1, 30)), BodyKind.Static, material: new PhysicsMaterial(0, 0));
        Grid grid = Platform(scene, scene.At(16, 5.5, 16));
        BodyHandle body = Body(scene, grid, BodyKind.Dynamic, new Vector3(0.5f, 0.5f, 0.5f), mass: 49);
        scene.Tick(10);   // resting on the floor, without friction
        BodyHandle box = scene.Add(scene.At(10, 6, 16.5), scene.Physics.CreateBox(new Vector3(0.8f, 0.8f, 0.8f)), mass: 49);
        scene.Physics.SetVelocity(box, new Vector3(6, 0, 0), Vector3.Zero);

        scene.Tick(20);

        Assert.True(scene.State(body).LinearVelocity.X > 1, $"The grid moves at {scene.State(body).LinearVelocity}.");
        Assert.True(scene.Offset(box).X < scene.State(body).Transform.Position.RelativeTo(scene.At(0, 0, 0)).X - 3, "The box went into the grid.");
    }

    [Fact]
    public void TheVoxelsOfAGridBodyDoNotCollideWithTheVoxelsOfOtherGrids()
    {
        using var scene = new VoxelScene(Small);
        scene.Add(scene.At(20.5, 0.5, 16.5), scene.Physics.CreateBox(Vector3.One));   // holds the chunks of the terrain in the world
        Grid grid = Platform(scene, scene.At(16, 3, 16));
        BodyHandle body = Body(scene, grid, BodyKind.Dynamic, new Vector3(0.5f, 0.5f, 0.5f), mass: 49);

        scene.Tick(30);

        Assert.True(scene.State(body).Transform.Position.RelativeTo(scene.At(0, 0, 0)).Y < 0, "The grid stopped on the terrain: the collision of a grid with other voxels is not the backend's.");
    }

    [Fact]
    public void AQueryMeetsTheBlocksOfAGridBodyOnce()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Platform(scene, scene.At(16, 10, 16));
        Body(scene, grid, BodyKind.Static, Vector3.Zero);
        scene.Tick();

        var hits = new PhysicsHit[8];
        int count = scene.Physics.RayCastAll(DimensionId.Default, scene.At(16.5, 14, 16.5), -Vector3.UnitY, 5, hits, new QueryFilter(uint.MaxValue));

        Assert.True(count >= 1);
        Assert.Equal(PhysicsHitKind.Block, hits[0].Kind);
        Assert.Same(grid, hits[0].Grid);
        Assert.Equal(new BlockCoordinate(0, 0, 0), hits[0].Block);
        Assert.DoesNotContain(hits[..count].ToArray(), hit => hit.Kind == PhysicsHitKind.Body);
    }

    [Fact]
    public void BoxesRestingOnAGridBodyAllocateNothing()
    {
        using var scene = new VoxelScene(Small);
        Grid grid = Platform(scene, scene.At(16, 10, 16));
        Body(scene, grid, BodyKind.Kinematic, Vector3.Zero);
        PhysicsShape crate = scene.Physics.CreateBox(new Vector3(0.8f, 0.8f, 0.8f));
        for (int i = 0; i < 5; i++)
        {
            scene.Add(scene.At(14.5 + i, 11.5, 16.5), crate);
        }

        scene.Tick(60);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        scene.Tick(30);

        Assert.Equal(0, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    /// <summary>Creates a grid with a platform of stone, 7 by 7 cells around its origin, at y = 0 in the grid: its top is 1 block above the origin.</summary>
    private static Grid Platform(VoxelScene scene, in WorldPosition origin)
    {
        Grid grid = scene.Probe.Voxels.CreateGrid(scene.Probe.Context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(origin) });
        uint stone = scene.State("stone");
        for (int z = -3; z <= 3; z++)
        {
            for (int x = -3; x <= 3; x++)
            {
                var block = new BlockCoordinate(x, 0, z);
                grid.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(block)).Palette.Set(VoxelCoordinates.ToIndex(block), stone);
            }
        }

        return grid;
    }

    /// <summary>Makes a grid a body whose shape is its voxels, centered on a point of the grid.</summary>
    private static BodyHandle Body(VoxelScene scene, Grid grid, BodyKind kind, Vector3 center, float mass = 1) =>
        scene.Physics.AddBody(DimensionId.Default, grid.Entity, new BodyDescription
        {
            Kind = kind,
            Shape = scene.Physics.CreateVoxels(grid, center),
            Center = center,
            Mass = mass,
        });
}
