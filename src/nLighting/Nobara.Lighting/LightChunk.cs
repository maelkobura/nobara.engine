// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>
/// The light of one chunk: one 16-bit word per block (sky in the top four
/// bits, then the block channel), stored as one value while every block
/// holds it, and as an array in native memory otherwise.
/// </summary>
/// <remarks>
/// Without color, the array holds one byte per block (sky, then the one level
/// of the block channel); with color, one 16-bit word. Written on the loop
/// thread, or in a propagation job that owns the chunk.
/// </remarks>
internal sealed unsafe class LightChunk : IDisposable
{
    /// <summary>The value of a block before any light is computed: full sky, no block light.</summary>
    public const ushort DefaultValue = 0xF000;

    public const int Volume = VoxelCoordinates.ChunkVolume;

    private readonly bool _colored;
    private void* _data;
    private ushort _uniform;
    private bool _disposed;

    public LightChunk(Grid grid, ChunkCoordinate coordinate, Chunk blocks, bool colored, SkyColumn column, ushort initial)
    {
        Grid = grid;
        Coordinate = coordinate;
        Blocks = blocks;
        _colored = colored;
        Column = column;
        BaseY = coordinate.Y * VoxelCoordinates.ChunkSize;
        _uniform = initial;
    }

    public Grid Grid { get; }

    public ChunkCoordinate Coordinate { get; }

    /// <summary>Gets the blocks of the chunk in nVoxel.</summary>
    public Chunk Blocks { get; }

    /// <summary>Gets the heights of the column of chunks, from which the cells exposed to the sky follow.</summary>
    public SkyColumn Column { get; }

    /// <summary>Gets the coordinate along Y of the lowest layer of the chunk.</summary>
    public int BaseY { get; }

    /// <summary>Gets or sets the squared distance to the nearest point of interest, which orders the work of a pass.</summary>
    public float Priority;

    /// <summary>Gets whether the chunk left memory.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Gets the loaded chunk on each side, by <see cref="BlockFace"/>.</summary>
    public LightChunk?[] Neighbors { get; } = new LightChunk?[6];

    /// <summary>Gets or sets the work of the next propagation pass, or <see langword="null"/> when it has none.</summary>
    public WorkState? Work { get; set; }

    /// <summary>Gets or sets whether a value changed during the current pass.</summary>
    public bool Changed;

    /// <summary>Gets or sets whether a value of a block on the border changed during the current pass.</summary>
    public bool BorderChanged;

    /// <summary>Gets the version, incremented after each pass that changed the light.</summary>
    public uint Version;

    public bool IsUniform => _data == null;

    /// <summary>Gets the bytes of the array: 0 in uniform mode.</summary>
    public int StorageBytes => _data == null ? 0 : _colored ? Volume * sizeof(ushort) : Volume;

    /// <summary>Gets the value of every block in uniform mode.</summary>
    public ushort UniformValue => _uniform;

    [HotPath]
    public ushort Get(int index)
    {
        if (_data == null) return _uniform;
        if (_colored) return ((ushort*)_data)[index];
        byte packed = ((byte*)_data)[index];
        return (ushort)(((packed & 0xF0) << 8) | (packed & 0x0F));
    }

    /// <summary>Tells whether a cell is above the highest opaque block of its column, where the sky channel is 15.</summary>
    [HotPath]
    public bool IsExposed(int index) => Column.Heights[index & 1023] < BaseY + (index >> 10);

    /// <summary>Writes the value of a block, leaving uniform mode at the first block that differs.</summary>
    [HotPath]
    public void Set(int index, ushort value)
    {
        if (_data == null)
        {
            if (value == _uniform) return;
            Expand();
        }

        if (_colored)
        {
            ref ushort cell = ref ((ushort*)_data)[index];
            if (cell == value) return;
            cell = value;
        }
        else
        {
            byte packed = (byte)(((value >> 8) & 0xF0) | (value & 0x0F));
            ref byte cell = ref ((byte*)_data)[index];
            if (cell == packed) return;
            cell = packed;
        }

        Changed = true;
        if (Faces.IsBorder(index)) BorderChanged = true;
    }

