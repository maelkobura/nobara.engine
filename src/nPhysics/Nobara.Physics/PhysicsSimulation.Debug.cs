// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;

namespace Nobara.Physics;

/// <summary>What the debug window draws: the bodies with their shapes, and the characters. Read on the loop thread, outside hot path code.</summary>
internal sealed partial class PhysicsSimulation
{
    /// <summary>Lists the bodies to draw, with their shape and their pose after the last step.</summary>
    internal IEnumerable<DrawnBody> DrawnBodies()
    {
        for (int i = 0; i < _bodyHigh; i++)
        {
            BodyRecord body = _bodies[i];
            if (body.Alive)
            {
                yield return new DrawnBody(Get(body.Shape), body.Position, body.Rotation, body.Kind, body.Asleep, body.World is null);
            }
        }
    }

    /// <summary>Lists the boxes of the characters to draw: their center, and half their size.</summary>
    internal IEnumerable<(WorldPosition Center, Vector3 Half)> DrawnCharacters()
    {
        for (int i = 0; i < _characterHigh; i++)
        {
            CharacterRecord record = _characters[i];
            if (!record.Alive || record.World is null || record.World.IsDestroyed || !record.World.IsAlive(record.Entity) || !record.World.Has(record.Entity, _characterComponent))
            {
                continue;
            }

            Vector3 size = record.World.Read(record.Entity, _characterComponent).Size;
            WorldPosition feet = record.World.Read(record.Entity, _transform).Position;
            yield return (feet.Offset(new Vector3(0, size.Y / 2, 0)), size / 2);
        }
    }

    /// <summary>Gets the box that holds a shape other than a compound, in its own frame: a mesh by its vertices.</summary>
    internal static void BoundsOf(ShapeDescription shape, out Vector3 minimum, out Vector3 maximum)
    {
        if (shape.Kind == ShapeKind.Mesh && shape.Points.Length > 0)
        {
            ReadOnlySpan<Vector3> points = shape.Points.Span;
            minimum = maximum = points[0];
            for (int i = 1; i < points.Length; i++)
            {
                minimum = Vector3.Min(minimum, points[i]);
                maximum = Vector3.Max(maximum, points[i]);
            }

            return;
        }

        Bounds(shape, out minimum, out maximum);
    }

    /// <summary>A body as the debug window draws it.</summary>
    internal readonly record struct DrawnBody(ShapeDescription Shape, WorldPosition Center, Quaternion Rotation, BodyKind Kind, bool Asleep, bool Frozen);
}
