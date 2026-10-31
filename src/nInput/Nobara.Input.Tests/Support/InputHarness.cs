// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Input.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Input.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real nFrames and nInput, a
/// fake window backend that hands out the raw input a test scripts, and test
/// modules whose resources are compiled by the tests and whose steps run the
/// code a test gives them.
/// </summary>
internal sealed class InputHarness : IDisposable
{
    public const string Window = "example.window";

    private readonly List<TestModule> _modules = [];

    public InputHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-input-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the window backend.</summary>
    public FakeBackend Backend { get; } = new();

    /// <summary>Gets or sets whether the engine runs in development mode, which reloads resources.</summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>Gets the user settings the distribution sets, by key.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    /// <summary>Gets the error policies of test modules, by identifier; such a module owns the code of the test assembly.</summary>
    public Dictionary<string, F.Policy> Policies { get; } = [];

    /// <summary>Gets nInput, as the compiler reads it from its assembly.</summary>
    public static CompiledModule Input { get; } = CompiledModule.FromAssembly(typeof(InputModule).Assembly.Location)!;

    /// <summary>Gets the path of <c>config/input.xml</c>.</summary>
    public string ConfigFile => Path.Combine(Root, "config", "input.xml");

    /// <summary>Wraps resource elements in a root element.</summary>
    public static string Nof(string ns, string body) => $"<Nobara version=\"1.0.0\" namespace=\"{ns}\">\n{body}\n</Nobara>";

