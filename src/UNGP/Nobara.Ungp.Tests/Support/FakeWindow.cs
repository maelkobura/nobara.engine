// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Graphics;

namespace Nobara.Ungp.Tests.Support;

/// <summary>A graphics backend that creates <see cref="FakeDevice"/> devices.</summary>
internal sealed class FakeGraphicsBackend(string name) : IGraphicsBackend
{
    public string Name { get; } = name;

    public FakeDevice? Device { get; private set; }

    public GraphicsDeviceOptions Options { get; private set; }

    public Action<FakeDevice>? Created { get; set; }

    public GraphicsDevice CreateDevice(IWindow window, in GraphicsDeviceOptions options)
    {
        Options = options;
        Device = new FakeDevice(framesInFlight: options.FramesInFlight);
        Created?.Invoke(Device);
        return Device;
    }
}

/// <summary>A window backend whose window the test changes; queued changes arrive at the next pump.</summary>
internal sealed class FakeWindowBackend : IWindowBackend
{
    private readonly Queue<(WindowEventKind Kind, Action<FakeWindow> Change)> _pending = [];

    public string Name => "fake";

    public FakeWindow? Window { get; private set; }

    public IReadOnlyList<MonitorInfo> GetMonitors() =>
        [new MonitorInfo(0, "Primary", new WindowRect(0, 0, 1920, 1080), new VideoMode(1920, 1080, 60), [new VideoMode(1920, 1080, 60)], 1f)];

    public IBackendWindow CreateWindow(string title, WindowSize size) => Window = new FakeWindow(size);

    public TimeSpan PumpEvents(IWindowEventSink sink, TimeSpan wait)
    {
        while (_pending.TryDequeue(out (WindowEventKind Kind, Action<FakeWindow> Change) next))
        {
            next.Change(Window!);
            sink.OnWindowEvent(next.Kind);
        }

        return TimeSpan.Zero;
    }

    public string GetKeyName(Scancode scancode) => scancode.ToString();

    public void Queue(WindowEventKind kind, Action<FakeWindow> change) => _pending.Enqueue((kind, change));

    public void Dispose()
    {
    }
}

/// <summary>The window of <see cref="FakeWindowBackend"/>.</summary>
internal sealed class FakeWindow(WindowSize size) : IBackendWindow
{
    public WindowSize Size { get; set; } = size;

    public WindowSize PixelSize { get; set; } = size;

    public int Monitor => 0;

    public bool HasFocus => true;

    public bool IsMinimized { get; set; }

    public NativeSurface Surface => default;

    public IOpenGLSurface? OpenGL => null;

    public void SetTitle(string title)
    {
    }

    public string? ApplyDisplay(in DisplayRequest request) => null;

    public void SetCursorVisible(bool visible)
    {
    }

    public void SetCursorShape(CursorShape shape)
    {
    }

    public void SetCursorConfined(bool confined)
    {
    }

    public void SetRelativeMouseMode(bool relative)
    {
    }

    public void SetTextInput(bool active)
    {
    }

    public void SetTextInputArea(WindowRect area, int cursor)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>Keeps the messages logged by the engine.</summary>
internal sealed class MemoryLogSink : ILogSink
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Write(in LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public void Flush()
    {
    }
}
