// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Physics;

/// <summary>
/// A query shape placed in a frame: its center, and the vectors that span it.
/// A sphere is a point and a radius, a capsule a segment and a radius, a box
/// a center and three half axes.
/// </summary>
[HotPath]
internal readonly struct PlacedShape
{
    public PlacedShape(in QueryShape shape, Vector3 center, Quaternion rotation)
    {
        Kind = shape.Kind;
        Center = center;
        Radius = shape.Radius;
        Quaternion turn = Quaternion.Normalize(rotation);
        Vector3 half = shape.Size / 2;
        Segment = shape.Kind == ShapeKind.Capsule ? Vector3.Transform(new Vector3(0, shape.Length / 2, 0), turn) : Vector3.Zero;
        AxisX = Vector3.Transform(new Vector3(half.X, 0, 0), turn);
        AxisY = Vector3.Transform(new Vector3(0, half.Y, 0), turn);
        AxisZ = Vector3.Transform(new Vector3(0, 0, half.Z), turn);
        Extent = shape.Kind switch
        {
            ShapeKind.Sphere => new Vector3(Radius),
            ShapeKind.Capsule => Vector3.Abs(Segment) + new Vector3(Radius),
            _ => Vector3.Abs(AxisX) + Vector3.Abs(AxisY) + Vector3.Abs(AxisZ),
        };
    }

    private PlacedShape(in PlacedShape shape, Vector3 center)
    {
        this = shape;
        Center = center;
    }

    public ShapeKind Kind { get; }

    public Vector3 Center { get; }

    public float Radius { get; }

    /// <summary>Gets half the axis of a capsule, from its center to the center of a half sphere.</summary>
    public Vector3 Segment { get; }

    /// <summary>Gets the half axes of a box.</summary>
    public Vector3 AxisX { get; }

    public Vector3 AxisY { get; }

    public Vector3 AxisZ { get; }

    /// <summary>Gets half the size of the box that holds the shape, along the axes of the frame.</summary>
    public Vector3 Extent { get; }

    /// <summary>Gets the shape moved by an offset.</summary>
    [HotPath]
    public PlacedShape Moved(Vector3 offset) => new(this, Center + offset);
}

/// <summary>
/// The geometry of the queries against the boxes of the voxels, in the frame
/// of a grid, where those boxes are aligned with the axes: rays by slabs,
/// sweeps of spheres and capsules by conservative advancement, sweeps of
/// boxes by separating axes over the motion, and overlaps.
/// </summary>
internal static class QueryGeometry
{
    /// <summary>The distance below which a moving shape touches a box.</summary>
    public const float Contact = 1e-4f;

    private const int MaxAdvances = 64;
    private const int SegmentSearches = 32;

    /// <summary>Clips a ray to a box: the distance at which it enters, and the normal of the face it enters by.</summary>
    /// <returns><see langword="true"/> when the ray meets the box between <paramref name="near"/> and <paramref name="far"/>.</returns>
    [HotPath]
    public static bool Ray(Vector3 origin, Vector3 direction, Vector3 minimum, Vector3 maximum, ref float near, float far, out Vector3 normal)
    {
        normal = Vector3.Zero;
        float enter = near;
        float exit = far;
        if (!Slab(origin.X, direction.X, minimum.X, maximum.X, Vector3.UnitX, ref enter, ref exit, ref normal)
            || !Slab(origin.Y, direction.Y, minimum.Y, maximum.Y, Vector3.UnitY, ref enter, ref exit, ref normal)
            || !Slab(origin.Z, direction.Z, minimum.Z, maximum.Z, Vector3.UnitZ, ref enter, ref exit, ref normal))
        {
            return false;
        }

        if (enter <= near)
        {
            normal = -direction;   // starts inside
        }

        near = enter;
        return true;
    }

