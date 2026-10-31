// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Graphics;

/// <summary>The names of the configuration keys of nGraphics.</summary>
public static class GraphicsSettings
{
    /// <summary>The name of the graphics backend, or <see cref="AutomaticBackend"/>. Scope <c>user</c>; applies at the next start.</summary>
    public const string Backend = "graphics.backend";

    /// <summary>The synchronization with the monitor: <c>off</c>, <c>on</c>, or <c>adaptive</c>. Scope <c>user</c>; applies at the next frame.</summary>
    public const string VSync = "graphics.vsync";

    /// <summary>The number of frames in flight, from 1 to 3. Scope <c>distribution</c>; applies at start.</summary>
    public const string FramesInFlight = "graphics.framesInFlight";

    /// <summary>The value of <see cref="Backend"/> that takes the first backend registered.</summary>
    public const string AutomaticBackend = "auto";

    /// <summary>Reads a value of <see cref="VSync"/>.</summary>
    /// <param name="value">The value of the key.</param>
    /// <returns>The mode; <see cref="VSyncMode.On"/> for an unknown value.</returns>
    public static VSyncMode ParseVSync(string value) => value.ToLowerInvariant() switch
    {
        "off" => VSyncMode.Off,
        "adaptive" => VSyncMode.Adaptive,
        _ => VSyncMode.On,
    };
}
