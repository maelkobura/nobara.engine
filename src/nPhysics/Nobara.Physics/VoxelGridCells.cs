// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The cells of every chunk of a grid in memory, as nPhysics gives them to a
/// backend for a shape of kind <see cref="ShapeKind.Voxels"/>: the
/// <see cref="VoxelChunkCells"/> of each chunk, found by its coordinate or by a
/// slot, and the box of the cells that hold a block. A backend reads it from
/// the threads of its solver while a world steps.
/// </summary>
/// <remarks>
/// <para>
/// Positions are in the frame of the grid: cell (x, y, z) spans from
/// (x, y, z) to (x + 1, y + 1, z + 1). Each chunk in memory that holds a block
/// has a slot, from 0 to <see cref="ChunkSlots"/>, kept while the chunk stays
/// in memory: a backend can number the boxes of the grid by slot, cell, and
/// box (<see cref="ChunkStride"/>).
/// </para>
/// <para>
/// nPhysics changes it on the loop thread only, outside the step: when a chunk
/// of the grid enters or leaves memory, and when its blocks change.
/// </para>
/// </remarks>
public sealed class VoxelGridCells
{
    /// <summary>The number of box indices of one chunk: its cells times <see cref="VoxelChunkCells.MaxBoxesPerCell"/>.</summary>
    public const int ChunkStride = Chunk.Volume * VoxelChunkCells.MaxBoxesPerCell;

    /// <summary>The largest number of chunk slots, so that every box index fits in an <see langword="int"/>.</summary>
    public const int MaxChunks = int.MaxValue / ChunkStride;

    private const int Size = VoxelCoordinates.ChunkSize;

    private readonly Dictionary<ChunkCoordinate, int> _slots = [];
    private SlotEntry[] _entries = new SlotEntry[4];
    private int _high;
    private int _free = -1;

    internal VoxelGridCells(Grid grid) => Grid = grid;

    /// <summary>Gets the grid.</summary>
    public Grid Grid { get; }

    /// <summary>Gets the number of chunk slots, used or not: every slot is below it.</summary>
    public int ChunkSlots => _high;

    /// <summary>Gets the number of chunks that hold a block.</summary>
    public int ChunkCount => _slots.Count;

    /// <summary>Gets whether no cell of the grid in memory holds a block.</summary>
    public bool IsEmpty { get; private set; } = true;

    /// <summary>Gets the lowest corner of the box of the cells that hold a block, in the frame of the grid; zero when <see cref="IsEmpty"/>.</summary>
    public Vector3 Minimum { get; private set; }

    /// <summary>Gets the highest corner of the box of the cells that hold a block, in the frame of the grid; zero when <see cref="IsEmpty"/>.</summary>
    public Vector3 Maximum { get; private set; }

    /// <summary>Gets a number that changes each time the box of the cells changes, for a backend that keeps bounds computed from it.</summary>
    public int BoundsVersion { get; private set; }

    /// <summary>Gets the shapes of the grid alive, by their slot in nPhysics.</summary>
    internal List<int> Shapes { get; } = [];

    /// <summary>Gets the slot of a chunk.</summary>
    /// <param name="chunk">The coordinate of the chunk in the grid.</param>
    /// <returns>The slot, or -1 when the chunk is not in memory or holds no block.</returns>
    [HotPath]
    public int SlotOf(ChunkCoordinate chunk) => _slots.TryGetValue(chunk, out int slot) ? slot : -1;

    /// <summary>Gets the cells of the chunk in a slot.</summary>
    /// <param name="slot">The slot, from 0 to <see cref="ChunkSlots"/>.</param>
    /// <returns>The cells, or <see langword="null"/> for a slot that holds no chunk.</returns>
    [HotPath]
    public VoxelChunkCells? GetChunk(int slot) => (uint)slot < (uint)_high ? _entries[slot].Cells : null;

    /// <summary>Gets the coordinate of the chunk in a slot.</summary>
    /// <param name="slot">The slot of a chunk.</param>
    /// <returns>The coordinate of the chunk in the grid.</returns>
    [HotPath]
    public ChunkCoordinate CoordinateOf(int slot) => _entries[slot].Coordinate;

    /// <summary>Gets the corner of the chunk in a slot, in the frame of the grid.</summary>
    /// <param name="slot">The slot of a chunk.</param>
    /// <returns>The corner of the chunk.</returns>
    [HotPath]
    public Vector3 CornerOf(int slot)
    {
        ChunkCoordinate chunk = _entries[slot].Coordinate;
        return new Vector3(chunk.X * (float)Size, chunk.Y * (float)Size, chunk.Z * (float)Size);
    }

