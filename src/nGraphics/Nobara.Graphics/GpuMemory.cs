// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>How much GPU memory the buffers of a device hold.</summary>
/// <param name="BlockCount">The number of blocks, dedicated ones included.</param>
/// <param name="DedicatedBlockCount">The number of blocks that hold one large buffer each.</param>
/// <param name="ReservedBytes">The size of all blocks.</param>
/// <param name="UsedBytes">The size of all buffers, alignment padding included.</param>
public readonly record struct GpuMemoryStatistics(int BlockCount, int DedicatedBlockCount, long ReservedBytes, long UsedBytes);

/// <summary>A range of a block given to one buffer.</summary>
internal readonly record struct GpuAllocation(GpuMemoryBlock Block, long Offset, long Size, bool Dedicated);

/// <summary>
/// Sub-allocates buffers from large blocks: first fit in a list of free
/// ranges sorted by offset, merged with their neighbors when freed. A buffer
/// larger than a quarter of a block gets a block of its own.
/// </summary>
internal sealed class GpuMemoryAllocator
{
    private readonly Func<long, GpuMemoryBlock> _createBlock;
    private readonly List<BlockState> _blocks = [];
    private int _dedicated;
    private long _dedicatedBytes;

    public GpuMemoryAllocator(long blockSize, long alignment, Func<long, GpuMemoryBlock> createBlock)
    {
        if (blockSize <= 0 || alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "The block size must be positive and the alignment a power of two.");
        }

        BlockSize = blockSize;
        Alignment = alignment;
        _createBlock = createBlock;
    }

    /// <summary>Gets the size of a shared block.</summary>
    public long BlockSize { get; }

    /// <summary>Gets the alignment of every buffer in a shared block.</summary>
    public long Alignment { get; }

    public GpuMemoryStatistics Statistics
    {
        get
        {
            long used = _dedicatedBytes;
            foreach (BlockState block in _blocks)
            {
                used += block.Used;
            }

            return new GpuMemoryStatistics(_blocks.Count + _dedicated, _dedicated, (_blocks.Count * BlockSize) + _dedicatedBytes, used);
        }
    }

    public GpuAllocation Allocate(long size, bool dedicated = false)
    {
        long aligned = Align(size);
        if (dedicated || aligned > BlockSize / 4)
        {
            GpuMemoryBlock own = _createBlock(size);
            _dedicated++;
            _dedicatedBytes += size;
            return new GpuAllocation(own, 0, size, Dedicated: true);
        }

        foreach (BlockState block in _blocks)
        {
            if (block.TryTake(aligned, out long offset))
            {
                return new GpuAllocation(block.Block, offset, aligned, Dedicated: false);
            }
        }

        var created = new BlockState(_createBlock(BlockSize), BlockSize);
        _blocks.Add(created);
        created.TryTake(aligned, out long first);
        return new GpuAllocation(created.Block, first, aligned, Dedicated: false);
    }

    /// <summary>Gives a range back; a block left empty is destroyed, unless it is the last shared block.</summary>
    [HotPath]
    public void Free(GpuAllocation allocation)
    {
        if (allocation.Dedicated)
        {
            _dedicated--;
            _dedicatedBytes -= allocation.Size;
            allocation.Block.Destroy();
            return;
        }

        for (int i = 0; i < _blocks.Count; i++)
        {
            BlockState block = _blocks[i];
            if (!ReferenceEquals(block.Block, allocation.Block))
            {
                continue;
            }

            block.Give(allocation.Offset, allocation.Size);
            if (block.Used == 0 && _blocks.Count > 1)
            {
                _blocks.RemoveAt(i);
                block.Block.Destroy();
            }

            return;
        }
    }

    /// <summary>Destroys every block; the device is going away.</summary>
    public void DestroyAll()
    {
        foreach (BlockState block in _blocks)
        {
            block.Block.Destroy();
        }

        _blocks.Clear();
    }

    private long Align(long size) => (size + Alignment - 1) & ~(Alignment - 1);

    /// <summary>A shared block and its free ranges, sorted by offset and never adjacent.</summary>
    private sealed class BlockState
    {
        private readonly List<(long Offset, long Size)> _free;

        public BlockState(GpuMemoryBlock block, long size)
        {
            Block = block;
            _free = new List<(long, long)>(8) { (0, size) };
        }

        public GpuMemoryBlock Block { get; }

        public long Used { get; private set; }

        public bool TryTake(long size, out long offset)
        {
            for (int i = 0; i < _free.Count; i++)
            {
                (long start, long length) = _free[i];
                if (length < size)
                {
                    continue;
                }

                if (length == size)
                {
                    _free.RemoveAt(i);
                }
                else
                {
                    _free[i] = (start + size, length - size);
                }

                Used += size;
                offset = start;
                return true;
            }

            offset = 0;
            return false;
        }

        [HotPath]
        public void Give(long offset, long size)
        {
            Used -= size;
            int index = 0;
            while (index < _free.Count && _free[index].Offset < offset)
            {
                index++;
            }

            bool joinsPrevious = index > 0 && _free[index - 1].Offset + _free[index - 1].Size == offset;
            bool joinsNext = index < _free.Count && offset + size == _free[index].Offset;
            if (joinsPrevious && joinsNext)
            {
                _free[index - 1] = (_free[index - 1].Offset, _free[index - 1].Size + size + _free[index].Size);
                _free.RemoveAt(index);
            }
            else if (joinsPrevious)
            {
                _free[index - 1] = (_free[index - 1].Offset, _free[index - 1].Size + size);
            }
            else if (joinsNext)
            {
                _free[index] = (offset, size + _free[index].Size);
            }
            else
            {
                _free.Insert(index, (offset, size));
            }
        }
    }
}
