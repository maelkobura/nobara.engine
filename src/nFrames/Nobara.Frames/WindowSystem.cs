// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Core.Loop;

namespace Nobara.Frames;

/// <summary>
/// The service of nFrames in the client context: the registry of backends,
/// the window, the subscriptions, and the pumping of events at the start of
/// each frame.
/// </summary>
internal sealed class WindowSystem : IWindowSystem, IWindowBackends, IWindowEventSink, IPhaseHandler
{
    /// <summary>The size of the window in windowed mode, in logical units.</summary>
    public static readonly WindowSize WindowedSize = new(1280, 720);

    private readonly ModuleContext _context;
    private readonly List<(ModuleContext Module, IWindowBackend Backend)> _backends = [];
    private readonly List<Subscriber<WindowEvent>> _windowHandlers = [];
    private readonly List<Subscriber<CloseRequest>> _closeHandlers = [];
    private readonly RawInputBuffer _input = new();
    private IWindowBackend? _backend;
    private GameWindow? _window;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private bool _started;
    private int _backgroundFps;
    private TimeSpan _waited;

    public WindowSystem(ModuleContext context)
    {
        _context = context;
    }

    /// <summary>Gets or sets what relays events on the event bus, or <see langword="null"/> without it.</summary>
    public IFramesNotifications? Notifications { get; set; }

    public bool IsCreated => _window is not null;

    public string BackendName => (_backend ?? throw NotCreated()).Name;

    public IWindow MainWindow => _window ?? throw NotCreated();

    public IReadOnlyList<MonitorInfo> Monitors => _window is null ? throw NotCreated() : _monitors;

    public IRawInput Input => _input;

    /// <summary>Gets whether the frame rate is limited: the window is minimized or has no focus.</summary>
    public bool Throttled => _window is not null && (!_window.HasFocus || _window.IsMinimized);

    /// <summary>Gets the background frame rate in use, from <c>frames.backgroundFps</c>.</summary>
    public int BackgroundFps => _backgroundFps;

    public WindowSubscription Subscribe(ModuleContext module, Action<WindowEvent> handler) => Add(_windowHandlers, module, handler);

    public WindowSubscription SubscribeCloseRequested(ModuleContext module, Action<CloseRequest> handler) => Add(_closeHandlers, module, handler);

