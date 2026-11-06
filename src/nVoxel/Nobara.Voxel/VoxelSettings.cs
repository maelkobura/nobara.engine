// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Voxel;

/// <summary>The configuration keys of nVoxel.</summary>
public static class VoxelSettings
{
    /// <summary>The horizontal radius of the view area, in chunks; a <c>user</c> key.</summary>
    public const string ViewRadius = "voxel.viewRadius";

    /// <summary>The vertical half-height of the view area, in chunks; a <c>user</c> key.</summary>
    public const string ViewHeight = "voxel.viewHeight";

    /// <summary>The horizontal radius of the simulation area, in chunks; a <c>distribution</c> key.</summary>
    public const string SimulationRadius = "voxel.simulationRadius";

    /// <summary>The vertical half-height of the simulation area, in chunks; a <c>distribution</c> key.</summary>
    public const string SimulationHeight = "voxel.simulationHeight";

    /// <summary>The number of cells drawn for random ticks in each active chunk at each tick; a <c>distribution</c> key.</summary>
    public const string RandomTicksPerChunk = "voxel.randomTicksPerChunk";

    /// <summary>The number of ticks between two block-tick passes of a chunk held at reduced frequency; a <c>distribution</c> key.</summary>
    public const string ReducedTickInterval = "voxel.reducedTickInterval";

    /// <summary>The number of ticks a chunk without a ticket stays loaded; a <c>distribution</c> key.</summary>
    public const string UnloadDelay = "voxel.unloadDelay";

    /// <summary>Whether nVoxel freezes the clock of a dimension that has no active chunk; a <c>distribution</c> key.</summary>
    public const string FreezeInactiveDimensions = "voxel.freezeInactiveDimensions";
}
