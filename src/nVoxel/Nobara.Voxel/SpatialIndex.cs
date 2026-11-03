// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Voxel;

/// <summary>An axis-aligned box represented relative to a precise world origin.</summary>
public readonly struct WorldBox
{
    /// <summary>Initializes a world box.</summary>
    public WorldBox(WorldPosition origin, Vector3 minimum, Vector3 maximum)
    {
        if (minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z) throw new ArgumentException("The minimum must not be after the maximum.");
        Origin = origin;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the precise origin used by the relative vectors.</summary>
    public WorldPosition Origin { get; }

    /// <summary>Gets the relative minimum corner.</summary>
    public Vector3 Minimum { get; }

    /// <summary>Gets the relative maximum corner.</summary>
    public Vector3 Maximum { get; }

    /// <summary>Creates a box centered on a world position.</summary>
    public static WorldBox Around(WorldPosition center, Vector3 halfExtents) => new(center, -halfExtents, halfExtents);

    /// <summary>Tells whether two boxes intersect.</summary>
    [HotPath]
    public bool Intersects(in WorldBox other)
    {
        Vector3 otherMinimum = other.Origin.RelativeTo(Origin) + other.Minimum;
        Vector3 otherMaximum = other.Origin.RelativeTo(Origin) + other.Maximum;
        return Minimum.X <= otherMaximum.X && Maximum.X >= otherMinimum.X && Minimum.Y <= otherMaximum.Y && Maximum.Y >= otherMinimum.Y && Minimum.Z <= otherMaximum.Z && Maximum.Z >= otherMinimum.Z;
    }
}

/// <summary>The world-space bounds currently registered for a grid.</summary>
public readonly struct GridWorldBounds
{
    internal GridWorldBounds(WorldBox box) => Box = box;

    /// <summary>Gets the axis-aligned world box.</summary>
    public WorldBox Box { get; }
}

/// <summary>Finds grids whose world-space bounds overlap a query box.</summary>
public sealed class GridSpatialIndex
{
    private readonly List<Grid> _grids = [];
    private readonly Dictionary<Grid, GridWorldBounds?> _bounds = [];

    /// <summary>Gets the number of indexed grids.</summary>
    public int Count => _grids.Count;

    /// <summary>Returns grids that overlap a box, in insertion order.</summary>
    public IReadOnlyList<Grid> Query(in WorldBox box)
    {
        var result = new List<Grid>();
        foreach (Grid grid in _grids)
        {
            GridWorldBounds? bounds = _bounds[grid];
            if (bounds is null || bounds.Value.Box.Intersects(box)) result.Add(grid);
        }

        return result;
    }

    /// <summary>Returns grids around a center and half extents.</summary>
    public IReadOnlyList<Grid> Query(WorldPosition center, Vector3 halfExtents) => Query(WorldBox.Around(center, halfExtents));

    internal void Insert(Grid grid)
    {
        if (!_bounds.ContainsKey(grid)) _grids.Add(grid);
        _bounds[grid] = Calculate(grid);
    }

    internal void Update(Grid grid)
    {
        if (!_bounds.ContainsKey(grid)) _grids.Add(grid);
        _bounds[grid] = Calculate(grid);
    }

    internal void Remove(Grid grid)
    {
        _bounds.Remove(grid);
        _grids.Remove(grid);
    }

    private static GridWorldBounds? Calculate(Grid grid)
    {
        if (grid.Bounds is not { } bounds) return null;
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        WorldPosition origin = grid.Transform.Position;
        for (int x = 0; x <= 1; x++)
        {
            for (int y = 0; y <= 1; y++)
            {
                for (int z = 0; z <= 1; z++)
                {
                    int localX = x == 0 ? bounds.Minimum.X : checked(bounds.Maximum.X + 1);
                    int localY = y == 0 ? bounds.Minimum.Y : checked(bounds.Maximum.Y + 1);
                    int localZ = z == 0 ? bounds.Minimum.Z : checked(bounds.Maximum.Z + 1);
                    Vector3 relative = grid.Transform.TransformPoint(new Vector3(localX, localY, localZ)).RelativeTo(origin);
                    minimum = Vector3.Min(minimum, relative);
                    maximum = Vector3.Max(maximum, relative);
                }
            }
        }

        return new GridWorldBounds(new WorldBox(origin, minimum, maximum));
    }
}
