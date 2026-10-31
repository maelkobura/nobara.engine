// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Configuration;
using Nobara.Core.Logging;

namespace Nobara.Frames;

/// <summary>The configuration keys of nFrames, all of <c>user</c> scope.</summary>
public static class FramesSettings
{
    /// <summary>The name of the window backend to use, such as <c>sdl2</c>; <c>auto</c>, the default, for the first one registered.</summary>
    public const string Backend = "frames.backend";

    /// <summary>The value of <see cref="Backend"/> that lets nFrames use the first backend registered.</summary>
    public const string AutomaticBackend = "auto";

    /// <summary>The mode of the window: <c>windowed</c>, <c>borderless</c>, or <c>exclusive</c>.</summary>
    public const string Mode = "frames.mode";

    /// <summary>The position of the monitor in <see cref="IWindowSystem.Monitors"/>; 0 is the primary monitor.</summary>
    public const string Monitor = "frames.monitor";

    /// <summary>The width of the video mode of exclusive full screen, in pixels; 0 for the width of the desktop.</summary>
    public const string FullscreenWidth = "frames.fullscreenWidth";

    /// <summary>The height of the video mode of exclusive full screen, in pixels; 0 for the height of the desktop.</summary>
    public const string FullscreenHeight = "frames.fullscreenHeight";

    /// <summary>The refresh rate of exclusive full screen, in hertz; 0 for the highest of the resolution.</summary>
    public const string FullscreenRefreshRate = "frames.fullscreenRefreshRate";

    /// <summary>The highest number of frames per second while the window has no focus or is minimized.</summary>
    public const string BackgroundFps = "frames.backgroundFps";

    /// <summary>Turns the keys into what the backend applies, with the monitors that exist.</summary>
    internal static DisplayRequest ReadDisplay(ModuleSettings settings, IReadOnlyList<MonitorInfo> monitors, Logger logger)
    {
        WindowMode mode = settings.GetEnum(Mode) switch
        {
            "borderless" => WindowMode.Borderless,
            "exclusive" => WindowMode.Exclusive,
            _ => WindowMode.Windowed,
        };

        int monitor = settings.GetInt(Monitor);
        if (monitor >= monitors.Count)
        {
            logger.Warning($"'{Monitor}' is {monitor}, but {monitors.Count} monitors are connected; the window uses the primary monitor.");
            monitor = 0;
        }

        VideoMode exclusive = default;
        if (mode == WindowMode.Exclusive)
        {
            exclusive = ChooseVideoMode(monitors[monitor], settings.GetInt(FullscreenWidth), settings.GetInt(FullscreenHeight), settings.GetFloat(FullscreenRefreshRate), logger);
        }

        return new DisplayRequest(mode, monitor, exclusive);
    }

    /// <summary>
    /// Chooses the video mode of a monitor nearest to the one asked for: the
    /// resolution of the desktop when the width or the height is 0; the same
    /// resolution, or the nearest in pixel count; then the refresh rate asked,
    /// or the nearest, or the highest when it is 0.
    /// </summary>
    internal static VideoMode ChooseVideoMode(MonitorInfo monitor, int width, int height, double refreshRate, Logger logger)
    {
        if (width == 0 || height == 0)
        {
            (width, height) = (monitor.DesktopMode.Width, monitor.DesktopMode.Height);
        }

        if (monitor.Modes.Count == 0)
        {
            return monitor.DesktopMode;
        }

        long wanted = (long)width * height;
        VideoMode bySize = monitor.Modes
            .OrderBy(m => m.Width == width && m.Height == height ? 0 : 1)
            .ThenBy(m => Math.Abs(((long)m.Width * m.Height) - wanted))
            .First();
        if (bySize.Width != width || bySize.Height != height)
        {
            logger.Warning($"Monitor {monitor.Index} has no video mode of {width}x{height}; the window uses {bySize.Width}x{bySize.Height}.");
        }

        IEnumerable<VideoMode> candidates = monitor.Modes.Where(m => m.Width == bySize.Width && m.Height == bySize.Height);
        return refreshRate <= 0
            ? candidates.OrderByDescending(m => m.RefreshRate).First()
            : candidates.OrderBy(m => Math.Abs(m.RefreshRate - refreshRate)).First();
    }
}