    /// <summary>Finds when a shape moving along a direction first touches a box.</summary>
    /// <param name="shape">The shape at the start.</param>
    /// <param name="direction">The direction, of length 1.</param>
    /// <param name="distance">How far it moves.</param>
    /// <param name="minimum">The minimum corner of the box.</param>
    /// <param name="maximum">The maximum corner of the box.</param>
    /// <param name="at">The distance at which it touches; 0 when it starts inside.</param>
    /// <param name="normal">The normal of the box where it touches, toward the shape.</param>
    /// <param name="point">A point of the box where it touches.</param>
    /// <returns><see langword="true"/> when it touches the box on the way.</returns>
    [HotPath]
    public static bool Sweep(in PlacedShape shape, Vector3 direction, float distance, Vector3 minimum, Vector3 maximum, out float at, out Vector3 normal, out Vector3 point)
    {
        if (shape.Kind == ShapeKind.Box)
        {
            return SweepBox(shape, direction, distance, minimum, maximum, out at, out normal, out point);
        }

        // Quick rejection: the ray of the center against the box grown by the extent of the shape.
        float near = 0;
        if (!Ray(shape.Center, direction, minimum - shape.Extent, maximum + shape.Extent, ref near, distance, out _))
        {
            at = 0;
            normal = point = Vector3.Zero;
            return false;
        }

        float t = 0;
        for (int i = 0; i < MaxAdvances; i++)
        {
            float gap = Distance(shape.Moved(direction * t), minimum, maximum, out Vector3 closest, out Vector3 nearest) - shape.Radius;
            if (gap <= Contact)
            {
                at = t;
                point = nearest;
                Vector3 away = closest - nearest;
                normal = t == 0 && gap <= 0 ? -direction : (away.LengthSquared() > 1e-12f ? Vector3.Normalize(away) : -direction);
                return true;
            }

            t += gap;   // the shape cannot reach the box before it has moved by the gap
            if (t > distance)
            {
                break;
            }
        }

        at = 0;
        normal = point = Vector3.Zero;
        return false;
    }

    /// <summary>Tells whether a shape overlaps a box.</summary>
    [HotPath]
    public static bool Overlaps(in PlacedShape shape, Vector3 minimum, Vector3 maximum)
    {
        if (shape.Kind == ShapeKind.Box)
        {
            return SweepBox(shape, Vector3.UnitY, 0, minimum, maximum, out _, out _, out _);
        }

        return Distance(shape, minimum, maximum, out _, out _) <= shape.Radius;
    }

    /// <summary>Gets the distance from the core of a sphere or a capsule, a point or a segment, to a box.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="minimum">The minimum corner of the box.</param>
    /// <param name="maximum">The maximum corner of the box.</param>
    /// <param name="closest">The point of the core closest to the box.</param>
    /// <param name="nearest">The point of the box closest to the core.</param>
    [HotPath]
    private static float Distance(in PlacedShape shape, Vector3 minimum, Vector3 maximum, out Vector3 closest, out Vector3 nearest)
    {
        if (shape.Kind == ShapeKind.Sphere || shape.Segment == Vector3.Zero)
        {
            closest = shape.Center;
            nearest = Vector3.Clamp(closest, minimum, maximum);
            return Vector3.Distance(closest, nearest);
        }

        // The square of the distance to a box is convex along the segment: a golden section search finds its least.
        Vector3 start = shape.Center - shape.Segment;
        Vector3 span = shape.Segment * 2;
        const float ratio = 0.618034f;
        float low = 0;
        float high = 1;
        float a = high - (ratio * (high - low));
        float b = low + (ratio * (high - low));
        float fa = Squared(start + (span * a), minimum, maximum);
        float fb = Squared(start + (span * b), minimum, maximum);
        for (int i = 0; i < SegmentSearches; i++)
        {
            if (fa <= fb)
            {
                high = b;
                b = a;
                fb = fa;
                a = high - (ratio * (high - low));
                fa = Squared(start + (span * a), minimum, maximum);
            }
            else
            {
                low = a;
                a = b;
                fa = fb;
                b = low + (ratio * (high - low));
                fb = Squared(start + (span * b), minimum, maximum);
            }
        }

        float best = (low + high) / 2;
        float atStart = Squared(start, minimum, maximum);
        float atEnd = Squared(start + span, minimum, maximum);
        if (atStart < Squared(start + (span * best), minimum, maximum))
        {
            best = 0;
        }

        if (atEnd < Squared(start + (span * best), minimum, maximum))
        {
            best = 1;
        }

        closest = start + (span * best);
        nearest = Vector3.Clamp(closest, minimum, maximum);
        return Vector3.Distance(closest, nearest);
    }

    [HotPath]
    private static float Squared(Vector3 point, Vector3 minimum, Vector3 maximum) =>
        Vector3.DistanceSquared(point, Vector3.Clamp(point, minimum, maximum));

