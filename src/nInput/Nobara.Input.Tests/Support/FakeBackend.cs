// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Frames;

namespace Nobara.Input.Tests.Support;

/// <summary>
/// A window backend that hands out, at the next pump, the raw input events a
/// test queues, and records the cursor and text input that nInput asks for.
/// </summary>
internal sealed class FakeBackend : IWindowBackend
{
    private readonly Queue<Action<IWindowEventSink>> _pending = [];

    public string Name => "fake";

    public FakeWindow? Window { get; private set; }

    public IReadOnlyList<MonitorInfo> GetMonitors() =>
        [new MonitorInfo(0, "Primary", new WindowRect(0, 0, 1920, 1080), new VideoMode(1920, 1080, 60), [new VideoMode(1920, 1080, 60)], 1f)];

    public IBackendWindow CreateWindow(string title, WindowSize size)
    {
        Window = new FakeWindow(size);
        return Window;
    }

    public TimeSpan PumpEvents(IWindowEventSink sink, TimeSpan wait)
    {
        while (_pending.TryDequeue(out Action<IWindowEventSink>? action))
        {
            action(sink);
        }

        return TimeSpan.Zero;
    }

    public string GetKeyName(Scancode scancode) => scancode == Scancode.W ? "Z" : scancode.ToString();

    public void Dispose()
    {
    }

    public void Queue(Action<IWindowEventSink> action) => _pending.Enqueue(action);

    public void Queue(RawInputEvent e, string text = "") => Queue(sink => sink.OnInput(e, text));
}

/// <summary>The window of <see cref="FakeBackend"/>.</summary>
internal sealed class FakeWindow(WindowSize size) : IBackendWindow
{
    public WindowSize Size => size;

    public WindowSize PixelSize => size;

    public int Monitor => 0;

    public bool HasFocus { get; set; } = true;

    public bool IsMinimized => false;

    public NativeSurface Surface => default;

    public IOpenGLSurface? OpenGL => null;

    public bool RelativeMouse { get; private set; }

    public bool TextInput { get; private set; }

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

    public void SetRelativeMouseMode(bool relative) => RelativeMouse = relative;

    public void SetTextInput(bool active) => TextInput = active;

    public void SetTextInputArea(WindowRect area, int cursor)
    {
    }

    public void Dispose()
    {
    }
}
