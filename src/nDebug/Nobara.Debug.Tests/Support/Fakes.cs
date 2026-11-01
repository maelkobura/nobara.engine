// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Graphics;

namespace Nobara.Debug.Tests.Support;

/// <summary>A graphics backend that creates <see cref="FakeDevice"/> devices.</summary>
internal sealed class FakeGraphicsBackend : IGraphicsBackend
{
    public string Name => "fake";

    public FakeDevice? Device { get; private set; }

    public GraphicsDevice CreateDevice(IWindow window, in GraphicsDeviceOptions options)
    {
        Device = new FakeDevice(framesInFlight: options.FramesInFlight);
        return Device;
    }
}

/// <summary>
/// A window backend that hands out, at the next pump, the raw input events a
/// test queues, and records what nInput and nDebug ask of the window.
/// </summary>
internal sealed class FakeWindowBackend : IWindowBackend
{
    private readonly Queue<(RawInputEvent Event, string Text)> _pending = [];

    public string Name => "fake";

    public FakeWindow? Window { get; private set; }

    public float ContentScale { get; set; } = 1;

    public IReadOnlyList<MonitorInfo> GetMonitors() =>
        [new MonitorInfo(0, "Primary", new WindowRect(0, 0, 1920, 1080), new VideoMode(1920, 1080, 60), [new VideoMode(1920, 1080, 60)], ContentScale)];

    public IBackendWindow CreateWindow(string title, WindowSize size) => Window = new FakeWindow(new WindowSize(1280, 720));

    public TimeSpan PumpEvents(IWindowEventSink sink, TimeSpan wait)
    {
        while (_pending.TryDequeue(out (RawInputEvent Event, string Text) next))
        {
            sink.OnInput(next.Event, next.Text);
        }

        return TimeSpan.Zero;
    }

    // The letter keys of an AZERTY keyboard: the key at the place of A prints Q.
    public string GetKeyName(Scancode scancode) => scancode switch
    {
        Scancode.A => "Q",
        Scancode.Q => "A",
        Scancode.W => "Z",
        Scancode.Z => "W",
        _ => scancode.ToString(),
    };

    public void Queue(RawInputEvent e, string text = "") => _pending.Enqueue((e, text));

    public void Dispose()
    {
    }
}

/// <summary>The window of <see cref="FakeWindowBackend"/>.</summary>
internal sealed class FakeWindow(WindowSize size) : IBackendWindow
{
    public WindowSize Size => size;

    public WindowSize PixelSize => size;

    public int Monitor => 0;

    public bool HasFocus => true;

    public bool IsMinimized => false;

    public NativeSurface Surface => default;

    public IOpenGLSurface? OpenGL => null;

    public bool RelativeMouse { get; private set; }

    public bool TextInput { get; private set; }

    public CursorShape Shape { get; private set; }

    public void SetTitle(string title)
    {
    }

    public string? ApplyDisplay(in DisplayRequest request) => null;

    public void SetCursorVisible(bool visible)
    {
    }

    public void SetCursorShape(CursorShape shape) => Shape = shape;

    public void SetCursorConfined(bool confined)
    {
    }

    public void SetRelativeMouseMode(bool relative) => RelativeMouse = relative;

    public void SetTextInput(bool active) => TextInput = active;

    public void SetTextInputArea(WindowRect area, int cursor)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>A clock that moves only when the test advances it.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
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
