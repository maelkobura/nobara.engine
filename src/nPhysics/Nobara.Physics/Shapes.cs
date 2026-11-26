// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>The kinds of shapes of the neutral API.</summary>
public enum ShapeKind : byte
{
    /// <summary>A sphere around the center.</summary>
    Sphere,

    /// <summary>A capsule along Y: a cylinder with a half sphere at each end.</summary>
    Capsule,

    /// <summary>A box centered on the center.</summary>
    Box,

    /// <summary>A cylinder along Y.</summary>
    Cylinder,

    /// <summary>The convex hull of a set of points.</summary>
    ConvexHull,

    /// <summary>Convex shapes placed in the frame of the body.</summary>
    Compound,

    /// <summary>A mesh of triangles, for static bodies only.</summary>
    Mesh,

    /// <summary>The voxels of a grid, read where nVoxel keeps them, for the body of that grid only.</summary>
    Voxels,
}

/// <summary>
/// Identifies a shape created by <see cref="IPhysicsSystem"/>. Shapes are
/// shared: many bodies can use one.
/// </summary>
public readonly struct PhysicsShape : IEquatable<PhysicsShape>
{
    [HotPath]
    internal PhysicsShape(int index, int generation) => (Index, Generation) = (index, generation);

    /// <summary>Gets the slot of the shape.</summary>
    public int Index { get; }

    /// <summary>Gets the generation of the slot, from 1; 0 for the default handle, which names no shape.</summary>
    public int Generation { get; }

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name the same shape.</returns>
    [HotPath]
    public static bool operator ==(PhysicsShape left, PhysicsShape right) => left.Equals(right);

    /// <summary>Compares two handles.</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when they name different shapes.</returns>
    [HotPath]
    public static bool operator !=(PhysicsShape left, PhysicsShape right) => !left.Equals(right);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(PhysicsShape other) => Index == other.Index && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PhysicsShape other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <inheritdoc/>
    public override string ToString() => $"shape {Index}#{Generation}";
}

/// <summary>One child of a compound shape.</summary>
/// <param name="Shape">A convex shape: sphere, capsule, box, cylinder, or convex hull.</param>
/// <param name="Position">Where its center sits in the frame of the compound, in blocks.</param>
/// <param name="Rotation">Its rotation in the frame of the compound.</param>
public readonly record struct CompoundChild(PhysicsShape Shape, Vector3 Position, Quaternion Rotation);

/// <summary>
/// What a shape is, as nPhysics keeps it: a backend reads it to build its own
/// shape, once per physics world that needs it.
/// </summary>
/// <remarks>Dimensions are in blocks. The values a kind does not use are 0 or empty.</remarks>
public sealed class ShapeDescription
{
    internal ShapeDescription(ShapeKind kind) => Kind = kind;

    /// <summary>Gets whether the shape is a sphere, a capsule, a box, a cylinder, a convex hull, a compound, or a mesh.</summary>
    public ShapeKind Kind { get; }

    /// <summary>Gets the radius of a sphere, a capsule, or a cylinder.</summary>
    public float Radius { get; internal init; }

    /// <summary>Gets the length along Y of a capsule, without its ends, or of a cylinder.</summary>
    public float Length { get; internal init; }

    /// <summary>Gets the size of a box along each axis.</summary>
    public Vector3 Size { get; internal init; }

    /// <summary>Gets the points of a convex hull, or the vertices of a mesh.</summary>
    public ReadOnlyMemory<Vector3> Points { get; internal init; }

    /// <summary>Gets the indices of the vertices of a mesh, three per triangle.</summary>
    public ReadOnlyMemory<int> Triangles { get; internal init; }

    /// <summary>Gets the children of a compound.</summary>
    public ReadOnlyMemory<CompoundChild> Children { get; internal init; }

    /// <summary>Gets the grid whose voxels a shape of kind <see cref="ShapeKind.Voxels"/> is made of.</summary>
    public Grid? Grid { get; internal init; }

    /// <summary>Gets where the origin of a shape of kind <see cref="ShapeKind.Voxels"/> lies in the frame of its grid, in blocks: the center of its body.</summary>
    public Vector3 Origin { get; internal init; }

    /// <summary>Gets the cells of the grid of a shape of kind <see cref="ShapeKind.Voxels"/>, which a backend reads while a world steps.</summary>
    public VoxelGridCells? Cells { get; internal init; }

    /// <summary>Gets whether the shape is convex, so that a compound can hold it.</summary>
    public bool IsConvex => Kind is not (ShapeKind.Compound or ShapeKind.Mesh or ShapeKind.Voxels);
}
