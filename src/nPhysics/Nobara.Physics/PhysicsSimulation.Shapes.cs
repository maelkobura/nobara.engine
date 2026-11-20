// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Physics;

/// <summary>The shapes: created once, shared by bodies and compounds, counted by their users.</summary>
internal sealed partial class PhysicsSimulation
{
    public PhysicsShape CreateSphere(float radius)
    {
        RequirePositive(radius, nameof(radius));
        return AddShape(new ShapeDescription(ShapeKind.Sphere) { Radius = radius });
    }

    public PhysicsShape CreateCapsule(float radius, float length)
    {
        RequirePositive(radius, nameof(radius));
        if (!float.IsFinite(length) || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length of a capsule is finite and not negative.");
        }

        return AddShape(new ShapeDescription(ShapeKind.Capsule) { Radius = radius, Length = length });
    }

    public PhysicsShape CreateBox(Vector3 size)
    {
        RequirePositive(size.X, nameof(size));
        RequirePositive(size.Y, nameof(size));
        RequirePositive(size.Z, nameof(size));
        return AddShape(new ShapeDescription(ShapeKind.Box) { Size = size });
    }

    public PhysicsShape CreateCylinder(float radius, float length)
    {
        RequirePositive(radius, nameof(radius));
        RequirePositive(length, nameof(length));
        return AddShape(new ShapeDescription(ShapeKind.Cylinder) { Radius = radius, Length = length });
    }

    public PhysicsShape CreateConvexHull(ReadOnlySpan<Vector3> points)
    {
        if (points.Length < 4)
        {
            throw new ArgumentException("A convex hull needs at least four points.", nameof(points));
        }

        RequireFinite(points, nameof(points));
        if (IsFlat(points))
        {
            throw new ArgumentException("The points of a convex hull lie in one plane: it would have no volume.", nameof(points));
        }

        return AddShape(new ShapeDescription(ShapeKind.ConvexHull) { Points = points.ToArray() });
    }

    public PhysicsShape CreateCompound(ReadOnlySpan<CompoundChild> children)
    {
        if (children.IsEmpty)
        {
            throw new ArgumentException("A compound needs at least one child.", nameof(children));
        }

        foreach (CompoundChild child in children)
        {
            ShapeDescription description = GetShape(child.Shape);
            if (!description.IsConvex)
            {
                throw new ArgumentException($"A compound holds convex shapes only; {child.Shape} is a {description.Kind}.", nameof(children));
            }

            if (!IsFinite(child.Position) || !IsFinite(child.Rotation) || child.Rotation.LengthSquared() < 1e-6f)
            {
                throw new ArgumentException($"The place of {child.Shape} in the compound is not a finite position and rotation.", nameof(children));
            }
        }

        CompoundChild[] copy = new CompoundChild[children.Length];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = children[i] with { Rotation = Quaternion.Normalize(children[i].Rotation) };
            AcquireShape(copy[i].Shape);
        }

