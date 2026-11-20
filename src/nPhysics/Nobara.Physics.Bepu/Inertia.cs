// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using Nobara.Core;
using BepuInertia = BepuPhysics.BodyInertia;

namespace Nobara.Physics.Bepu;

/// <summary>
/// Inertia tensors of the shapes of the neutral API, about the origin of the
/// shape, for a uniform density; and the conversions between the tensors of
/// nPhysics and the inverse tensors of BEPUphysics.
/// </summary>
internal static class Inertia
{
    /// <summary>Computes the tensor of a shape other than a mesh, about its origin.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="mass">The mass, above 0.</param>
    /// <param name="shapes">Where the descriptions of the children of a compound are read.</param>
    /// <param name="pool">The pool that holds the temporary hulls.</param>
    /// <returns>The tensor.</returns>
    [HotPath]
    public static Symmetric3x3 Of(ShapeDescription shape, float mass, IPhysicsSystem shapes, BufferPool pool)
    {
        if (shape.Kind == ShapeKind.Compound)
        {
            ReadOnlySpan<CompoundChild> children = shape.Children.Span;
            float volume = 0;
            for (int i = 0; i < children.Length; i++)
            {
                volume += Convex(shapes.GetShape(children[i].Shape), 1f, pool, out _).Volume;
            }

            Symmetric3x3 sum = default;
            for (int i = 0; i < children.Length; i++)
            {
                CompoundChild child = children[i];
                ShapeDescription description = shapes.GetShape(child.Shape);
                float childVolume = Convex(description, 1f, pool, out _).Volume;
                float childMass = volume > 0 ? mass * childVolume / volume : mass / children.Length;
                Symmetric3x3 local = Convex(description, childMass, pool, out _).Tensor;
                Symmetric3x3 placed = Shift(Rotate(local, child.Rotation), childMass, child.Position);
                sum = Add(sum, placed);
            }

            return sum;
        }

        return Convex(shape, mass, pool, out _).Tensor;
    }

    /// <summary>Converts the tensor of nPhysics to the inertia of a dynamic body of BEPUphysics.</summary>
    /// <param name="mass">The mass, above 0.</param>
    /// <param name="inertia">The tensor.</param>
    /// <returns>The inverse mass and the inverse tensor.</returns>
    [HotPath]
    public static BepuInertia ToBepu(float mass, in BodyInertia inertia)
    {
        var tensor = new Symmetric3x3 { XX = inertia.XX, YX = inertia.YX, YY = inertia.YY, ZX = inertia.ZX, ZY = inertia.ZY, ZZ = inertia.ZZ };
        Symmetric3x3.Invert(tensor, out Symmetric3x3 inverse);
        return new BepuInertia { InverseMass = 1f / mass, InverseInertiaTensor = inverse };
    }

    /// <summary>Converts a tensor of BEPUphysics to the tensor of nPhysics.</summary>
    /// <param name="tensor">The tensor.</param>
    /// <returns>The same tensor.</returns>
    [HotPath]
    public static BodyInertia ToNeutral(in Symmetric3x3 tensor) => new(tensor.XX, tensor.YX, tensor.YY, tensor.ZX, tensor.ZY, tensor.ZZ);

    /// <summary>Copies the points of a hull into the pool and builds the hull, recentered on its centroid by BEPUphysics.</summary>
    /// <param name="points">The points.</param>
    /// <param name="pool">The pool.</param>
    /// <param name="center">The centroid, in the frame of the points.</param>
    /// <returns>The hull, to dispose into the pool.</returns>
    [HotPath]
    public static ConvexHull BuildHull(ReadOnlySpan<Vector3> points, BufferPool pool, out Vector3 center)
    {
        pool.Take(points.Length, out Buffer<Vector3> copy);
        try
        {
            Span<Vector3> span = copy;
            span = span[..points.Length];
            points.CopyTo(span);
            return new ConvexHull(span, pool, out center);
        }
        finally
        {
            pool.Return(ref copy);
        }
    }

