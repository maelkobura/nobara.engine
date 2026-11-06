// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Voxel;

/// <summary>A point or a vector in double precision, in the frame of a grid or in the world.</summary>
internal readonly struct Double3
{
    [HotPath]
    public Double3(double x, double y, double z) => (X, Y, Z) = (x, y, z);

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    [HotPath]
    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    [HotPath]
    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    [HotPath]
    public static Double3 operator *(Double3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);

    [HotPath]
    public double Dot(Double3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    [HotPath]
    public double Length() => Math.Sqrt(Dot(this));

    [HotPath]
    public static Double3 From(Vector3 value) => new(value.X, value.Y, value.Z);
}

/// <summary>
/// The transform of a grid in double precision: converts world positions to
/// grid coordinates without losing precision far from the origin.
/// </summary>
/// <remarks>
/// The offset between two positions is computed from their whole chunk
/// coordinates and their local parts, in double precision, before the
/// rotation and the scale apply: a point a billion blocks from the origin
/// keeps the same precision as a point at the origin.
/// </remarks>
internal readonly struct GridFrame
{
    private readonly WorldPosition _origin;
    private readonly double _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;
    private readonly Double3 _scale;

    [HotPath]
    public GridFrame(in WorldTransform transform)
    {
        _origin = transform.Position;
        Quaternion q = Quaternion.Normalize(transform.Rotation);
        double x = q.X, y = q.Y, z = q.Z, w = q.W;

        // The rotation matrix of the quaternion, for column vectors: world = R * local.
        _m00 = 1 - (2 * ((y * y) + (z * z)));
        _m01 = 2 * ((x * y) - (z * w));
        _m02 = 2 * ((x * z) + (y * w));
        _m10 = 2 * ((x * y) + (z * w));
        _m11 = 1 - (2 * ((x * x) + (z * z)));
        _m12 = 2 * ((y * z) - (x * w));
        _m20 = 2 * ((x * z) - (y * w));
        _m21 = 2 * ((y * z) + (x * w));
        _m22 = 1 - (2 * ((x * x) + (y * y)));
        _scale = Double3.From(transform.Scale);
    }

    /// <summary>Gets the offset of a world position from the origin of the grid, in the axes of the world.</summary>
    [HotPath]
    public Double3 WorldOffset(in WorldPosition position) => Offset(position, _origin);

    /// <summary>Converts a world position to grid coordinates, in blocks.</summary>
    [HotPath]
    public Double3 ToLocal(in WorldPosition position) => ToLocalDirection(WorldOffset(position));

    /// <summary>Converts a direction or an offset in the axes of the world to the axes and the scale of the grid.</summary>
    [HotPath]
    public Double3 ToLocalDirection(Double3 world)
    {
        // The transpose of R is its inverse.
        double x = (_m00 * world.X) + (_m10 * world.Y) + (_m20 * world.Z);
        double y = (_m01 * world.X) + (_m11 * world.Y) + (_m21 * world.Z);
        double z = (_m02 * world.X) + (_m12 * world.Y) + (_m22 * world.Z);
        return new Double3(Divide(x, _scale.X), Divide(y, _scale.Y), Divide(z, _scale.Z));
    }

    /// <summary>Converts an offset in grid coordinates to the axes and the scale of the world.</summary>
    [HotPath]
    public Double3 ToWorldDirection(Double3 local)
    {
        double x = local.X * _scale.X;
        double y = local.Y * _scale.Y;
        double z = local.Z * _scale.Z;
        return new Double3(
            (_m00 * x) + (_m01 * y) + (_m02 * z),
            (_m10 * x) + (_m11 * y) + (_m12 * z),
            (_m20 * x) + (_m21 * y) + (_m22 * z));
    }

    /// <summary>Converts grid coordinates to the offset from the origin of the grid, in the axes of the world.</summary>
    [HotPath]
    public Double3 ToWorldOffset(Double3 local) => ToWorldDirection(local);

    /// <summary>Gets the chunk of the grid that contains a world position.</summary>
    [HotPath]
    public ChunkCoordinate ChunkOf(in WorldPosition position)
    {
        Double3 local = ToLocal(position);
        return new ChunkCoordinate(FloorChunk(local.X), FloorChunk(local.Y), FloorChunk(local.Z));
    }

    /// <summary>Gets the offset between two world positions in double precision.</summary>
    [HotPath]
    public static Double3 Offset(in WorldPosition position, in WorldPosition origin) => new(
        ((((long)position.ChunkX) - origin.ChunkX) * (double)WorldPosition.ChunkSize) + ((double)position.Local.X - origin.Local.X),
        ((((long)position.ChunkY) - origin.ChunkY) * (double)WorldPosition.ChunkSize) + ((double)position.Local.Y - origin.Local.Y),
        ((((long)position.ChunkZ) - origin.ChunkZ) * (double)WorldPosition.ChunkSize) + ((double)position.Local.Z - origin.Local.Z));

    [HotPath]
    private static int FloorChunk(double value) => (int)Math.Clamp(Math.Floor(value / VoxelCoordinates.ChunkSize), int.MinValue, int.MaxValue);

    [HotPath]
    private static double Divide(double value, double scale) => scale == 0 ? 0 : value / scale;
}
