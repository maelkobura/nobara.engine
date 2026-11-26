// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The shapes of kind <see cref="ShapeKind.Voxels"/>: the voxels of a grid, for
/// the body of that grid. nPhysics keeps one <see cref="VoxelGridCells"/> per
/// grid that has such a shape, follows its chunks as they enter and leave
/// memory and as their blocks change, and keeps the radius of each shape of
/// the grid to the box of its cells.
/// </summary>
internal sealed partial class PhysicsSimulation
{
    private readonly Dictionary<Grid, VoxelGridCells> _gridCells = [];
    private readonly List<Chunk> _residentChunks = [];
    private readonly List<(Grid Grid, ChunkCoordinate Chunk)> _gridFollow = [];

    public PhysicsShape CreateVoxels(Grid grid, Vector3 origin)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.IsDestroyed)
        {
            throw new ArgumentException($"The grid {grid.Identifier} is destroyed.", nameof(grid));
        }

        if (!IsFinite(origin))
        {
            throw new ArgumentException($"The origin {origin} is not finite.", nameof(origin));
        }

        if (!_started)
        {
            throw new InvalidOperationException("The voxels of a grid become a shape once nPhysics has started.");
        }

        VoxelGridCells cells = GridCellsOf(grid);
        PhysicsShape shape = AddShape(new ShapeDescription(ShapeKind.Voxels) { Grid = grid, Origin = origin, Cells = cells });
        cells.Shapes.Add(shape.Index);
        return shape;
    }

    /// <summary>Gets the cells of a grid, reading every chunk of the grid in memory the first time.</summary>
    private VoxelGridCells GridCellsOf(Grid grid)
    {
        if (_gridCells.TryGetValue(grid, out VoxelGridCells? cells))
        {
            return cells;
        }

        cells = new VoxelGridCells(grid);
        _gridCells.Add(grid, cells);
        _residentChunks.Clear();
        grid.Storage.GetResidentChunks(_residentChunks);
        foreach (Chunk chunk in _residentChunks)
        {
            FollowGridChunk(cells, chunk);
        }

        _residentChunks.Clear();
        return cells;
    }

    /// <summary>Lets go of the cells of a grid once its last shape is destroyed.</summary>
    private void ForgetVoxelShape(ShapeDescription description, int index)
    {
        VoxelGridCells cells = description.Cells!;
        cells.Shapes.Remove(index);
        if (cells.Shapes.Count > 0)
        {
            return;
        }

        ClearGridCells(cells);
        _gridCells.Remove(cells.Grid);
    }

    /// <summary>Puts a chunk of a grid in its cells, or reads its box again; a chunk without blocks is taken out.</summary>
    private void FollowGridChunk(VoxelGridCells grid, Chunk chunk)
    {
        ChunkCoordinate coordinate = chunk.Coordinate;
        if (!VoxelGridCells.BoxOf(chunk, out Vector3 low, out Vector3 high))
        {
            DropGridChunk(grid, coordinate);
            return;
        }

        int slot = grid.SlotOf(coordinate);
        VoxelChunkCells? held = slot >= 0 ? grid.GetChunk(slot) : null;
        var key = new VoxelChunkKey(grid.Grid, coordinate);
        VoxelChunkCells cells = held is not null && ReferenceEquals(held.Chunk, chunk) ? held : CellsOf(key, grid.Grid, chunk);
        if (!ReferenceEquals(held, cells))
        {
            cells.Users++;
        }

        bool changed = grid.Set(coordinate, cells, low, high, out VoxelChunkCells? released);
        if (released is not null)
        {
            ReleaseCells(key, released);
        }

        if (changed)
        {
            RefreshVoxelRadii(grid);
        }
    }

    /// <summary>Takes a chunk out of the cells of a grid.</summary>
    private void DropGridChunk(VoxelGridCells grid, ChunkCoordinate coordinate)
    {
        bool changed = grid.Set(coordinate, null, default, default, out VoxelChunkCells? released);
        if (released is not null)
        {
            ReleaseCells(new VoxelChunkKey(grid.Grid, coordinate), released);
        }

        if (changed)
        {
            RefreshVoxelRadii(grid);
        }
    }

    /// <summary>Takes every chunk out of the cells of a grid.</summary>
    private void ClearGridCells(VoxelGridCells grid)
    {
        foreach ((ChunkCoordinate chunk, VoxelChunkCells _) in grid.AllCells().ToArray())
        {
            DropGridChunk(grid, chunk);
        }
    }

    /// <summary>Empties the cells of the grids that were destroyed: their storage is gone.</summary>
    private void ForgetDestroyedGridCells()
    {
        foreach (VoxelGridCells cells in _gridCells.Values)
        {
            if (cells.Grid.IsDestroyed && cells.ChunkCount > 0)
            {
                ClearGridCells(cells);
            }
        }
    }

    /// <summary>
    /// Follows a chunk that enters or leaves memory, for the grids that have a
    /// shape of their voxels: a chunk that leaves is taken out at once, before
    /// it goes; one that enters is looked at before the next step.
    /// </summary>
    private void FollowGridResidency(in ChunkResidencyChange change)
    {
        if (!_gridCells.TryGetValue(change.Grid, out VoxelGridCells? cells))
        {
            return;
        }

        if (change.Kind == ChunkResidencyKind.Unloading)
        {
            DropGridChunk(cells, change.Chunk);
        }
        else
        {
            _gridFollow.Add((change.Grid, change.Chunk));
        }
    }

    /// <summary>
    /// Follows a batch of changed blocks, for the grids that have a shape of
    /// their voxels, before the next step: the notification comes from a tick
    /// system of nVoxel, which does not declare the transform of the grid that
    /// new cells read.
    /// </summary>
    [HotPath]
    private void FollowGridBlocks(Grid grid, ChunkCoordinate coordinate)
    {
        if (_gridCells.ContainsKey(grid))
        {
            _gridFollow.Add((grid, coordinate));
        }
    }

    /// <summary>Reads the chunks of grids that changed since the last step, before the bodies are placed.</summary>
    private void FollowPendingGridChunks()
    {
        for (int i = 0; i < _gridFollow.Count; i++)
        {
            (Grid grid, ChunkCoordinate coordinate) = _gridFollow[i];
            if (grid.IsDestroyed || !_gridCells.TryGetValue(grid, out VoxelGridCells? cells))
            {
                continue;
            }

            if (grid.Storage.TryGetChunk(coordinate, out Chunk? chunk) && chunk is not null)
            {
                FollowGridChunk(cells, chunk);
            }
            else
            {
                DropGridChunk(cells, coordinate);
            }
        }

        _gridFollow.Clear();
    }

    /// <summary>Sets the radius of every shape of a grid to the box of its cells.</summary>
    private void RefreshVoxelRadii(VoxelGridCells cells)
    {
        foreach (int index in cells.Shapes)
        {
            ref ShapeSlot slot = ref _shapes[index];
            slot.Radius = VoxelRadius(slot.Description!);
        }
    }

    /// <summary>Gets the distance from the origin of a shape of voxels to the farthest corner of the box of its cells.</summary>
    private static float VoxelRadius(ShapeDescription description)
    {
        VoxelGridCells cells = description.Cells!;
        if (cells.IsEmpty)
        {
            return 0;
        }

        Vector3 low = Vector3.Abs(cells.Minimum - description.Origin);
        Vector3 high = Vector3.Abs(cells.Maximum - description.Origin);
        return Vector3.Max(low, high).Length();
    }

    /// <summary>
    /// Gets the inertia of a shape of voxels when its body gives none: the one
    /// of the box of its cells at a uniform density, about the origin of the
    /// shape. The module that makes the grid a body gives the real one.
    /// </summary>
    [HotPath]
    private static BodyInertia VoxelInertia(ShapeDescription description, float mass)
    {
        VoxelGridCells cells = description.Cells!;
        Vector3 size = cells.IsEmpty ? Vector3.One : cells.Maximum - cells.Minimum;
        Vector3 center = cells.IsEmpty ? Vector3.Zero : ((cells.Minimum + cells.Maximum) / 2) - description.Origin;
        float k = mass / 12f;
        float c2 = center.LengthSquared();
        return new BodyInertia(
            XX: (k * ((size.Y * size.Y) + (size.Z * size.Z))) + (mass * (c2 - (center.X * center.X))),
            YX: -mass * center.Y * center.X,
            YY: (k * ((size.X * size.X) + (size.Z * size.Z))) + (mass * (c2 - (center.Y * center.Y))),
            ZX: -mass * center.Z * center.X,
            ZY: -mass * center.Z * center.Y,
            ZZ: (k * ((size.X * size.X) + (size.Y * size.Y))) + (mass * (c2 - (center.Z * center.Z))));
    }

    /// <summary>Gets the inertia of a shape when a body gives none: none for a mesh, the box of the cells for voxels, the backend's for the others.</summary>
    [HotPath]
    private BodyInertia DefaultInertia(ShapeDescription shape, float mass) => shape.Kind switch
    {
        ShapeKind.Mesh => default,
        ShapeKind.Voxels => VoxelInertia(shape, mass),
        _ => _backend!.ComputeInertia(shape, mass),
    };

    /// <summary>Checks that a shape of voxels is given to the body of its own grid.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="grid">The grid the entity of the body is, or <see langword="null"/>.</param>
    /// <param name="parameter">The name of the parameter that gave the shape.</param>
    [HotPath]
    private static void RequireOwnGrid(ShapeDescription shape, Grid? grid, string parameter)
    {
        if (shape.Kind == ShapeKind.Voxels && !ReferenceEquals(grid, shape.Grid))
        {
            throw new ArgumentException($"The voxels of the grid {shape.Grid!.Identifier} are the shape of the body of that grid only.", parameter);
        }
    }
}
