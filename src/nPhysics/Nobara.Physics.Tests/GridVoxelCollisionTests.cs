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

/// <summary>How the body of a grid collides with other voxels: its surface cells, its settings, and its counts.</summary>
public sealed class GridVoxelCollisionTests : IDisposable
{
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TheSurfaceCellsAreMarkedByChunkAndCounted()
    {
        var collision = new GridVoxelCollision();
        collision.SetSurface(new BlockCoordinate(1, 2, 3), true);
        collision.SetSurface(new BlockCoordinate(-1, 0, 0), true);
        collision.SetSurface(new BlockCoordinate(-1, 0, 0), true);   // marked once

        Assert.Equal(2, collision.SurfaceCount);
        Assert.True(collision.IsSurface(new BlockCoordinate(1, 2, 3)));
        Assert.False(collision.IsSurface(new BlockCoordinate(1, 2, 4)));
        ReadOnlySpan<ulong> words = collision.GetSurface(new ChunkCoordinate(-1, 0, 0));
        Assert.Equal(GridVoxelCollision.WordsPerChunk, words.Length);
        int index = VoxelCoordinates.ToIndex(new BlockCoordinate(-1, 0, 0));
        Assert.NotEqual(0UL, words[index >> 6] & (1UL << (index & 63)));

        collision.SetSurface(new BlockCoordinate(-1, 0, 0), false);
        Assert.Equal(1, collision.SurfaceCount);
        Assert.True(collision.GetSurface(new ChunkCoordinate(-1, 0, 0)).IsEmpty);   // a chunk without surface cell is let go

        collision.ClearSurface(new ChunkCoordinate(0, 0, 0));
        Assert.Equal(0, collision.SurfaceCount);
        Assert.False(collision.IsSurface(new BlockCoordinate(1, 2, 3)));
    }

    [Fact]
    public void ClearingTheSurfaceReusesTheWordsOfItsChunks()
    {
        var collision = new GridVoxelCollision();
        collision.SetSurface(new BlockCoordinate(0, 0, 0), true);
        collision.ClearSurface();
        collision.SetSurface(new BlockCoordinate(5, 5, 5), false);

        long before = GC.GetAllocatedBytesForCurrentThread();
        collision.SetSurface(new BlockCoordinate(40, 0, 0), true);

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(1, collision.SurfaceCount);
        Assert.False(collision.IsSurface(new BlockCoordinate(0, 0, 0)));
    }

    [Fact]
    public void TheCountsAreTakenOnce()
    {
        var collision = new GridVoxelCollision();
        collision.Count(10, 0);
        collision.Count(4, 3);

        Assert.Equal(new VoxelContactCounts(2, 14, 3), collision.TakeCounts());
        Assert.Equal(default, collision.TakeCounts());
    }

    [Fact]
    public void ABudgetIsZeroOrMore()
    {
        var collision = new GridVoxelCollision { PairBudget = 64 };
        Assert.Equal(64, collision.PairBudget);
        Assert.Throws<ArgumentOutOfRangeException>(() => collision.PairBudget = -1);
    }

    [Fact]
    public void TheCollisionOfAGridReachesItsCells()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = probe.Voxels.CreateGrid(probe.Context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(WorldPosition.FromBlocks(8, 20, 8)) });
        grid.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0)).Palette.Set(0, 1);
        var collision = new GridVoxelCollision();

        probe.Physics.SetVoxelCollision(grid, collision);
        VoxelGridCells cells = probe.Physics.GetShape(probe.Physics.CreateVoxels(grid, Vector3.Zero)).Cells!;
        Assert.Same(collision, cells.Collision);   // set before the cells exist

        probe.Physics.SetVoxelCollision(grid, null);
        Assert.Null(cells.Collision);
        probe.Physics.SetVoxelCollision(grid, collision);
        Assert.Same(collision, cells.Collision);
    }

    [Fact]
    public void TheTerrainAndADestroyedGridHaveNoCollision()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Assert.True(probe.Voxels.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        Grid grid = probe.Voxels.CreateGrid(probe.Context, DimensionId.Default, new GridOptions());
        probe.Voxels.Destroy(probe.Context, grid);

        Assert.Throws<ArgumentException>(() => probe.Physics.SetVoxelCollision(terrain!, new GridVoxelCollision()));
        Assert.Throws<ArgumentException>(() => probe.Physics.SetVoxelCollision(grid, new GridVoxelCollision()));
        Assert.Throws<ArgumentNullException>(() => probe.Physics.SetVoxelCollision(null!, null));
    }
}
