// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Voxel;

/// <summary>Whether a block could be read.</summary>
public enum BlockReadStatus
{
    /// <summary>The block was read: its chunk is in memory, or the grid keeps its chunks and an absent chunk is air.</summary>
    Loaded,

    /// <summary>The chunk of the block is not loaded: its state is unknown, not air.</summary>
    NotLoaded,

    /// <summary>The block is outside the bounds of the grid.</summary>
    OutOfBounds,
}

/// <summary>A block read in grid coordinates.</summary>
/// <param name="Status">Whether it could be read.</param>
/// <param name="State">Its state when <see cref="Status"/> is <see cref="BlockReadStatus.Loaded"/>, otherwise 0.</param>
[HotPath]
public readonly record struct BlockRead(BlockReadStatus Status, uint State)
{
    /// <summary>Gets whether the block was read.</summary>
    public bool IsLoaded => Status == BlockReadStatus.Loaded;
}

/// <summary>What a spatial query met.</summary>
public enum VoxelHitKind
{
    /// <summary>A block whose collision the query meets.</summary>
    Block,

    /// <summary>A chunk that is not loaded: the query cannot tell what it holds.</summary>
    NotLoaded,
}

/// <summary>What a raycast met first.</summary>
public readonly struct VoxelRayHit
{
    internal VoxelRayHit(VoxelHitKind kind, Grid grid, BlockCoordinate position, BlockFace? face, double distance, uint state, BlockCoordinate origin)
    {
        Kind = kind;
        Grid = grid;
        Position = position;
        Face = face;
        Distance = distance;
        State = state;
        Origin = origin;
    }

    /// <summary>Gets whether the ray met a block or a chunk that is not loaded.</summary>
    public VoxelHitKind Kind { get; }

    /// <summary>Gets the grid.</summary>
    public Grid Grid { get; }

    /// <summary>Gets the cell met, in grid coordinates: the block, or the first cell of the chunk that is not loaded.</summary>
    public BlockCoordinate Position { get; }

    /// <summary>Gets the chunk of <see cref="Position"/>.</summary>
    public ChunkCoordinate Chunk => VoxelCoordinates.ToChunk(Position);

    /// <summary>Gets the face of the block, or of its collision box, through which the ray entered; <see langword="null"/> when the ray starts inside.</summary>
    public BlockFace? Face { get; }

    /// <summary>Gets the distance from the origin of the ray to the hit, in blocks of the world.</summary>
    public double Distance { get; }

    /// <summary>Gets the state of the block, or 0 for a chunk that is not loaded.</summary>
    public uint State { get; }

    /// <summary>Gets the origin cell of the block: <see cref="Position"/>, or the origin of the multi-block it belongs to.</summary>
    public BlockCoordinate Origin { get; }
}

/// <summary>A block or a chunk that is not loaded, met by a box or sphere query.</summary>
/// <param name="Kind">A block, or a chunk that is not loaded.</param>
/// <param name="Grid">The grid.</param>
/// <param name="Position">The block, or the lowest corner of the chunk that is not loaded, in grid coordinates.</param>
/// <param name="State">The state of the block, or 0.</param>
public readonly record struct VoxelQueryHit(VoxelHitKind Kind, Grid Grid, BlockCoordinate Position, uint State)
{
    /// <summary>Gets the chunk of <see cref="Position"/>.</summary>
    public ChunkCoordinate Chunk => VoxelCoordinates.ToChunk(Position);
}

/// <summary>
/// Spatial queries over the grids of one context: block reads in grid
/// coordinates, and raycasts, boxes, and spheres in the world. Resolve it
/// with <c>context.Resolve&lt;IVoxelQueries&gt;()</c>.
/// </summary>
/// <remarks>
/// World queries take the candidate grids from the spatial index, then query
/// each grid in its own frame, rotated grids included, in double precision:
/// a query a billion blocks from the origin is as precise as at the origin.
/// A chunk that is not loaded is reported as such, never read as air. Use the
/// service on the loop thread of the context.
/// </remarks>
public interface IVoxelQueries
{
    /// <summary>Reads one block.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="position">The block, in grid coordinates.</param>
    /// <returns>The block and whether it could be read.</returns>
    BlockRead Read(Grid grid, BlockCoordinate position);

