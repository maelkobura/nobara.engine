// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Core;

namespace Nobara.Voxel;

/// <summary>Applies the quarter-turn rules of nBlocks and block data.</summary>
public static class BlockRotation
{
    /// <summary>Rotates a block state around the vertical axis.</summary>
    /// <param name="blocks">The block registry.</param>
    /// <param name="state">The state to rotate.</param>
    /// <param name="quarterTurns">The number of clockwise quarter turns; negative values rotate counterclockwise.</param>
    /// <returns>The rotated state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is not known.</exception>
    [HotPath]
    public static uint RotateState(IBlockRegistry blocks, uint state, int quarterTurns = 1)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        int turns = quarterTurns % 4;
        if (turns < 0) turns += 4;
        for (int i = 0; i < turns; i++) state = blocks.Rotate(state);
        return state;
    }

    /// <summary>Rotates one block-data value by quarter turns.</summary>
    /// <param name="type">The data type.</param>
    /// <param name="value">The value returned by that type.</param>
    /// <param name="quarterTurns">The number of clockwise quarter turns.</param>
    /// <returns>The transformed value.</returns>
    public static object RotateData(IBlockDataType type, object value, int quarterTurns = 1)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        int turns = quarterTurns % 4;
        if (turns < 0) turns += 4;
        for (int i = 0; i < turns; i++) value = type.Rotate(value);
        return value;
    }
}
