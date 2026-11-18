// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Game;

[assembly: ModuleEntry(typeof(Nobara.Voxel.Sound.VoxelSoundModule))]

namespace Nobara.Voxel.Sound;

/// <summary>
/// The client extension of nVoxel that plays the sounds of blocks that are
/// placed and broken: it reads the properties <c>PlaceSound</c> and
/// <c>BreakSound</c> of every block and plays them, at the position of the
/// block, when the hooks <c>Placed</c> and <c>Broken</c> report it.
/// </summary>
public sealed class VoxelSoundModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.voxel.sound";

    private readonly List<BlockHookHandle> _subscriptions = [];
    private FreeProperty? _place;
    private FreeProperty? _break;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _place = context.Resources.DeclareProperty(BlockSounds.PlaceProperty, PropertyType.ObjectId);
        _break = context.Resources.DeclareProperty(BlockSounds.BreakProperty, PropertyType.ObjectId);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        IBlockRegistry blocks = context.Resolve<IBlockRegistry>();
        IBlockHooks hooks = context.Resolve<IBlockHooks>();
        BlockSounds sounds = BlockSounds.Build(context, blocks, context.Resolve<ISoundRegistry>(), _place!, _break!);
        var player = new BlockSoundPlayer(
            context.Resolve<IAudioSystem>(),
            context.Resolve<IVoxelWorlds>(),
            context.Resolve<IMultiBlockRegistry>(),
            context.Resolve<IGameTime>());
        var placed = new SoundHandler(player, sounds.Place, oldState: false);
        var broken = new SoundHandler(player, sounds.Break, oldState: true);

        // Only the types that have a sound subscribe, so that a batch of blocks without sound costs nothing.
        foreach (BlockType type in sounds.Types)
        {
            _subscriptions.Add(hooks.Subscribe(context, hooks.Placed, type, placed));
            _subscriptions.Add(hooks.Subscribe(context, hooks.Broken, type, broken));
        }

        context.Logger.Info($"{sounds.Types.Length} types of blocks have a sound when they are placed or broken.");
    }

    /// <inheritdoc/>
    public void Stop(ModuleContext context)
    {
        foreach (BlockHookHandle subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private sealed class SoundHandler(BlockSoundPlayer player, SoundEvent?[] sounds, bool oldState) : IBlockBatchHandler
    {
        public void Handle(BlockBatchView batch) => player.Play(batch, sounds, oldState);
    }
}