    /// <summary>Reads blocks of one grid, without allocation.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="positions">The blocks.</param>
    /// <param name="results">Receives one result per block, at the same index.</param>
    /// <exception cref="ArgumentException"><paramref name="results"/> is shorter than <paramref name="positions"/>.</exception>
    void Read(Grid grid, ReadOnlySpan<BlockCoordinate> positions, Span<BlockRead> results);

    /// <summary>Reads the six direct neighbors of a block, in the order of <see cref="BlockFace"/>.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="position">The block.</param>
    /// <param name="results">Receives six results.</param>
    /// <exception cref="ArgumentException"><paramref name="results"/> holds fewer than six results.</exception>
    void ReadNeighbors(Grid grid, BlockCoordinate position, Span<BlockRead> results);

    /// <summary>Casts a ray through the grids of a dimension, by a grid traversal (DDA) in each grid.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="origin">The start of the ray.</param>
    /// <param name="direction">The direction in the world; it need not be unit length.</param>
    /// <param name="maxDistance">The length of the ray, in blocks of the world.</param>
    /// <param name="hit">The nearest block whose collision the ray meets, or the nearest chunk that is not loaded, if nearer.</param>
    /// <returns><see langword="true"/> when the ray met something.</returns>
    /// <remarks>A block whose shape is not a full cube is tested against its collision boxes; a block without collision lets the ray through.</remarks>
    bool Raycast(DimensionId dimension, in WorldPosition origin, Vector3 direction, float maxDistance, out VoxelRayHit hit);

    /// <summary>Adds the blocks other than air whose cells overlap a box of the world, and the chunks that are not loaded in it.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="box">The box, aligned with the axes of the world.</param>
    /// <param name="results">Receives the hits, grid by grid; it is not cleared.</param>
    void QueryBox(DimensionId dimension, in WorldBox box, List<VoxelQueryHit> results);

    /// <summary>Adds the blocks other than air whose cells overlap a sphere of the world, and the chunks that are not loaded in it.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="center">The center.</param>
    /// <param name="radius">The radius, in blocks of the world.</param>
    /// <param name="results">Receives the hits, grid by grid; it is not cleared.</param>
    void QuerySphere(DimensionId dimension, in WorldPosition center, float radius, List<VoxelQueryHit> results);
}

/// <summary>The spatial queries of one context.</summary>
internal sealed class VoxelQueries : IVoxelQueries
{
    private const double Epsilon = 1e-9;

    private readonly VoxelWorlds _worlds;
    private readonly ChunkLoader _loader;
    private IShapeRegistry _shapes = null!;
    private IMultiBlockRegistry _multiBlocks = null!;

    public VoxelQueries(VoxelWorlds worlds, ChunkLoader loader) => (_worlds, _loader) = (worlds, loader);

    public void Start(IShapeRegistry shapes, IMultiBlockRegistry multiBlocks) => (_shapes, _multiBlocks) = (shapes, multiBlocks);

    [HotPath]
    public BlockRead Read(Grid grid, BlockCoordinate position)
    {
        if (grid.Bounds is { } bounds && !bounds.Contains(position)) return new BlockRead(BlockReadStatus.OutOfBounds, 0);
        if (!_loader.IsReadable(grid, VoxelCoordinates.ToChunk(position), out Chunk? chunk)) return new BlockRead(BlockReadStatus.NotLoaded, 0);
        return new BlockRead(BlockReadStatus.Loaded, chunk is null ? 0 : chunk.GetState(VoxelCoordinates.ToIndex(position)));
    }

