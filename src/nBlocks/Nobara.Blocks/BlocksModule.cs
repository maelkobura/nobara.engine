// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Items;

[assembly: ModuleEntry(typeof(Nobara.Blocks.BlocksModule))]

namespace Nobara.Blocks;

/// <summary>
/// The module of the block registry: it defines the resource type
/// <c>Block</c>, keeps the types of blocks, their states and their tags, and
/// the block hooks. It publishes <see cref="IBlockRegistry"/> and
/// <see cref="IBlockHooks"/>, and registers the item component
/// <c>nobara.blocks.placeable</c> with nItems.
/// </summary>
public sealed class BlocksModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.blocks";

    /// <summary>The type prefix of the block identifiers: <c>block</c>.</summary>
    public const string ResourceType = "block";

    /// <summary>The configuration key of the most states that one type of block may have.</summary>
    public const string MaxStatesPerTypeKey = "blocks.maxStatesPerType";

    /// <summary>The type prefix of the identifiers of the items that blocks generate.</summary>
    internal const string ItemType = "item";

    private BlockRegistry? _registry;
    private BlockHooks? _hooks;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _registry = new BlockRegistry();
        _hooks = new BlockHooks(_registry);
        context.Resources.RegisterDecoder(ResourceType, BlockDefinition.Decode);
        context.Resources.RegisterReloadHandler(ResourceType, reload => Reload(context, reload));
        context.Publish<IBlockRegistry>(_registry);
        context.Publish<IBlockHooks>(_hooks);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The registry is built here, before the modules that depend on nBlocks
    /// initialize, so that they can read the types and subscribe to hooks.
    /// The item component is registered here too: nItems reads the items when
    /// it starts.
    /// </remarks>
    public void Initialize(ModuleContext context)
    {
        _registry!.RegisterPlaceable(context, context.Resolve<IItemRegistry>());
        _registry.Freeze(
            context.Resources.GetAll<BlockDefinition>(ResourceType),
            context.Resources.GetTags(ResourceType),
            context.Settings.GetInt(MaxStatesPerTypeKey));
        context.Logger.Info($"The block registry holds {_registry.Types.Count} types, {_registry.StateCount} states, and {_registry.Tags.Count} tags.");
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context) => _hooks!.Start(context);

    private void Reload(ModuleContext context, ResourceReload reload)
    {
        if (_registry!.Reload(reload, context.Resources.GetAll<BlockDefinition>(ResourceType), context.Resources.GetTags(ResourceType)) && _hooks!.IsStarted)
        {
            _hooks.Rebuild();
        }
    }
}
