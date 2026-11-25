// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Tests;

/// <summary>The cells of a chunk as a backend reads them, and the inner faces whose contacts it drops.</summary>
public sealed class VoxelCellTests
{
    private static readonly Vector3 Up = Vector3.UnitY;

    [Fact]
    public void ACellOfAirOrOfABlockHasNoBoxOrAWholeCube()
    {
        using Chunk chunk = Chunk(new LocalBlockCoordinate(4, 4, 4));
        VoxelChunkCells cells = VoxelChunkCells.Cubes(null, chunk, Quaternion.Identity);

        Assert.True(cells.GetBoxes(Index(3, 4, 4)).IsEmpty);
        CollisionBox box = Assert.Single(cells.GetBoxes(Index(4, 4, 4)).ToArray());
        Assert.Equal(Vector3.Zero, box.Minimum);
        Assert.Equal(Vector3.One, box.Maximum);
    }

    [Fact]
    public void AContactThroughAFaceThatTheNeighborCoversIsInner()
    {
        // A floor of two cells side by side: (4, 4, 4) and (5, 4, 4).
        using Chunk chunk = Chunk(new LocalBlockCoordinate(4, 4, 4), new LocalBlockCoordinate(5, 4, 4));
        VoxelChunkCells cells = VoxelChunkCells.Cubes(null, chunk, Quaternion.Identity);
        int second = Index(5, 4, 4);

        Assert.True(cells.IsFaceCovered(second, BlockFace.West));
        Assert.False(cells.IsFaceCovered(second, BlockFace.East));
        Assert.False(cells.IsFaceCovered(second, BlockFace.Up));
        Assert.True(cells.IsInnerContact(second, 0, Vector3.Normalize(new Vector3(-1, 0.3f, 0))));   // the side of the second cell, toward the first: a body sliding over the joint
        Assert.False(cells.IsInnerContact(second, 0, Up));                                          // the top: kept
        Assert.False(cells.IsInnerContact(second, 0, Vector3.UnitX));                               // the free side: kept
        Assert.False(cells.IsInnerContact(second, 0, Vector3.Normalize(new Vector3(-0.3f, 1, 0))));  // mostly up: kept
    }

    [Fact]
    public void TheNormalIsTurnedIntoTheFrameOfTheGrid()
    {
        using Chunk chunk = Chunk(new LocalBlockCoordinate(4, 4, 4), new LocalBlockCoordinate(5, 4, 4));
        Quaternion quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);   // the local -X of the grid points along +Z of the world
        VoxelChunkCells cells = VoxelChunkCells.Cubes(null, chunk, quarter);

        Assert.True(cells.IsInnerContact(Index(5, 4, 4), 0, Vector3.Transform(-Vector3.UnitX, quarter)));
        Assert.False(cells.IsInnerContact(Index(5, 4, 4), 0, -Vector3.UnitX));
    }

    [Fact]
    public void AFaceOnTheBorderOfAChunkIsCoveredOnlyThroughTheGrid()
    {
        using Chunk chunk = Chunk(new LocalBlockCoordinate(0, 4, 4));
        VoxelChunkCells alone = VoxelChunkCells.Cubes(null, chunk, Quaternion.Identity);

        Assert.False(alone.IsFaceCovered(Index(0, 4, 4), BlockFace.West));   // no grid: the neighbor chunk is unknown, the contact is kept
    }

    [Fact]
    public void OneBoxCoversTheFacesItSpansWhole()
    {
        byte slab = VoxelShapes.CoveredFaces([new CollisionBox(Vector3.Zero, new Vector3(1, 0.5f, 1))]);
        byte cube = VoxelShapes.CoveredFaces([new CollisionBox(Vector3.Zero, Vector3.One)]);
        byte halves = VoxelShapes.CoveredFaces([new CollisionBox(Vector3.Zero, new Vector3(0.5f, 1, 1)), new CollisionBox(new Vector3(0.5f, 0, 0), Vector3.One)]);

        Assert.Equal(1 << (int)BlockFace.Down, slab);
        Assert.Equal(0x3F, cube);
        Assert.Equal((1 << (int)BlockFace.West) | (1 << (int)BlockFace.East), halves);   // two boxes do not cover the top together: kept, a seam at worst
    }

    [Fact]
    public void AShapeOfManyBoxesKeepsSixteenAndMergesTheRest()
    {
        var boxes = new List<CollisionBox>();
        for (int i = 0; i < 20; i++)
        {
            boxes.Add(new CollisionBox(new Vector3(i / 20f, 0, 0), new Vector3((i + 1) / 20f, 0.1f, 1)));
        }

        CollisionBox[] bounded = VoxelShapes.Bound(new CollisionShape(boxes));

        Assert.Equal(VoxelChunkCells.MaxBoxesPerCell, bounded.Length);
        Assert.Equal(15 / 20f, bounded[15].Minimum.X);
        Assert.Equal(1f, bounded[15].Maximum.X);
    }

    private static int Index(int x, int y, int z) => VoxelCoordinates.ToIndex(new LocalBlockCoordinate(x, y, z));

    private static Chunk Chunk(params LocalBlockCoordinate[] blocks)
    {
        var chunk = new Chunk(default);
        foreach (LocalBlockCoordinate block in blocks)
        {
            chunk.Palette.Set(VoxelCoordinates.ToIndex(block), 1);
        }

        return chunk;
    }
}

