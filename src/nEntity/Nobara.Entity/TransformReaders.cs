// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Numerics;
using Nobara.Core.Mathematics;

namespace Nobara.Entity;

/// <summary>
/// How entity definitions write the components of the entity module:
/// <c>&lt;Position&gt;</c>, <c>&lt;Rotation&gt;</c>, and <c>&lt;Scale&gt;</c>
/// as vectors; the parent link is never written.
/// </summary>
internal static class TransformReaders
{
    /// <summary>Reads <c>nobara.entity.transform</c>: a position in blocks, a quaternion, and a scale.</summary>
    public static WorldTransform World(DefinitionValues values, WorldTransform fallback)
    {
        WorldPosition position = fallback.Position;
        if (values.GetVectorOrNull("Position") is { } blocks)
        {
            Vector3 at = Vector3Of(values, "Position", blocks);
            position = WorldPosition.FromBlocks(at.X, at.Y, at.Z);
        }

        return new WorldTransform(position, Rotation(values, fallback.Rotation), Vector(values, "Scale", fallback.Scale));
    }

    /// <summary>Reads <c>nobara.entity.local_transform</c>: a position, a quaternion, and a scale in the frame of the parent.</summary>
    public static LocalTransform Local(DefinitionValues values, LocalTransform fallback) =>
        new(Vector(values, "Position", fallback.Position), Rotation(values, fallback.Rotation), Vector(values, "Scale", fallback.Scale));

    /// <summary>Refuses <c>nobara.entity.parent</c>, which only <see cref="EntityWorld.SetParent"/> sets.</summary>
    public static ParentLink Parent(DefinitionValues values, ParentLink fallback) =>
        throw values.Error("the parent link is not written in a definition: set the parent of an entity with SetParent once it exists.");

    private static Vector3 Vector(DefinitionValues values, string name, Vector3 fallback) =>
        values.GetVectorOrNull(name) is { } vector ? Vector3Of(values, name, vector) : fallback;

    private static Vector3 Vector3Of(DefinitionValues values, string name, IReadOnlyList<double> vector) =>
        vector.Count == 3
            ? new Vector3((float)vector[0], (float)vector[1], (float)vector[2])
            : throw values.Error($"<{name}> has {vector.Count} components, expected <X>, <Y>, and <Z>.", values.GetElement(name));

    private static Quaternion Rotation(DefinitionValues values, Quaternion fallback)
    {
        if (values.GetVectorOrNull("Rotation") is not { } vector)
        {
            return fallback;
        }

        var rotation = vector.Count == 4
            ? new Quaternion((float)vector[0], (float)vector[1], (float)vector[2], (float)vector[3])
            : throw values.Error($"<Rotation> has {vector.Count} components, expected a quaternion: <X>, <Y>, <Z>, and <W>.", values.GetElement("Rotation"));
        float length = rotation.Length();
        return length > 1e-6f
            ? Quaternion.Normalize(rotation)
            : throw values.Error(string.Create(CultureInfo.InvariantCulture, $"<Rotation> has a length of {length}; a rotation is a quaternion of length 1, such as <X>0</X><Y>0</Y><Z>0</Z><W>1</W>."), values.GetElement("Rotation"));
    }
}