    /// <summary>Returns to uniform mode when every block holds the same value.</summary>
    /// <returns><see langword="true"/> when the array was released.</returns>
    public bool Compact()
    {
        if (_data == null) return false;
        if (_colored)
        {
            var values = new ReadOnlySpan<ushort>(_data, Volume);
            if (values.IndexOfAnyExcept(values[0]) >= 0) return false;
            _uniform = values[0];
        }
        else
        {
            var values = new ReadOnlySpan<byte>(_data, Volume);
            if (values.IndexOfAnyExcept(values[0]) >= 0) return false;
            _uniform = (ushort)(((values[0] & 0xF0) << 8) | (values[0] & 0x0F));
        }

        NativeMemory.Free(_data);
        _data = null;
        return true;
    }

    /// <summary>Copies the values of every block, as 16-bit words, by local index.</summary>
    public void CopyTo(Span<ushort> destination)
    {
        if (_data == null)
        {
            destination[..Volume].Fill(_uniform);
            return;
        }

        if (_colored)
        {
            new ReadOnlySpan<ushort>(_data, Volume).CopyTo(destination);
            return;
        }

        for (int i = 0; i < Volume; i++) destination[i] = Get(i);
    }

    public void Dispose()
    {
        _disposed = true;
        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }

        GC.SuppressFinalize(this);
    }

    [HotPath]
    private void Expand()
    {
        if (_colored)
        {
            _data = NativeMemory.Alloc(Volume, sizeof(ushort));
            new Span<ushort>(_data, Volume).Fill(_uniform);
        }
        else
        {
            _data = NativeMemory.Alloc(Volume);
            new Span<byte>(_data, Volume).Fill((byte)(((_uniform >> 8) & 0xF0) | (_uniform & 0x0F)));
        }
    }
}

/// <summary>Index arithmetic of the cells of a chunk and of the cells of its faces.</summary>
[HotPath]
internal static class Faces
{
    /// <summary>The cells of one face of a chunk.</summary>
    public const int Cells = 1024;

    /// <summary>The opposite face, by <see cref="BlockFace"/>: north and south, east and west, up and down.</summary>
    public static int Opposite(int face) => face < 4 ? (face + 2) & 3 : face ^ 1;

    /// <summary>Tells whether a cell is on the border of its chunk.</summary>
    public static bool IsBorder(int index)
    {
        int x = index & 31;
        int z = (index >> 5) & 31;
        int y = index >> 10;
        return x == 0 || x == 31 || y == 0 || y == 31 || z == 0 || z == 31;
    }

    /// <summary>Gets the cell beside another one, in the same chunk.</summary>
    /// <param name="index">The local index of the cell.</param>
    /// <param name="face">The side, by <see cref="BlockFace"/>.</param>
    /// <param name="neighbor">The local index of the cell beside it.</param>
    /// <returns><see langword="false"/> when that cell is in the next chunk.</returns>
    public static bool Step(int index, int face, out int neighbor)
    {
        int x = index & 31;
        int z = (index >> 5) & 31;
        int y = index >> 10;
        switch (face)
        {
            case 0: z--; break;
            case 1: x++; break;
            case 2: z++; break;
            case 3: x--; break;
            case 4: y++; break;
            default: y--; break;
        }

        if (((x | y | z) & ~31) != 0)
        {
            neighbor = -1;
            return false;
        }

        neighbor = x + (32 * (z + (32 * y)));
        return true;
    }

    /// <summary>Gets the cell of a chunk on one of its faces.</summary>
    /// <param name="face">The face, by <see cref="BlockFace"/>.</param>
    /// <param name="slot">The position on the face, from 0 to 1,023, shared by the cells that touch across it.</param>
    /// <returns>The local index.</returns>
    public static int Cell(int face, int slot)
    {
        int u = slot & 31;
        int v = slot >> 5;
        return face switch
        {
            0 => u + (32 * (32 * v)),
            1 => 31 + (32 * (u + (32 * v))),
            2 => u + (32 * (31 + (32 * v))),
            3 => 32 * (u + (32 * v)),
            4 => u + (32 * (v + (32 * 31))),
            _ => u + (32 * v),
        };
    }

