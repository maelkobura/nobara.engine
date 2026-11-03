// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using Nobara.Voxel;

namespace Nobara.Voxel.Tests;

public sealed class ChunkTests
{
    [Fact]
    public void VersionAndNaturalChannelTrackStateChanges()
    {
        using var chunk = new Chunk(new ChunkCoordinate(0, 0, 0));

        Assert.Equal(0u, chunk.Version);
        Assert.True(chunk.IsNatural(0));
        Assert.True(chunk.SetState(0, 7));
        Assert.Equal(1u, chunk.Version);
        Assert.False(chunk.SetState(0, 7));
        Assert.False(chunk.IsNatural(0));
        Assert.True(chunk.SetState(0, 8, natural: true));
        Assert.True(chunk.IsNatural(0));
        Assert.Equal(2u, chunk.Version);
    }

    [Fact]
    public void DenseChannelsAllocateLazilyAndReleaseOnCompaction()
    {
        var channel = new DenseChannelDefinition("example.moisture", DenseChannelBits.Eight, 3);
        using var chunk = new Chunk(new ChunkCoordinate(0, 0, 0));

        Assert.Equal(3u, chunk.GetChannel(channel, 10));
        Assert.True(chunk.SetChannel(channel, 10, 9));
        Assert.Equal(9u, chunk.GetChannel(channel, 10));
        Assert.True(chunk.SetChannel(channel, 10, 3));
        chunk.CompactChannels();
        Assert.Equal(3u, chunk.GetChannel(channel, 10));
    }

    [Fact]
    public void FrozenCopyIncludesRequestedBorder()
    {
        using var chunk = new Chunk(new ChunkCoordinate(0, 0, 0));
        using var border = new Chunk(new ChunkCoordinate(1, 0, 0));
        chunk.SetState(0, 5);
        border.SetState(1, 6);

        ChunkSnapshot snapshot = chunk.CopyFrozen(border);

        Assert.Equal(5u, snapshot.GetState(0));
        Assert.True(snapshot.TryGetBorderState(border.Coordinate, 1, out uint state));
        Assert.Equal(6u, state);
    }

    [Fact]
    public void BlockDataTypeProvidesSerializationAndRotationHook()
    {
        var dataType = new BlockDataType<int>(
            "example.counter",
            static (value, writer) =>
            {
                Span<byte> bytes = writer.GetSpan(sizeof(int));
                BitConverter.TryWriteBytes(bytes, value);
                writer.Advance(sizeof(int));
            },
            static source => BitConverter.ToInt32(source),
            static value => value + 1);
        using var chunk = new Chunk(new ChunkCoordinate(0, 0, 0));
        chunk.SetBlockData(dataType, 3, 4);
        Assert.Equal(4, chunk.GetBlockData(dataType, 3));

        var writer = new ArrayBufferWriter<byte>();
        dataType.Serialize(4, writer);
        Assert.Equal(4, dataType.Deserialize(writer.WrittenSpan));
        Assert.Equal(5, dataType.Rotate(4));
    }
}
