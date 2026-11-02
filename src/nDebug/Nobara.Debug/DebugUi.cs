// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using ImGuiNET;
using Nobara.Core;
using Nobara.Core.Loop;
using Nobara.Core.Profiling;
using Nobara.Frames;
using Nobara.Input;

namespace Nobara.Debug;

/// <summary>
/// The debug user interface of one client context: the context of Dear ImGui,
/// the input layer that feeds it, and the windows that modules register.
/// </summary>
/// <remarks>
/// Dear ImGui keeps its current context in a native global: every entry point
/// makes the context of this instance current before it calls Dear ImGui.
/// </remarks>
internal sealed class DebugUi : IDebugUi, IInputLayerHandler, IPhaseHandler, IDisposable
{
    /// <summary>The identifier of the action that opens and closes the tools.</summary>
    public const string ToggleAction = "inputaction:nobara.debug.toggle";

    /// <summary>The size of the default font, in pixels at a scale of 1.</summary>
    public const float FontSize = 13;

    private readonly ModuleContext _context;
    private readonly List<DebugWindow> _windows = [];
    private readonly List<DebugWindow> _removed = [];
    private readonly ImGuiKeys _keys = new();
    private readonly ProfilerMarker _frameMarker;
    private IWindowSystem? _frames;
    private InputLayer? _layer;
    private InputAction? _toggle;
    private ImGuiPass? _pass;
    private nint _imgui;
    private bool _isOpen;
    private bool _drawing;
    private bool _drewLastFrame;
    private bool _wantMouse;
    private bool _wantKeyboard;
    private bool _hadFocus = true;
    private int _sequence;
    private float _fontScale;
    private float _windowScale;
    private Vector2 _mouse = new(-float.MaxValue, -float.MaxValue);
    private TimeSpan _pendingDelta;
    private ImGuiMouseCursor _cursor = ImGuiMouseCursor.Arrow;

    public DebugUi(ModuleContext context)
    {
        _context = context;
        _frameMarker = context.Profiler.CreateMarker("Debug frame");
    }

