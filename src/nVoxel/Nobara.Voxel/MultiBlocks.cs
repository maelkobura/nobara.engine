// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Core;

namespace Nobara.Voxel;

/// <summary>Information about one cell of a multi-block.</summary>
public readonly struct MultiBlockCell
{
    [HotPath]
    internal MultiBlockCell(BlockType type, BlockCoordinate origin, BlockCell offset, int part, int orientation)
    {
        Type = type;
        Origin = origin;
        Offset = offset;
        Part = part;
        Orientation = orientation;
    }

    /// <summary>Gets the block type.</summary>
    public BlockType Type { get; }

    /// <summary>Gets the calculated origin in grid-local coordinates.</summary>
    public BlockCoordinate Origin { get; }

    /// <summary>Gets the cell offset from the origin after orientation.</summary>
    public BlockCell Offset { get; }

    /// <summary>Gets the part index stored in the state.</summary>
    public int Part { get; }

    /// <summary>Gets the orientation value stored in the state.</summary>
    public int Orientation { get; }
}

/// <summary>Provides rotated footprints and calculated multi-block origins.</summary>
public interface IMultiBlockRegistry
{
    /// <summary>Gets whether the registry has been initialized.</summary>
    bool IsFrozen { get; }

    /// <summary>Gets whether a state belongs to a multi-block type.</summary>
    /// <param name="state">The block state.</param>
    /// <returns><see langword="true"/> when the type occupies multiple cells.</returns>
    bool IsMultiBlock(uint state);

    /// <summary>Gets whether a state is the origin part of a multi-block.</summary>
    /// <param name="state">The block state.</param>
    /// <returns><see langword="true"/> for part zero.</returns>
    bool IsOrigin(uint state);

    /// <summary>Gets the rotated footprint for a state.</summary>
    /// <param name="state">The block state.</param>
    /// <returns>A cached list of offsets, origin first.</returns>
    IReadOnlyList<BlockCell> GetFootprint(uint state);

    /// <summary>Gets the rotated offset represented by the part of a state.</summary>
    /// <param name="state">The block state.</param>
    /// <returns>The offset from the origin.</returns>
    BlockCell GetOffset(uint state);

    /// <summary>Calculates the origin of a cell carrying a state.</summary>
    /// <param name="state">The block state.</param>
    /// <param name="cell">The cell coordinate carrying the state.</param>
    /// <returns>The origin coordinate.</returns>
    BlockCoordinate GetOrigin(uint state, BlockCoordinate cell);

    /// <summary>Gets the complete identity of a cell carrying a state.</summary>
    /// <param name="state">The block state.</param>
    /// <param name="cell">The cell coordinate carrying the state.</param>
    /// <returns>The calculated multi-block information.</returns>
    MultiBlockCell GetCell(uint state, BlockCoordinate cell);
}

internal sealed class MultiBlockRegistry : IMultiBlockRegistry
{
    private IBlockRegistry? _blocks;
    private Layout[] _layouts = [];
    private bool _frozen;

    public void Initialize(IBlockRegistry blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        _blocks = blocks;
        _layouts = new Layout[blocks.Types.Count];
        Freeze();
    }

    public bool IsFrozen => _frozen;

    public bool IsMultiBlock(uint state) => LayoutFor(state).IsMultiBlock;

    public bool IsOrigin(uint state) => PartOf(state) == 0;

    public IReadOnlyList<BlockCell> GetFootprint(uint state) => LayoutFor(state).Footprint[OrientationOf(state)];

    public BlockCell GetOffset(uint state)
    {
        Layout layout = LayoutFor(state);
        return layout.Footprint[OrientationOf(state)][PartOf(state)];
    }

    public BlockCoordinate GetOrigin(uint state, BlockCoordinate cell)
    {
        BlockCell offset = GetOffset(state);
        return new BlockCoordinate(checked(cell.X - offset.X), checked(cell.Y - offset.Y), checked(cell.Z - offset.Z));
    }

    public MultiBlockCell GetCell(uint state, BlockCoordinate cell)
    {
        BlockType type = _blocks!.TypeOf(state);
        return new MultiBlockCell(type, GetOrigin(state, cell), GetOffset(state), PartOf(state), OrientationOf(state));
    }

    internal void Freeze()
    {
        if (_frozen) throw new InvalidOperationException("The multi-block registry is already frozen.");
        for (int i = 0; i < _blocks!.Types.Count; i++)
        {
            BlockType type = _blocks.Types[i];
            int orientations = type.TryGetProperty("orientation", out StateProperty? orientation) ? orientation.Count : 1;
            var footprints = new IReadOnlyList<BlockCell>[orientations];
            for (int value = 0; value < orientations; value++)
            {
                var cells = new BlockCell[type.Footprint.Count];
                for (int part = 0; part < cells.Length; part++) cells[part] = Rotate(type.Footprint[part], type.Orientations, value);
                footprints[value] = Array.AsReadOnly(cells);
            }

            _layouts[i] = new Layout(type, footprints);
        }

        _frozen = true;
    }

    private Layout LayoutFor(uint state)
    {
        if (!_frozen) throw new InvalidOperationException("Multi-blocks are not initialized.");
        return _layouts[_blocks!.TypeOf(state).Index];
    }

    private int PartOf(uint state)
    {
        BlockType type = _blocks!.TypeOf(state);
        return type.TryGetProperty("part", out StateProperty? part) ? _blocks!.GetValue(state, part) : 0;
    }

    private int OrientationOf(uint state)
    {
        BlockType type = _blocks!.TypeOf(state);
        return type.TryGetProperty("orientation", out StateProperty? orientation) ? _blocks!.GetValue(state, orientation) : 0;
    }

    private static BlockCell Rotate(BlockCell cell, BlockOrientations set, int orientation)
    {
        Matrix3 matrix = OrientationMatrices.Get(set, orientation);
        var transformed = matrix.ApplyDirection(new System.Numerics.Vector3(cell.X, cell.Y, cell.Z));
        return new BlockCell((int)transformed.X, (int)transformed.Y, (int)transformed.Z);
    }

    private sealed class Layout(BlockType type, IReadOnlyList<BlockCell>[] footprint)
    {
        public bool IsMultiBlock { get; } = type.Footprint.Count > 1;
        public IReadOnlyList<BlockCell>[] Footprint { get; } = footprint;
    }
}
