// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Frames;
using F = Nobara.Formats;

namespace Nobara.Graphics.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real nFrames and nGraphics, a
/// fake window backend, fake graphics backends, and a game module that keeps
/// its context.
/// </summary>
internal sealed class GraphicsHarness : IDisposable
{
    private readonly List<F.SettingValue> _settings = [];

    public GraphicsHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-graphics-tests", Guid.NewGuid().ToString("N"));
    }

    public string Root { get; }

    public MemoryLogSink Log { get; } = new();

    public FakeWindowBackend Windows { get; } = new();

    /// <summary>Gets the graphics backends the backend module registers, in order.</summary>
    public List<FakeGraphicsBackend> Backends { get; } = [new FakeGraphicsBackend("fake")];

    /// <summary>Gets the game module, once loaded.</summary>
    public Probe Game { get; private set; } = null!;

    /// <summary>Gives a key a value: the default of a user key, or the value of a distribution key.</summary>
    public GraphicsHarness Setting(string key, string value)
    {
        F.SettingValue.Types.Operation operation = key == GraphicsSettings.FramesInFlight ? F.SettingValue.Types.Operation.Set : F.SettingValue.Types.Operation.Default;
        _settings.Add(new F.SettingValue { Key = key, Operation = operation, Value = value });
        return this;
    }

    public Engine Start()
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
        };
        distribution.Settings.AddRange(_settings);
        foreach (string id in new[] { "nobara.frames", "example.window", "nobara.graphics", "example.graphics", "example.game" })
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        Engine engine = Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(Root),
            FramePacing = FramePacing.Free,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        engine.MainContext.Start();
        return engine;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Source(GraphicsHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.frames" => Compiled(typeof(FramesModule)),
                "nobara.graphics" => Compiled(typeof(GraphicsModule)),
                "example.window" => Backend("example.window", "nobara.frames"),
                "example.graphics" => Backend("example.graphics", "nobara.graphics"),
                _ => Game(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.frames" => new LoadedModule(typeof(FramesModule).Assembly, () => new FramesModule()),
            "nobara.graphics" => new LoadedModule(typeof(GraphicsModule).Assembly, () => new GraphicsModule()),
            "example.window" => new LoadedModule(typeof(Probe).Assembly, () => new WindowBackendModule(harness)),
            "example.graphics" => new LoadedModule(typeof(Probe).Assembly, () => new GraphicsBackendModule(harness)),
            _ => new LoadedModule(typeof(Probe).Assembly, () => harness.Game = new Probe()),
        };

        private static byte[] Compiled(Type module)
        {
            using Stream stream = module.Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static byte[] Backend(string id, string of)
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Client, BackendOf = of };
            manifest.Dependencies.Add(new F.Dependency { Id = of, Range = "^1.0" });
            if (of == "nobara.graphics")
            {
                manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
            }

            return manifest.ToByteArray();
        }

        private static byte[] Game()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "example.game", Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.graphics", Range = "^1.0" });
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
            return manifest.ToByteArray();
        }
    }

    private sealed class WindowBackendModule(GraphicsHarness harness) : IModule
    {
        public void Initialize(ModuleContext context) => context.Resolve<IWindowBackends>().Register(context, harness.Windows);
    }

    private sealed class GraphicsBackendModule(GraphicsHarness harness) : IModule
    {
        public void Initialize(ModuleContext context)
        {
            foreach (FakeGraphicsBackend backend in harness.Backends)
            {
                context.Resolve<IGraphicsBackends>().Register(context, backend);
            }
        }
    }
}

/// <summary>A graphics backend that creates <see cref="FakeDevice"/> devices.</summary>
internal sealed class FakeGraphicsBackend(string name) : IGraphicsBackend
{
    public string Name { get; } = name;

    public FakeDevice? Device { get; private set; }

    public GraphicsDeviceOptions Options { get; private set; }

    public GraphicsDevice CreateDevice(IWindow window, in GraphicsDeviceOptions options)
    {
        Options = options;
        Device = new FakeDevice(framesInFlight: options.FramesInFlight);
        return Device;
    }
}

/// <summary>A module of the test assembly that keeps its context.</summary>
internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public void Register(ModuleContext context) => Context = context;
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