    /// <summary>Gets the position on a face of a cell of that face.</summary>
    public static int Slot(int face, int index)
    {
        int x = index & 31;
        int z = (index >> 5) & 31;
        int y = index >> 10;
        return face switch
        {
            0 or 2 => x + (32 * y),
            1 or 3 => z + (32 * y),
            _ => x + (32 * z),
        };
    }

    /// <summary>Takes each component of a light value (sky, red, green, blue) from another, at least zero.</summary>
    /// <param name="value">The light value.</param>
    /// <param name="filter">The levels to take from each component.</param>
    /// <param name="loss">The level to take from each component on top.</param>
    public static ushort Subtract(ushort value, ushort filter, int loss)
    {
        int sky = (value >> 12) - (filter >> 12) - loss;
        int red = ((value >> 8) & 15) - ((filter >> 8) & 15) - loss;
        int green = ((value >> 4) & 15) - ((filter >> 4) & 15) - loss;
        int blue = (value & 15) - (filter & 15) - loss;
        return (ushort)((Math.Max(sky, 0) << 12) | (Math.Max(red, 0) << 8) | (Math.Max(green, 0) << 4) | Math.Max(blue, 0));
    }

    /// <summary>Keeps the brightest of each component.</summary>
    public static ushort Max(ushort a, ushort b) =>
        (ushort)(Math.Max(a & 0xF000, b & 0xF000) | Math.Max(a & 0xF00, b & 0xF00) | Math.Max(a & 0x0F0, b & 0x0F0) | Math.Max(a & 0x00F, b & 0x00F));

    /// <summary>Tells whether a value is brighter than another in at least one component.</summary>
    public static bool Raises(ushort value, ushort over) => Max(value, over) != over;

    /// <summary>Keeps the components of a value that are brighter than those of another.</summary>
    public static ushort Above(ushort value, ushort than)
    {
        ushort kept = 0;
        for (int shift = 0; shift < 16; shift += 4)
        {
            int level = (value >> shift) & 15;
            if (level > ((than >> shift) & 15)) kept |= (ushort)(level << shift);
        }

        return kept;
    }

    /// <summary>Keeps the components of a value that are at least those of another.</summary>
    public static ushort KeepAtLeast(ushort value, ushort than)
    {
        ushort kept = 0;
        for (int shift = 0; shift < 16; shift += 4)
        {
            int level = (value >> shift) & 15;
            if (level >= ((than >> shift) & 15)) kept |= (ushort)(level << shift);
        }

        return kept;
    }
}

/// <summary>
/// The work of one chunk in a propagation pass: the cells to propagate from,
/// the removals to start, and the fronts that cross its faces.
/// </summary>
/// <remarks>Taken from a pool for a pass, and returned once the pass ends.</remarks>
internal sealed class WorkState
{
    /// <summary>The cells whose light spreads in the next addition round, one bit per cell.</summary>
    public readonly ulong[] Pending = new ulong[LightChunk.Volume / 64];

    /// <summary>The light that enters through each face, by face and slot: the value before the filter of the cell.</summary>
    public readonly ushort[] IncomingAdd = new ushort[6 * Faces.Cells];

    /// <summary>The removals that enter through each face, by face and slot: the value of the cell they come from.</summary>
    public readonly ushort[] IncomingRemoval = new ushort[6 * Faces.Cells];

    /// <summary>The light that leaves through each face, by face and slot, for the chunk on that side.</summary>
    public readonly ushort[] OutgoingAdd = new ushort[6 * Faces.Cells];

    /// <summary>The removals that leave through each face, by face and slot, for the chunk on that side.</summary>
    public readonly ushort[] OutgoingRemoval = new ushort[6 * Faces.Cells];

