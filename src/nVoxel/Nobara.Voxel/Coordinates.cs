// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Voxel;

/// <summary>Identifies one block in the local coordinates of a voxel grid.</summary>
public readonly struct BlockCoordinate : IEquatable<BlockCoordinate>
{
    /// <summary>Initializes a block coordinate.</summary>
    [HotPath]
    public BlockCoordinate(int x, int y, int z) => (X, Y, Z) = (x, y, z);

    /// <summary>Gets the coordinate along X.</summary>
    public int X { get; }

    /// <summary>Gets the coordinate along Y.</summary>
    public int Y { get; }

    /// <summary>Gets the coordinate along Z.</summary>
    public int Z { get; }

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(BlockCoordinate other) => X == other.X && Y == other.Y && Z == other.Z;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BlockCoordinate other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <inheritdoc/>
    public override string ToString() => $"({X}, {Y}, {Z})";

    /// <summary>Compares two coordinates.</summary>
    public static bool operator ==(BlockCoordinate left, BlockCoordinate right) => left.Equals(right);

    /// <summary>Compares two coordinates.</summary>
    public static bool operator !=(BlockCoordinate left, BlockCoordinate right) => !left.Equals(right);
}

/// <summary>Identifies one 32-block chunk in the local coordinates of a grid.</summary>
public readonly struct ChunkCoordinate : IEquatable<ChunkCoordinate>, IComparable<ChunkCoordinate>
{
    /// <summary>Initializes a chunk coordinate.</summary>
    [HotPath]
    public ChunkCoordinate(int x, int y, int z) => (X, Y, Z) = (x, y, z);

    /// <summary>Gets the coordinate along X.</summary>
    public int X { get; }

    /// <summary>Gets the coordinate along Y.</summary>
    public int Y { get; }

    /// <summary>Gets the coordinate along Z.</summary>
    public int Z { get; }

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(ChunkCoordinate other) => X == other.X && Y == other.Y && Z == other.Z;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ChunkCoordinate other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <inheritdoc/>
    public int CompareTo(ChunkCoordinate other) => X != other.X ? X.CompareTo(other.X) : Y != other.Y ? Y.CompareTo(other.Y) : Z.CompareTo(other.Z);

    /// <inheritdoc/>
    public override string ToString() => $"({X}, {Y}, {Z})";

    /// <summary>Compares two coordinates.</summary>
    public static bool operator ==(ChunkCoordinate left, ChunkCoordinate right) => left.Equals(right);

    /// <summary>Compares two coordinates.</summary>
    public static bool operator !=(ChunkCoordinate left, ChunkCoordinate right) => !left.Equals(right);
}

/// <summary>Identifies a block inside a chunk, with each component from 0 to 31.</summary>
public readonly struct LocalBlockCoordinate : IEquatable<LocalBlockCoordinate>
{
    /// <summary>Initializes a local coordinate.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is outside 0 to 31.</exception>
    [HotPath]
    public LocalBlockCoordinate(int x, int y, int z)
    {
        if ((uint)x >= 32) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= 32) throw new ArgumentOutOfRangeException(nameof(y));
        if ((uint)z >= 32) throw new ArgumentOutOfRangeException(nameof(z));
        (X, Y, Z) = (x, y, z);
    }

    /// <summary>Gets the local coordinate along X.</summary>
    public int X { get; }

    /// <summary>Gets the local coordinate along Y.</summary>
    public int Y { get; }

    /// <summary>Gets the local coordinate along Z.</summary>
    public int Z { get; }

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(LocalBlockCoordinate other) => X == other.X && Y == other.Y && Z == other.Z;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LocalBlockCoordinate other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => X | (Y << 5) | (Z << 10);

    /// <summary>Compares two local coordinates.</summary>
    public static bool operator ==(LocalBlockCoordinate left, LocalBlockCoordinate right) => left.Equals(right);

    /// <summary>Compares two local coordinates.</summary>
    public static bool operator !=(LocalBlockCoordinate left, LocalBlockCoordinate right) => !left.Equals(right);
}