    public bool ToolsEnabled { get; private set; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (value && !ToolsEnabled)
            {
                throw new InvalidOperationException(
                    $"The debug tools are not available: '{DebugSettings.Tools}' is '{_context.Settings.GetEnum(DebugSettings.Tools)}'{(_context.DevelopmentMode ? string.Empty : " and the engine does not run in development mode")}.");
            }

            _isOpen = value;
            UpdateLayer();
        }
    }

    public IReadOnlyList<DebugWindow> Windows => _windows;

    public bool WantsMouse => _wantMouse;

    public bool WantsKeyboard => _wantKeyboard;

    /// <summary>Gets the input layer of nDebug, for tests.</summary>
    internal InputLayer? Layer => _layer;

    /// <summary>Gets the pass that draws the frames, for tests.</summary>
    internal ImGuiPass? Pass => _pass;

    /// <summary>Gets whether a frame of Dear ImGui is drawn: the tools are open, or a screen is.</summary>
    internal bool Visible
    {
        get
        {
            if (_isOpen)
            {
                return true;
            }

            foreach (DebugWindow window in _windows)
            {
                if (window.Kind == DebugWindowKind.Screen && window.IsOpen && IsActive(window))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public DebugWindow AddWindow(ModuleContext module, DebugWindowOptions options, IDebugWindow window)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrEmpty(options.Title);
        var added = new DebugWindow(this, module, options, window, ++_sequence);
        _windows.Add(added);
        UpdateLayer();
        return added;
    }

    /// <summary>Reads the configuration and registers the input layer and the pass.</summary>
    public void Initialize(IInputSystem input, IWindowSystem frames, ImGuiPass pass)
    {
        string tools = _context.Settings.GetEnum(DebugSettings.Tools);
        ToolsEnabled = tools == DebugSettings.ToolsAlways || (tools == DebugSettings.ToolsDevelopment && _context.DevelopmentMode);
        _frames = frames;
        _pass = pass;
        _toggle = input.GetAction(ToggleAction);
        _layer = input.AddLayer(_context, new InputLayerOptions
        {
            Name = DebugModule.ModuleId,
            Priority = InputLayerPriority.Debug,
            ReceivesRawInput = true,
            Handler = this,
        });
        UpdateLayer();
    }

    /// <summary>Creates the context of Dear ImGui and its font atlas.</summary>
    public unsafe void Start()
    {
        _imgui = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imgui);
        ImGuiIOPtr io = ImGui.GetIO();

        // Nothing is written to the working folder: no imgui.ini, no log.
        io.NativePtr->IniFilename = null;
        io.NativePtr->LogFilename = null;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.HasMouseCursors;

        // A window that forgets an End is closed at the end of the frame, with a
        // message, instead of stopping the process. Dear ImGui requires at least
        // one way to report it: its own debug log, and a tooltip in development mode.
        io.ConfigErrorRecovery = true;
        io.ConfigErrorRecoveryEnableAssert = false;
        io.ConfigErrorRecoveryEnableDebugLog = true;
        io.ConfigErrorRecoveryEnableTooltip = _context.DevelopmentMode;
        UpdateScale();
    }

    /// <summary>Removes the input layer.</summary>
    public void Stop()
    {
        _layer?.Dispose();
        _layer = null;
    }

    public void Dispose()
    {
        if (_imgui != 0)
        {
            ImGui.DestroyContext(_imgui);
            _imgui = 0;
        }
    }

    // At the start of each frame, after nInput evaluated the input: the time of
    // the frame, and the focus of the window.
    public void Run(in PhaseTime time)
    {
        _pendingDelta += time.Delta;
        bool focus = _frames?.IsCreated == true && _frames.MainWindow.HasFocus;
        if (focus != _hadFocus && _drewLastFrame && _imgui != 0)
        {
            MakeCurrent();
            ImGui.GetIO().AddFocusEvent(focus);
        }

        _hadFocus = focus;
    }

    public InputResult OnRawInput(in RawInputEvent e, IRawInput input)
    {
        if (e.Kind is RawInputKind.MouseMotion or RawInputKind.MouseButtonDown or RawInputKind.MouseButtonUp)
        {
            _mouse = new Vector2(e.X, e.Y);
        }

        if (!_drewLastFrame || _imgui == 0)
        {
            return InputResult.Pass;
        }

        MakeCurrent();
        ImGuiIOPtr io = ImGui.GetIO();
        switch (e.Kind)
        {
            case RawInputKind.MouseMotion:
                io.AddMousePosEvent(e.X, e.Y);
                return _wantMouse ? InputResult.Consume : InputResult.Pass;
            case RawInputKind.MouseButtonDown:
            case RawInputKind.MouseButtonUp:
                io.AddMousePosEvent(e.X, e.Y);
                if (Button(e.Button) is int button)
                {
                    io.AddMouseButtonEvent(button, e.Kind == RawInputKind.MouseButtonDown);
                }

                return _wantMouse ? InputResult.Consume : InputResult.Pass;
            case RawInputKind.MouseWheel:
                io.AddMouseWheelEvent(e.DeltaX, e.DeltaY);
                return _wantMouse ? InputResult.Consume : InputResult.Pass;
            case RawInputKind.KeyDown:
            case RawInputKind.KeyUp:
                io.AddKeyEvent(ImGuiKey.ModCtrl, (e.Modifiers & KeyModifiers.Control) != 0);
                io.AddKeyEvent(ImGuiKey.ModShift, (e.Modifiers & KeyModifiers.Shift) != 0);
                io.AddKeyEvent(ImGuiKey.ModAlt, (e.Modifiers & KeyModifiers.Alt) != 0);
                io.AddKeyEvent(ImGuiKey.ModSuper, (e.Modifiers & KeyModifiers.Meta) != 0);
                ImGuiKey key = _keys.Map(e.Scancode, input);
                if (key != ImGuiKey.None && !e.IsRepeat)
                {
                    io.AddKeyEvent(key, e.Kind == RawInputKind.KeyDown);
                }

                return _wantKeyboard ? InputResult.Consume : InputResult.Pass;
            default:
                return InputResult.Pass;
        }
    }

    public InputResult OnAction(InputAction action, ActionEdge edge)
    {
        if (action != _toggle || edge != ActionEdge.Pressed || !ToolsEnabled)
        {
            return InputResult.Pass;
        }

        IsOpen = !_isOpen;
        return InputResult.Consume;
    }

    public void OnText(in RawInputEvent e, ReadOnlySpan<char> text)
    {
        if (e.Kind != RawInputKind.TextInput || !_drewLastFrame || _imgui == 0)
        {
            return;
        }

        MakeCurrent();
        ImGuiIOPtr io = ImGui.GetIO();
        foreach (char c in text)
        {
            io.AddInputCharacterUTF16(c);
        }
    }

    /// <summary>
    /// Builds the frame of Dear ImGui, when something shows: the screens, then
    /// the tools. nDebug calls it while nRenderer draws the frame, before the
    /// pass of the user interface begins.
    /// </summary>
    /// <param name="pixelWidth">The width of the target, in pixels.</param>
    /// <param name="pixelHeight">The height of the target, in pixels.</param>
    /// <returns><see langword="true"/> when the draw data of Dear ImGui holds the frame.</returns>
    public bool BuildFrame(int pixelWidth, int pixelHeight)
    {
        if (_imgui == 0)
        {
            return false;
        }

        if (!Visible)
        {
            if (_drewLastFrame)
            {
                Hide();
            }

            return false;
        }

        MakeCurrent();
        ImGuiIOPtr io = ImGui.GetIO();
        if (!_drewLastFrame)
        {
            // Dear ImGui did not see the input while nothing showed.
            io.AddFocusEvent(_hadFocus);
            io.AddMousePosEvent(_mouse.X, _mouse.Y);
        }

        UpdateScale();
        io.DisplaySize = new Vector2(pixelWidth / _windowScale, pixelHeight / _windowScale);
        io.DisplayFramebufferScale = new Vector2(_windowScale);
        io.DeltaTime = (float)Math.Max(_pendingDelta.TotalSeconds, 1e-4);
        _pendingDelta = TimeSpan.Zero;

        using (_frameMarker.Begin())
        {
            ImGui.NewFrame();
            _drawing = true;
            try
            {
                DrawWindows();
            }
            finally
            {
                _drawing = false;
                ImGui.Render();
                RemovePending();
            }
        }

        _drewLastFrame = true;
        _wantMouse = io.WantCaptureMouse;
        _wantKeyboard = io.WantCaptureKeyboard || io.WantTextInput;
        UpdateTextFocus(io.WantTextInput);
        UpdateCursor(ImGui.GetMouseCursor());
        return true;
    }

    internal void Remove(DebugWindow window)
    {
        if (_drawing)
        {
            _removed.Add(window);
        }
        else
        {
            _windows.Remove(window);
            UpdateLayer();
        }
    }

    private static int? Button(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.X1 => 3,
        MouseButton.X2 => 4,
        _ => null,
    };

    private static CursorShape Shape(ImGuiMouseCursor cursor) => cursor switch
    {
        ImGuiMouseCursor.TextInput => CursorShape.Text,
        ImGuiMouseCursor.ResizeAll => CursorShape.Move,
        ImGuiMouseCursor.ResizeNS => CursorShape.ResizeVertical,
        ImGuiMouseCursor.ResizeEW => CursorShape.ResizeHorizontal,
        ImGuiMouseCursor.ResizeNESW => CursorShape.ResizeDiagonalUp,
        ImGuiMouseCursor.ResizeNWSE => CursorShape.ResizeDiagonalDown,
        ImGuiMouseCursor.Hand => CursorShape.Hand,
        ImGuiMouseCursor.NotAllowed => CursorShape.NotAllowed,
        _ => CursorShape.Arrow,
    };

    private bool IsActive(DebugWindow window) => _context.IsModuleActive(window.Module.Module.Id);

    private void MakeCurrent()
    {
        if (ImGui.GetCurrentContext() != _imgui)
        {
            ImGui.SetCurrentContext(_imgui);
        }
    }

    private void DrawWindows()
    {
        // Screens first, so that the tools stay above them.
        for (int i = 0; i < _windows.Count; i++)
        {
            DebugWindow window = _windows[i];
            if (window.Kind == DebugWindowKind.Screen && window.IsOpen && !window.IsRemoved && IsActive(window))
            {
                Draw(window);
            }
        }

        if (!_isOpen)
        {
            return;
        }

        DrawMenuBar();
        for (int i = 0; i < _windows.Count; i++)
        {
            DebugWindow window = _windows[i];
            if (window.Kind == DebugWindowKind.Tool && window.IsOpen && !window.IsRemoved && IsActive(window))
            {
                Draw(window);
            }
        }
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
        {
            return;
        }

        if (ImGui.BeginMenu("Windows"))
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                DebugWindow window = _windows[i];
                if (window.Kind == DebugWindowKind.Tool && !window.IsRemoved && IsActive(window) && ImGui.MenuItem(window.Title, string.Empty, window.IsOpen))
                {
                    window.IsOpen = !window.IsOpen;
                }
            }

            ImGui.EndMenu();
        }

        if (ImGui.MenuItem("Close"))
        {
            _isOpen = false;
            UpdateLayer();
        }

        ImGui.EndMainMenuBar();
    }

    private void Draw(DebugWindow window)
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGuiWindowFlags flags = ImGuiWindowFlags.None;
        switch (window.Placement)
        {
            case DebugWindowPlacement.Center:
                ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
                break;
            case DebugWindowPlacement.Fullscreen:
                ImGui.SetNextWindowPos(viewport.WorkPos);
                ImGui.SetNextWindowSize(viewport.WorkSize);
                flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;
                break;
        }

        if (window.Placement != DebugWindowPlacement.Fullscreen && window.Size != Vector2.Zero)
        {
            ImGui.SetNextWindowSize(window.Size, ImGuiCond.Appearing);
        }

        bool open = true;
        bool expanded = window.Kind == DebugWindowKind.Tool
            ? ImGui.Begin(window.Label, ref open, flags)
            : ImGui.Begin(window.Label, flags);
        try
        {
            if (expanded)
            {
                using (window.Marker.Begin())
                {
                    window.Content.Draw(window);
                }
            }
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            window.Module.ReportFault(exception);
        }
        finally
        {
            ImGui.End();
        }

        if (!open)
        {
            window.IsOpen = false;
        }
    }

    private void RemovePending()
    {
        if (_removed.Count == 0)
        {
            return;
        }

        foreach (DebugWindow window in _removed)
        {
            _windows.Remove(window);
        }

        _removed.Clear();
        UpdateLayer();
    }

    // Nothing shows any more: Dear ImGui releases its keys and buttons, and the
    // layer lets everything through.
    private void Hide()
    {
        MakeCurrent();
        ImGui.GetIO().AddFocusEvent(false);
        _drewLastFrame = false;
        _wantMouse = false;
        _wantKeyboard = false;
        UpdateTextFocus(false);
        UpdateCursor(ImGuiMouseCursor.Arrow);
    }

    // The cursor shows while the tools or a screen show.
    private void UpdateLayer()
    {
        if (_layer is null || _layer.IsRemoved)
        {
            return;
        }

        CursorRequest cursor = Visible ? CursorRequest.Cursor : CursorRequest.None;
        if (_layer.Cursor != cursor)
        {
            _layer.Cursor = cursor;
        }
    }

    private void UpdateTextFocus(bool wanted)
    {
        if (_layer is null || _layer.IsRemoved)
        {
            return;
        }

        if (wanted && !_layer.HasTextFocus && _layer.Enabled)
        {
            _layer.TakeTextFocus();
        }
        else if (!wanted && _layer.HasTextFocus)
        {
            _layer.ReleaseTextFocus();
        }
    }

    private void UpdateCursor(ImGuiMouseCursor cursor)
    {
        if (cursor == _cursor || cursor == ImGuiMouseCursor.None)
        {
            return;
        }

        _cursor = cursor;
        if (_frames?.IsCreated == true)
        {
            _frames.MainWindow.CursorShape = Shape(cursor);
        }
    }

    // The font is rasterized at the scale of the monitor, and the sizes of the
    // style follow it; Dear ImGui works in logical units of the window.
    private unsafe void UpdateScale()
    {
        float windowScale = 1;
        float monitorScale = 1;
        if (_frames?.IsCreated == true)
        {
            IWindow window = _frames.MainWindow;
            windowScale = window.Scale > 0 ? window.Scale : 1;
            IReadOnlyList<MonitorInfo> monitors = _frames.Monitors;
            if (window.Monitor >= 0 && window.Monitor < monitors.Count && monitors[window.Monitor].ContentScale > 0)
            {
                monitorScale = monitors[window.Monitor].ContentScale;
            }
        }

        float fontScale = Math.Clamp(Math.Max(monitorScale, windowScale), 0.5f, 4f);
        if (fontScale == _fontScale && windowScale == _windowScale)
        {
            return;
        }

        _fontScale = fontScale;
        _windowScale = windowScale;
        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.Clear();
        ImFontConfig* config = ImGuiNative.ImFontConfig_ImFontConfig();
        config->SizePixels = FontSize * fontScale;
        io.Fonts.AddFontDefault(config);
        ImGuiNative.ImFontConfig_destroy(config);
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        _pass?.UploadAtlas(new ReadOnlySpan<byte>(pixels, width * height * bytesPerPixel), width, height);
        io.Fonts.SetTexID(ImGuiPass.AtlasId);
        io.Fonts.ClearTexData();
        io.FontGlobalScale = 1 / windowScale;

        // The style starts again from its defaults before it is scaled.
        ImGuiStylePtr style = ImGui.GetStyle();
        ImGuiStyle* defaults = ImGuiNative.ImGuiStyle_ImGuiStyle();
        *style.NativePtr = *defaults;
        ImGuiNative.ImGuiStyle_destroy(defaults);
        ImGui.StyleColorsDark();
        style.ScaleAllSizes(fontScale / windowScale);
    }
}
