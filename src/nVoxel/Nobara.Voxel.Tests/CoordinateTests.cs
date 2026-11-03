// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Mathematics;
using Nobara.Voxel;

namespace Nobara.Voxel.Tests;

public sealed class CoordinateTests
{
    [Fact]
    public void NegativeCoordinatesUseFloorChunkAndLocalCoordinates()
    {
        var block = new BlockCoordinate(-1, -32, -33);

        Assert.Equal(new ChunkCoordinate(-1, -1, -2), VoxelCoordinates.ToChunk(block));
        Assert.Equal(new LocalBlockCoordinate(31, 0, 31), VoxelCoordinates.ToLocal(block));
        Assert.Equal(31 + (32 * (31 + (32 * 0))), VoxelCoordinates.ToIndex(block));
    }

    [Fact]
    public void CombiningChunkAndLocalCoordinatesRejectsOverflow()
    {
        Assert.Equal(int.MaxValue, VoxelCoordinates.ToBlock(new ChunkCoordinate(int.MaxValue >> 5, int.MaxValue >> 5, int.MaxValue >> 5), new LocalBlockCoordinate(31, 31, 31)).X);
        Assert.Equal(int.MinValue, VoxelCoordinates.ToBlock(new ChunkCoordinate(int.MinValue >> 5, int.MinValue >> 5, int.MinValue >> 5), new LocalBlockCoordinate(0, 0, 0)).X);
        Assert.Throws<OverflowException>(() => VoxelCoordinates.ToBlock(new ChunkCoordinate((int.MaxValue >> 5) + 1, 0, 0), new LocalBlockCoordinate(0, 0, 0)));
    }

    [Theory]
    [InlineData(-1_000_000_000)]
    [InlineData(1_000_000_000)]
    [InlineData(-2_147_483_648)]
    [InlineData(2_147_483_647)]
    public void WorldPositionRoundTripsAtLargeCoordinates(int coordinate)
    {
        var block = new BlockCoordinate(coordinate, coordinate, coordinate);
        WorldPosition position = VoxelCoordinates.ToWorldPosition(block);
        if (coordinate == int.MaxValue)
        {
            Assert.Equal(67108863, position.ChunkX);
            Assert.Equal(31f, position.Local.X);
        }

        Assert.Equal(block, VoxelCoordinates.ToBlock(position));
    }
}
