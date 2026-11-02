// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context;
using Nobara.Core;
using Nobara.Game;

[assembly: ModuleEntry(typeof(Nobara.Entity.EntityModule))]

namespace Nobara.Entity;

/// <summary>
/// The module of the entity worlds: one ECS world per dimension, component
/// types, entity systems run as tick systems of nGame, change detection, and
/// the hierarchy of entities. It publishes <see cref="IEntityWorlds"/>.
/// </summary>
public sealed class EntityModule : IModule
{
    private EntityWorlds? _worlds;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _worlds = new EntityWorlds(context);
        context.Publish<IEntityWorlds>(_worlds);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _worlds!.Initialize(context.Resolve<ITickSchedule>(), context.Resolve<IGameTime>(), context.Resolve<IWorldSessions>());
        _worlds.AddSystem(context, TransformSystem.Name, new TransformSystem(_worlds), new EntitySystemOptions
        {
            Stage = TickStages.Late,
            Query = new QueryDescription { All = [_worlds.ParentLink, _worlds.LocalTransform, _worlds.WorldTransform] },
            Reads = [_worlds.ParentLink, _worlds.LocalTransform],
            Writes = [_worlds.WorldTransform],
        });
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context) => _worlds?.Dispose();
}