        return AddShape(new ShapeDescription(ShapeKind.Compound) { Children = copy });
    }

    public PhysicsShape CreateMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> triangles)
    {
        if (triangles.IsEmpty || triangles.Length % 3 != 0)
        {
            throw new ArgumentException("A mesh needs at least one triangle, three indices each.", nameof(triangles));
        }

        RequireFinite(vertices, nameof(vertices));
        foreach (int index in triangles)
        {
            if ((uint)index >= (uint)vertices.Length)
            {
                throw new ArgumentException($"The index {index} names no vertex: the mesh has {vertices.Length}.", nameof(triangles));
            }
        }

        return AddShape(new ShapeDescription(ShapeKind.Mesh) { Points = vertices.ToArray(), Triangles = triangles.ToArray() });
    }

    public ShapeDescription GetShape(PhysicsShape shape) =>
        IsShapeAlive(shape) ? _shapes[shape.Index].Description! : throw new ArgumentException($"The {shape} names no shape.", nameof(shape));

    /// <inheritdoc/>
    [HotPath]
    public ShapeDescription Get(PhysicsShape shape) =>
        IsShapeAlive(shape) ? _shapes[shape.Index].Description! : throw new ArgumentException($"The {shape} names no shape.", nameof(shape));

    public void DestroyShape(PhysicsShape shape)
    {
        ShapeDescription description = GetShape(shape);
        ref ShapeSlot slot = ref _shapes[shape.Index];
        if (slot.Users > 0)
        {
            throw new InvalidOperationException($"The {shape} is used by {slot.Users} bodies or compounds: remove them first.");
        }

        foreach (CompoundChild child in description.Children.Span)
        {
            ReleaseShape(child.Shape);
        }

        slot.Description = null;
        slot.Generation++;
        slot.NextFree = _shapeFree;
        _shapeFree = shape.Index;
        _shapeCount--;
    }

    [HotPath]
    private bool IsShapeAlive(PhysicsShape shape) =>
        shape.Generation != 0 && (uint)shape.Index < (uint)_shapeHigh && _shapes[shape.Index].Generation == shape.Generation && _shapes[shape.Index].Description is not null;

    [HotPath]
    private void AcquireShape(PhysicsShape shape) => _shapes[shape.Index].Users++;

    [HotPath]
    private void ReleaseShape(PhysicsShape shape)
    {
        if (IsShapeAlive(shape))
        {
            _shapes[shape.Index].Users--;
        }
    }

    private PhysicsShape AddShape(ShapeDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int index;
        if (_shapeFree >= 0)
        {
            index = _shapeFree;
            _shapeFree = _shapes[index].NextFree;
        }
        else
        {
            if (_shapeHigh == _shapes.Length)
            {
                Array.Resize(ref _shapes, _shapes.Length * 2);
            }

            index = _shapeHigh++;
            _shapes[index].Generation = 1;
        }

        ref ShapeSlot slot = ref _shapes[index];
        slot.Description = description;
        slot.Radius = RadiusOf(description);
        slot.Users = 0;
        slot.NextFree = -1;
        _shapeCount++;
        return new PhysicsShape(index, slot.Generation);
    }

    /// <summary>Gets the radius of the sphere around the origin of a shape that holds it.</summary>
    private float RadiusOf(ShapeDescription description)
    {
        switch (description.Kind)
        {
            case ShapeKind.Sphere:
                return description.Radius;
            case ShapeKind.Capsule:
                return description.Radius + (description.Length / 2);
            case ShapeKind.Box:
                return description.Size.Length() / 2;
            case ShapeKind.Cylinder:
                return MathF.Sqrt((description.Radius * description.Radius) + (description.Length * description.Length / 4));
            case ShapeKind.Compound:
                float compound = 0;
                foreach (CompoundChild child in description.Children.Span)
                {
                    compound = MathF.Max(compound, child.Position.Length() + _shapes[child.Shape.Index].Radius);
                }

                return compound;
            default:
                float points = 0;
                foreach (Vector3 point in description.Points.Span)
                {
                    points = MathF.Max(points, point.Length());
                }

                return points;
        }
    }

    private static bool IsFlat(ReadOnlySpan<Vector3> points)
    {
        Vector3 origin = points[0];
        int second = -1;
        for (int i = 1; i < points.Length && second < 0; i++)
        {
            if (Vector3.DistanceSquared(points[i], origin) > 1e-12f)
            {
                second = i;
            }
        }

        if (second < 0)
        {
            return true;
        }

        Vector3 normal = Vector3.Zero;
        for (int i = second + 1; i < points.Length && normal.LengthSquared() < 1e-12f; i++)
        {
            normal = Vector3.Cross(points[second] - origin, points[i] - origin);
        }

        if (normal.LengthSquared() < 1e-12f)
        {
            return true;
        }

        foreach (Vector3 point in points)
        {
            if (MathF.Abs(Vector3.Dot(point - origin, normal)) > 1e-6f * normal.Length())
            {
                return false;
            }
        }

        return true;
    }

    private static void RequirePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value is finite and above 0.");
        }
    }

    private static void RequireFinite(ReadOnlySpan<Vector3> points, string name)
    {
        foreach (Vector3 point in points)
        {
            if (!IsFinite(point))
            {
                throw new ArgumentException($"The point {point} is not finite.", name);
            }
        }
    }

    [HotPath]
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    [HotPath]
    private static bool IsFinite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private struct ShapeSlot
    {
        public ShapeDescription? Description;

        /// <summary>The radius of the sphere around the origin of the shape that holds it, for the ghosts.</summary>
        public float Radius;
        public int Generation;
        public int Users;
        public int NextFree;
    }
}