    /// <summary>The removals that start in this chunk: local index in the low 16 bits, removed value in the high 16 bits.</summary>
    public readonly List<uint> Seeds = [];

    /// <summary>The removals that a lane left in its queue when its budget ran out, in the same form as <see cref="Seeds"/>.</summary>
    public readonly List<uint> RemovalQueue = [];

    /// <summary>The number of the tick when the chunk got this work, to measure how late the light is.</summary>
    public long Since;

    public bool HasPending;
    public bool HasIncomingAdd;
    public bool HasIncomingRemoval;
    public bool HasOutgoingAdd;
    public bool HasOutgoingRemoval;

    public bool HasRemovalWork => Seeds.Count > 0 || RemovalQueue.Count > 0 || HasIncomingRemoval;

    public bool HasAdditionWork => HasPending || HasIncomingAdd;

    [HotPath]
    public void MarkPending(int index)
    {
        Pending[index >> 6] |= 1UL << (index & 63);
        HasPending = true;
    }

    public void Clear()
    {
        if (HasPending) Array.Clear(Pending);
        if (HasIncomingAdd) Array.Clear(IncomingAdd);
        if (HasIncomingRemoval) Array.Clear(IncomingRemoval);
        if (HasOutgoingAdd) Array.Clear(OutgoingAdd);
        if (HasOutgoingRemoval) Array.Clear(OutgoingRemoval);
        Seeds.Clear();
        RemovalQueue.Clear();
        HasPending = HasIncomingAdd = HasIncomingRemoval = HasOutgoingAdd = HasOutgoingRemoval = false;
    }
}

/// <summary>The queues of one lane of a propagation job, reused from chunk to chunk.</summary>
internal sealed class Scratch
{
    private const int Mask = LightChunk.Volume - 1;

    private readonly ushort[] _additions = new ushort[LightChunk.Volume];
    private readonly ulong[] _queued = new ulong[LightChunk.Volume / 64];
    private readonly ushort[] _removals = new ushort[LightChunk.Volume];
    private readonly ushort[] _removed = new ushort[LightChunk.Volume];
    private int _additionHead;
    private int _additionCount;
    private int _removalHead;
    private int _removalCount;

    /// <summary>The operations of the lane in the current round: cells taken from a queue, and seeds.</summary>
    public int Operations;

    /// <summary>The operations the lane may do in the current round.</summary>
    public int Quota;

    public bool IsSpent
    {
        [HotPath]
        get => Operations >= Quota;
    }

    public bool HasAdditions
    {
        [HotPath]
        get => _additionCount > 0;
    }

    public bool HasRemovals
    {
        [HotPath]
        get => _removalCount > 0;
    }

    /// <summary>Queues a cell whose light spreads; a cell already queued stays once, and spreads its value when it leaves the queue.</summary>
    [HotPath]
    public void PushAddition(int index)
    {
        ref ulong word = ref _queued[index >> 6];
        ulong bit = 1UL << (index & 63);
        if ((word & bit) != 0) return;
        word |= bit;
        _additions[(_additionHead + _additionCount++) & Mask] = (ushort)index;
    }

    [HotPath]
    public int PopAddition()
    {
        int index = _additions[_additionHead];
        _additionHead = (_additionHead + 1) & Mask;
        _additionCount--;
        _queued[index >> 6] &= ~(1UL << (index & 63));
        return index;
    }

    /// <summary>Queues a cell whose light was removed; a cell already queued keeps the brightest removed value of each component.</summary>
    [HotPath]
    public void PushRemoval(int index, ushort removed)
    {
        ref ushort current = ref _removed[index];
        if (current == 0) _removals[(_removalHead + _removalCount++) & Mask] = (ushort)index;
        current = Faces.Max(current, removed);
    }

    [HotPath]
    public int PopRemoval(out ushort removed)
    {
        int index = _removals[_removalHead];
        _removalHead = (_removalHead + 1) & Mask;
        _removalCount--;
        removed = _removed[index];
        _removed[index] = 0;
        return index;
    }
}
