// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>
/// Where an object of the render world is: its position, its rotation, and its
/// scale. The renderer applies them in this order: scale, rotation, then
/// translation to the position.
/// </summary>
/// <param name="Position">The position of the origin of the object.</param>
/// <param name="Rotation">The rotation; a unit quaternion.</param>
/// <param name="Scale">The scale along each axis of the object.</param>
[HotPath]
public readonly record struct RenderTransform(WorldPosition Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>Gets a transform at a position, without rotation or scale.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The transform.</returns>
    [HotPath]
    public static RenderTransform At(in WorldPosition position) => new(position, Quaternion.Identity, Vector3.One);

    /// <summary>Gets the transform between two others: positions and scales are interpolated linearly, rotations spherically.</summary>
    /// <param name="from">The transform at 0.</param>
    /// <param name="to">The transform at 1.</param>
    /// <param name="amount">The fraction of the way, from 0 to 1.</param>
    /// <returns>The transform.</returns>
    [HotPath]
    public static RenderTransform Interpolate(in RenderTransform from, in RenderTransform to, float amount) => new(
        WorldPosition.Lerp(from.Position, to.Position, amount),
        Quaternion.Slerp(from.Rotation, to.Rotation, amount),
        Vector3.Lerp(from.Scale, to.Scale, amount));

    /// <summary>Gets the matrix of the transform seen from an origin, such as the camera: the translation is the offset from the origin.</summary>
    /// <param name="origin">The origin.</param>
    /// <returns>The matrix, for row vectors as in <see cref="System.Numerics"/>.</returns>
    [HotPath]
    public Matrix4x4 ToMatrix(in WorldPosition origin)
    {
        Matrix4x4 matrix = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation);
        matrix.Translation = Position.RelativeTo(origin);
        return matrix;
    }
}

/// <summary>
/// A camera: where it is, where it looks, and its perspective projection. It
/// looks along its local -Z axis, with +Y up, as in glTF.
/// </summary>
/// <remarks>
/// The projection reverses depth: the near plane has depth 1 and the far
/// plane depth 0, which keeps the precision of a floating-point depth buffer
/// over long distances. Clear depth to <see cref="ClearDepth"/> and test with
/// <see cref="DepthCompare"/>. The view matrix puts the camera at the origin:
/// positions are made relative to <see cref="Position"/> on the CPU first.
/// </remarks>
public readonly record struct Camera()
{
    /// <summary>The depth to clear a depth buffer to: the farthest depth, 0.</summary>
    public const float ClearDepth = 0;

    /// <summary>Gets the depth test that keeps the nearest fragments with a reversed depth.</summary>
    public static CompareFunction DepthCompare => CompareFunction.GreaterEqual;

    /// <summary>Gets the position of the camera.</summary>
    public WorldPosition Position { get; init; }

    /// <summary>Gets the rotation of the camera; a unit quaternion.</summary>
    public Quaternion Rotation { get; init; } = Quaternion.Identity;

    /// <summary>Gets the vertical field of view, in radians; 70 degrees by default.</summary>
    public float VerticalFieldOfView { get; init; } = 70f * MathF.PI / 180f;

    /// <summary>Gets the distance of the near plane, in blocks; 0.05 by default.</summary>
    public float NearPlane { get; init; } = 0.05f;

    /// <summary>Gets the distance of the far plane, in blocks; infinite by default.</summary>
    public float FarPlane { get; init; } = float.PositiveInfinity;

    /// <summary>Gets the camera between two others: positions are interpolated linearly, rotations spherically, the projection is the one of <paramref name="to"/>.</summary>
    /// <param name="from">The camera at 0.</param>
    /// <param name="to">The camera at 1.</param>
    /// <param name="amount">The fraction of the way, from 0 to 1.</param>
    /// <returns>The camera.</returns>
    [HotPath]
    public static Camera Interpolate(in Camera from, in Camera to, float amount) => to with
    {
        Position = WorldPosition.Lerp(from.Position, to.Position, amount),
        Rotation = Quaternion.Slerp(from.Rotation, to.Rotation, amount),
    };

    /// <summary>Gets the view matrix: the inverse of the rotation of the camera, which stands at the origin.</summary>
    /// <returns>The matrix, for row vectors.</returns>
    [HotPath]
    public Matrix4x4 GetViewMatrix() => Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(Rotation));

    /// <summary>Gets the projection matrix, with reversed depth from 0 to 1.</summary>
    /// <param name="aspectRatio">The width divided by the height of the view.</param>
    /// <returns>The matrix, for row vectors.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The aspect ratio, the field of view, or a plane is not positive, or the far plane is not beyond the near one.</exception>
    [HotPath]
    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        if (!(aspectRatio > 0) || !(VerticalFieldOfView > 0 && VerticalFieldOfView < MathF.PI) || !(NearPlane > 0) || !(FarPlane > NearPlane))
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio), "A projection needs a positive aspect ratio, a field of view between 0 and pi, a positive near plane, and a far plane beyond it.");
        }

        float scale = 1f / MathF.Tan(VerticalFieldOfView / 2);

        // Depth is near / -z for an infinite far plane: 1 at the near plane, 0 at infinity.
        float a = float.IsPositiveInfinity(FarPlane) ? 0 : NearPlane / (FarPlane - NearPlane);
        float b = float.IsPositiveInfinity(FarPlane) ? NearPlane : NearPlane * FarPlane / (FarPlane - NearPlane);
        return new Matrix4x4(
            scale / aspectRatio, 0, 0, 0,
            0, scale, 0, 0,
            0, 0, a, -1,
            0, 0, b, 0);
    }
}
