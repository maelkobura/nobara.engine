// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Audio;

/// <summary>The names of the configuration keys of nAudio.</summary>
public static class AudioSettings
{
    /// <summary>The name of the audio backend, or <see cref="AutomaticBackend"/>. Scope <c>user</c>; applies at the next start.</summary>
    public const string Backend = "audio.backend";

    /// <summary>The mode of HRTF: <c>auto</c>, <c>on</c>, or <c>off</c>. Scope <c>user</c>; applies at once.</summary>
    public const string Hrtf = "audio.hrtf";

    /// <summary>The most voices of the device used at once. Scope <c>distribution</c>; read at start.</summary>
    public const string RealVoices = "audio.realVoices";

    /// <summary>The most instances of an event whose <c>&lt;MaxInstances&gt;</c> is 0 or absent; 0 for no limit. Scope <c>distribution</c>; read at start.</summary>
    public const string MaxInstances = "audio.maxInstances";

    /// <summary>The value of <see cref="Backend"/> that takes the first backend registered.</summary>
    public const string AutomaticBackend = "auto";

    /// <summary>Reads a value of <see cref="Hrtf"/>.</summary>
    /// <param name="value">The value of the key.</param>
    /// <returns>The mode; <see cref="HrtfMode.Auto"/> for an unknown value.</returns>
    public static HrtfMode ParseHrtf(string value) => value.ToLowerInvariant() switch
    {
        "on" => HrtfMode.On,
        "off" => HrtfMode.Off,
        _ => HrtfMode.Auto,
    };
}
