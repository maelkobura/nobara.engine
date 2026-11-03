// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Memory;

namespace Nobara.Voxel;

/// <summary>A compact palette of 32-bit block state indices.</summary>
/// <remarks>
/// The uniform case stores one value and no index array. In palette mode the
/// indices use 1, 2, 4, 8, or 16 bits and never cross a 64-bit word. Reads and
/// writes do not allocate after a width has been reached.
/// </remarks>
public sealed class Palette : IDisposable
{
    private readonly int _capacity;
    private NativeArray<uint>? _values;
    private NativeArray<ulong>? _indices;
    private int _count;
    private int _bits;
    private uint _uniform;

    /// <summary>Creates a uniform palette.</summary>
    /// <param name="uniform">The state stored by every cell.</param>
    /// <param name="capacity">The maximum number of palette entries.</param>
    public Palette(uint uniform = 0, int capacity = ushort.MaxValue + 1)
    {
        if (capacity is < 1 or > ushort.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _uniform = uniform;
        _count = 1;
    }

    /// <summary>Gets whether this palette stores one value for every cell.</summary>
    public bool IsUniform => _values is null;

    /// <summary>Gets the number of distinct entries in the palette.</summary>
    public int Count => _count;

    /// <summary>Gets the number of bits used by each index, or 0 in uniform mode.</summary>
    public int BitsPerIndex => _bits;

    /// <summary>Gets a palette entry by its index.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is not in the palette.</exception>
    [HotPath]
    public uint GetEntry(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        return _values is null ? _uniform : _values[index];
    }

    /// <summary>Gets the state represented by a cell index.</summary>
    /// <param name="cell">The cell index.</param>
    /// <returns>The block state index.</returns>
    [HotPath]
    public uint Get(int cell)
    {
        RequireCell(cell);
        if (_values is null) return _uniform;
        ulong word = _indices![cell >> BitOperationsFor(_bits)];
        int shift = (cell & ((64 / _bits) - 1)) * _bits;
        return _values[(int)((word >> shift) & Mask(_bits))];
    }

    /// <summary>Sets the state represented by a cell index.</summary>
    /// <param name="cell">The cell index.</param>
    /// <param name="state">The block state index.</param>
    /// <returns><see langword="true"/> when the represented value changed.</returns>
    public bool Set(int cell, uint state)
    {
        RequireCell(cell);
        if (_values is null)
        {
            if (_capacity == 1) throw new InvalidOperationException("The block state palette is full.");
            if (_uniform == state) return false;
            EnsurePalette(state);
        }

        int paletteIndex = Find(state);
        if (paletteIndex < 0)
        {
            paletteIndex = Add(state);
        }

        int shift = (cell & ((64 / _bits) - 1)) * _bits;
        ulong mask = Mask(_bits) << shift;
        ref ulong word = ref _indices![cell >> BitOperationsFor(_bits)];
        uint old = (uint)((word & mask) >> shift);
        if (old == (uint)paletteIndex) return false;
        word = (word & ~mask) | ((ulong)paletteIndex << shift);
        return true;
    }

    /// <summary>Removes unused entries and returns to uniform mode when possible.</summary>
    /// <remarks>This is intended for a background compaction job; it may allocate temporary managed memory.</remarks>
    public void Compact(int cellCount)
    {
        if (_values is null) return;
        if (cellCount is < 0 or > VoxelCoordinates.ChunkVolume) throw new ArgumentOutOfRangeException(nameof(cellCount));

        var used = new HashSet<uint>();
        for (int i = 0; i < cellCount; i++) used.Add(Get(i));
        if (used.Count == 0) used.Add(0);
        if (used.Count == 1)
        {
            _uniform = used.First();
            _values?.Dispose();
            _indices?.Dispose();
            _values = null;
            _indices = null;
            _count = 1;
            _bits = 0;
            return;
        }

        var ordered = used.ToArray();
        Array.Sort(ordered);
        var remap = new Dictionary<uint, int>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++) remap[ordered[i]] = i;
        var values = new NativeArray<uint>(ordered.Length);
        ordered.AsSpan().CopyTo(values.AsSpan());
        var oldIndices = _indices!;
        int bits = BitsFor(ordered.Length);
        var indices = new NativeArray<ulong>((cellCount + (64 / bits) - 1) / (64 / bits));
        for (int i = 0; i < cellCount; i++)
        {
            int shift = (i & ((64 / _bits) - 1)) * _bits;
            uint state = _values[(int)((oldIndices[i >> BitOperationsFor(_bits)] >> shift) & Mask(_bits))];
            int newIndex = remap[state];
            int newShift = (i & ((64 / bits) - 1)) * bits;
            ref ulong word = ref indices[i >> BitOperationsFor(bits)];
            word |= (ulong)newIndex << newShift;
        }

        _values.Dispose();
        oldIndices.Dispose();
        _values = values;
        _indices = indices;
        _count = ordered.Length;
        _bits = bits;
    }

    /// <summary>Releases the native palette and index memory.</summary>
    public void Dispose()
    {
        _values?.Dispose();
        _indices?.Dispose();
        _values = null;
        _indices = null;
        GC.SuppressFinalize(this);
    }

    private void EnsurePalette(uint state)
    {
        _bits = 1;
        _values = new NativeArray<uint>(2);
        _values[0] = _uniform;
        _values[1] = state;
        _count = 2;
        _indices = new NativeArray<ulong>(VoxelCoordinates.ChunkVolume / 64);
        _indices.Clear();
    }

    private int Find(uint state)
    {
        for (int i = 0; i < _count; i++) if (_values![i] == state) return i;
        return -1;
    }

    private int Add(uint state)
    {
        if (_count == _capacity) throw new InvalidOperationException("The block state palette is full.");
        int nextBits = BitsFor(_count + 1);
        if (nextBits != _bits) Resize(nextBits);
        if (_count == _values!.Length)
        {
            int length = Math.Min(_capacity, Math.Max(_count * 2, 2));
            var values = new NativeArray<uint>(length);
            _values.AsSpan().CopyTo(values.AsSpan());
            _values.Dispose();
            _values = values;
        }
        int index = _count++;
        _values![index] = state;
        return index;
    }

    private void Resize(int bits)
    {
        int words = (VoxelCoordinates.ChunkVolume + (64 / bits) - 1) / (64 / bits);
        var indices = new NativeArray<ulong>(words);
        for (int i = 0; i < VoxelCoordinates.ChunkVolume; i++)
        {
            int oldShift = (i & ((64 / _bits) - 1)) * _bits;
            uint value = (uint)((_indices![i >> BitOperationsFor(_bits)] >> oldShift) & Mask(_bits));
            int newShift = (i & ((64 / bits) - 1)) * bits;
            indices[i >> BitOperationsFor(bits)] |= (ulong)value << newShift;
        }

        _indices!.Dispose();
        _indices = indices;
        _bits = bits;
    }

    private static int BitsFor(int count) => count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : count <= 256 ? 8 : 16;

    [HotPath]
    private static int BitOperationsFor(int bits) => bits switch { 1 => 6, 2 => 5, 4 => 4, 8 => 3, 16 => 2, _ => throw new InvalidOperationException() };

    [HotPath]
    private static ulong Mask(int bits) => (1UL << bits) - 1;

    [HotPath]
    private static void RequireCell(int cell)
    {
        if ((uint)cell >= VoxelCoordinates.ChunkVolume) throw new ArgumentOutOfRangeException(nameof(cell));
    }
}