    /// <summary>Puts the cells of a chunk in its slot, or takes the chunk out; the caller counts the users of the cells.</summary>
    /// <param name="coordinate">The coordinate of the chunk.</param>
    /// <param name="cells">The cells of the chunk, or <see langword="null"/> to take it out.</param>
    /// <param name="low">The lowest corner of the cells that hold a block, in the frame of the chunk.</param>
    /// <param name="high">The highest corner of the cells that hold a block, in the frame of the chunk.</param>
    /// <param name="released">The cells the slot held before, when they are not <paramref name="cells"/>.</param>
    /// <returns><see langword="true"/> when the box of the cells changed.</returns>
    internal bool Set(ChunkCoordinate coordinate, VoxelChunkCells? cells, Vector3 low, Vector3 high, out VoxelChunkCells? released)
    {
        bool held = _slots.TryGetValue(coordinate, out int slot);
        released = held && !ReferenceEquals(_entries[slot].Cells, cells) ? _entries[slot].Cells : null;
        if (cells is null)
        {
            if (!held)
            {
                return false;
            }

            _slots.Remove(coordinate);
            _entries[slot] = new SlotEntry { NextFree = _free };
            _free = slot;
            return ComputeBounds();
        }

        if (!held)
        {
            if (_slots.Count >= MaxChunks)
            {
                throw new InvalidOperationException($"The grid {Grid.Identifier} holds more than {MaxChunks} chunks with blocks: its voxels cannot be a shape.");
            }

            slot = Allocate();
            _slots.Add(coordinate, slot);
        }

        Vector3 corner = new(coordinate.X * (float)Size, coordinate.Y * (float)Size, coordinate.Z * (float)Size);
        _entries[slot] = new SlotEntry { Cells = cells, Coordinate = coordinate, Minimum = corner + low, Maximum = corner + high, NextFree = -1 };
        return ComputeBounds();
    }

    /// <summary>Forgets every chunk at the end of a world session, when nPhysics forgets every cell at once.</summary>
    internal void Clear()
    {
        _slots.Clear();
        Array.Clear(_entries);
        _high = 0;
        _free = -1;
        ComputeBounds();
    }

    /// <summary>Gets the cells of every chunk, to let go of them.</summary>
    internal IEnumerable<(ChunkCoordinate Chunk, VoxelChunkCells Cells)> AllCells()
    {
        for (int i = 0; i < _high; i++)
        {
            if (_entries[i].Cells is { } cells)
            {
                yield return (_entries[i].Coordinate, cells);
            }
        }
    }

    private int Allocate()
    {
        if (_free >= 0)
        {
            int slot = _free;
            _free = _entries[slot].NextFree;
            return slot;
        }

        if (_high == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }

        return _high++;
    }

    /// <summary>Joins the boxes of the chunks.</summary>
    private bool ComputeBounds()
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        for (int i = 0; i < _high; i++)
        {
            if (_entries[i].Cells is not null)
            {
                low = Vector3.Min(low, _entries[i].Minimum);
                high = Vector3.Max(high, _entries[i].Maximum);
            }
        }

        bool empty = _slots.Count == 0;
        if (empty)
        {
            low = high = Vector3.Zero;
        }

        if (empty == IsEmpty && low == Minimum && high == Maximum)
        {
            return false;
        }

        IsEmpty = empty;
        Minimum = low;
        Maximum = high;
        BoundsVersion++;
        return true;
    }

    /// <summary>Gets the box of the cells of a chunk that hold a block, in the frame of the chunk.</summary>
    /// <returns><see langword="false"/> when no cell holds a block.</returns>
    internal static bool BoxOf(Chunk chunk, out Vector3 low, out Vector3 high)
    {
        Palette palette = chunk.Palette;
        if (palette.IsUniform)
        {
            bool full = palette.GetEntry(0) != 0;
            low = Vector3.Zero;
            high = full ? new Vector3(Size) : Vector3.Zero;
            return full;
        }

        int minX = Size;
        int minY = Size;
        int minZ = Size;
        int maxX = -1;
        int maxY = -1;
        int maxZ = -1;
        for (int i = 0; i < Chunk.Volume; i++)
        {
            if (palette.Get(i) == 0)
            {
                continue;
            }

            int x = i % Size;
            int y = i / (Size * Size);
            int z = i / Size % Size;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        low = new Vector3(minX, minY, minZ);
        high = new Vector3(maxX + 1, maxY + 1, maxZ + 1);
        return maxX >= 0;
    }

    private struct SlotEntry
    {
        public VoxelChunkCells? Cells;
        public ChunkCoordinate Coordinate;
        public Vector3 Minimum;
        public Vector3 Maximum;
        public int NextFree;
    }
}
