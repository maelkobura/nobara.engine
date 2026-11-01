// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Renderer;

/// <summary>
/// A position in the world with the same precision everywhere: whole chunk
/// coordinates on 32 bits, and a position in the chunk in single precision,
/// in blocks.
/// </summary>
/// <remarks>
/// A chunk is a cube of <see cref="ChunkSize"/> blocks. The local position is
/// kept from 0 (included) to <see cref="ChunkSize"/> (excluded) on each axis:
/// the constructor moves the whole part to the chunk coordinates. The
/// renderer never sends such a position to the GPU: it subtracts the position
/// of the camera first (<see cref="RelativeTo"/>), so the GPU only sees small
/// coordinates.
/// </remarks>
public readonly struct WorldPosition : IEquatable<WorldPosition>
{
    /// <summary>The number of blocks along each edge of a chunk.</summary>
    public const int ChunkSize = 32;

    /// <summary>Initializes a position.</summary>
    /// <param name="chunkX">The chunk coordinate along X.</param>
    /// <param name="chunkY">The chunk coordinate along Y.</param>
    /// <param name="chunkZ">The chunk coordinate along Z.</param>
    /// <param name="local">The position in the chunk, in blocks; any value, carried over to the chunk coordinates.</param>
    [HotPath]
    public WorldPosition(int chunkX, int chunkY, int chunkZ, Vector3 local)
    {
        int carryX = (int)MathF.Floor(local.X / ChunkSize);
        int carryY = (int)MathF.Floor(local.Y / ChunkSize);
        int carryZ = (int)MathF.Floor(local.Z / ChunkSize);
        ChunkX = chunkX + carryX;
        ChunkY = chunkY + carryY;
        ChunkZ = chunkZ + carryZ;
        Local = new Vector3(Wrap(local.X - (carryX * ChunkSize)), Wrap(local.Y - (carryY * ChunkSize)), Wrap(local.Z - (carryZ * ChunkSize)));
    }

    /// <summary>Gets the chunk coordinate along X.</summary>
    public int ChunkX { get; }

    /// <summary>Gets the chunk coordinate along Y.</summary>
    public int ChunkY { get; }

    /// <summary>Gets the chunk coordinate along Z.</summary>
    public int ChunkZ { get; }

    /// <summary>Gets the position in the chunk, in blocks, from 0 to <see cref="ChunkSize"/> on each axis.</summary>
    public Vector3 Local { get; }

    /// <summary>Gets the position of a point given in blocks from the origin of the world.</summary>
    /// <param name="x">The coordinate along X, in blocks.</param>
    /// <param name="y">The coordinate along Y, in blocks.</param>
    /// <param name="z">The coordinate along Z, in blocks.</param>
    /// <returns>The position.</returns>
    [HotPath]
    public static WorldPosition FromBlocks(double x, double y, double z)
    {
        double chunkX = Math.Floor(x / ChunkSize);
        double chunkY = Math.Floor(y / ChunkSize);
        double chunkZ = Math.Floor(z / ChunkSize);
        return new WorldPosition(
            (int)chunkX,
            (int)chunkY,
            (int)chunkZ,
            new Vector3((float)(x - (chunkX * ChunkSize)), (float)(y - (chunkY * ChunkSize)), (float)(z - (chunkZ * ChunkSize))));
    }

    /// <summary>Gets the position moved by an offset.</summary>
    /// <param name="offset">The offset, in blocks.</param>
    /// <returns>The moved position.</returns>
    [HotPath]
    public WorldPosition Offset(Vector3 offset) => new(ChunkX, ChunkY, ChunkZ, Local + offset);

    /// <summary>
    /// Gets this position seen from another one: the offset from
    /// <paramref name="origin"/> to this position, in blocks. The difference of
    /// the chunk coordinates is computed in double precision before it is
    /// rounded, so the result is exact near the origin, however far both are
    /// from the origin of the world.
    /// </summary>
    /// <param name="origin">The origin, such as the position of the camera.</param>
    /// <returns>The offset.</returns>
    [HotPath]
    public Vector3 RelativeTo(in WorldPosition origin) => new(
        (float)((((long)ChunkX - origin.ChunkX) * (double)ChunkSize) + ((double)Local.X - origin.Local.X)),
        (float)((((long)ChunkY - origin.ChunkY) * (double)ChunkSize) + ((double)Local.Y - origin.Local.Y)),
        (float)((((long)ChunkZ - origin.ChunkZ) * (double)ChunkSize) + ((double)Local.Z - origin.Local.Z)));

    /// <summary>Gets the position between two others.</summary>
    /// <param name="from">The position at 0.</param>
    /// <param name="to">The position at 1.</param>
    /// <param name="amount">The fraction of the way, usually from 0 to 1.</param>
    /// <returns>The position.</returns>
    [HotPath]
    public static WorldPosition Lerp(in WorldPosition from, in WorldPosition to, float amount) => from.Offset(to.RelativeTo(from) * amount);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(WorldPosition other) =>
        ChunkX == other.ChunkX && ChunkY == other.ChunkY && ChunkZ == other.ChunkZ && Local.Equals(other.Local);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is WorldPosition other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => HashCode.Combine(ChunkX, ChunkY, ChunkZ, Local.X, Local.Y, Local.Z);

    /// <inheritdoc/>
    public override string ToString() => $"chunk ({ChunkX}, {ChunkY}, {ChunkZ}) + {Local}";

    /// <summary>Compares two positions.</summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    [HotPath]
    public static bool operator ==(WorldPosition left, WorldPosition right) => left.Equals(right);

    /// <summary>Compares two positions.</summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    [HotPath]
    public static bool operator !=(WorldPosition left, WorldPosition right) => !left.Equals(right);

    // A value a hair below the chunk size, after rounding, stays in the chunk.
    [HotPath]
    private static float Wrap(float value) => value >= ChunkSize ? MathF.BitDecrement(ChunkSize) : Math.Max(0, value);
}