    [HotPath]
    public void Read(Grid grid, ReadOnlySpan<BlockCoordinate> positions, Span<BlockRead> results)
    {
        if (results.Length < positions.Length) throw new ArgumentException("There is one result per position.", nameof(results));
        for (int i = 0; i < positions.Length; i++) results[i] = Read(grid, positions[i]);
    }

    [HotPath]
    public void ReadNeighbors(Grid grid, BlockCoordinate position, Span<BlockRead> results)
    {
        if (results.Length < 6) throw new ArgumentException("A block has six neighbors.", nameof(results));
        results[(int)BlockFace.North] = Neighbor(grid, position, 0, 0, -1);
        results[(int)BlockFace.East] = Neighbor(grid, position, 1, 0, 0);
        results[(int)BlockFace.South] = Neighbor(grid, position, 0, 0, 1);
        results[(int)BlockFace.West] = Neighbor(grid, position, -1, 0, 0);
        results[(int)BlockFace.Up] = Neighbor(grid, position, 0, 1, 0);
        results[(int)BlockFace.Down] = Neighbor(grid, position, 0, -1, 0);
    }

    public bool Raycast(DimensionId dimension, in WorldPosition origin, Vector3 direction, float maxDistance, out VoxelRayHit hit)
    {
        hit = default;
        double length = Math.Sqrt(((double)direction.X * direction.X) + ((double)direction.Y * direction.Y) + ((double)direction.Z * direction.Z));
        if (length == 0 || !(maxDistance > 0)) return false;
        var unit = new Double3(direction.X / length, direction.Y / length, direction.Z / length);
        Vector3 end = new((float)(unit.X * maxDistance), (float)(unit.Y * maxDistance), (float)(unit.Z * maxDistance));
        var box = new WorldBox(origin, Vector3.Min(Vector3.Zero, end), Vector3.Max(Vector3.Zero, end));
        bool found = false;
        foreach (Grid grid in _worlds.SpatialIndex.Query(box))
        {
            if (grid.Dimension != dimension || grid.IsDestroyed) continue;
            if (Cast(grid, origin, unit, maxDistance, out VoxelRayHit candidate) && (!found || candidate.Distance < hit.Distance))
            {
                hit = candidate;
                found = true;
            }
        }

        return found;
    }

    public void QueryBox(DimensionId dimension, in WorldBox box, List<VoxelQueryHit> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (Grid grid in _worlds.SpatialIndex.Query(box))
        {
            if (grid.Dimension != dimension || grid.IsDestroyed) continue;
            var frame = new GridFrame(grid.Transform);
            Double3 center = frame.WorldOffset(box.Origin) + (Double3.From(box.Minimum + box.Maximum) * 0.5);
            Double3 half = Double3.From(box.Maximum - box.Minimum) * 0.5;
            var region = new BoxRegion(center, half);
            Collect(grid, frame, region, LocalBounds(frame, center, half), results);
        }
    }

    public void QuerySphere(DimensionId dimension, in WorldPosition center, float radius, List<VoxelQueryHit> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (!(radius >= 0)) throw new ArgumentOutOfRangeException(nameof(radius));
        foreach (Grid grid in _worlds.SpatialIndex.Query(center, new Vector3(radius)))
        {
            if (grid.Dimension != dimension || grid.IsDestroyed) continue;
            var frame = new GridFrame(grid.Transform);
            Double3 offset = frame.WorldOffset(center);
            var region = new SphereRegion(offset, radius);
            Collect(grid, frame, region, LocalBounds(frame, offset, new Double3(radius, radius, radius)), results);
        }
    }

