// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;

namespace Nobara.Core.Tests;

/// <summary>The shared transform: composition with a local transform, and its inverse, far from the origin.</summary>
public sealed class WorldTransformTests
{
    [Fact]
    public void A_local_transform_is_scaled_rotated_then_moved()
    {
        var parent = new WorldTransform(WorldPosition.FromBlocks(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), new Vector3(2));

        WorldTransform child = parent.Combine(new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), new Vector3(3));

        AssertNear(new Vector3(10, 0, -2), child.Position.RelativeTo(default));
        AssertNear(new Vector3(-1, 0, 0), Vector3.Transform(Vector3.UnitX, child.Rotation));
        Assert.Equal(new Vector3(6), child.Scale);
    }

    [Fact]
    public void The_inverse_gives_back_the_local_point_a_billion_blocks_from_the_origin()
    {
        var parent = new WorldTransform(WorldPosition.FromBlocks(1e9, -2e8, 5e8), Quaternion.Normalize(new Quaternion(0.3f, 0.5f, -0.2f, 0.8f)), new Vector3(1, 2, 0.5f));
        var local = new Vector3(3.25f, -1.5f, 7);

        WorldPosition world = parent.TransformPoint(local);

        AssertNear(local, parent.InverseTransformPoint(world));
    }

    [Fact]
    public void An_interpolated_transform_lies_between_the_two()
    {
        WorldTransform from = WorldTransform.At(WorldPosition.FromBlocks(0, 0, 0));
        var to = new WorldTransform(WorldPosition.FromBlocks(4, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1), new Vector3(3));

        WorldTransform half = WorldTransform.Interpolate(from, to, 0.5f);

        AssertNear(new Vector3(2, 0, 0), half.Position.RelativeTo(default));
        Assert.Equal(new Vector3(2), half.Scale);
        Assert.Equal(WorldTransform.Identity, WorldTransform.At(default));
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"Expected {expected}, got {actual}.");
}
