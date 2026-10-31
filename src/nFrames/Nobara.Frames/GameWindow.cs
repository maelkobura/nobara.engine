// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Frames;

/// <summary>
/// The window that modules see: the backend window, with the states that
/// nFrames keeps from one event to the next.
/// </summary>
internal sealed class GameWindow : IWindow
{
    private readonly IBackendWindow _native;
    private string _title;
    private bool _cursorVisible = true;
    private CursorShape _cursorShape;
    private bool _cursorConfined;
    private bool _relative;

    public GameWindow(IBackendWindow native, string defaultTitle)
    {
        _native = native;
        DefaultTitle = defaultTitle;
        _title = defaultTitle;
        Refresh();
    }

    public IBackendWindow Native => _native;

    public string Title
    {
        get => _title;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _title = value;
            _native.SetTitle(value);
        }
    }

    public string DefaultTitle { get; }

    public WindowMode Mode { get; private set; }

    public int Monitor { get; private set; }

    public VideoMode? ExclusiveMode { get; private set; }

    public WindowSize Size { get; private set; }

    public WindowSize PixelSize { get; private set; }

    public float Scale => Size.Width > 0 ? (float)PixelSize.Width / Size.Width : 1f;

    public bool HasFocus { get; private set; }

    public bool IsMinimized { get; private set; }

    public NativeSurface Surface => _native.Surface;

    public IOpenGLSurface? OpenGL => _native.OpenGL;

    public bool CursorVisible
    {
        get => _cursorVisible;
        set
        {
            _cursorVisible = value;
            _native.SetCursorVisible(value);
        }
    }

    public CursorShape CursorShape
    {
        get => _cursorShape;
        set
        {
            _cursorShape = value;
            _native.SetCursorShape(value);
        }
    }

    public bool CursorConfined
    {
        get => _cursorConfined;
        set
        {
            _cursorConfined = value;
            _native.SetCursorConfined(value);
        }
    }

    public bool RelativeMouseMode
    {
        get => _relative;
        set
        {
            _relative = value;
            _native.SetRelativeMouseMode(value);
        }
    }

    public bool IsTextInputActive { get; private set; }

    public void StartTextInput()
    {
        IsTextInputActive = true;
        _native.SetTextInput(true);
    }

    public void StopTextInput()
    {
        IsTextInputActive = false;
        _native.SetTextInput(false);
    }

    public void SetTextInputArea(WindowRect area, int cursor) => _native.SetTextInputArea(area, cursor);

    /// <summary>Records the display that the backend applied.</summary>
    /// <returns>What changed besides the mode.</returns>
    public Changes Displayed(in DisplayRequest request)
    {
        Mode = request.Mode;
        ExclusiveMode = request.Mode == WindowMode.Exclusive ? request.Exclusive : null;
        return Refresh();
    }

    /// <summary>Reads the state of the backend window again.</summary>
    /// <returns>What changed, as flags of <see cref="Changes"/>.</returns>
    public Changes Refresh()
    {
        Changes changes = Changes.None;
        float scale = Scale;
        WindowSize size = _native.Size;
        WindowSize pixels = _native.PixelSize;
        if (size != Size || pixels != PixelSize)
        {
            changes |= Changes.Size;
        }

        Size = size;
        PixelSize = pixels;
        if (Math.Abs(Scale - scale) > 1e-4f)
        {
            changes |= Changes.Scale;
        }

        int monitor = _native.Monitor;
        if (monitor != Monitor)
        {
            changes |= Changes.Monitor;
        }

        Monitor = monitor;
        HasFocus = _native.HasFocus;
        IsMinimized = _native.IsMinimized;
        return changes;
    }

    [Flags]
    public enum Changes
    {
        None = 0,
        Size = 1,
        Scale = 2,
        Monitor = 4,
    }
}