    /// <summary>
    /// Finds when a box moving along a direction first touches a box aligned
    /// with the axes, by the fifteen separating axes of two boxes: along each,
    /// the moving box overlaps the other during an interval of the motion;
    /// they touch when the intervals of all the axes meet.
    /// </summary>
    [HotPath]
    private static bool SweepBox(in PlacedShape shape, Vector3 direction, float distance, Vector3 minimum, Vector3 maximum, out float at, out Vector3 normal, out Vector3 point)
    {
        Vector3 center = (minimum + maximum) / 2;
        Vector3 half = (maximum - minimum) / 2;
        Vector3 offset = shape.Center - center;
        Vector3 velocity = direction * distance;
        float enter = float.NegativeInfinity;
        float exit = float.PositiveInfinity;
        Vector3 best = -direction;
        bool separated = false;
        Span<Vector3> edges = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Span<Vector3> axes = [shape.AxisX, shape.AxisY, shape.AxisZ];
        for (int i = 0; i < 3 && !separated; i++)
        {
            separated = !Axis(edges[i], shape, offset, half, velocity, ref enter, ref exit, ref best);
        }

        for (int i = 0; i < 3 && !separated; i++)
        {
            if (axes[i].LengthSquared() > 1e-12f)
            {
                separated = !Axis(Vector3.Normalize(axes[i]), shape, offset, half, velocity, ref enter, ref exit, ref best);
            }
        }

        for (int i = 0; i < 3 && !separated; i++)
        {
            for (int j = 0; j < 3 && !separated; j++)
            {
                Vector3 cross = Vector3.Cross(edges[i], axes[j]);
                if (cross.LengthSquared() > 1e-10f)
                {
                    separated = !Axis(Vector3.Normalize(cross), shape, offset, half, velocity, ref enter, ref exit, ref best);
                }
            }
        }

        if (separated || enter > exit || enter > 1 || exit < 0)
        {
            at = 0;
            normal = point = Vector3.Zero;
            return false;
        }

        float time = MathF.Max(enter, 0);
        at = time * distance;
        normal = enter <= 0 ? -direction : best;
        Vector3 moved = shape.Center + (velocity * time);
        Vector3 support = moved - Support(shape, normal);   // the point of the moving box deepest toward the other
        point = Vector3.Clamp(support, minimum, maximum);
        return true;
    }

    /// <summary>Narrows the interval of the motion during which the boxes overlap along an axis.</summary>
    /// <returns><see langword="false"/> when they never overlap along it.</returns>
    [HotPath]
    private static bool Axis(Vector3 axis, in PlacedShape shape, Vector3 offset, Vector3 half, Vector3 velocity, ref float enter, ref float exit, ref Vector3 best)
    {
        float reach = MathF.Abs(Vector3.Dot(shape.AxisX, axis)) + MathF.Abs(Vector3.Dot(shape.AxisY, axis)) + MathF.Abs(Vector3.Dot(shape.AxisZ, axis))
            + (half.X * MathF.Abs(axis.X)) + (half.Y * MathF.Abs(axis.Y)) + (half.Z * MathF.Abs(axis.Z));
        float gap = Vector3.Dot(offset, axis);
        float speed = Vector3.Dot(velocity, axis);
        if (MathF.Abs(speed) < 1e-9f)
        {
            return MathF.Abs(gap) <= reach;
        }

        float first = (-reach - gap) / speed;
        float last = (reach - gap) / speed;
        if (first > last)
        {
            (first, last) = (last, first);
        }

        if (first > enter)
        {
            enter = first;
            best = speed < 0 ? axis : -axis;   // from the box toward the moving shape
        }

        exit = MathF.Min(exit, last);
        return enter <= exit;
    }

    /// <summary>Gets the farthest point of a box from its center along a direction.</summary>
    [HotPath]
    private static Vector3 Support(in PlacedShape shape, Vector3 direction) =>
        (shape.AxisX * MathF.Sign(Vector3.Dot(shape.AxisX, direction))) + (shape.AxisY * MathF.Sign(Vector3.Dot(shape.AxisY, direction))) + (shape.AxisZ * MathF.Sign(Vector3.Dot(shape.AxisZ, direction)));

    [HotPath]
    private static bool Slab(float origin, float direction, float minimum, float maximum, Vector3 axis, ref float enter, ref float exit, ref Vector3 normal)
    {
        if (MathF.Abs(direction) < 1e-12f)
        {
            return origin >= minimum && origin <= maximum;
        }

        float a = (minimum - origin) / direction;
        float b = (maximum - origin) / direction;
        Vector3 face = -axis;
        if (a > b)
        {
            (a, b) = (b, a);
            face = axis;
        }

        if (a > enter)
        {
            enter = a;
            normal = face;
        }

        exit = MathF.Min(exit, b);
        return enter <= exit;
    }
}
