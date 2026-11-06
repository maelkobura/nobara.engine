// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Core;

namespace Nobara.Voxel;

/// <summary>
/// The height maps of one context: for each column of blocks, the highest
/// opaque block known. Resolve it with <c>context.Resolve&lt;IHeightMaps&gt;()</c>.
/// </summary>
/// <remarks>
/// A block is opaque for the height map when its <c>&lt;Opacity&gt;</c> is
/// <c>opaque</c> and its oriented shape is opaque on its top or bottom face:
/// a full cube or a bottom slab stops the sky, a fence post does not.
/// nVoxel keeps one map of 32 × 32 heights per column of chunks for every
/// grid without bounds, updated when chunks load and when batches apply; the
/// map of a column is released once none of its chunks is in memory. A
/// bounded grid computes a height on demand.
/// </remarks>
public interface IHeightMaps
{
    /// <summary>Gets the highest opaque block known in a column of blocks.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="x">The block coordinate along X, in the grid.</param>
    /// <param name="z">The block coordinate along Z, in the grid.</param>
    /// <param name="height">The coordinate along Y of the highest opaque block, or <see cref="int.MinValue"/>.</param>
    /// <returns><see langword="true"/> when an opaque block is known in the column: in a loaded chunk, or in a bounded grid.</returns>
    bool TryGetHeight(Grid grid, int x, int z, out int height);
}

/// <summary>The height maps of the grids without bounds, and the opacity of each state for them.</summary>
internal sealed class HeightMaps : IHeightMaps, IVoxelChangeObserver
{
    private const int Unknown = int.MinValue;

    private readonly Dictionary<Grid, Dictionary<(int X, int Z), Column>> _maps = [];
    private IShapeRegistry _shapes = null!;
    private IBlockRegistry _blocks = null!;

    // Per state: 0 not opaque, 1 opaque, 2 depends on the block data.
    private byte[] _opaque = [];

    public void Start(IShapeRegistry shapes, IBlockRegistry blocks)
    {
        _shapes = shapes;
        _blocks = blocks;
        _opaque = new byte[blocks.StateCount];
        for (uint state = 0; state < (uint)_opaque.Length; state++)
        {
            BlockType type = blocks.TypeOf(state);
            if (type.IsAir || blocks.GetOpacity(state) != BlockOpacity.Opaque) continue;
            if (shapes.TryGet(type.Shape, out IBlockShape? shape) && shape!.IsInstanceDependent)
            {
                _opaque[state] = 2;
                continue;
            }

            FaceMask faces = shapes.Get(state, BlockDataView.Empty).Opacity;
            _opaque[state] = faces[BlockFace.Up] || faces[BlockFace.Down] ? (byte)1 : (byte)0;
        }
    }

