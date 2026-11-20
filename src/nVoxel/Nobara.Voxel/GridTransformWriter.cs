// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Voxel;

/// <summary>
/// The only way to move a grid after its creation: it writes the transform of
/// a grid and updates the grid in the spatial index. nVoxel gives it, through
/// <see cref="IVoxelWorlds.ClaimGridTransforms"/>, to the one module that
/// reserves <see cref="Reservation"/>, the physics module in a game.
/// </summary>
/// <remarks>Call it on the loop thread of the context.</remarks>
public sealed class GridTransformWriter
{
    /// <summary>The exclusive resource a module reserves in its <c>module.nobara</c> to write grid transforms: <c>voxel.grid.transform</c>.</summary>
    public const string Reservation = "voxel.grid.transform";

    internal GridTransformWriter(string moduleId) => ModuleId = moduleId;

    /// <summary>Gets the identifier of the module that holds the writer.</summary>
    public string ModuleId { get; }

    /// <summary>Writes the transform of a grid and updates its entry in the spatial index.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="transform">The new transform.</param>
    /// <exception cref="ObjectDisposedException">The grid is destroyed.</exception>
    [HotPath]
    public void Set(Grid grid, in WorldTransform transform)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.SetTransform(transform);
    }
}