    public void Register(ModuleContext module, IWindowBackend backend)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(backend);
        if (_started)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' registers window backend '{backend.Name}' after nFrames started; backends register during initialization.");
        }

        foreach ((ModuleContext other, IWindowBackend existing) in _backends)
        {
            if (string.Equals(existing.Name, backend.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modules '{other.Module.Id}' and '{module.Module.Id}' both register a window backend named '{backend.Name}'.");
            }
        }

        _backends.Add((module, backend));
        _context.Logger.Debug($"Window backend '{backend.Name}' registered by module '{module.Module.Id}'.");
    }

    /// <summary>Chooses the backend, creates the window, and shows it as the configuration says.</summary>
    /// <exception cref="InvalidOperationException">No backend is registered.</exception>
    public void Start()
    {
        _started = true;
        _backend = ChooseBackend();
        _input.Backend = _backend;
        _monitors = _backend.GetMonitors();
        _backgroundFps = _context.Settings.GetInt(FramesSettings.BackgroundFps);

        string title = ComposeTitle(_context);
        _window = new GameWindow(_backend.CreateWindow(title, WindowedSize), title);
        _context.Logger.Info($"Window created by backend '{_backend.Name}': {title}.");
        ApplyDisplay(notify: false);
    }

    /// <summary>Destroys the window and frees the backend in use.</summary>
    public void Release()
    {
        _window?.Native.Dispose();
        _window = null;
        _input.Backend = null;
        _backend?.Dispose();
        _backend = null;
    }

    /// <summary>Applies a change of a key of nFrames.</summary>
    public void SettingChanged(string key)
    {
        if (_window is null)
        {
            return;
        }

        switch (key)
        {
            case FramesSettings.BackgroundFps:
                _backgroundFps = _context.Settings.GetInt(FramesSettings.BackgroundFps);
                break;
            case FramesSettings.Backend:
                _context.Logger.Info($"'{FramesSettings.Backend}' changed; the window backend changes at the next start of the game.");
                break;
            case FramesSettings.Mode:
            case FramesSettings.Monitor:
            case FramesSettings.FullscreenWidth:
            case FramesSettings.FullscreenHeight:
            case FramesSettings.FullscreenRefreshRate:
                ApplyDisplay(notify: true);
                break;
        }
    }

    /// <summary>
    /// The start of each frame: waits when the frame rate is limited, then
    /// pumps the events of the backend into the window state, the
    /// subscribers, and the raw input buffer.
    /// </summary>
    public void Run(in PhaseTime time)
    {
        _input.Begin(time.Frame);
        if (_backend is null)
        {
            return;
        }

        // The work of the previous frame is its real time minus what this handler waited in it.
        TimeSpan wait = TimeSpan.Zero;
        if (Throttled)
        {
            TimeSpan interval = TimeSpan.FromSeconds(1.0 / _backgroundFps);
            wait = interval - (time.Delta - _waited);
        }

        _waited = _backend.PumpEvents(this, wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        while (Throttled && wait - _waited > TimeSpan.Zero)
        {
            TimeSpan waited = _backend.PumpEvents(this, wait - _waited);
            if (waited <= TimeSpan.Zero)
            {
                break;
            }

            _waited += waited;
        }
    }

    public void OnWindowEvent(WindowEventKind kind)
    {
        if (_window is null)
        {
            return;
        }

        bool focus = _window.HasFocus;
        bool minimized = _window.IsMinimized;
        GameWindow.Changes changes = _window.Refresh();
        switch (kind)
        {
            case WindowEventKind.FocusGained or WindowEventKind.FocusLost:
                if (_window.HasFocus != focus)
                {
                    Emit(_window.HasFocus ? WindowEventKind.FocusGained : WindowEventKind.FocusLost);
                    Notifications?.FocusChanged(_window.HasFocus);
                }

                break;
            case WindowEventKind.Minimized or WindowEventKind.Restored:
                if (_window.IsMinimized != minimized)
                {
                    Emit(_window.IsMinimized ? WindowEventKind.Minimized : WindowEventKind.Restored);
                }

                break;
            case WindowEventKind.MonitorsChanged:
                _monitors = _backend!.GetMonitors();
                Emit(WindowEventKind.MonitorsChanged);
                break;
            case WindowEventKind.Suspending or WindowEventKind.Resumed or WindowEventKind.SurfaceLost or WindowEventKind.SurfaceRecreated:
                Emit(kind);
                break;
        }

        // Sizes, scale, and monitor are compared with the previous state, whatever event reported them.
        EmitChanges(changes);
    }

    public void OnCloseRequested()
    {
        if (_window is null)
        {
            return;
        }

        var request = new CloseRequest(_window);
        Dispatch(_closeHandlers, request);
        if (request.IsCancelled)
        {
            _context.Logger.Info("A close of the window was requested and cancelled.");
            return;
        }

        Notifications?.CloseRequested();
        _context.ForwardStopRequest();
    }

    public void OnKeyboardLayoutChanged() => _input.LayoutChanged();

    [HotPath]
    public void OnInput(in RawInputEvent e, ReadOnlySpan<char> text) => _input.Add(e, text);

    internal static string ComposeTitle(ModuleContext context) =>
        $"{context.Distribution.Name} {context.Distribution.Version} — {RuntimeInformation.RuntimeIdentifier} — {context.Side}";

    private IWindowBackend ChooseBackend()
    {
        if (_backends.Count == 0)
        {
            throw new InvalidOperationException("No window backend is registered: the distribution needs a backend module of nobara.frames, such as nobara.frames.sdl2.");
        }

        string wanted = _context.Settings.GetString(FramesSettings.Backend);
        if (wanted.Length > 0 && !string.Equals(wanted, FramesSettings.AutomaticBackend, StringComparison.OrdinalIgnoreCase))
        {
            foreach ((_, IWindowBackend backend) in _backends)
            {
                if (string.Equals(backend.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return backend;
                }
            }

            _context.Logger.Warning($"'{FramesSettings.Backend}' names the window backend '{wanted}', which no module registers; nFrames uses '{_backends[0].Backend.Name}'. Registered: {string.Join(", ", _backends.Select(b => b.Backend.Name))}.");
        }

        return _backends[0].Backend;
    }

    private void ApplyDisplay(bool notify)
    {
        DisplayRequest request = FramesSettings.ReadDisplay(_context.Settings, _monitors, _context.Logger);
        if (_window!.Native.ApplyDisplay(request) is string error)
        {
            _context.Logger.Warning($"The window cannot change to {Describe(request)}: {error}");
            return;
        }

        bool focus = _window.HasFocus;
        GameWindow.Changes changes = _window.Displayed(request);
        _context.Logger.Info($"The window is {Describe(request)}.");
        if (notify)
        {
            Emit(WindowEventKind.ModeChanged);
            EmitChanges(changes);
            if (focus != _window.HasFocus)
            {
                Emit(_window.HasFocus ? WindowEventKind.FocusGained : WindowEventKind.FocusLost);
            }
        }
    }

    private string Describe(in DisplayRequest request)
    {
        string monitor = $"monitor {request.Monitor} ('{_monitors[request.Monitor].Name}')";
        return request.Mode switch
        {
            WindowMode.Windowed => $"windowed on {monitor}",
            WindowMode.Borderless => $"borderless full screen on {monitor}",
            _ => $"exclusive full screen on {monitor} at {request.Exclusive.Width}x{request.Exclusive.Height}, {request.Exclusive.RefreshRate:0.##} Hz",
        };
    }

    private void EmitChanges(GameWindow.Changes changes)
    {
        if ((changes & GameWindow.Changes.Size) != 0)
        {
            Emit(WindowEventKind.Resized);
        }

        if ((changes & GameWindow.Changes.Scale) != 0)
        {
            Emit(WindowEventKind.ScaleChanged);
        }

        if ((changes & GameWindow.Changes.Monitor) != 0)
        {
            Emit(WindowEventKind.MonitorChanged);
        }
    }

    private void Emit(WindowEventKind kind) => Dispatch(_windowHandlers, new WindowEvent(kind, _window!));

    private static WindowSubscription Add<T>(List<Subscriber<T>> list, ModuleContext module, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(handler);
        Subscriber<T>? subscriber = null;
        var subscription = new WindowSubscription(_ => list.Remove(subscriber!));
        subscriber = new Subscriber<T>(module, handler, subscription);
        list.Add(subscriber);
        return subscription;
    }

    // Each handler runs with the exceptions charged to its module; a handler removed during the dispatch does not run.
    private static void Dispatch<T>(List<Subscriber<T>> list, T value)
    {
        foreach (Subscriber<T> subscriber in list.ToArray())
        {
            if (subscriber.Subscription.Revoked || !subscriber.Module.IsModuleActive(subscriber.Module.Module.Id))
            {
                continue;
            }

            try
            {
                subscriber.Handler(value);
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                subscriber.Module.ReportFault(exception);
            }
        }
    }

    private static InvalidOperationException NotCreated() =>
        new("The window is created when nFrames starts: use it from the start step on.");

    private sealed record Subscriber<T>(ModuleContext Module, Action<T> Handler, WindowSubscription Subscription);
}
