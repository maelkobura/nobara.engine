// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Game;

[assembly: ModuleEntry(typeof(Nobara.Items.ItemsModule))]

namespace Nobara.Items;

/// <summary>
/// The module of the item registry: it defines the resource type
/// <c>Item</c>, keeps the item types and their tags, the types of item
/// components, and the usage service. It publishes
/// <see cref="IItemRegistry"/> and <see cref="IItemUsage"/>.
/// </summary>
public sealed class ItemsModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.items";

    /// <summary>The type prefix of the item identifiers: <c>item</c>.</summary>
    public const string ResourceType = "item";

    /// <summary>The configuration key of the default number of items in one stack.</summary>
    public const string DefaultMaxStackKey = "items.defaultMaxStack";

    /// <summary>The qualified name of the tick system that times the prolonged uses, in stage <see cref="TickStages.Schedule"/>.</summary>
    public const string ProlongedUseSystem = "nobara.items.prolonged_uses";

    private ItemRegistry? _registry;
    private ItemUsage? _usage;
    private IGameTime? _time;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _registry = new ItemRegistry();
        _usage = new ItemUsage(_registry);
        context.Resources.RegisterDecoder(ResourceType, ItemDefinition.Decode);
        context.Resources.RegisterReloadHandler(ResourceType, reload => Reload(context, reload));
        context.Publish<IItemRegistry>(_registry);
        context.Publish<IItemUsage>(_usage);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _time = context.Resolve<IGameTime>();
        _usage!.Time = _time;
        context.Resolve<ITickSchedule>().AddSystem(
            context,
            ProlongedUseSystem,
            TickStages.Schedule,
            new ProlongedUseTimer(_usage),
            new TickSystemOptions { After = [TickStages.SchedulerSystem] });
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        _registry!.Freeze(
            context.Resources.GetAll<ItemDefinition>(ResourceType),
            context.Resources.GetTags(ResourceType),
            context.Settings.GetInt(DefaultMaxStackKey),
            _time!.TickRate,
            context.Distribution.Modules,
            context.IsModuleActive);
        _usage!.Rebuild();
        context.Logger.Info($"The item registry holds {_registry.Items.Count} items, {_registry.Tags.Count} tags, and {_registry.ComponentTypes.Count} component types.");
    }

    private void Reload(ModuleContext context, ResourceReload reload)
    {
        _registry!.Reload(reload, context.Resources.GetAll<ItemDefinition>(ResourceType), context.Resources.GetTags(ResourceType), _time!.TickRate);
        _usage!.Rebuild();
    }

    /// <summary>The tick system that times the prolonged uses.</summary>
    private sealed class ProlongedUseTimer(ItemUsage usage) : ITickSystem
    {
        public void Run(in TickTime time) => usage.Advance();
    }
}
