// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Frames;

/// <summary>What happened to the window, or to the application on platforms that suspend it.</summary>
public enum WindowEventKind
{
    /// <summary>The size of the window changed, in logical units or in pixels: <see cref="IWindow.Size"/> and <see cref="IWindow.PixelSize"/> hold the new sizes.</summary>
    Resized,

    /// <summary>The number of pixels per logical unit changed, such as after a move to a monitor with another scaling: <see cref="IWindow.Scale"/>.</summary>
    ScaleChanged,

    /// <summary>The window got the keyboard focus.</summary>
    FocusGained,

    /// <summary>The window lost the keyboard focus, because the player chose another window or the operating system took it.</summary>
    FocusLost,

    /// <summary>The window was minimized: render nothing until <see cref="Restored"/>.</summary>
    Minimized,

    /// <summary>The window is visible again after <see cref="Minimized"/>.</summary>
    Restored,

    /// <summary>Most of the window moved to another monitor: <see cref="IWindow.Monitor"/>.</summary>
    MonitorChanged,

    /// <summary>The window changed mode, monitor, or video mode, after a change of the configuration: <see cref="IWindow.Mode"/>, <see cref="IWindow.ExclusiveMode"/>.</summary>
    ModeChanged,

    /// <summary>A monitor was connected, disconnected, or changed: <see cref="IWindowSystem.Monitors"/> is up to date.</summary>
    MonitorsChanged,

    /// <summary>The operating system is about to suspend the application, on a mobile platform or a console. Save what must be saved.</summary>
    Suspending,

    /// <summary>The application runs again after <see cref="Suspending"/>.</summary>
    Resumed,

    /// <summary>The surface of the window was taken away: the graphics backend frees what depends on it until <see cref="SurfaceRecreated"/>.</summary>
    SurfaceLost,

    /// <summary>The surface of the window is back: the graphics backend creates again what depends on it, as after a resize.</summary>
    SurfaceRecreated,
}

/// <summary>An event of the window.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Window">The window, already up to date.</param>
public readonly record struct WindowEvent(WindowEventKind Kind, IWindow Window);

/// <summary>
/// A request to close the window, from the player or from the operating
/// system. A handler cancels it to ask the player first, such as "quit without
/// saving?"; otherwise the game stops.
/// </summary>
public sealed class CloseRequest
{
    internal CloseRequest(IWindow window)
    {
        Window = window;
    }

    /// <summary>Gets the window that the player wants to close.</summary>
    public IWindow Window { get; }

    /// <summary>Gets whether a handler cancelled the request.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>Keeps the window open and the game running. The handler that cancels stops the game later, if the player confirms.</summary>
    public void Cancel() => IsCancelled = true;
}

/// <summary>A handler subscribed to the events of the window. Disposing it removes the handler at once.</summary>
public sealed class WindowSubscription : IDisposable
{
    private readonly Action<WindowSubscription> _remove;

    internal WindowSubscription(Action<WindowSubscription> remove)
    {
        _remove = remove;
    }

    /// <summary>Gets whether the handler still receives events.</summary>
    public bool IsActive => !Revoked;

    internal bool Revoked { get; private set; }

    /// <summary>Removes the handler.</summary>
    public void Dispose()
    {
        if (!Revoked)
        {
            Revoked = true;
            _remove(this);
        }
    }
}

/// <summary>
/// Emitted on the event bus when the window gains or loses the keyboard focus,
/// for a game that pauses when the player leaves it.
/// </summary>
/// <param name="HasFocus"><see langword="true"/> when the window got the focus.</param>
public readonly record struct WindowFocusChanged(bool HasFocus);

/// <summary>
/// Emitted on the event bus when a close of the window was requested and no
/// handler of <see cref="IWindowSystem.SubscribeCloseRequested"/> cancelled
/// it, just before nFrames hands the stop request to the context.
/// </summary>
/// <remarks>
/// It only informs: to keep the window open, cancel the
/// <see cref="CloseRequest"/> through <see cref="IWindowSystem"/>.
/// </remarks>
public readonly record struct WindowCloseRequested;
