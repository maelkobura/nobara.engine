// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>The shape that a sweep moves or an overlap places: a sphere, a capsule, or a box.</summary>
/// <remarks>Create it with <see cref="Sphere"/>, <see cref="Capsule"/>, or <see cref="Box"/>; it holds no handle and needs no destruction.</remarks>
[HotPath]
public readonly record struct QueryShape
{
    private QueryShape(ShapeKind kind, float radius, float length, Vector3 size) => (Kind, Radius, Length, Size) = (kind, radius, length, size);

    /// <summary>Gets the kind of the shape: <see cref="ShapeKind.Sphere"/>, <see cref="ShapeKind.Capsule"/>, or <see cref="ShapeKind.Box"/>.</summary>
    public ShapeKind Kind { get; }

    /// <summary>Gets the radius of a sphere or a capsule, in blocks.</summary>
    public float Radius { get; }

    /// <summary>Gets the length of the cylinder of a capsule between its two half spheres, along its Y axis.</summary>
    public float Length { get; }

    /// <summary>Gets the size of a box along each of its axes.</summary>
    public Vector3 Size { get; }

    /// <summary>Gets the radius of the sphere that holds the shape, around its center.</summary>
    public float BoundingRadius => Kind switch
    {
        ShapeKind.Sphere => Radius,
        ShapeKind.Capsule => Radius + (Length / 2),
        _ => Size.Length() / 2,
    };

    /// <summary>Creates a sphere.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not finite and above 0.</exception>
    [HotPath]
    public static QueryShape Sphere(float radius)
    {
        RequirePositive(radius, nameof(radius));
        return new QueryShape(ShapeKind.Sphere, radius, 0, default);
    }

    /// <summary>Creates a capsule along its Y axis.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <param name="length">The length of the cylinder between the two half spheres, from 0.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not finite and above 0, or the length is negative or not finite.</exception>
    [HotPath]
    public static QueryShape Capsule(float radius, float length)
    {
        RequirePositive(radius, nameof(radius));
        if (!float.IsFinite(length) || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length of a capsule is finite and not negative.");
        }

        return new QueryShape(ShapeKind.Capsule, radius, length, default);
    }

    /// <summary>Creates a box.</summary>
    /// <param name="size">The size along each axis, above 0.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not finite and above 0.</exception>
    [HotPath]
    public static QueryShape Box(Vector3 size)
    {
        RequirePositive(size.X, nameof(size));
        RequirePositive(size.Y, nameof(size));
        RequirePositive(size.Z, nameof(size));
        return new QueryShape(ShapeKind.Box, 0, 0, size);
    }

    [HotPath]
    private static void RequirePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The sizes of a query shape are finite and above 0.");
        }
    }
}

/// <summary>
/// What a query meets: the layers of the bodies it hits, the voxels when the
/// mask holds <see cref="CollisionFilter.VoxelLayer"/>, and one body it
/// ignores, such as the body that casts it.
/// </summary>
/// <remarks>
/// The default filter meets every layer, voxels included, and ignores no body.
/// A body is met when it belongs to a layer of <see cref="Mask"/>.
/// </remarks>
[HotPath]
public readonly record struct QueryFilter
{
    private readonly uint _excluded;

    /// <summary>Creates a filter.</summary>
    /// <param name="mask">The layers the query meets; <see cref="CollisionFilter.VoxelLayer"/> for the voxels.</param>
    /// <param name="ignored">A body the query goes through, or the default handle for none.</param>
    [HotPath]
    public QueryFilter(uint mask, BodyHandle ignored = default)
    {
        _excluded = ~mask;
        Ignored = ignored;
    }

    /// <summary>Gets the filter that meets everything.</summary>
    public static QueryFilter All => default;

    /// <summary>Gets the layers the query meets.</summary>
    public uint Mask
    {
        [HotPath]
        get => ~_excluded;
    }

    /// <summary>Gets the body the query goes through, or the default handle for none.</summary>
    public BodyHandle Ignored { get; }

    /// <summary>Gets whether the query meets the voxels.</summary>
    public bool MeetsVoxels
    {
        [HotPath]
        get => (Mask & CollisionFilter.VoxelLayer) != 0;
    }
}

/// <summary>What a query met.</summary>
public enum PhysicsHitKind : byte
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>A body.</summary>
    Body,

    /// <summary>A collision box of a block.</summary>
    Block,

    /// <summary>A chunk that is not loaded: what it holds is unknown, and the query reports nothing past it.</summary>
    NotLoaded,
}

/// <summary>One result of a query.</summary>
/// <param name="Kind">What the query met.</param>
/// <param name="Distance">How far along its direction a ray or a sweep went before it met the hit, in blocks; 0 for an overlap, or when the shape starts inside.</param>
/// <param name="Point">Where the hit is: the point a ray reaches, or a point of the surface met by a sweep; the center of the shape for an overlap.</param>
/// <param name="Normal">The normal of the surface met, toward the query; the opposite of the direction when the shape starts inside, and zero for an overlap.</param>
/// <param name="Body">The body met, for <see cref="PhysicsHitKind.Body"/>.</param>
/// <param name="Grid">The grid of the block or of the chunk that is not loaded.</param>
/// <param name="Block">The block met, in the coordinates of its grid; for a chunk that is not loaded, a cell of that chunk.</param>
/// <param name="State">The state of the block; 0 otherwise.</param>
[HotPath]
public readonly record struct PhysicsHit(
    PhysicsHitKind Kind,
    float Distance,
    WorldPosition Point,
    Vector3 Normal,
    BodyHandle Body,
    Grid? Grid,
    BlockCoordinate Block,
    uint State)
{
    /// <summary>Gets whether the query met something.</summary>
    public bool IsHit => Kind != PhysicsHitKind.None;

    /// <summary>Gets the chunk of <see cref="Block"/>.</summary>
    public ChunkCoordinate Chunk => VoxelCoordinates.ToChunk(Block);
}

/// <summary>One ray of a batch of <see cref="IPhysicsSystem.RayCast(DimensionId, ReadOnlySpan{RayQuery}, Span{PhysicsHit})"/>.</summary>
/// <param name="Origin">The start of the ray.</param>
/// <param name="Direction">Its direction; it need not be of length 1.</param>
/// <param name="MaxDistance">Its length, in blocks.</param>
/// <param name="Filter">What it meets.</param>
[HotPath]
public readonly record struct RayQuery(WorldPosition Origin, Vector3 Direction, float MaxDistance, QueryFilter Filter = default);

/// <summary>One sweep of a batch of <see cref="IPhysicsSystem.Sweep(DimensionId, ReadOnlySpan{SweepQuery}, Span{PhysicsHit})"/>.</summary>
/// <param name="Shape">The shape it moves.</param>
/// <param name="Origin">Where the center of the shape starts.</param>
/// <param name="Rotation">The rotation of the shape, which does not change on the way.</param>
/// <param name="Direction">The direction it moves along; it need not be of length 1.</param>
/// <param name="MaxDistance">How far it moves, in blocks.</param>
/// <param name="Filter">What it meets.</param>
[HotPath]
public readonly record struct SweepQuery(QueryShape Shape, WorldPosition Origin, Quaternion Rotation, Vector3 Direction, float MaxDistance, QueryFilter Filter = default);