    /// <summary>Compiles the resources of a module that depends on nInput.</summary>
    /// <param name="moduleId">The module.</param>
    /// <param name="nof">The content of its single <c>.nof</c> file, <c>input.nof</c>.</param>
    /// <param name="dependencies">Compiled modules it depends on, besides nInput.</param>
    public static CompilationResult Compile(string moduleId, string nof, params CompiledModule[] dependencies)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-input-tests", "nof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "input.nof"), nof);
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = moduleId,
                ResourcesDirectory = root,
                Dependencies = [Input, .. dependencies],
                Plugins = [new InputPlugin()],
            });
            return compiler.Compile(Directory.EnumerateFiles(root, "*.nof"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Compiles resources that must compile.</summary>
    public static F.ResourceBundle Bundle(string moduleId, string nof, params CompiledModule[] dependencies)
    {
        CompilationResult result = Compile(moduleId, nof, dependencies);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Bundle!;
    }

    /// <summary>Adds a business module that depends on nInput.</summary>
    /// <param name="id">The identifier of the module.</param>
    /// <param name="nof">Its resources, or <see langword="null"/> for none.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    /// <param name="dependsOn">Other modules of the test it depends on, with their compiled form for the compiler.</param>
    public InputHarness Module(string id, string? nof = null, Action<Probe>? behavior = null, params (string Id, CompiledModule Compiled)[] dependsOn)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
        manifest.Dependencies.Add(new F.Dependency { Id = InputModule.ModuleId, Range = "^1.0" });
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
        foreach ((string dependency, _) in dependsOn)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        byte[]? resources = nof is null ? null : Bundle(id, nof, [.. dependsOn.Select(d => d.Compiled)]).ToByteArray();
        _modules.Add(new TestModule(manifest, resources, behavior));
        return this;
    }

    /// <summary>Gets the compiled form of a test module, for a module that overrides its resources.</summary>
    public static CompiledModule Compiled(string id, string nof)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
        manifest.Dependencies.Add(new F.Dependency { Id = InputModule.ModuleId, Range = "^1.0" });
        return new CompiledModule(manifest, null, Bundle(id, nof));
    }

    /// <summary>Loads and starts the engine.</summary>
    public Engine Start()
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
            Loop = StandardLoop(),
        };
        foreach ((string module, F.Policy policy) in Policies)
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = module, Policy = policy });
        }

        foreach ((string key, string value) in Settings)
        {
            distribution.Settings.Add(new F.SettingValue { Key = key, Operation = F.SettingValue.Types.Operation.Default, Value = value });
        }

        foreach (string id in new[] { "nobara.frames", Window, InputModule.ModuleId }.Concat(_modules.Select(m => m.Manifest.Id)))
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        Engine engine = Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(Root),
            TimeProvider = Clock,
            FramePacing = FramePacing.Free,
            DevelopmentMode = DevelopmentMode,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        engine.MainContext.Start();
        return engine;
    }

    /// <summary>Runs one frame that lasts <paramref name="seconds"/>, 1/60 s by default.</summary>
    public void Frame(Engine engine, double seconds = 1.0 / 60)
    {
        Clock.Advance(TimeSpan.FromSeconds(seconds));
        engine.MainContext.RunFrame();
    }

    /// <summary>Queues a key for the next frame.</summary>
    public InputHarness Key(Scancode key, bool down, bool repeat = false)
    {
        Backend.Queue(RawInputEvent.Key(down, key, KeyModifiers.None, repeat));
        return this;
    }

    /// <summary>Queues a press and a release of a key for the next frame.</summary>
    public InputHarness Tap(Scancode key) => Key(key, true).Key(key, false);

    /// <summary>Queues a mouse button for the next frame.</summary>
    public InputHarness Button(MouseButton button, bool down)
    {
        Backend.Queue(RawInputEvent.MouseButtonEvent(down, button, 1, 10, 10));
        return this;
    }

    /// <summary>Queues a movement of the mouse for the next frame.</summary>
    public InputHarness Move(float dx, float dy)
    {
        Backend.Queue(RawInputEvent.MouseMotion(10, 10, dx, dy));
        return this;
    }

    /// <summary>Queues a movement of the wheel for the next frame.</summary>
    public InputHarness Wheel(float dx, float dy)
    {
        Backend.Queue(RawInputEvent.MouseWheel(10, 10, dx, dy));
        return this;
    }

    /// <summary>Queues typed text for the next frame.</summary>
    public InputHarness Text(string text)
    {
        Backend.Queue(RawInputEvent.Text(), text);
        return this;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static F.Loop StandardLoop()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "Render", Side = F.Side.Client });
        return loop;
    }

    private static byte[] Embedded(Assembly assembly)
    {
        using Stream stream = assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)
            ?? throw new InvalidOperationException($"{assembly.GetName().Name} embeds no compiled manifest.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static F.ModuleManifest BackendManifest()
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = Window, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Client, BackendOf = "nobara.frames" };
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
        return manifest;
    }

    private sealed record TestModule(F.ModuleManifest Manifest, byte[]? Resources, Action<Probe>? Behavior);

    private sealed class Source(InputHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.frames" => Embedded(typeof(IWindowSystem).Assembly),
                InputModule.ModuleId => Embedded(typeof(InputModule).Assembly),
                Window => BackendManifest().ToByteArray(),
                _ => harness._modules.Single(p => p.Manifest.Id == module.Id).Manifest.ToByteArray(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image)
        {
            switch (image.Id)
            {
                case "nobara.frames":
                    return new LoadedModule(typeof(IWindowSystem).Assembly, () => new FramesModule());
                case InputModule.ModuleId:
                    return new LoadedModule(typeof(InputModule).Assembly, () => new InputModule());
                case Window:
                    return new LoadedModule(null, () => new BackendModule(harness.Backend));
            }

            TestModule test = harness._modules.Single(p => p.Manifest.Id == image.Id);
            return new LoadedModule(
                harness.Policies.ContainsKey(image.Id) ? typeof(Probe).Assembly : null,
                () =>
                {
                    var probe = new Probe();
                    test.Behavior?.Invoke(probe);
                    return probe;
                },
                name => name == "nobara/resources.pb" && test.Resources is not null ? new MemoryStream(test.Resources) : null);
        }
    }

    /// <summary>The module of the fake backend: it registers it during its initialization.</summary>
    private sealed class BackendModule(FakeBackend backend) : IModule
    {
        public void Initialize(ModuleContext context) => context.Resolve<IWindowBackends>().Register(context, backend);
    }
}

/// <summary>A test module whose steps run the code a test gives it, with the service of nInput at hand.</summary>
internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public IInputSystem Input { get; private set; } = null!;

    public Action<Probe>? OnRegister { get; set; }

    public Action<Probe>? OnInitialize { get; set; }

    public Action<Probe>? OnStart { get; set; }

    public void Register(ModuleContext context)
    {
        Context = context;
        OnRegister?.Invoke(this);
    }

    public void Initialize(ModuleContext context)
    {
        Input = context.Resolve<IInputSystem>();
        OnInitialize?.Invoke(this);
    }

    public void Start(ModuleContext context) => OnStart?.Invoke(this);
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
