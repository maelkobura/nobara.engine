// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Mathematics;
using Nobara.Entity;

namespace Nobara.Audio.Tests.Samples;

/// <summary>
/// An anchor over an entity of nEntity, as the guide "Play a sound" shows it:
/// a module that depends on nEntity and on nAudio keeps one per entity that
/// makes sounds.
/// </summary>
internal sealed class EntitySoundAnchor(EntityWorld world, EntityId entity, ComponentType<WorldTransform> transform) : ISoundAnchor
{
    public bool TryGetTransform(out WorldTransform value)
    {
        if (!world.IsAlive(entity))
        {
            value = default;
            return false;
        }

        value = world.Read(entity, transform);
        return true;
    }
}
