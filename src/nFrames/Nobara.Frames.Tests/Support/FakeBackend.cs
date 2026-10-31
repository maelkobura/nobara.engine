// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Frames.Tests.Support;

/// <summary>
/// A window backend that the test drives: it queues the events the test
/// gives it and hands them out at the next pump, and records what nFrames
/// asks of it.
/// </summary>
internal sealed class FakeBackend(string name) : IWindowBackend
{
    private readonly Queue<Action<IWindowEventSink>> _pending = [];

    public string Name { get; } = name;

    public List<MonitorInfo> Monitors { get; } =
    [
        new MonitorInfo(0, "Primary", new WindowRect(0, 0, 1920, 1080), new VideoMode(1920, 1080, 60), [new VideoMode(1920, 1080, 144), new VideoMode(1920, 1080, 60), new VideoMode(1280, 720, 60)], 1f),
        new MonitorInfo(1, "Side", new WindowRect(1920, 0, 2560, 1440), new VideoMode(2560, 1440, 60), [new VideoMode(2560, 1440, 60)], 1.5f),
    ];

    public FakeWindow? Window { get; private set; }

    /// <summary>Gets the waits nFrames asked for, one per pump.</summary>
    public List<TimeSpan> Waits { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Gets or sets what moves the clock of the loop by the time waited.</summary>
    public Action<TimeSpan>? OnWait { get; set; }

    public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors.ToArray();

    public IBackendWindow CreateWindow(string title, WindowSize size)
    {
        Window = new FakeWindow(title, size);
        return Window;
    }

    /// <summary>Waits the whole time asked for, then hands out the queued events.</summary>
    public TimeSpan PumpEvents(IWindowEventSink sink, TimeSpan wait)
    {
        Waits.Add(wait);
        OnWait?.Invoke(wait);
        while (_pending.TryDequeue(out Action<IWindowEventSink>? action))
        {
            action(sink);
        }

        return wait;
    }

    public string GetKeyName(Scancode scancode) => scancode == Scancode.W ? "Z" : scancode.ToString();

    public void Dispose() => Disposed = true;

    /// <summary>Queues an event for the next pump.</summary>
    public void Queue(Action<IWindowEventSink> action) => _pending.Enqueue(action);

    /// <summary>Queues a change of the window and its event.</summary>
    public void Queue(WindowEventKind kind, Action<FakeWindow> change) => Queue(sink =>
    {
        change(Window!);
        sink.OnWindowEvent(kind);
    });
}

/// <summary>The window of <see cref="FakeBackend"/>: its state is what the test sets.</summary>
internal sealed class FakeWindow(string title, WindowSize size) : IBackendWindow
{
    public string Title { get; private set; } = title;

    public WindowSize Size { get; set; } = size;

    public WindowSize PixelSize { get; set; } = size;

    public int Monitor { get; set; }

    public bool HasFocus { get; set; } = true;

    public bool IsMinimized { get; set; }

    public NativeSurface Surface => default;

    public IOpenGLSurface? OpenGL => null;

    public List<DisplayRequest> Displays { get; } = [];

    public string? RefuseDisplay { get; set; }

    public bool? RelativeMouse { get; private set; }

    public bool? TextInput { get; private set; }

    public bool Disposed { get; private set; }

    public void SetTitle(string title) => Title = title;

    public string? ApplyDisplay(in DisplayRequest request)
    {
        if (RefuseDisplay is not null)
        {
            return RefuseDisplay;
        }

        Displays.Add(request);
        Monitor = request.Monitor;
        if (request.Mode == WindowMode.Exclusive)
        {
            Size = PixelSize = new WindowSize(request.Exclusive.Width, request.Exclusive.Height);
        }

        return null;
    }

    public void SetCursorVisible(bool visible)
    {
    }

    public void SetCursorShape(CursorShape shape)
    {
    }

    public void SetCursorConfined(bool confined)
    {
    }

    public void SetRelativeMouseMode(bool relative) => RelativeMouse = relative;

    public void SetTextInput(bool active) => TextInput = active;

    public void SetTextInputArea(WindowRect area, int cursor)
    {
    }

    public void Dispose() => Disposed = true;
}
