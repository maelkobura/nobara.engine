// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Nobara.Core.Logging;
using Silk.NET.Maths;
using Silk.NET.SDL;
using SdlEvent = Silk.NET.SDL.Event;
using SdlScancode = Silk.NET.SDL.Scancode;

namespace Nobara.Frames.SDL2;

/// <summary>
/// The window backend over SDL2, through the bindings of Silk.NET. SDL keeps
/// state for the whole process: one instance initializes its video subsystem
/// when nFrames first uses it, and shuts it down when disposed.
/// </summary>
internal sealed unsafe class Sdl2Backend : IWindowBackend
{
    private const uint InitVideo = 0x20;

    private readonly Logger _logger;
    private Sdl? _sdl;
    private Sdl2Window? _window;
    private bool _closeReported;

    public Sdl2Backend(Logger logger)
    {
        _logger = logger;
    }

    public string Name => "sdl2";

    internal Sdl Api => _sdl ?? throw new ObjectDisposedException(nameof(Sdl2Backend));

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        Sdl sdl = Initialize();
        int count = sdl.GetNumVideoDisplays();
        var monitors = new List<MonitorInfo>(Math.Max(count, 0));
        for (int index = 0; index < count; index++)
        {
            string name = Marshal.PtrToStringUTF8((nint)sdl.GetDisplayName(index)) ?? string.Empty;
            Rectangle<int> bounds;
            sdl.GetDisplayBounds(index, &bounds);
            DisplayMode desktop;
            sdl.GetDesktopDisplayMode(index, &desktop);
            var modes = new List<VideoMode>();
            int modeCount = sdl.GetNumDisplayModes(index);
            for (int m = 0; m < modeCount; m++)
            {
                DisplayMode mode;
                if (sdl.GetDisplayMode(index, m, &mode) == 0)
                {
                    var video = new VideoMode(mode.W, mode.H, mode.RefreshRate);
                    if (!modes.Contains(video))
                    {
                        modes.Add(video);
                    }
                }
            }

            float dpi;
            float scale = sdl.GetDisplayDPI(index, &dpi, null, null) == 0 && dpi > 0 ? dpi / 96f : 1f;
            monitors.Add(new MonitorInfo(
                index,
                name,
                new WindowRect(bounds.Origin.X, bounds.Origin.Y, bounds.Size.X, bounds.Size.Y),
                new VideoMode(desktop.W, desktop.H, desktop.RefreshRate),
                modes,
                scale));
        }

