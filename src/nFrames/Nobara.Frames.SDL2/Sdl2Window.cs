// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Silk.NET.Maths;
using Silk.NET.SDL;
using SdlWindow = Silk.NET.SDL.Window;

namespace Nobara.Frames.SDL2;

/// <summary>The window of the SDL2 backend, created with OpenGL support.</summary>
internal sealed unsafe class Sdl2Window : IBackendWindow, IOpenGLSurface
{
    // SDL_WINDOWPOS_CENTERED_DISPLAY(index) is this mask with the index of the display.
    private const int CenteredOnDisplay = 0x2FFF0000;

    private readonly Sdl2Backend _backend;
    private readonly Dictionary<CursorShape, nint> _cursors = [];
    private SdlWindow* _window;

    public Sdl2Window(Sdl2Backend backend, string title, WindowSize size)
    {
        _backend = backend;
        Sdl sdl = backend.Api;

        // The pixel format of a window is fixed when it is created, before any context.
        sdl.GLSetAttribute(GLattr.RedSize, 8);
        sdl.GLSetAttribute(GLattr.GreenSize, 8);
        sdl.GLSetAttribute(GLattr.BlueSize, 8);
        sdl.GLSetAttribute(GLattr.AlphaSize, 8);
        sdl.GLSetAttribute(GLattr.DepthSize, 24);
        sdl.GLSetAttribute(GLattr.StencilSize, 8);
        sdl.GLSetAttribute(GLattr.Doublebuffer, 1);
        sdl.GLSetAttribute(GLattr.Multisamplebuffers, 0);

        uint flags = (uint)(WindowFlags.Opengl | WindowFlags.Hidden | WindowFlags.Resizable | WindowFlags.AllowHighdpi);
        _window = sdl.CreateWindow(title, CenteredOnDisplay, CenteredOnDisplay, size.Width, size.Height, flags);
        if (_window is null)
        {
            throw new InvalidOperationException($"SDL cannot create the window: {backend.Error()}");
        }

        SysWMInfo info = default;
        sdl.GetVersion(&info.Version);
        Surface = sdl.GetWindowWMInfo(_window, &info) && info.Subsystem == SysWMType.Windows
            ? new NativeSurface(NativeSurfaceKind.Win32, info.Info.Win.Hwnd, info.Info.Win.HInstance)
            : new NativeSurface(NativeSurfaceKind.None, 0, 0);
    }

    public WindowSize Size
    {
        get
        {
            int width, height;
            Api.GetWindowSize(Handle, &width, &height);
            return new WindowSize(width, height);
        }
    }

    public WindowSize PixelSize
    {
        get
        {
            int width, height;
            Api.GetWindowSizeInPixels(Handle, &width, &height);
            return new WindowSize(width, height);
        }
    }

    public int Monitor => Math.Max(Api.GetWindowDisplayIndex(Handle), 0);

    public bool HasFocus => (Flags & WindowFlags.InputFocus) != 0;

    public bool IsMinimized => (Flags & WindowFlags.Minimized) != 0;

    public NativeSurface Surface { get; }

    public IOpenGLSurface? OpenGL => this;

    internal SdlWindow* Handle => _window is not null ? _window : throw new ObjectDisposedException(nameof(Sdl2Window));

    private Sdl Api => _backend.Api;

    private WindowFlags Flags => (WindowFlags)Api.GetWindowFlags(Handle);

    public void SetTitle(string title) => Api.SetWindowTitle(Handle, title);

    public string? ApplyDisplay(in DisplayRequest request)
    {
        Sdl sdl = Api;
        bool wasFullscreen = (Flags & WindowFlags.Fullscreen) != 0;
        if (wasFullscreen && sdl.SetWindowFullscreen(Handle, 0) < 0)
        {
            return _backend.Error();
        }

        // A full screen window covers the monitor that holds it: move it there first.
        if (wasFullscreen || request.Mode != WindowMode.Windowed || Monitor != request.Monitor)
        {
            sdl.SetWindowPosition(Handle, CenteredOnDisplay | request.Monitor, CenteredOnDisplay | request.Monitor);
        }

        switch (request.Mode)
        {
            case WindowMode.Borderless:
                if (sdl.SetWindowFullscreen(Handle, (uint)WindowFlags.FullscreenDesktop) < 0)
                {
                    return _backend.Error();
                }

                break;
            case WindowMode.Exclusive:
                var mode = new DisplayMode { W = request.Exclusive.Width, H = request.Exclusive.Height, RefreshRate = (int)Math.Round(request.Exclusive.RefreshRate) };
                if (sdl.SetWindowDisplayMode(Handle, &mode) < 0 || sdl.SetWindowFullscreen(Handle, (uint)WindowFlags.Fullscreen) < 0)
                {
                    return _backend.Error();
                }

                break;
        }

        sdl.ShowWindow(Handle);
        return null;
    }