    [HotPath]
    private BlockRead Neighbor(Grid grid, BlockCoordinate position, int x, int y, int z)
    {
        long nx = (long)position.X + x, ny = (long)position.Y + y, nz = (long)position.Z + z;
        if (nx is < int.MinValue or > int.MaxValue || ny is < int.MinValue or > int.MaxValue || nz is < int.MinValue or > int.MaxValue)
        {
            return new BlockRead(BlockReadStatus.OutOfBounds, 0);
        }

        return Read(grid, new BlockCoordinate((int)nx, (int)ny, (int)nz));
    }

    private bool Cast(Grid grid, in WorldPosition origin, Double3 worldDirection, double maxDistance, out VoxelRayHit hit)
    {
        hit = default;
        var frame = new GridFrame(grid.Transform);
        Double3 o = frame.ToLocal(origin);
        Double3 d = frame.ToLocalDirection(worldDirection);
        double t0 = 0, t1 = maxDistance;
        BlockFace? face = null;
        if (grid.Bounds is { } bounds)
        {
            var min = new Double3(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z);
            var max = new Double3((long)bounds.Maximum.X + 1, (long)bounds.Maximum.Y + 1, (long)bounds.Maximum.Z + 1);
            if (!Slab(o, d, min, max, ref t0, ref t1, out face)) return false;
        }

        Double3 start = o + (d * t0);
        long cx = (long)Math.Floor(start.X + (d.X * Epsilon));
        long cy = (long)Math.Floor(start.Y + (d.Y * Epsilon));
        long cz = (long)Math.Floor(start.Z + (d.Z * Epsilon));
        int sx = Math.Sign(d.X), sy = Math.Sign(d.Y), sz = Math.Sign(d.Z);
        double dx = sx == 0 ? double.PositiveInfinity : Math.Abs(1 / d.X);
        double dy = sy == 0 ? double.PositiveInfinity : Math.Abs(1 / d.Y);
        double dz = sz == 0 ? double.PositiveInfinity : Math.Abs(1 / d.Z);
        double tx = sx == 0 ? double.PositiveInfinity : t0 + ((sx > 0 ? cx + 1 - start.X : start.X - cx) * dx);
        double ty = sy == 0 ? double.PositiveInfinity : t0 + ((sy > 0 ? cy + 1 - start.Y : start.Y - cy) * dy);
        double tz = sz == 0 ? double.PositiveInfinity : t0 + ((sz > 0 ? cz + 1 - start.Z : start.Z - cz) * dz);
        double t = t0;
        while (t <= t1)
        {
            if (cx is < int.MinValue or > int.MaxValue || cy is < int.MinValue or > int.MaxValue || cz is < int.MinValue or > int.MaxValue) return false;
            var cell = new BlockCoordinate((int)cx, (int)cy, (int)cz);
            double exit = Math.Min(tx, Math.Min(ty, tz));
            if (TestCell(grid, cell, o, d, t, Math.Min(exit, t1), face, out hit)) return true;
            if (tx <= ty && tx <= tz)
            {
                t = tx;
                tx += dx;
                cx += sx;
                face = sx > 0 ? BlockFace.West : BlockFace.East;
            }
            else if (ty <= tz)
            {
                t = ty;
                ty += dy;
                cy += sy;
                face = sy > 0 ? BlockFace.Down : BlockFace.Up;
            }
            else
            {
                t = tz;
                tz += dz;
                cz += sz;
                face = sz > 0 ? BlockFace.North : BlockFace.South;
            }
        }

        return false;
    }

