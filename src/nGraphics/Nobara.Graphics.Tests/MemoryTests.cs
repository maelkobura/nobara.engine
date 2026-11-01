// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Graphics.Tests.Support;

namespace Nobara.Graphics.Tests;

/// <summary>Buffers share large blocks of GPU memory; large buffers get their own.</summary>
public sealed class MemoryTests
{
    // Blocks of 1 MiB; the alignment is 256, the uniform alignment of the fake device.
    private readonly FakeDevice _device = new(blockSize: 1024 * 1024);

    [Fact]
    public void Small_buffers_share_a_block_at_aligned_offsets()
    {
        GpuBuffer a = _device.CreateBuffer(new BufferDescription(100, BufferUsage.Vertex));
        GpuBuffer b = _device.CreateBuffer(new BufferDescription(300, BufferUsage.Uniform));
        GpuBuffer c = _device.CreateBuffer(new BufferDescription(16, BufferUsage.Index));

        Assert.Same(a.Block, b.Block);
        Assert.Same(a.Block, c.Block);
        Assert.Equal([0L, 256L, 768L], new[] { a.Offset, b.Offset, c.Offset });
        Assert.Equal(new GpuMemoryStatistics(1, 0, 1024 * 1024, 256 + 512 + 256), _device.Memory);
        Assert.Equal(1, _device.NativeObjects);
    }

    [Fact]
    public void A_large_buffer_has_a_block_of_its_own()
    {
        GpuBuffer small = _device.CreateBuffer(new BufferDescription(64, BufferUsage.Vertex));
        GpuBuffer large = _device.CreateBuffer(new BufferDescription(600_000, BufferUsage.Vertex) { Name = "terrain" });
        Assert.NotSame(small.Block, large.Block);
        Assert.Equal(0, large.Offset);
        Assert.Equal(600_000, large.Block.Size);
        Assert.Equal(1, _device.Memory.DedicatedBlockCount);

        large.Dispose();
        Assert.Equal(0, _device.Memory.DedicatedBlockCount);
        Assert.Equal(1, _device.NativeObjects);
    }

    [Fact]
    public void A_dedicated_buffer_has_a_block_of_its_own_however_small()
    {
        _device.CreateBuffer(new BufferDescription(64, BufferUsage.Vertex));
        GpuBuffer indices = _device.CreateBuffer(new BufferDescription(64, BufferUsage.Index) { Dedicated = true });
        Assert.Equal(0, indices.Offset);
        Assert.Equal(64, indices.Block.Size);
        Assert.Equal(1, _device.Memory.DedicatedBlockCount);
    }

    [Fact]
    public void Freed_ranges_merge_and_are_reused()
    {
        GpuBuffer a = _device.CreateBuffer(new BufferDescription(256, BufferUsage.Vertex));
        GpuBuffer b = _device.CreateBuffer(new BufferDescription(256, BufferUsage.Vertex));
        GpuBuffer c = _device.CreateBuffer(new BufferDescription(256, BufferUsage.Vertex));
        a.Dispose();
        b.Dispose();

        // a and b merged: a buffer of 512 bytes fits where they were.
        GpuBuffer d = _device.CreateBuffer(new BufferDescription(512, BufferUsage.Vertex));
        Assert.Equal(0, d.Offset);
        Assert.Equal(512, c.Offset);
    }

    [Fact]
    public void A_full_block_opens_another_and_an_empty_extra_block_is_destroyed()
    {
        var buffers = new List<GpuBuffer>();
        for (int i = 0; i < 5; i++)
        {
            buffers.Add(_device.CreateBuffer(new BufferDescription(256 * 1024, BufferUsage.Storage)));
        }

        Assert.Equal(2, _device.Memory.BlockCount);
        Assert.NotSame(buffers[0].Block, buffers[4].Block);

        buffers[4].Dispose();
        Assert.Equal(1, _device.Memory.BlockCount);
        Assert.True(buffers[4].Block.IsDestroyed);

        // The last shared block stays, even empty.
        foreach (GpuBuffer buffer in buffers.Take(4))
        {
            buffer.Dispose();
        }

        Assert.Equal(1, _device.Memory.BlockCount);
        Assert.Equal(0, _device.Memory.UsedBytes);
    }
}
