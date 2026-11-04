// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Game;

namespace Nobara.Voxel;

/// <summary>The dimensions, terrain grids, grid storage, and spatial index of one context.</summary>
public interface IVoxelWorlds
{
    /// <summary>Gets the declared dimensions in deterministic identifier order.</summary>
    IReadOnlyList<DimensionId> Dimensions { get; }

    /// <summary>Gets the grids currently held by the context.</summary>
    IReadOnlyList<Grid> Grids { get; }

    /// <summary>Gets the spatial index of finite and infinite grids.</summary>
    GridSpatialIndex SpatialIndex { get; }

    /// <summary>Gets the ECS component that points from a grid entity to its storage.</summary>
    Nobara.Entity.ComponentType<GridComponent> GridComponent { get; }

    /// <summary>Declares a dimension owned by nVoxel and its terrain policy.</summary>
    /// <param name="dimension">The identifier, other than the already declared default.</param>
    /// <param name="clock">The initial game clock.</param>
    /// <param name="terrainBounds">Optional bounds for the terrain grid.</param>
    /// <exception cref="ArgumentException">The dimension was declared already.</exception>
    /// <exception cref="InvalidOperationException">A session is active or the game time is not available yet.</exception>
    void AddDimension(DimensionId dimension, ulong clock = 0, GridBounds? terrainBounds = null);

    /// <summary>Removes a declared dimension and its game clock.</summary>
    /// <exception cref="ArgumentException">The dimension is absent, is default, or still has grids.</exception>
    void RemoveDimension(DimensionId dimension);

    /// <summary>Gets a grid by its dimension and handle.</summary>
    bool TryGet(DimensionId dimension, GridHandle handle, out Grid? grid);

    /// <summary>Creates a grid entity and its separate chunk storage.</summary>
    /// <param name="owner">The module that owns the grid and must destroy it.</param>
    /// <param name="dimension">The dimension that contains it.</param>
    /// <param name="options">The transform, bounds, and terrain flag.</param>
    /// <returns>The grid.</returns>
    /// <exception cref="InvalidOperationException">No world session is active, or a client already has a grid world for another dimension.</exception>
    Grid CreateGrid(ModuleContext owner, DimensionId dimension, GridOptions? options = null);

    /// <summary>Destroys a grid and its ECS entity.</summary>
    /// <param name="owner">The module that created it.</param>
    /// <param name="grid">The grid to destroy.</param>
    /// <exception cref="ArgumentException">The grid belongs to another module or context.</exception>
    void Destroy(ModuleContext owner, Grid grid);

    /// <summary>Splits selected resident blocks into a new grid.</summary>
    /// <param name="owner">The module that owns the source grid.</param>
    /// <param name="source">The source grid.</param>
    /// <param name="selection">The local cells to move; incomplete multi-blocks stay in the source.</param>
    /// <returns>The new grid. Its changes are applied on the next World tick.</returns>
    Grid Split(ModuleContext owner, Grid source, IReadOnlyCollection<BlockCoordinate> selection);

    /// <summary>Merges one grid into another with a vertical-axis quarter-turn and integer offset.</summary>
    /// <param name="owner">The module that owns both grids.</param>
    /// <param name="target">The grid that receives the blocks.</param>
    /// <param name="absorbed">The grid that is destroyed after its removal batch.</param>
    /// <param name="quarterTurns">The clockwise relative quarter-turn, from 0 to 3.</param>
    /// <param name="offset">The target-grid offset applied after rotation.</param>
    void Merge(ModuleContext owner, Grid target, Grid absorbed, int quarterTurns, BlockCoordinate offset);

    /// <summary>Gets the terrain grid of a dimension, when its session has started.</summary>
    bool TryGetTerrain(DimensionId dimension, out Grid? terrain);
}
