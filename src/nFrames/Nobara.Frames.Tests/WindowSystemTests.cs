// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using Nobara.Event;
using Nobara.Frames.Tests.Support;

namespace Nobara.Frames.Tests;

/// <summary>The window, its backend, its display, and the requests to close it.</summary>
public sealed class WindowSystemTests : IDisposable
{
    private readonly FramesHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Creates_the_window_when_it_starts_with_the_composed_title()
    {
        Exception? early = null;
        IWindowSystem windows = null!;
        using Engine engine = _harness.Module("example.game", p => p.OnInitialize = p =>
        {
            windows = p.Windows;
            early = Record.Exception(() => windows.MainWindow);
        }).Start();

        Assert.IsType<InvalidOperationException>(early);
        Assert.True(windows.IsCreated);
        Assert.Equal("fake", windows.BackendName);
        string title = $"Example 0.1.0 — {RuntimeInformation.RuntimeIdentifier} — Client";
        Assert.Equal(title, windows.MainWindow.DefaultTitle);
        Assert.Equal(title, _harness.Backend.Window!.Title);
        Assert.Equal(2, windows.Monitors.Count);

        windows.MainWindow.Title = "Example — My world";
        Assert.Equal("Example — My world", _harness.Backend.Window.Title);
    }

    [Fact]
    public void Shows_the_window_as_the_configuration_says_when_it_starts()
    {
        using Engine engine = _harness.Start();

        DisplayRequest display = Assert.Single(_harness.Backend.Window!.Displays);
        Assert.Equal(new DisplayRequest(WindowMode.Windowed, 0, default), display);
        Assert.Equal(new WindowSize(1280, 720), _harness.Backend.Window.Size);
    }

    [Fact]
    public void Chooses_the_exclusive_video_mode_nearest_to_the_keys()
    {
        IWindow window = null!;
        using Engine engine = _harness
            .Setting(FramesSettings.Mode, "exclusive")
            .Module("example.game", p => p.OnStart = p => window = p.Windows.MainWindow)
            .Start();

        // The resolution of the desktop at its highest refresh rate.
        Assert.Equal(new DisplayRequest(WindowMode.Exclusive, 0, new VideoMode(1920, 1080, 144)), _harness.Backend.Window!.Displays[0]);
        Assert.Equal(WindowMode.Exclusive, window.Mode);
        Assert.Equal(new VideoMode(1920, 1080, 144), window.ExclusiveMode);
    }

    [Fact]
    public void Picks_the_nearest_mode_and_warns_when_the_resolution_does_not_exist()
    {
        MonitorInfo monitor = _harness.Backend.Monitors[0];
        var log = new FramesHarness();
        try
        {
            using Engine engine = log.Load();
            var logger = engine.MainContext.Logger;
            Assert.Equal(new VideoMode(1280, 720, 60), FramesSettings.ChooseVideoMode(monitor, 1300, 700, 0, logger));
            Assert.Equal(new VideoMode(1920, 1080, 60), FramesSettings.ChooseVideoMode(monitor, 1920, 1080, 75, logger));
            Assert.Contains(log.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no video mode of 1300x700", StringComparison.Ordinal));
        }
        finally
        {
            log.Dispose();
        }
    }

    [Fact]
    public void Uses_the_primary_monitor_when_the_key_names_one_that_is_not_connected()
    {
        using Engine engine = _harness.Setting(FramesSettings.Monitor, "5").Setting(FramesSettings.Mode, "borderless").Start();

        Assert.Equal(new DisplayRequest(WindowMode.Borderless, 0, default), _harness.Backend.Window!.Displays[0]);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("2 monitors are connected", StringComparison.Ordinal));
    }