/// <summary>The chunks of voxels that nPhysics hands to the worlds of a backend.</summary>
public sealed class VoxelLayerTests : IDisposable
{
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AWorldHoldsTheChunksAroundItsDynamicBodiesOnly()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = Floor(probe, WorldPosition.FromBlocks(0, 0, 0), new ChunkCoordinate(0, 0, 0), new ChunkCoordinate(2, 0, 0));
        probe.CreateBody(WorldPosition.FromBlocks(16, 1.5, 16));                                // dynamic, over chunk (0, 0, 0)
        probe.CreateBody(WorldPosition.FromBlocks(80, 1.5, 16), BodyKind.Kinematic);            // kinematic, over chunk (2, 0, 0): it never touches voxels

        _harness.Tick(engine);

        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        VoxelChunkCollider chunk = Assert.Single(world.VoxelChunks.Values);
        Assert.Same(grid, chunk.Grid);
        Assert.Equal(new ChunkCoordinate(0, 0, 0), chunk.Chunk.Coordinate);
        Assert.Equal(PhysicsFrame.ToLocal(world.Options.Origin, WorldPosition.FromBlocks(0, 0, 0)), chunk.Position);
        Assert.NotNull(chunk.Cells);
        Assert.Equal(1, probe.Physics.Statistics.VoxelChunks);
    }

    [Fact]
    public void AChunkLeavesTheWorldOnceNoBodyHasNeededItForAWhile()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Floor(probe, WorldPosition.FromBlocks(0, 0, 0), new ChunkCoordinate(0, 0, 0), new ChunkCoordinate(2, 0, 0));
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(16, 1.5, 16));
        _harness.Tick(engine);
        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        Assert.Single(world.VoxelChunks);

        probe.Physics.Teleport(body, WorldPosition.FromBlocks(80, 1.5, 16), Quaternion.Identity);
        _harness.Tick(engine);
        Assert.Equal(2, world.VoxelChunks.Count);   // the old chunk stays for a while, so that a body on a border does not add and remove it at each step

        _harness.Tick(engine, PhysicsSimulation.VoxelGrace);
        Assert.Equal(new ChunkCoordinate(2, 0, 0), Assert.Single(world.VoxelChunks.Values).Chunk.Coordinate);
    }

    [Fact]
    public void TheChunksOfADestroyedGridLeaveTheWorlds()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = Floor(probe, WorldPosition.FromBlocks(0, 0, 0), new ChunkCoordinate(0, 0, 0), new ChunkCoordinate(0, 0, 0));
        probe.CreateBody(WorldPosition.FromBlocks(16, 1.5, 16));
        _harness.Tick(engine);
        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        Assert.Single(world.VoxelChunks);

        probe.Voxels.Destroy(probe.Context, grid);
        _harness.Tick(engine);

        Assert.Empty(world.VoxelChunks);
        Assert.Equal(0, probe.Physics.Statistics.VoxelChunks);
    }

    [Theory]
    [InlineData(1_000_000_000, -1_000_000_000)]
    [InlineData(-1_000_000_000, 1_000_000_000)]
    public void ChunksFarFromTheOriginArePlacedExactly(int x, int z)
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        WorldPosition corner = WorldPosition.FromBlocks(x, 0, z);
        probe.Activate(corner.Offset(new Vector3(16, 16, 16)), new ChunkArea(2, 1));
        Floor(probe, corner, new ChunkCoordinate(0, 0, 0), new ChunkCoordinate(0, 0, 0));
        probe.CreateBody(corner.Offset(new Vector3(16.25f, 1.5f, 16.75f)));

        _harness.Tick(engine);

        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        VoxelChunkCollider chunk = Assert.Single(world.VoxelChunks.Values);
        Assert.Equal(PhysicsFrame.ToLocal(world.Options.Origin, corner), chunk.Position);
        Assert.True(world.LargestCoordinate < 64, $"The backend received {world.LargestCoordinate}.");
    }

    /// <summary>Creates a grid whose chunks, from one to another along X, hold one layer of blocks at y = 0.</summary>
    private static Grid Floor(PhysicsProbe probe, WorldPosition corner, ChunkCoordinate first, ChunkCoordinate last)
    {
        Grid grid = probe.Voxels.CreateGrid(probe.Context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(corner) });
        for (int x = first.X; x <= last.X; x++)
        {
            Chunk chunk = grid.Storage.GetOrCreateChunk(new ChunkCoordinate(x, first.Y, first.Z));
            for (int cz = 0; cz < VoxelCoordinates.ChunkSize; cz++)
            {
                for (int cx = 0; cx < VoxelCoordinates.ChunkSize; cx++)
                {
                    chunk.Palette.Set(VoxelCoordinates.ToIndex(new LocalBlockCoordinate(cx, 0, cz)), 1);
                }
            }
        }

        return grid;
    }
}
