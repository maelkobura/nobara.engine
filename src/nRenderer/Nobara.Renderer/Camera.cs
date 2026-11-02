// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Graphics;

namespace Nobara.Renderer;

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