    [Fact]
    public void Changes_the_display_while_the_game_runs()
    {
        Probe game = null!;
        var events = new List<WindowEventKind>();
        using Engine engine = _harness.Module("example.game", p =>
        {
            game = p;
            p.OnInitialize = p => p.Windows.Subscribe(p.Context, e => events.Add(e.Kind));
        }).Start();

        game.Context.Settings.Set(FramesSettings.Mode, "borderless");
        game.Context.Settings.Set(FramesSettings.Monitor, 1);
        _harness.Frame(engine);

        // Two keys changed together give one display.
        Assert.Equal(2, _harness.Backend.Window!.Displays.Count);

        Assert.Equal(new DisplayRequest(WindowMode.Borderless, 1, default), _harness.Backend.Window!.Displays[^1]);
        Assert.Equal(WindowMode.Borderless, game.Windows.MainWindow.Mode);
        Assert.Equal(1, game.Windows.MainWindow.Monitor);
        Assert.Contains(WindowEventKind.ModeChanged, events);
        Assert.Contains(WindowEventKind.MonitorChanged, events);

        // Exclusive, then windowed again.
        game.Context.Settings.Set(FramesSettings.Mode, "exclusive");
        _harness.Frame(engine);
        Assert.Equal(new DisplayRequest(WindowMode.Exclusive, 1, new VideoMode(2560, 1440, 60)), _harness.Backend.Window.Displays[^1]);
        Assert.Contains(WindowEventKind.Resized, events);

        game.Context.Settings.Reset(FramesSettings.Mode);
        _harness.Frame(engine);
        Assert.Equal(WindowMode.Windowed, _harness.Backend.Window.Displays[^1].Mode);
        Assert.Null(game.Windows.MainWindow.ExclusiveMode);
    }

