// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

[assembly: ModuleEntry(typeof(Nobara.Audio.OpenAL.OpenALModule))]

namespace Nobara.Audio.OpenAL;

/// <summary>The module of the OpenAL Soft backend: it registers the backend <c>openal</c> with nAudio.</summary>
public sealed class OpenALModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.audio.openal";

    /// <inheritdoc/>
    public void Initialize(ModuleContext context) =>
        context.Resolve<IAudioBackends>().Register(context, new OpenALBackend());
}

/// <summary>The OpenAL Soft backend: the default output device, real voices as sources, and EFX for filters and reverberation.</summary>
/// <param name="loopback">Whether to open the loopback device of OpenAL Soft instead of the default output: it plays nothing, and the tests render it on demand.</param>
/// <param name="library">The full path of the native library, for the tests; by default, the library in the folder of the bindings, otherwise the one Silk.NET finds.</param>
internal sealed class OpenALBackend(bool loopback = false, string? library = null) : IAudioBackend
{
    public string Name => "openal";

    /// <summary>Gets the device opened last.</summary>
    public ALDevice? Opened { get; private set; }

    public AudioDevice Open(in AudioDeviceOptions options) => Opened = ALDevice.Open(options, loopback, library ?? OpenALLibrary.Find());
}