    private bool TestCell(Grid grid, BlockCoordinate cell, Double3 o, Double3 d, double enter, double exit, BlockFace? face, out VoxelRayHit hit)
    {
        hit = default;
        if (grid.Bounds is { } bounds && !bounds.Contains(cell)) return false;
        ChunkCoordinate chunkCoordinate = VoxelCoordinates.ToChunk(cell);
        if (!_loader.IsReadable(grid, chunkCoordinate, out Chunk? chunk))
        {
            hit = new VoxelRayHit(VoxelHitKind.NotLoaded, grid, cell, face, enter, 0, cell);
            return true;
        }

        if (chunk is null) return false;
        int index = VoxelCoordinates.ToIndex(cell);
        uint state = chunk.GetState(index);
        if (state == 0) return false;
        CollisionShape collision = _shapes.Get(state, new BlockDataView(chunk, index)).Collision;
        if (collision.Count == 0) return false;
        double best = double.PositiveInfinity;
        BlockFace? bestFace = face;
        if (collision.Count == 1 && collision[0].Minimum == Vector3.Zero && collision[0].Maximum == Vector3.One)
        {
            best = enter;
        }
        else
        {
            Double3 local = o - new Double3(cell.X, cell.Y, cell.Z);
            for (int i = 0; i < collision.Count; i++)
            {
                double near = enter, far = exit;
                if (Slab(local, d, Double3.From(collision[i].Minimum), Double3.From(collision[i].Maximum), ref near, ref far, out BlockFace? boxFace) && near < best)
                {
                    best = near;
                    bestFace = near > 0 ? boxFace : null;
                }
            }

            if (double.IsPositiveInfinity(best)) return false;
        }

        BlockCoordinate origin = _multiBlocks.IsMultiBlock(state) ? _multiBlocks.GetOrigin(state, cell) : cell;
        hit = new VoxelRayHit(VoxelHitKind.Block, grid, cell, bestFace, best, state, origin);
        return true;
    }

    // Clips [near, far] to the part of the ray inside a box; gives the face through which the ray enters.
    private static bool Slab(Double3 o, Double3 d, Double3 min, Double3 max, ref double near, ref double far, out BlockFace? face)
    {
        face = null;
        if (!Axis(o.X, d.X, min.X, max.X, BlockFace.West, BlockFace.East, ref near, ref far, ref face)) return false;
        if (!Axis(o.Y, d.Y, min.Y, max.Y, BlockFace.Down, BlockFace.Up, ref near, ref far, ref face)) return false;
        return Axis(o.Z, d.Z, min.Z, max.Z, BlockFace.North, BlockFace.South, ref near, ref far, ref face);
    }

    private static bool Axis(double o, double d, double min, double max, BlockFace low, BlockFace high, ref double near, ref double far, ref BlockFace? face)
    {
        if (d == 0) return o >= min && o <= max;
        double a = (min - o) / d, b = (max - o) / d;
        BlockFace enter = low;
        if (a > b)
        {
            (a, b) = (b, a);
            enter = high;
        }

        if (a > near)
        {
            near = a;
            face = enter;
        }

        far = Math.Min(far, b);
        return near <= far;
    }

