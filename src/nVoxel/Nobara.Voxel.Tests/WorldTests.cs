// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Context;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Voxel.Tests;

public sealed class WorldTests
{
    [Fact]
    public void TerrainAndBoundedGridsAreIndexedAndReleasedWithTheSession()
    {
        using var harness = new VoxelHarness();
        using var engine = harness.Start();
        harness.Tick(engine, 2);

        Assert.Equal(4, harness.Owner.Worlds.Grids.Count);
        Assert.True(harness.Owner.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        Assert.NotNull(terrain);
        GridSpatialIndex index = harness.Owner.Worlds.SpatialIndex;
        IReadOnlyList<Grid> candidates = index.Query(WorldPosition.FromBlocks(10, 0, 0), new Vector3(3));
        Assert.Contains(harness.Owner.CreatedGrids[1], candidates);
        Assert.DoesNotContain(harness.Owner.CreatedGrids[2], candidates);

        harness.Owner.Sessions.End();
        harness.Tick(engine);

        Assert.Empty(harness.Owner.Worlds.Grids);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void TheIndexFillsAListWithoutAllocatingOnceItHasGrown()
    {
        using var harness = new VoxelHarness();
        using var engine = harness.Start();
        harness.Tick(engine, 2);
        GridSpatialIndex index = harness.Owner.Worlds.SpatialIndex;
        var box = WorldBox.Around(WorldPosition.FromBlocks(10, 0, 0), new Vector3(3));
        var results = new List<Grid>();
        index.Query(box, results);

        Assert.Equal(index.Query(box), results);
        results.Clear();

        // The runtime may allocate now and then on this thread, such as while it compiles methods again: a round without allocation must come.
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                results.Clear();
                index.Query(box, results);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TheStorageListsItsResidentChunksWithoutAllocatingOnceTheListHasGrown()
    {
        using var harness = new VoxelHarness();
        using var engine = harness.Start();
        harness.Tick(engine, 2);
        GridStorage storage = harness.Owner.CreatedGrids[1].Storage;
        Chunk first = storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        Chunk second = storage.GetOrCreateChunk(new ChunkCoordinate(1, 0, 0));
        var chunks = new List<Chunk>();
        storage.GetResidentChunks(chunks);

        Assert.Equal(storage.ChunkCount, chunks.Count);
        Assert.Contains(first, chunks);
        Assert.Contains(second, chunks);

        // The runtime may allocate now and then on this thread, such as while it compiles methods again: a round without allocation must come.
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                chunks.Clear();
                storage.GetResidentChunks(chunks);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ASecondSessionCreatesItsGridsInTheNewEntityWorld()
    {
        using var harness = new VoxelHarness();
        using var engine = harness.Start();
        harness.Tick(engine, 2);
        Assert.True(harness.Owner.Worlds.TryGetTerrain(DimensionId.Default, out Grid? firstTerrain));

        for (int session = 0; session < 2; session++)
        {
            harness.Owner.Sessions.End();
            harness.Tick(engine);
            Assert.Empty(harness.Owner.Worlds.Grids);

            harness.Owner.Sessions.Start();
            harness.Tick(engine, 2);

            Assert.Equal(SessionState.Active, harness.Owner.Sessions.State);
            Assert.Equal(4, harness.Owner.Worlds.Grids.Count);
            Assert.True(harness.Owner.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
            Assert.NotSame(firstTerrain, terrain);
        }
    }

    [Fact]
    public void GridBoundsRejectWritesOutsideTheGrid()
    {
        using var harness = new VoxelHarness();
        using var engine = harness.Start();
        harness.Tick(engine, 2);
        Grid grid = harness.Owner.CreatedGrids[0];
        Chunk chunk = grid.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        chunk.SetState(0, 12);

        Assert.Equal(12u, grid.GetBlock(new BlockCoordinate(0, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetBlock(new BlockCoordinate(3, 0, 0)));
    }

    [Fact]
    public void ContextsOwnIndependentVoxelWorlds()
    {
        using var first = new VoxelHarness();
        using var second = new VoxelHarness();
        using var firstEngine = first.Start();
        using var secondEngine = second.Start();
        first.Tick(firstEngine, 2);
        second.Tick(secondEngine, 2);

        Assert.NotSame(first.Owner.Worlds, second.Owner.Worlds);
        Assert.NotSame(first.Owner.Worlds.SpatialIndex, second.Owner.Worlds.SpatialIndex);
        Assert.Equal(4, first.Owner.Worlds.Grids.Count);
        Assert.Equal(4, second.Owner.Worlds.Grids.Count);
    }
}