/// <summary>Conversions between block, chunk, local and continuous coordinates.</summary>
public static class VoxelCoordinates
{
    /// <summary>Gets the number of blocks in one chunk.</summary>
    public const int ChunkSize = 32;

    /// <summary>Gets the number of blocks in one chunk.</summary>
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    /// <summary>Converts a block coordinate to its chunk coordinate using floor division.</summary>
    [HotPath]
    public static ChunkCoordinate ToChunk(BlockCoordinate block) => new(block.X >> 5, block.Y >> 5, block.Z >> 5);

    /// <summary>Converts a block coordinate to its local chunk coordinate.</summary>
    [HotPath]
    public static LocalBlockCoordinate ToLocal(BlockCoordinate block) => new(block.X & 31, block.Y & 31, block.Z & 31);

    /// <summary>Gets the row-major index <c>x + 32 * (z + 32 * y)</c> of a local coordinate.</summary>
    [HotPath]
    public static int ToIndex(LocalBlockCoordinate local) => local.X + (ChunkSize * (local.Z + (ChunkSize * local.Y)));

    /// <summary>Gets the row-major index of a block coordinate inside its chunk.</summary>
    [HotPath]
    public static int ToIndex(BlockCoordinate block) => ToIndex(ToLocal(block));

    /// <summary>Combines a chunk and local coordinate, refusing integer overflow.</summary>
    /// <exception cref="OverflowException">The combined coordinate is outside the signed 32-bit range.</exception>
    [HotPath]
    public static BlockCoordinate ToBlock(ChunkCoordinate chunk, LocalBlockCoordinate local) => new(
        checked((chunk.X * ChunkSize) + local.X),
        checked((chunk.Y * ChunkSize) + local.Y),
        checked((chunk.Z * ChunkSize) + local.Z));

    /// <summary>Converts a block coordinate to a continuous position at its corner.</summary>
    [HotPath]
    public static WorldPosition ToWorldPosition(BlockCoordinate block) => WorldPosition.FromBlocks(block.X, block.Y, block.Z);

    /// <summary>Converts a continuous position to the block containing it.</summary>
    /// <exception cref="OverflowException">A coordinate is outside the signed 32-bit block range.</exception>
    [HotPath]
    public static BlockCoordinate ToBlock(in WorldPosition position)
    {
        double x = ((long)position.ChunkX * ChunkSize) + (double)MathF.Floor(position.Local.X);
        double y = ((long)position.ChunkY * ChunkSize) + (double)MathF.Floor(position.Local.Y);
        double z = ((long)position.ChunkZ * ChunkSize) + (double)MathF.Floor(position.Local.Z);
        return new BlockCoordinate(ToInt32(Math.Floor(x)), ToInt32(Math.Floor(y)), ToInt32(Math.Floor(z)));
    }

    /// <summary>Converts a block center in grid space to a world position through a grid transform.</summary>
    [HotPath]
    public static WorldPosition ToWorldPosition(BlockCoordinate block, in WorldTransform transform) =>
        transform.TransformPoint(new Vector3(block.X + 0.5f, block.Y + 0.5f, block.Z + 0.5f));

    /// <summary>Converts a world position to the containing block in a transformed grid.</summary>
    /// <exception cref="OverflowException">The transformed coordinate is outside the signed 32-bit block range.</exception>
    [HotPath]
    public static BlockCoordinate ToBlock(in WorldPosition position, in WorldTransform transform)
    {
        Vector3 local = transform.InverseTransformPoint(position);
        return new BlockCoordinate(ToInt32(Math.Floor(local.X)), ToInt32(Math.Floor(local.Y)), ToInt32(Math.Floor(local.Z)));
    }

    [HotPath]
    private static int ToInt32(double value) => value is < int.MinValue or > int.MaxValue ? throw new OverflowException($"Block coordinate {value} is outside the signed 32-bit range.") : checked((int)value);
}