    [Fact]
    public void Keeps_the_display_and_warns_when_the_backend_refuses_it()
    {
        Probe game = null!;
        using Engine engine = _harness.Module("example.game", p => game = p).Start();
        _harness.Backend.Window!.RefuseDisplay = "no such mode";

        game.Context.Settings.Set(FramesSettings.Mode, "exclusive");
        _harness.Frame(engine);

        Assert.Equal(WindowMode.Windowed, game.Windows.MainWindow.Mode);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.EndsWith("no such mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Uses_the_backend_that_the_key_names()
    {
        _harness.Other = new FakeBackend("other");
        using Engine engine = _harness.Setting(FramesSettings.Backend, "OTHER").Start();

        Assert.Null(_harness.Backend.Window);
        Assert.NotNull(_harness.Other.Window);
    }

    [Fact]
    public void Uses_the_first_backend_and_warns_when_the_key_names_an_absent_one()
    {
        _harness.Other = new FakeBackend("other");
        using Engine engine = _harness.Setting(FramesSettings.Backend, "vulkan").Start();

        Assert.NotNull(_harness.Backend.Window);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'vulkan', which no module registers", StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_two_backends_of_the_same_name()
    {
        _harness.Other = new FakeBackend("FAKE");

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Start());

        Assert.Contains("both register a window backend named 'FAKE'", stop.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_distribution_without_a_backend()
    {
        _harness.WithBackend = false;

        ModuleCompositionException error = Assert.Throws<ModuleCompositionException>(() => _harness.Load());

        Assert.Contains("nobara.frames", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_changes_of_the_window_once_each()
    {
        Probe game = null!;
        var events = new List<WindowEventKind>();
        using Engine engine = _harness.Module("example.game", p =>
        {
            game = p;
            p.OnInitialize = p => p.Windows.Subscribe(p.Context, e => events.Add(e.Kind));
        }).Start();

        _harness.Backend.Queue(WindowEventKind.Resized, w => (w.Size, w.PixelSize) = (new WindowSize(800, 600), new WindowSize(1200, 900)));
        _harness.Backend.Queue(WindowEventKind.Resized, _ => { });
        _harness.Backend.Queue(WindowEventKind.FocusLost, w => w.HasFocus = false);
        _harness.Backend.Queue(WindowEventKind.Minimized, w => w.IsMinimized = true);
        _harness.Frame(engine);

        Assert.Equal([WindowEventKind.Resized, WindowEventKind.ScaleChanged, WindowEventKind.FocusLost, WindowEventKind.Minimized], events);
        IWindow window = game.Windows.MainWindow;
        Assert.Equal(new WindowSize(800, 600), window.Size);
        Assert.Equal(new WindowSize(1200, 900), window.PixelSize);
        Assert.Equal(1.5f, window.Scale);
        Assert.False(window.HasFocus);
        Assert.True(window.IsMinimized);
    }

    [Fact]
    public void Removes_a_handler_whose_handle_is_disposed()
    {
        var events = new List<WindowEventKind>();
        WindowSubscription subscription = null!;
        using Engine engine = _harness.Module("example.game", p => p.OnInitialize = p => subscription = p.Windows.Subscribe(p.Context, e => events.Add(e.Kind))).Start();

        subscription.Dispose();
        _harness.Backend.Queue(WindowEventKind.FocusLost, w => w.HasFocus = false);
        _harness.Frame(engine);

        Assert.Empty(events);
        Assert.False(subscription.IsActive);
    }

    [Fact]
    public void Relays_the_focus_and_the_accepted_close_on_the_event_bus()
    {
        _harness.WithEvents = true;
        var relayed = new List<string>();
        using Engine engine = _harness.Module("example.game", p => p.OnInitialize = p =>
        {
            var bus = p.Context.Resolve<IEventBus>();
            bus.Subscribe(p.Context, (ref WindowFocusChanged e) => relayed.Add($"focus {e.HasFocus}"));
            bus.Subscribe(p.Context, (ref WindowCloseRequested e) => relayed.Add("close"));
        }).Start();

        _harness.Backend.Queue(WindowEventKind.FocusLost, w => w.HasFocus = false);
        _harness.Backend.Queue(WindowEventKind.FocusGained, w => w.HasFocus = true);
        _harness.Backend.Queue(sink => sink.OnCloseRequested());
        _harness.Frame(engine);

        Assert.Equal(["focus False", "focus True", "close"], relayed);
    }

    [Fact]
    public void A_cancelled_close_keeps_the_game_running()
    {
        var requests = new List<CloseRequest>();
        using Engine engine = _harness.Module("example.game", p => p.OnInitialize = p => p.Windows.SubscribeCloseRequested(p.Context, r =>
        {
            requests.Add(r);
            r.Cancel();
        })).Start();

        _harness.Backend.Queue(sink => sink.OnCloseRequested());
        _harness.Frame(engine);

        Assert.True(Assert.Single(requests).IsCancelled);
        Assert.False(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void An_accepted_close_stops_the_game()
    {
        using Engine engine = _harness.Start();

        _harness.Backend.Queue(sink => sink.OnCloseRequested());
        _harness.Frame(engine);

        Assert.True(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void An_accepted_close_goes_to_the_module_that_intercepts_stop_requests()
    {
        var received = new List<ulong>();
        using Engine engine = _harness.Module(
            "example.flow",
            p => p.OnRegister = p => p.Context.InterceptStopRequests(() => received.Add(p.Context.Loop.Frame)),
            LoopReservations.Contexts).Start();

        _harness.Backend.Queue(sink => sink.OnCloseRequested());
        _harness.Frame(engine);
        Assert.Empty(received);
        Assert.False(engine.MainContext.IsStopRequested);

        // The request reaches the module at the synchronization point of the next frame.
        _harness.Frame(engine);
        Assert.Equal([2UL], received);
        Assert.False(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void An_exception_of_a_handler_is_charged_to_its_module()
    {
        using Engine engine = _harness.Module("example.game", p => p.OnInitialize = p => p.Windows.Subscribe(p.Context, _ => throw new InvalidOperationException("broken"))).Start();

        _harness.Backend.Queue(WindowEventKind.FocusLost, w => w.HasFocus = false);
        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Frame(engine));

        Assert.Equal("example.game", stop.ModuleId);
    }

    [Fact]
    public void Hands_out_the_raw_input_of_the_frame()
    {
        IRawInput input = null!;
        var seen = new List<string>();
        using Engine engine = _harness.Module("example.input", p => p.OnInitialize = p => input = p.Windows.Input).Start();

        _harness.Backend.Queue(sink =>
        {
            sink.OnInput(RawInputEvent.Key(true, Scancode.W, KeyModifiers.LeftShift), []);
            sink.OnInput(RawInputEvent.MouseMotion(10, 20, 1, -2), []);
            sink.OnInput(RawInputEvent.Text(), "é!");
            sink.OnInput(RawInputEvent.Composition(1, 2), "かな");
            sink.OnInput(RawInputEvent.Key(false, Scancode.W, KeyModifiers.None), []);
        });
        _harness.Frame(engine);

        foreach (RawInputEvent e in input.Events)
        {
            seen.Add($"{e.Kind} {e.Scancode} {e.Modifiers} {e.X},{e.Y} {e.DeltaX},{e.DeltaY} '{input.GetText(e)}' {e.SelectionStart}/{e.SelectionLength}");
        }

        Assert.Equal(
            [
                "KeyDown W LeftShift 0,0 0,0 '' 0/0",
                "MouseMotion Unknown None 10,20 1,-2 '' 0/0",
                "TextInput Unknown None 0,0 0,0 'é!' 0/0",
                "TextComposition Unknown None 0,0 0,0 'かな' 1/2",
                "KeyUp W None 0,0 0,0 '' 0/0",
            ],
            seen);
        Assert.Equal(engine.MainContext.Frame, input.Frame);

        // The next frame starts empty.
        _harness.Frame(engine);
        Assert.True(input.Events.IsEmpty);
    }

    [Fact]
    public void Names_keys_as_the_layout_does_until_it_changes()
    {
        IRawInput input = null!;
        using Engine engine = _harness.Module("example.input", p => p.OnStart = p => input = p.Windows.Input).Start();

        Assert.Equal("Z", input.GetKeyName(Scancode.W));
        int version = input.LayoutVersion;
        _harness.Backend.Queue(sink => sink.OnKeyboardLayoutChanged());
        _harness.Frame(engine);

        Assert.Equal(version + 1, input.LayoutVersion);
    }

    [Fact]
    public void Turns_text_input_and_relative_mouse_mode_on_and_off()
    {
        IWindow window = null!;
        using Engine engine = _harness.Module("example.input", p => p.OnStart = p => window = p.Windows.MainWindow).Start();

        window.StartTextInput();
        window.RelativeMouseMode = true;
        Assert.True(window.IsTextInputActive);
        Assert.True(_harness.Backend.Window!.TextInput);
        Assert.True(_harness.Backend.Window.RelativeMouse);

        window.StopTextInput();
        window.RelativeMouseMode = false;
        Assert.False(window.IsTextInputActive);
        Assert.False(_harness.Backend.Window.TextInput);
        Assert.False(_harness.Backend.Window.RelativeMouse);
    }

    [Fact]
    public void Limits_the_frame_rate_without_focus()
    {
        Probe game = null!;
        using Engine engine = _harness.Module("example.game", p => game = p).Start();

        // With the focus, no wait.
        _harness.Frame(engine, TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimeSpan.Zero, _harness.Backend.Waits[^1]);

        _harness.Backend.Queue(WindowEventKind.FocusLost, w => w.HasFocus = false);
        _harness.Frame(engine, TimeSpan.FromMilliseconds(5));

        // Without it, each frame waits for the rest of 1/30 s after the 5 ms it took.
        _harness.Frame(engine, TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 30) - TimeSpan.FromMilliseconds(5), _harness.Backend.Waits[^1]);
        _harness.Frame(engine, TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 30) - TimeSpan.FromMilliseconds(5), _harness.Backend.Waits[^1]);

        // The key changes the rate while the game runs.
        game.Context.Settings.Set(FramesSettings.BackgroundFps, 10);
        _harness.Frame(engine, TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(95), _harness.Backend.Waits[^1]);

        // Back in focus, no wait.
        _harness.Backend.Queue(WindowEventKind.FocusGained, w => w.HasFocus = true);
        _harness.Frame(engine, TimeSpan.FromMilliseconds(5));
        _harness.Frame(engine, TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimeSpan.Zero, _harness.Backend.Waits[^1]);
    }

    [Fact]
    public void Frees_the_window_and_the_backend_when_it_stops()
    {
        using Engine engine = _harness.Start();

        engine.MainContext.Stop();

        Assert.True(_harness.Backend.Window!.Disposed);
        Assert.True(_harness.Backend.Disposed);
    }
}
