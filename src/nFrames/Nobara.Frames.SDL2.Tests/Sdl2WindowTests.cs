// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Silk.NET.SDL;
using F = Nobara.Formats;
using SdlEvent = Silk.NET.SDL.Event;
using SdlScancode = Silk.NET.SDL.Scancode;

namespace Nobara.Frames.SDL2.Tests;

/// <summary>
/// The SDL2 backend with a real window. These tests open a window on the
/// desktop and change the display mode of the primary monitor.
/// </summary>
[Trait("Category", "Window")]
[Collection(nameof(WindowCollection))]
public sealed unsafe class Sdl2WindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nobara-sdl2-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Opens_a_titled_window_changes_its_display_while_running_and_closes_it()
    {
        Probe game = null!;
        using (Engine engine = Load(p => game = p))
        {
            engine.MainContext.Start();
            IWindowSystem windows = game.Context.Resolve<IWindowSystem>();
            IWindow window = windows.MainWindow;
            Assert.Equal("sdl2", windows.BackendName);
            Assert.Equal($"Example 0.1.0 — {RuntimeInformation.RuntimeIdentifier} — Client", window.Title);
            Assert.Equal(NativeSurfaceKind.Win32, window.Surface.Kind);
            Assert.NotEqual(0, window.Surface.Window);
            Assert.NotNull(window.OpenGL);
            Assert.NotEmpty(windows.Monitors);
            Assert.Equal(WindowMode.Windowed, window.Mode);
            Assert.True(window.Size.Width > 0 && window.PixelSize.Width >= window.Size.Width);

            game.Context.Settings.Set(FramesSettings.Mode, "borderless");
            engine.MainContext.RunFrame();
            Assert.Equal(WindowMode.Borderless, window.Mode);
            MonitorInfo monitor = windows.Monitors[window.Monitor];
            Assert.Equal(monitor.Bounds.Width, window.Size.Width);

            game.Context.Settings.Set(FramesSettings.Mode, "exclusive");
            engine.MainContext.RunFrame();
            Assert.Equal(WindowMode.Exclusive, window.Mode);
            Assert.NotNull(window.ExclusiveMode);

            game.Context.Settings.Set(FramesSettings.Mode, "windowed");
            engine.MainContext.RunFrame();
            Assert.Equal(WindowMode.Windowed, window.Mode);

            window.Title = "Example — test";
            Assert.Equal("Example — test", window.Title);
        }

        // The engine stopped the context: SDL is shut down and can start again.
        using Sdl sdl = Sdl.GetApi();
        Assert.Equal(0u, sdl.WasInit(0x20));
    }

    [Fact]
    public void Delivers_keys_text_and_close_requests_from_the_event_queue_of_sdl()
    {
        Probe game = null!;
        using Engine engine = Load(p => game = p);
        engine.MainContext.Start();
        IRawInput input = game.Context.Resolve<IWindowSystem>().Input;
        using Sdl sdl = Sdl.GetApi();

        var key = new SdlEvent { Key = new KeyboardEvent { Type = (uint)EventType.Keydown, State = 1, Keysym = new Keysym { Scancode = SdlScancode.ScancodeW, Mod = (ushort)Keymod.Lshift } } };
        sdl.PushEvent(&key);
        var text = new SdlEvent { Text = new TextInputEvent { Type = (uint)EventType.Textinput } };
        "é"u8.CopyTo(new Span<byte>(text.Text.Text, 32));
        sdl.PushEvent(&text);
        engine.MainContext.RunFrame();

        RawInputEvent[] events = input.Events.ToArray();
        RawInputEvent down = Assert.Single(events, e => e.Kind == RawInputKind.KeyDown);
        Assert.Equal(Scancode.W, down.Scancode);
        Assert.Equal(KeyModifiers.LeftShift, down.Modifiers & KeyModifiers.LeftShift);
        Assert.Equal("é", input.GetText(Assert.Single(events, e => e.Kind == RawInputKind.TextInput)).ToString());
        Assert.False(string.IsNullOrEmpty(input.GetKeyName(Scancode.W)));

        var quit = new SdlEvent { Type = (uint)EventType.Quit };
        sdl.PushEvent(&quit);
        engine.MainContext.RunFrame();
        Assert.True(engine.MainContext.IsStopRequested);
    }

    private Engine Load(Action<Probe> setup)
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
        };
        foreach (string id in new[] { "nobara.frames", "nobara.frames.sdl2", "example.game" })
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        return Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(setup),
            Paths = EnginePaths.At(_root),
            FramePacing = FramePacing.Free,
            LogToConsole = false,
            LogToFile = false,
        });
    }

    private sealed class Source(Action<Probe> setup) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.frames" => Compiled(typeof(FramesModule)),
                "nobara.frames.sdl2" => Compiled(typeof(Sdl2Module)),
                _ => GameManifest(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.frames" => new LoadedModule(typeof(FramesModule).Assembly, () => new FramesModule()),
            "nobara.frames.sdl2" => new LoadedModule(typeof(Sdl2Module).Assembly, () => new Sdl2Module()),
            _ => new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe();
                setup(probe);
                return probe;
            }),
        };

        private static byte[] Compiled(Type module)
        {
            using Stream stream = module.Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static byte[] GameManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "example.game", Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
            return manifest.ToByteArray();
        }
    }
}

/// <summary>A module of the test assembly that keeps its context.</summary>
internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public void Register(ModuleContext context) => Context = context;
}

/// <summary>SDL keeps one state per process: the window tests run one at a time.</summary>
[CollectionDefinition(nameof(WindowCollection), DisableParallelization = true)]
public sealed class WindowCollection
{
}
