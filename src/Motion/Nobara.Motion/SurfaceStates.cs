// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>
/// What the surface of a grid needs to know of each block state, read once
/// when Motion starts: whether it collides, and whether its collision boxes
/// fill its whole cell, so that it hides the faces of its neighbors.
/// </summary>
/// <remarks>
/// A state whose shape depends on its instance collides, and never fills
/// its cell: its boxes change with its block data.
/// </remarks>
internal sealed class SurfaceStates
{
    private const byte Collides = 1;
    private const byte Fills = 2;
    private const float Tolerance = 1e-4f;

    private readonly byte[] _flags;

    private SurfaceStates(byte[] flags) => _flags = flags;

    /// <summary>Reads the collision of every state.</summary>
    public static SurfaceStates Build(IBlockRegistry blocks, IShapeRegistry shapes)
    {
        byte[] flags = new byte[blocks.StateCount];
        for (uint state = 1; state < (uint)flags.Length; state++)
        {
            BlockType type = blocks.TypeOf(state);
            if (type.IsAir)
            {
                continue;
            }

            if (shapes.TryGet(type.Shape, out IBlockShape? shape) && shape!.IsInstanceDependent)
            {
                flags[state] = Collides;
                continue;
            }

            CollisionShape collision = shapes.Get(state, BlockDataView.Empty).Collision;
            byte flag = collision.Count > 0 ? Collides : (byte)0;
            for (int i = 0; i < collision.Count; i++)
            {
                CollisionBox box = collision[i];
                if (box.Minimum.X <= Tolerance && box.Minimum.Y <= Tolerance && box.Minimum.Z <= Tolerance
                    && box.Maximum.X >= 1 - Tolerance && box.Maximum.Y >= 1 - Tolerance && box.Maximum.Z >= 1 - Tolerance)
                {
                    flag |= Fills;
                }
            }

            flags[state] = flag;
        }

        return new SurfaceStates(flags);
    }

    /// <summary>Gets whether a state collides.</summary>
    [HotPath]
    public bool CollidesAt(uint state) => state < (uint)_flags.Length && (_flags[state] & Collides) != 0;

    /// <summary>Gets whether a state fills its cell: a neighbor never touches the voxels through it.</summary>
    [HotPath]
    public bool FillsAt(uint state) => state < (uint)_flags.Length && (_flags[state] & Fills) != 0;

    /// <summary>
    /// Tells whether a cell of a grid is a surface cell: it collides, and at
    /// least one of its six neighbors does not fill its cell. A neighbor in a
    /// chunk that is not in memory is open.
    /// </summary>
    /// <param name="grid">The grid.</param>
    /// <param name="cell">The cell, in the coordinates of the grid.</param>
    /// <param name="chunk">The chunk of the cell, or <see langword="null"/> when it is not in memory.</param>
    [HotPath]
    public bool IsSurface(Grid grid, BlockCoordinate cell, Chunk? chunk)
    {
        if (chunk is null || !CollidesAt(chunk.Palette.Get(VoxelCoordinates.ToIndex(cell))))
        {
            return false;
        }

        return !FillsAt(StateAt(grid, chunk, new BlockCoordinate(cell.X + 1, cell.Y, cell.Z)))
            || !FillsAt(StateAt(grid, chunk, new BlockCoordinate(cell.X - 1, cell.Y, cell.Z)))
            || !FillsAt(StateAt(grid, chunk, new BlockCoordinate(cell.X, cell.Y + 1, cell.Z)))
            || !FillsAt(StateAt(grid, chunk, new BlockCoordinate(cell.X, cell.Y - 1, cell.Z)))
            || !FillsAt(StateAt(grid, chunk, new BlockCoordinate(cell.X, cell.Y, cell.Z + 1)))
            || !FillsAt(StateAt(grid, chunk, new BlockCoordinate(cell.X, cell.Y, cell.Z - 1)));
    }

    /// <summary>Gets the state of a cell, from the chunk given when the cell is in it; 0 for a chunk that is not in memory.</summary>
    [HotPath]
    private static uint StateAt(Grid grid, Chunk near, BlockCoordinate cell)
    {
        ChunkCoordinate coordinate = VoxelCoordinates.ToChunk(cell);
        if (coordinate == near.Coordinate)
        {
            return near.Palette.Get(VoxelCoordinates.ToIndex(cell));
        }

        return grid.Storage.TryGetChunk(coordinate, out Chunk? chunk) && chunk is not null ? chunk.Palette.Get(VoxelCoordinates.ToIndex(cell)) : 0;
    }
}
