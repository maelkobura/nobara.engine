// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;

namespace Nobara.Core.Mathematics;

/// <summary>
/// Where something is in the world: its position, its rotation, and its
/// scale. They apply in this order: scale, rotation, then translation to the
/// position.
/// </summary>
/// <remarks>
/// The position is a <see cref="WorldPosition"/>, so the transform keeps the
/// same precision everywhere in the world. The engine shares this type
/// between the modules that place things in the world, such as entities and
/// rendering.
/// </remarks>
/// <param name="Position">The position of the origin.</param>
/// <param name="Rotation">The rotation; a unit quaternion.</param>
/// <param name="Scale">The scale along each local axis.</param>
[HotPath]
public readonly record struct WorldTransform(WorldPosition Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>Gets the transform at the origin of the world, without rotation or scale.</summary>
    public static WorldTransform Identity => new(default, Quaternion.Identity, Vector3.One);

    /// <summary>Gets a transform at a position, without rotation or scale.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The transform.</returns>
    public static WorldTransform At(in WorldPosition position) => new(position, Quaternion.Identity, Vector3.One);

    /// <summary>Gets the transform between two others: positions and scales are interpolated linearly, rotations spherically.</summary>
    /// <param name="from">The transform at 0.</param>
    /// <param name="to">The transform at 1.</param>
    /// <param name="amount">The fraction of the way, from 0 to 1.</param>
    /// <returns>The transform.</returns>
    public static WorldTransform Interpolate(in WorldTransform from, in WorldTransform to, float amount) => new(
        WorldPosition.Lerp(from.Position, to.Position, amount),
        Quaternion.Slerp(from.Rotation, to.Rotation, amount),
        Vector3.Lerp(from.Scale, to.Scale, amount));

    /// <summary>
    /// Gets the transform of something placed relative to this one, such as a
    /// child entity relative to its parent: the local position is scaled,
    /// rotated, then moved to this position; the rotations and the scales
    /// combine.
    /// </summary>
    /// <param name="localPosition">The position in the frame of this transform.</param>
    /// <param name="localRotation">The rotation in the frame of this transform; a unit quaternion.</param>
    /// <param name="localScale">The scale in the frame of this transform.</param>
    /// <returns>The transform in the world.</returns>
    /// <remarks>
    /// Scales combine axis by axis. With a non-uniform scale and a rotation
    /// below it, the result is an approximation: a transform cannot hold the
    /// shear that the exact product would have.
    /// </remarks>
    public WorldTransform Combine(Vector3 localPosition, Quaternion localRotation, Vector3 localScale) => new(
        TransformPoint(localPosition),
        Quaternion.Normalize(Quaternion.Concatenate(localRotation, Rotation)),
        Scale * localScale);

    /// <summary>Gets the position in the world of a point given in the frame of this transform.</summary>
    /// <param name="local">The point, in the frame of this transform.</param>
    /// <returns>The position in the world.</returns>
    public WorldPosition TransformPoint(Vector3 local) => Position.Offset(Vector3.Transform(local * Scale, Rotation));

    /// <summary>Gets a position in the world as a point in the frame of this transform: the inverse of <see cref="TransformPoint"/>.</summary>
    /// <param name="position">The position in the world.</param>
    /// <returns>The point, in the frame of this transform.</returns>
    /// <remarks>An axis whose scale is 0 gives 0.</remarks>
    public Vector3 InverseTransformPoint(in WorldPosition position)
    {
        Vector3 rotated = Vector3.Transform(position.RelativeTo(Position), Quaternion.Conjugate(Rotation));
        return new Vector3(Divide(rotated.X, Scale.X), Divide(rotated.Y, Scale.Y), Divide(rotated.Z, Scale.Z));
    }

    /// <summary>Gets the matrix of the transform seen from an origin, such as the camera: the translation is the offset from the origin.</summary>
    /// <param name="origin">The origin.</param>
    /// <returns>The matrix, for row vectors as in <see cref="System.Numerics"/>.</returns>
    public Matrix4x4 ToMatrix(in WorldPosition origin)
    {
        Matrix4x4 matrix = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation);
        matrix.Translation = Position.RelativeTo(origin);
        return matrix;
    }

    private static float Divide(float value, float scale) => scale == 0 ? 0 : value / scale;
}