    /// <summary>The tensor about the origin and the volume of a convex shape.</summary>
    [HotPath]
    private static (Symmetric3x3 Tensor, float Volume) Convex(ShapeDescription shape, float mass, BufferPool pool, out Vector3 center)
    {
        center = default;
        switch (shape.Kind)
        {
            case ShapeKind.Sphere:
                return (Tensor(new Sphere(shape.Radius).ComputeInertia(mass)), 4f / 3f * MathF.PI * shape.Radius * shape.Radius * shape.Radius);
            case ShapeKind.Capsule:
                return (Tensor(new Capsule(shape.Radius, shape.Length).ComputeInertia(mass)), MathF.PI * shape.Radius * shape.Radius * ((4f / 3f * shape.Radius) + shape.Length));
            case ShapeKind.Box:
                return (Tensor(new Box(shape.Size.X, shape.Size.Y, shape.Size.Z).ComputeInertia(mass)), shape.Size.X * shape.Size.Y * shape.Size.Z);
            case ShapeKind.Cylinder:
                return (Tensor(new Cylinder(shape.Radius, shape.Length).ComputeInertia(mass)), MathF.PI * shape.Radius * shape.Radius * shape.Length);
            case ShapeKind.ConvexHull:
                ConvexHull hull = BuildHull(shape.Points.Span, pool, out center);
                try
                {
                    var triangles = new ConvexHull.ConvexHullTriangleSource(hull);
                    MeshInertiaHelper.ComputeClosedInertia(ref triangles, mass, out float volume, out Symmetric3x3 tensor);
                    return (Shift(tensor, mass, center), volume);
                }
                finally
                {
                    hull.Dispose(pool);
                }

            default:
                throw new ArgumentException($"A {shape.Kind} has no inertia of its own here.", nameof(shape));
        }
    }

    [HotPath]
    private static Symmetric3x3 Tensor(in BepuInertia inertia)
    {
        Symmetric3x3.Invert(inertia.InverseInertiaTensor, out Symmetric3x3 tensor);
        return tensor;
    }

    /// <summary>Turns a tensor: R I Rᵀ, where the columns of R are the axes of the child in the frame of the compound.</summary>
    [HotPath]
    private static Symmetric3x3 Rotate(in Symmetric3x3 tensor, Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);
        Vector3 x = Vector3.Transform(Vector3.UnitX, rotation);
        Vector3 y = Vector3.Transform(Vector3.UnitY, rotation);
        Vector3 z = Vector3.Transform(Vector3.UnitZ, rotation);

        // I R^T, column by column: the tensor applied to each row of R.
        Vector3 rowX = new(x.X, y.X, z.X);
        Vector3 rowY = new(x.Y, y.Y, z.Y);
        Vector3 rowZ = new(x.Z, y.Z, z.Z);
        Vector3 ix = Apply(tensor, rowX);
        Vector3 iy = Apply(tensor, rowY);
        Vector3 iz = Apply(tensor, rowZ);
        return new Symmetric3x3
        {
            XX = Vector3.Dot(rowX, ix),
            YX = Vector3.Dot(rowY, ix),
            YY = Vector3.Dot(rowY, iy),
            ZX = Vector3.Dot(rowZ, ix),
            ZY = Vector3.Dot(rowZ, iy),
            ZZ = Vector3.Dot(rowZ, iz),
        };
    }

    [HotPath]
    private static Vector3 Apply(in Symmetric3x3 m, Vector3 v) => new(
        (m.XX * v.X) + (m.YX * v.Y) + (m.ZX * v.Z),
        (m.YX * v.X) + (m.YY * v.Y) + (m.ZY * v.Z),
        (m.ZX * v.X) + (m.ZY * v.Y) + (m.ZZ * v.Z));

    /// <summary>Moves a tensor from the center of mass to a point at an offset from it (parallel axis theorem).</summary>
    [HotPath]
    private static Symmetric3x3 Shift(in Symmetric3x3 tensor, float mass, Vector3 offset)
    {
        float square = offset.LengthSquared();
        return new Symmetric3x3
        {
            XX = tensor.XX + (mass * (square - (offset.X * offset.X))),
            YX = tensor.YX - (mass * offset.Y * offset.X),
            YY = tensor.YY + (mass * (square - (offset.Y * offset.Y))),
            ZX = tensor.ZX - (mass * offset.Z * offset.X),
            ZY = tensor.ZY - (mass * offset.Z * offset.Y),
            ZZ = tensor.ZZ + (mass * (square - (offset.Z * offset.Z))),
        };
    }

    [HotPath]
    private static Symmetric3x3 Add(in Symmetric3x3 a, in Symmetric3x3 b) => new()
    {
        XX = a.XX + b.XX,
        YX = a.YX + b.YX,
        YY = a.YY + b.YY,
        ZX = a.ZX + b.ZX,
        ZY = a.ZY + b.ZY,
        ZZ = a.ZZ + b.ZZ,
    };
}