    public void SetCursorVisible(bool visible) => Api.ShowCursor(visible ? 1 : 0);

    public void SetCursorShape(CursorShape shape)
    {
        if (!_cursors.TryGetValue(shape, out nint cursor))
        {
            cursor = (nint)Api.CreateSystemCursor(SystemCursor(shape));
            _cursors[shape] = cursor;
        }

        if (cursor != 0)
        {
            Api.SetCursor((Cursor*)cursor);
        }
    }

    public void SetCursorConfined(bool confined) => Api.SetWindowGrab(Handle, confined ? SdlBool.True : SdlBool.False);

    public void SetRelativeMouseMode(bool relative) => Api.SetRelativeMouseMode(relative ? SdlBool.True : SdlBool.False);

    public void SetTextInput(bool active)
    {
        if (active)
        {
            Api.StartTextInput();
        }
        else
        {
            Api.StopTextInput();
        }
    }

    public void SetTextInputArea(WindowRect area, int cursor)
    {
        var rect = new Rectangle<int>(area.X + cursor, area.Y, Math.Max(area.Width - cursor, 1), area.Height);
        Api.SetTextInputRect(&rect);
    }

    public IOpenGLContext CreateContext(in OpenGLContextOptions options)
    {
        Sdl sdl = Api;
        sdl.GLSetAttribute(GLattr.ContextMajorVersion, options.Major);
        sdl.GLSetAttribute(GLattr.ContextMinorVersion, options.Minor);
        sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)(options.CoreProfile ? GLprofile.Core : GLprofile.Compatibility));
        sdl.GLSetAttribute(GLattr.ContextFlags, options.Debug ? (int)GLcontextFlag.DebugFlag : 0);
        void* context = sdl.GLCreateContext(Handle);
        if (context is null)
        {
            throw new InvalidOperationException($"The driver cannot create an OpenGL {options.Major}.{options.Minor} context: {_backend.Error()}");
        }

        return new Sdl2GLContext(this, _backend, context);
    }

    public void Dispose()
    {
        if (_window is null)
        {
            return;
        }

        foreach (nint cursor in _cursors.Values.Where(c => c != 0))
        {
            Api.FreeCursor((Cursor*)cursor);
        }

        _cursors.Clear();
        Api.DestroyWindow(_window);
        _window = null;
        _backend.WindowDestroyed(this);
    }

    private static SystemCursor SystemCursor(CursorShape shape) => shape switch
    {
        CursorShape.Text => Silk.NET.SDL.SystemCursor.SystemCursorIbeam,
        CursorShape.Hand => Silk.NET.SDL.SystemCursor.SystemCursorHand,
        CursorShape.Crosshair => Silk.NET.SDL.SystemCursor.SystemCursorCrosshair,
        CursorShape.Wait => Silk.NET.SDL.SystemCursor.SystemCursorWait,
        CursorShape.ResizeHorizontal => Silk.NET.SDL.SystemCursor.SystemCursorSizewe,
        CursorShape.ResizeVertical => Silk.NET.SDL.SystemCursor.SystemCursorSizens,
        CursorShape.ResizeDiagonalDown => Silk.NET.SDL.SystemCursor.SystemCursorSizenwse,
        CursorShape.ResizeDiagonalUp => Silk.NET.SDL.SystemCursor.SystemCursorSizenesw,
        CursorShape.Move => Silk.NET.SDL.SystemCursor.SystemCursorSizeall,
        CursorShape.NotAllowed => Silk.NET.SDL.SystemCursor.SystemCursorNo,
        _ => Silk.NET.SDL.SystemCursor.SystemCursorArrow,
    };
}

/// <summary>An OpenGL context created by SDL2 on the window.</summary>
internal sealed unsafe class Sdl2GLContext : IOpenGLContext
{
    private readonly Sdl2Window _window;
    private readonly Sdl2Backend _backend;
    private void* _context;

    public Sdl2GLContext(Sdl2Window window, Sdl2Backend backend, void* context)
    {
        _window = window;
        _backend = backend;
        _context = context;
    }

    public void MakeCurrent()
    {
        if (_backend.Api.GLMakeCurrent(_window.Handle, Context) < 0)
        {
            throw new InvalidOperationException($"The OpenGL context cannot be made current: {_backend.Error()}");
        }
    }

    public void SwapBuffers() => _backend.Api.GLSwapWindow(_window.Handle);

    public bool SetSwapInterval(int interval) => _backend.Api.GLSetSwapInterval(interval) == 0;

    public nint GetProcAddress(string name) => (nint)_backend.Api.GLGetProcAddress(name);

    public void Dispose()
    {
        if (_context is not null)
        {
            _backend.Api.GLDeleteContext(_context);
            _context = null;
        }
    }

    private void* Context => _context is not null ? _context : throw new ObjectDisposedException(nameof(Sdl2GLContext));
}
