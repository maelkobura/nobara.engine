// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Resources;

[assembly: ModuleEntry(typeof(Nobara.Audio.AudioModule))]

namespace Nobara.Audio;

/// <summary>
/// The module of audio data: it defines the resource types <c>Sound</c> and
/// <c>Bus</c>, provides the default tree of buses, numbers the sound events,
/// and, on a client, decodes them with NVorbis in background jobs. It
/// publishes <see cref="ISoundRegistry"/>.
/// </summary>
public sealed class AudioModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.audio";

    /// <summary>The type prefix of the sound identifiers: <c>sound</c>.</summary>
    public const string SoundType = AudioRules.SoundType;

    /// <summary>The type prefix of the bus identifiers: <c>bus</c>.</summary>
    public const string BusType = AudioRules.BusType;

    /// <summary>The identifier of the master bus, the root of the tree.</summary>
    public const string MasterBus = AudioRules.MasterBus;

    private SoundRegistry? _registry;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _registry = new SoundRegistry(context.Jobs, context.Logger, decodes: context.Side == Side.Client);
        ModuleResources resources = context.Resources;
        resources.RegisterDecoder(SoundType, data => SoundDefinition.Decode(resources, data));
        resources.RegisterDecoder(BusType, BusDefinition.Decode);
        resources.RegisterReloadHandler(SoundType, reload => _registry.ReloadSounds(reload, resources.GetAll<SoundDefinition>(SoundType)));
        resources.RegisterReloadHandler(BusType, RefuseBuses);
        context.Publish<ISoundRegistry>(_registry);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The registry is built here, before the modules that depend on nAudio
    /// initialize. On a client, the sounds that do not stream start decoding
    /// in background jobs; nothing waits for them.
    /// </remarks>
    public void Initialize(ModuleContext context)
    {
        _registry!.Freeze(context.Resources.GetAll<BusDefinition>(BusType), context.Resources.GetAll<SoundDefinition>(SoundType));
        context.Logger.Info($"The sound registry holds {_registry.Sounds.Count} sounds and {_registry.Buses.Count} buses.");
    }

    private static void RefuseBuses(ResourceReload reload)
    {
        const string Reason = "the tree of buses takes effect at the next start";
        foreach (ResourceData data in reload.Added.Concat(reload.Changed))
        {
            reload.Refuse(data.ObjectId, Reason);
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, Reason);
        }
    }
}