    public bool TryGetHeight(Grid grid, int x, int z, out int height)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.Bounds is { } bounds)
        {
            height = Unknown;
            if (x < bounds.Minimum.X || x > bounds.Maximum.X || z < bounds.Minimum.Z || z > bounds.Maximum.Z) return false;
            for (int y = bounds.Maximum.Y; y >= bounds.Minimum.Y; y--)
            {
                var cell = new BlockCoordinate(x, y, z);
                if (!grid.Storage.TryGetChunk(VoxelCoordinates.ToChunk(cell), out Chunk? chunk))
                {
                    // Skip the rest of an absent chunk: its cells are air.
                    y = (int)Math.Max((long)bounds.Minimum.Y, (long)(y >> 5) * VoxelCoordinates.ChunkSize);
                    continue;
                }

                if (IsOpaque(chunk!, VoxelCoordinates.ToIndex(cell)))
                {
                    height = y;
                    return true;
                }
            }

            return false;
        }

        height = Unknown;
        if (!_maps.TryGetValue(grid, out Dictionary<(int X, int Z), Column>? columns)) return false;
        if (!columns.TryGetValue((x >> 5, z >> 5), out Column? column)) return false;
        height = column.Heights[(x & 31) + ((z & 31) * 32)];
        return height != Unknown;
    }

    internal void OnLoaded(Grid grid, Chunk chunk)
    {
        if (grid.Bounds is not null) return;
        Column column = GetColumn(grid, chunk.Coordinate);
        column.Resident++;
        int baseY = chunk.Coordinate.Y * VoxelCoordinates.ChunkSize;
        for (int z = 0; z < 32; z++)
        {
            for (int x = 0; x < 32; x++)
            {
                int at = x + (z * 32);
                if (column.Heights[at] >= baseY + 31) continue;
                for (int y = 31; y >= 0; y--)
                {
                    if (baseY + y <= column.Heights[at]) break;
                    if (IsOpaque(chunk, x + (32 * (z + (32 * y)))))
                    {
                        column.Heights[at] = baseY + y;
                        break;
                    }
                }
            }
        }
    }

    internal void OnUnloading(Grid grid, Chunk chunk)
    {
        if (grid.Bounds is not null || !_maps.TryGetValue(grid, out Dictionary<(int X, int Z), Column>? columns)) return;
        (int, int) key = (chunk.Coordinate.X, chunk.Coordinate.Z);
        if (columns.TryGetValue(key, out Column? column) && --column.Resident <= 0) columns.Remove(key);
    }

    internal void OnGridDestroying(Grid grid) => _maps.Remove(grid);

    public void OnApplied(Grid grid, Chunk chunk, List<VoxelBlockChange> changes)
    {
        if (grid.Bounds is not null || changes.Count == 0) return;
        Column column = GetColumn(grid, chunk.Coordinate);
        int baseY = chunk.Coordinate.Y * VoxelCoordinates.ChunkSize;
        foreach (VoxelBlockChange change in changes)
        {
            int x = change.LocalIndex & 31;
            int z = (change.LocalIndex >> 5) & 31;
            int y = baseY + (change.LocalIndex >> 10);
            int at = x + (z * 32);
            if (IsOpaque(chunk, change.LocalIndex))
            {
                if (y > column.Heights[at]) column.Heights[at] = y;
            }
            else if (y == column.Heights[at])
            {
                column.Heights[at] = Scan(grid, chunk.Coordinate, x, z, y - 1);
            }
        }
    }

    private bool IsOpaque(Chunk chunk, int localIndex)
    {
        uint state = chunk.GetState(localIndex);
        byte opaque = state < (uint)_opaque.Length ? _opaque[state] : (byte)0;
        if (opaque != 2) return opaque == 1;
        FaceMask faces = _shapes.Get(state, new BlockDataView(chunk, localIndex)).Opacity;
        return faces[BlockFace.Up] || faces[BlockFace.Down];
    }

    // The highest opaque block at or below a height, through the chunks of the column in memory.
    private int Scan(Grid grid, ChunkCoordinate from, int x, int z, int top)
    {
        int chunkY = top >> 5;
        int lowest = LowestResident(grid, from);
        for (; chunkY >= lowest; chunkY--)
        {
            if (!grid.Storage.TryGetChunk(new ChunkCoordinate(from.X, chunkY, from.Z), out Chunk? chunk)) continue;
            int start = Math.Min(31, top - (chunkY * VoxelCoordinates.ChunkSize));
            for (int y = start; y >= 0; y--)
            {
                if (IsOpaque(chunk!, x + (32 * (z + (32 * y))))) return (chunkY * VoxelCoordinates.ChunkSize) + y;
            }
        }

        return Unknown;
    }

    private static int LowestResident(Grid grid, ChunkCoordinate column)
    {
        int lowest = int.MaxValue;
        foreach (KeyValuePair<ChunkCoordinate, Chunk> pair in grid.Storage.ResidentChunks)
        {
            if (pair.Key.X == column.X && pair.Key.Z == column.Z && pair.Key.Y < lowest) lowest = pair.Key.Y;
        }

        return lowest;
    }

    private Column GetColumn(Grid grid, ChunkCoordinate chunk)
    {
        if (!_maps.TryGetValue(grid, out Dictionary<(int X, int Z), Column>? columns))
        {
            columns = [];
            _maps.Add(grid, columns);
        }

        if (!columns.TryGetValue((chunk.X, chunk.Z), out Column? column))
        {
            column = new Column();
            columns.Add((chunk.X, chunk.Z), column);
        }

        return column;
    }

    private sealed class Column
    {
        public Column() => Array.Fill(Heights, Unknown);

        public int[] Heights { get; } = new int[32 * 32];

        public int Resident;
    }
}