        return monitors;
    }

    public IBackendWindow CreateWindow(string title, WindowSize size)
    {
        Sdl sdl = Initialize();
        if (_window is not null)
        {
            throw new InvalidOperationException("The SDL2 backend creates a single window.");
        }

        _window = new Sdl2Window(this, title, size);
        return _window;
    }

    public TimeSpan PumpEvents(IWindowEventSink sink, TimeSpan wait)
    {
        Sdl sdl = Api;
        _closeReported = false;
        TimeSpan waited = TimeSpan.Zero;
        SdlEvent e;
        if (wait > TimeSpan.Zero)
        {
            long start = Stopwatch.GetTimestamp();
            int received = sdl.WaitEventTimeout(&e, (int)Math.Ceiling(wait.TotalMilliseconds));
            waited = Stopwatch.GetElapsedTime(start);
            if (received != 0)
            {
                Handle(sink, ref e);
            }
        }

        while (sdl.PollEvent(&e) != 0)
        {
            Handle(sink, ref e);
        }

        return waited;
    }

    public string GetKeyName(Scancode scancode)
    {
        Sdl sdl = Api;
        int key = sdl.GetKeyFromScancode((SdlScancode)(int)scancode);
        return Marshal.PtrToStringUTF8((nint)sdl.GetKeyName(key)) ?? string.Empty;
    }

    public void Dispose()
    {
        if (_sdl is null)
        {
            return;
        }

        _window?.Dispose();
        _window = null;
        _sdl.QuitSubSystem(InitVideo);
        _sdl.Quit();
        _sdl.Dispose();
        _sdl = null;
    }

    internal string Error() => Marshal.PtrToStringUTF8((nint)Api.GetError()) ?? "unknown error";

    internal void WindowDestroyed(Sdl2Window window)
    {
        if (_window == window)
        {
            _window = null;
        }
    }

    private Sdl Initialize()
    {
        if (_sdl is not null)
        {
            return _sdl;
        }

        Sdl sdl = Sdl.GetApi();
        if (sdl.WasInit(InitVideo) != 0)
        {
            throw new InvalidOperationException("The video subsystem of SDL is already initialized in this process; SDL keeps one state per process, so a single window backend may use it.");
        }

        // The core handles Ctrl+C; per-monitor scaling gives logical and pixel sizes.
        sdl.SetHint("SDL_NO_SIGNAL_HANDLERS", "1");
        sdl.SetHint("SDL_WINDOWS_DPI_AWARENESS", "permonitorv2");
        sdl.SetHint("SDL_WINDOWS_DPI_SCALING", "1");
        sdl.SetHint("SDL_IME_SHOW_UI", "1");
        if (sdl.Init(InitVideo) < 0)
        {
            string error = Marshal.PtrToStringUTF8((nint)sdl.GetError()) ?? "unknown error";
            sdl.Dispose();
            throw new InvalidOperationException($"SDL cannot initialize its video subsystem: {error}");
        }

        // SDL2 starts text input with its video subsystem; nFrames starts it on demand.
        sdl.StopTextInput();
        Silk.NET.SDL.Version version;
        sdl.GetVersion(&version);
        _logger.Info($"SDL {version.Major}.{version.Minor}.{version.Patch} initialized.");
        _sdl = sdl;
        return sdl;
    }

    private void Handle(IWindowEventSink sink, ref SdlEvent e)
    {
        switch ((EventType)e.Type)
        {
            case EventType.Quit:
                // Closing the last window also sends a quit event: report one request.
                if (!_closeReported)
                {
                    _closeReported = true;
                    sink.OnCloseRequested();
                }

                break;
            case EventType.Windowevent:
                HandleWindow(sink, (WindowEventID)e.Window.Event);
                break;
            case EventType.Displayevent:
                sink.OnWindowEvent(WindowEventKind.MonitorsChanged);
                break;
            case EventType.AppWillenterbackground:
                sink.OnWindowEvent(WindowEventKind.Suspending);
                break;
            case EventType.AppDidenterforeground:
                sink.OnWindowEvent(WindowEventKind.Resumed);
                break;
            case EventType.Keydown:
            case EventType.Keyup:
                sink.OnInput(
                    RawInputEvent.Key(
                        e.Type == (uint)EventType.Keydown,
                        (Scancode)(int)e.Key.Keysym.Scancode,
                        Modifiers(e.Key.Keysym.Mod),
                        e.Key.Repeat != 0),
                    []);
                break;
            case EventType.Keymapchanged:
                sink.OnKeyboardLayoutChanged();
                break;
            case EventType.Textinput:
                fixed (byte* text = e.Text.Text)
                {
                    SendText(sink, RawInputEvent.Text(), text);
                }

                break;
            case EventType.Textediting:
                fixed (byte* text = e.Edit.Text)
                {
                    SendText(sink, RawInputEvent.Composition(e.Edit.Start, e.Edit.Length), text);
                }

                break;
            case EventType.Mousemotion:
                sink.OnInput(RawInputEvent.MouseMotion(e.Motion.X, e.Motion.Y, e.Motion.Xrel, e.Motion.Yrel), []);
                break;
            case EventType.Mousebuttondown:
            case EventType.Mousebuttonup:
                sink.OnInput(
                    RawInputEvent.MouseButtonEvent(e.Type == (uint)EventType.Mousebuttondown, Button(e.Button.Button), e.Button.Clicks, e.Button.X, e.Button.Y),
                    []);
                break;
            case EventType.Mousewheel:
                // Direction 1 means the system flips the wheel: give the physical direction back.
                float flip = e.Wheel.Direction == 1 ? -1f : 1f;
                sink.OnInput(RawInputEvent.MouseWheel(e.Wheel.MouseX, e.Wheel.MouseY, e.Wheel.PreciseX * flip, e.Wheel.PreciseY * flip), []);
                break;
        }
    }

    private void HandleWindow(IWindowEventSink sink, WindowEventID id)
    {
        switch (id)
        {
            case WindowEventID.SizeChanged:
            case WindowEventID.Resized:
                sink.OnWindowEvent(WindowEventKind.Resized);
                break;
            case WindowEventID.Minimized:
                sink.OnWindowEvent(WindowEventKind.Minimized);
                break;
            case WindowEventID.Restored:
            case WindowEventID.Maximized:
            case WindowEventID.Shown:
                sink.OnWindowEvent(WindowEventKind.Restored);
                break;
            case WindowEventID.FocusGained:
                sink.OnWindowEvent(WindowEventKind.FocusGained);
                break;
            case WindowEventID.FocusLost:
                sink.OnWindowEvent(WindowEventKind.FocusLost);
                break;
            case WindowEventID.Moved:
            case WindowEventID.DisplayChanged:
                sink.OnWindowEvent(WindowEventKind.MonitorChanged);
                break;
            case WindowEventID.Close:
                if (!_closeReported)
                {
                    _closeReported = true;
                    sink.OnCloseRequested();
                }

                break;
        }
    }

    private static void SendText(IWindowEventSink sink, in RawInputEvent e, byte* utf8)
    {
        // SDL2 gives at most 32 bytes, ended by a zero.
        int length = 0;
        while (length < 32 && utf8[length] != 0)
        {
            length++;
        }

        Span<char> chars = stackalloc char[32];
        int count = Encoding.UTF8.GetChars(new ReadOnlySpan<byte>(utf8, length), chars);
        sink.OnInput(e, chars[..count]);
    }

    private static MouseButton Button(byte button) => button switch
    {
        1 => MouseButton.Left,
        2 => MouseButton.Middle,
        3 => MouseButton.Right,
        4 => MouseButton.X1,
        5 => MouseButton.X2,
        _ => MouseButton.None,
    };

    private static KeyModifiers Modifiers(ushort mod)
    {
        var keymod = (Keymod)mod;
        KeyModifiers result = KeyModifiers.None;
        if ((keymod & Keymod.Lshift) != 0)
        {
            result |= KeyModifiers.LeftShift;
        }

        if ((keymod & Keymod.Rshift) != 0)
        {
            result |= KeyModifiers.RightShift;
        }

        if ((keymod & Keymod.Lctrl) != 0)
        {
            result |= KeyModifiers.LeftControl;
        }

        if ((keymod & Keymod.Rctrl) != 0)
        {
            result |= KeyModifiers.RightControl;
        }

        if ((keymod & Keymod.Lalt) != 0)
        {
            result |= KeyModifiers.LeftAlt;
        }

        if ((keymod & Keymod.Ralt) != 0)
        {
            result |= KeyModifiers.RightAlt;
        }

        if ((keymod & Keymod.Lgui) != 0)
        {
            result |= KeyModifiers.LeftMeta;
        }

        if ((keymod & Keymod.Rgui) != 0)
        {
            result |= KeyModifiers.RightMeta;
        }

        if ((keymod & Keymod.Num) != 0)
        {
            result |= KeyModifiers.NumLock;
        }

        if ((keymod & Keymod.Caps) != 0)
        {
            result |= KeyModifiers.CapsLock;
        }

        if ((keymod & Keymod.Scroll) != 0)
        {
            result |= KeyModifiers.ScrollLock;
        }

        return result;
    }
}