    // The cells of a grid that a region of the world may overlap: the box around the region seen in the frame of the grid.
    private static (long MinX, long MinY, long MinZ, long MaxX, long MaxY, long MaxZ) LocalBounds(GridFrame frame, Double3 center, Double3 half)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            var offset = new Double3((corner & 1) == 0 ? -half.X : half.X, (corner & 2) == 0 ? -half.Y : half.Y, (corner & 4) == 0 ? -half.Z : half.Z);
            Double3 local = frame.ToLocalDirection(center + offset);
            minX = Math.Min(minX, local.X);
            minY = Math.Min(minY, local.Y);
            minZ = Math.Min(minZ, local.Z);
            maxX = Math.Max(maxX, local.X);
            maxY = Math.Max(maxY, local.Y);
            maxZ = Math.Max(maxZ, local.Z);
        }

        return ((long)Math.Floor(minX), (long)Math.Floor(minY), (long)Math.Floor(minZ), (long)Math.Floor(maxX), (long)Math.Floor(maxY), (long)Math.Floor(maxZ));
    }

    private void Collect<TRegion>(Grid grid, GridFrame frame, TRegion region, (long MinX, long MinY, long MinZ, long MaxX, long MaxY, long MaxZ) range, List<VoxelQueryHit> results)
        where TRegion : struct, IRegion
    {
        long minX = range.MinX, minY = range.MinY, minZ = range.MinZ, maxX = range.MaxX, maxY = range.MaxY, maxZ = range.MaxZ;
        if (grid.Bounds is { } bounds)
        {
            minX = Math.Max(minX, bounds.Minimum.X);
            minY = Math.Max(minY, bounds.Minimum.Y);
            minZ = Math.Max(minZ, bounds.Minimum.Z);
            maxX = Math.Min(maxX, bounds.Maximum.X);
            maxY = Math.Min(maxY, bounds.Maximum.Y);
            maxZ = Math.Min(maxZ, bounds.Maximum.Z);
        }

        minX = Math.Max(minX, int.MinValue);
        minY = Math.Max(minY, int.MinValue);
        minZ = Math.Max(minZ, int.MinValue);
        maxX = Math.Min(maxX, int.MaxValue);
        maxY = Math.Min(maxY, int.MaxValue);
        maxZ = Math.Min(maxZ, int.MaxValue);
        if (minX > maxX || minY > maxY || minZ > maxZ) return;

        Double3 halfCell = AxisLengths(frame) * 0.5;
        for (long chunkY = minY >> 5; chunkY <= maxY >> 5; chunkY++)
        {
            for (long chunkZ = minZ >> 5; chunkZ <= maxZ >> 5; chunkZ++)
            {
                for (long chunkX = minX >> 5; chunkX <= maxX >> 5; chunkX++)
                {
                    var chunkCoordinate = new ChunkCoordinate((int)chunkX, (int)chunkY, (int)chunkZ);
                    long x0 = Math.Max(minX, chunkX * 32), x1 = Math.Min(maxX, (chunkX * 32) + 31);
                    long y0 = Math.Max(minY, chunkY * 32), y1 = Math.Min(maxY, (chunkY * 32) + 31);
                    long z0 = Math.Max(minZ, chunkZ * 32), z1 = Math.Min(maxZ, (chunkZ * 32) + 31);
                    if (!_loader.IsReadable(grid, chunkCoordinate, out Chunk? chunk))
                    {
                        if (Touches(frame, region, halfCell, x0, y0, z0, x1, y1, z1))
                        {
                            results.Add(new VoxelQueryHit(VoxelHitKind.NotLoaded, grid, new BlockCoordinate((int)(chunkX * 32), (int)(chunkY * 32), (int)(chunkZ * 32)), 0));
                        }

                        continue;
                    }

                    if (chunk is null) continue;
                    for (long y = y0; y <= y1; y++)
                    {
                        for (long z = z0; z <= z1; z++)
                        {
                            for (long x = x0; x <= x1; x++)
                            {
                                var cell = new BlockCoordinate((int)x, (int)y, (int)z);
                                uint state = chunk.GetState(VoxelCoordinates.ToIndex(cell));
                                if (state == 0) continue;
                                Double3 cellCenter = frame.ToWorldOffset(new Double3(x + 0.5, y + 0.5, z + 0.5));
                                if (region.Overlaps(frame, cellCenter, halfCell)) results.Add(new VoxelQueryHit(VoxelHitKind.Block, grid, cell, state));
                            }
                        }
                    }
                }
            }
        }
    }

    // Whether the region overlaps the cells of a range: tested against the box of the range, as one oriented box.
    private static bool Touches<TRegion>(GridFrame frame, TRegion region, Double3 halfCell, long x0, long y0, long z0, long x1, long y1, long z1)
        where TRegion : struct, IRegion
    {
        Double3 center = frame.ToWorldOffset(new Double3((x0 + x1 + 1) * 0.5, (y0 + y1 + 1) * 0.5, (z0 + z1 + 1) * 0.5));
        var half = new Double3(halfCell.X * (x1 - x0 + 1), halfCell.Y * (y1 - y0 + 1), halfCell.Z * (z1 - z0 + 1));
        return region.Overlaps(frame, center, half);
    }

    // The length in the world of one block along each axis of the grid.
    private static Double3 AxisLengths(GridFrame frame) => new(
        frame.ToWorldDirection(new Double3(1, 0, 0)).Length(),
        frame.ToWorldDirection(new Double3(0, 1, 0)).Length(),
        frame.ToWorldDirection(new Double3(0, 0, 1)).Length());

    /// <summary>A region of the world, offset from the origin of a grid, tested against oriented boxes of that grid.</summary>
    private interface IRegion
    {
        /// <summary>Tells whether the region overlaps a box of the grid: its center offset from the origin of the grid, and its half lengths along the axes of the grid, in the world.</summary>
        bool Overlaps(GridFrame frame, Double3 center, Double3 half);
    }

    private readonly struct BoxRegion(Double3 center, Double3 half) : IRegion
    {
        public bool Overlaps(GridFrame frame, Double3 boxCenter, Double3 boxHalf)
        {
            // Separating axes between the box of the world (axes of the world) and the oriented box (axes of the grid).
            Span<Double3> axes = stackalloc Double3[3];
            axes[0] = Unit(frame.ToWorldDirection(new Double3(1, 0, 0)));
            axes[1] = Unit(frame.ToWorldDirection(new Double3(0, 1, 0)));
            axes[2] = Unit(frame.ToWorldDirection(new Double3(0, 0, 1)));
            Double3 between = boxCenter - center;
            ReadOnlySpan<Double3> world = [new Double3(1, 0, 0), new Double3(0, 1, 0), new Double3(0, 0, 1)];
            if (Separates(world[0], between, axes, boxHalf)) return false;
            if (Separates(world[1], between, axes, boxHalf)) return false;
            if (Separates(world[2], between, axes, boxHalf)) return false;
            for (int i = 0; i < 3; i++)
            {
                if (Separates(axes[i], between, axes, boxHalf)) return false;
            }

            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    Double3 axis = Cross(world[i], axes[j]);
                    if (axis.Dot(axis) > Epsilon && Separates(axis, between, axes, boxHalf)) return false;
                }
            }

            return true;
        }

        private bool Separates(Double3 axis, Double3 between, ReadOnlySpan<Double3> axes, Double3 boxHalf)
        {
            double a = (Math.Abs(axis.X) * half.X) + (Math.Abs(axis.Y) * half.Y) + (Math.Abs(axis.Z) * half.Z);
            double b = (Math.Abs(axis.Dot(axes[0])) * boxHalf.X) + (Math.Abs(axis.Dot(axes[1])) * boxHalf.Y) + (Math.Abs(axis.Dot(axes[2])) * boxHalf.Z);
            return Math.Abs(between.Dot(axis)) > a + b + Epsilon;
        }

        private static Double3 Cross(Double3 a, Double3 b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));
    }

    private readonly struct SphereRegion(Double3 center, double radius) : IRegion
    {
        public bool Overlaps(GridFrame frame, Double3 boxCenter, Double3 boxHalf)
        {
            // The center in the frame of the oriented box, then its distance to the box.
            Double3 between = center - boxCenter;
            double distance = 0;
            distance += Outside(between.Dot(Unit(frame.ToWorldDirection(new Double3(1, 0, 0)))), boxHalf.X);
            distance += Outside(between.Dot(Unit(frame.ToWorldDirection(new Double3(0, 1, 0)))), boxHalf.Y);
            distance += Outside(between.Dot(Unit(frame.ToWorldDirection(new Double3(0, 0, 1)))), boxHalf.Z);
            return distance <= (radius * radius) + Epsilon;
        }

        private static double Outside(double value, double half)
        {
            double excess = Math.Abs(value) - half;
            return excess > 0 ? excess * excess : 0;
        }
    }

    private static Double3 Unit(Double3 value)
    {
        double length = value.Length();
        return length == 0 ? value : value * (1 / length);
    }
}
