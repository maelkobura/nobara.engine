// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Frames.SDL2;
using Nobara.Graphics;
using Nobara.Graphics.OpenGL;
using Nobara.Input;
using Nobara.Input.Build;
using Nobara.Renderer;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Debug.Tests.Support;

/// <summary>
/// Runs the engine with nFrames, nInput, nGraphics, nRenderer, nDebug, and a
/// test game: a pipeline that clears the target and declares the insertion
/// point of the user interface, and a layer of the game that reads the raw
/// input and two actions, <c>Fire</c> on the left button and <c>Jump</c> on
/// Space. Other test modules run the code a test gives them.
/// </summary>
internal sealed class DebugHarness : IDisposable
{
    public const string Game = "example.game";
    public const string Window = "example.window";
    public const string Graphics = "example.graphics";
    public const string Fire = "inputaction:example.game.fire";
    public const string Jump = "inputaction:example.game.jump";

    private static readonly Lazy<byte[]> s_gameResources = new(() => Bundle(Game, """
        <Nobara version="1.0.0" namespace="Example/Game">
          <InputAction><Name>Fire</Name><Content><Value>Button</Value><Bindings><Key>Mouse.Left</Key></Bindings></Content></InputAction>
          <InputAction><Name>Jump</Name><Content><Value>Button</Value><Bindings><Key>Key.Space</Key></Bindings></Content></InputAction>
        </Nobara>
        """));

    private readonly List<TestModule> _modules = [];

    public DebugHarness(bool gpu = false)
    {
        Gpu = gpu;
        Root = Path.Combine(Path.GetTempPath(), "nobara-debug-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets whether the engine runs on SDL2 and OpenGL.</summary>
    public bool Gpu { get; }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the fake window backend.</summary>
    public FakeWindowBackend Windows { get; } = new();

    /// <summary>Gets the fake graphics backend.</summary>
    public FakeGraphicsBackend Backend { get; } = new();

    /// <summary>Gets or sets whether the engine runs in development mode.</summary>
    public bool DevelopmentMode { get; set; } = true;

    /// <summary>Gets or sets whether the profiler measures from the start.</summary>
    public bool Profiling { get; set; }

    /// <summary>Gets or sets whether the distribution holds nDebug; without it, the game does not depend on it.</summary>
    public bool WithDebug { get; set; } = true;

    /// <summary>Gets the values the distribution gives to keys: <c>Set</c> for its own keys, <c>Default</c> for the keys of the player.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    /// <summary>Gets the keys of the player that the distribution locks, with their value.</summary>
    public Dictionary<string, string> Locks { get; } = [];

    /// <summary>Gets the error policies of test modules, by identifier.</summary>
    public Dictionary<string, F.Policy> Policies { get; } = [];

    /// <summary>Gets the module of the test game.</summary>
    public GameProbe GameModule { get; private set; } = null!;

    /// <summary>Gets the service of nDebug.</summary>
    public IDebugUi Debug => GameModule.Context.Resolve<IDebugUi>();

    /// <summary>Gets the implementation of the service of nDebug.</summary>
    public DebugUi Ui => (DebugUi)Debug;

    /// <summary>Gets the fake window.</summary>
    public FakeWindow FakeWindow => Windows.Window!;

    /// <summary>Gets the fake device.</summary>
    public FakeDevice Device => Backend.Device!;

    /// <summary>Gets the errors logged.</summary>
    public IEnumerable<string> Errors => Log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message);

    /// <summary>Gets nInput, as the compiler reads it from its assembly.</summary>
    public static CompiledModule InputImage { get; } = CompiledModule.FromAssembly(typeof(InputModule).Assembly.Location)!;

    /// <summary>Adds a client module of the test.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="debug">How it depends on nDebug: <see langword="null"/> for not at all, <see langword="true"/> for an optional dependency, <see langword="false"/> for a required one.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    public DebugHarness Module(string id, bool? debug, Action<Probe>? behavior = null)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
        if (debug is bool optional)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = DebugModule.ModuleId, Range = "^1.0", Optional = optional });
        }

        _modules.Add(new TestModule(manifest, behavior));
        return this;
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
        };
        foreach ((string module, F.Policy policy) in Policies)
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = module, Policy = policy });
        }

        if (Gpu)
        {
            distribution.Settings.Add(new F.SettingValue { Key = GraphicsSettings.VSync, Operation = F.SettingValue.Types.Operation.Default, Value = "off" });
        }

        foreach ((string key, string value) in Settings)
        {
            F.SettingValue.Types.Operation operation = key.StartsWith("debug.", StringComparison.Ordinal) ? F.SettingValue.Types.Operation.Set : F.SettingValue.Types.Operation.Default;
            distribution.Settings.Add(new F.SettingValue { Key = key, Operation = operation, Value = value });
        }

        foreach ((string key, string value) in Locks)
        {
            distribution.Settings.Add(new F.SettingValue { Key = key, Operation = F.SettingValue.Types.Operation.Lock, Value = value });
        }

        IEnumerable<string> modules = new[] { "nobara.frames", WindowId, "nobara.graphics", GraphicsId, RendererModule.ModuleId, InputModule.ModuleId, Game }
            .Concat(WithDebug ? [DebugModule.ModuleId] : [])
            .Concat(_modules.Select(m => m.Manifest.Id));
        foreach (string id in modules)
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
            Profiling = Profiling,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        try
        {
            engine.MainContext.Start();
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        return engine;
    }

    /// <summary>Runs one frame that lasts 1/60 s.</summary>
    public void Frame(Engine engine, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
            engine.MainContext.RunFrame();
        }
    }

    /// <summary>Queues a movement of the mouse to a position, in logical units of the window, for the next frame.</summary>
    public DebugHarness MoveTo(float x, float y)
    {
        Windows.Queue(RawInputEvent.MouseMotion(x, y, 1, 1));
        return this;
    }

    /// <summary>Queues a press and a release of the left button at a position, for the next frame.</summary>
    public DebugHarness Click(float x, float y) => Button(x, y, down: true).Button(x, y, down: false);

    /// <summary>Queues the left button at a position, for the next frame.</summary>
    public DebugHarness Button(float x, float y, bool down)
    {
        Windows.Queue(RawInputEvent.MouseButtonEvent(down, MouseButton.Left, 1, x, y));
        return this;
    }

    /// <summary>Queues a press and a release of a key, for the next frame.</summary>
    public DebugHarness Tap(Scancode key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Windows.Queue(RawInputEvent.Key(true, key, modifiers));
        Windows.Queue(RawInputEvent.Key(false, key, modifiers));
        return this;
    }

    /// <summary>Queues typed text, for the next frame.</summary>
    public DebugHarness Text(string text)
    {
        Windows.Queue(RawInputEvent.Text(), text);
        return this;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private string WindowId => Gpu ? "nobara.frames.sdl2" : Window;

    private string GraphicsId => Gpu ? "nobara.graphics.opengl" : Graphics;

    private static byte[] Bundle(string moduleId, string nof)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-debug-tests", "nof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "game.nof"), nof);
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = moduleId,
                ResourcesDirectory = root,
                Dependencies = [InputImage],
                Plugins = [new InputPlugin()],
            });
            CompilationResult result = compiler.Compile(Directory.EnumerateFiles(root, "*.nof"));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return result.Bundle!.ToByteArray();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Embedded(Assembly assembly)
    {
        using Stream stream = assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)
            ?? throw new InvalidOperationException($"{assembly.GetName().Name} embeds no compiled manifest.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] BackendManifest(string id, string of)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Client, BackendOf = of };
        manifest.Dependencies.Add(new F.Dependency { Id = of, Range = "^1.0" });
        if (of == "nobara.graphics")
        {
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
        }

        return manifest.ToByteArray();
    }

    private byte[] GameManifest()
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = Game, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
        foreach (string dependency in new[] { "nobara.frames", "nobara.graphics", RendererModule.ModuleId, InputModule.ModuleId })
        {
            manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        if (WithDebug)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = DebugModule.ModuleId, Range = "^1.0" });
        }

        return manifest.ToByteArray();
    }

    private sealed record TestModule(F.ModuleManifest Manifest, Action<Probe>? Behavior);

    private sealed class Source(DebugHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.frames" => Embedded(typeof(FramesModule).Assembly),
                "nobara.frames.sdl2" => Embedded(typeof(Sdl2Module).Assembly),
                "nobara.graphics" => Embedded(typeof(GraphicsModule).Assembly),
                "nobara.graphics.opengl" => Embedded(typeof(OpenGLModule).Assembly),
                RendererModule.ModuleId => Embedded(typeof(RendererModule).Assembly),
                InputModule.ModuleId => Embedded(typeof(InputModule).Assembly),
                DebugModule.ModuleId => Embedded(typeof(DebugModule).Assembly),
                Window => BackendManifest(Window, "nobara.frames"),
                Graphics => BackendManifest(Graphics, "nobara.graphics"),
                Game => harness.GameManifest(),
                _ => harness._modules.Single(m => m.Manifest.Id == module.Id).Manifest.ToByteArray(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image)
        {
            switch (image.Id)
            {
                case "nobara.frames":
                    return new LoadedModule(typeof(FramesModule).Assembly, () => new FramesModule());
                case "nobara.frames.sdl2":
                    return new LoadedModule(typeof(Sdl2Module).Assembly, () => new Sdl2Module());
                case "nobara.graphics":
                    return new LoadedModule(typeof(GraphicsModule).Assembly, () => new GraphicsModule());
                case "nobara.graphics.opengl":
                    return new LoadedModule(typeof(OpenGLModule).Assembly, () => new OpenGLModule());
                case RendererModule.ModuleId:
                    return new LoadedModule(typeof(RendererModule).Assembly, () => new RendererModule());
                case InputModule.ModuleId:
                    return new LoadedModule(typeof(InputModule).Assembly, () => new InputModule());
                case DebugModule.ModuleId:
                    return new LoadedModule(typeof(DebugModule).Assembly, () => new DebugModule());
                case Window:
                    return new LoadedModule(null, () => new WindowBackendModule(harness.Windows));
                case Graphics:
                    return new LoadedModule(null, () => new GraphicsBackendModule(harness.Backend));
                case Game:
                    return new LoadedModule(
                        harness.Policies.ContainsKey(Game) ? typeof(GameProbe).Assembly : null,
                        () => harness.GameModule = new GameProbe(),
                        name => name == "nobara/resources.pb" ? new MemoryStream(s_gameResources.Value) : null);
            }

            TestModule test = harness._modules.Single(m => m.Manifest.Id == image.Id);
            return new LoadedModule(
                harness.Policies.ContainsKey(image.Id) ? typeof(Probe).Assembly : null,
                () =>
                {
                    var probe = new Probe();
                    test.Behavior?.Invoke(probe);
                    return probe;
                });
        }
    }

    private sealed class WindowBackendModule(FakeWindowBackend backend) : IModule
    {
        public void Initialize(ModuleContext context) => context.Resolve<IWindowBackends>().Register(context, backend);
    }

    private sealed class GraphicsBackendModule(FakeGraphicsBackend backend) : IModule
    {
        public void Initialize(ModuleContext context) => context.Resolve<IGraphicsBackends>().Register(context, backend);
    }
}

/// <summary>
/// The test game: it registers <see cref="TestPipeline"/>, and a layer of
/// priority <see cref="InputLayerPriority.Game"/> that asks for relative mode
/// and records the raw input and the actions that reach it.
/// </summary>
internal sealed class GameProbe : IModule, IInputLayerHandler
{
    public ModuleContext Context { get; private set; } = null!;

    public InputLayer Layer { get; private set; } = null!;

    public TestPipeline Pipeline { get; private set; } = null!;

    public List<RawInputEvent> Raw { get; } = [];

    public List<(string Action, ActionEdge Edge)> Actions { get; } = [];

    public void Register(ModuleContext context) => Context = context;

    public void Initialize(ModuleContext context)
    {
        Pipeline = new TestPipeline();
        context.Resolve<IRenderer>().RegisterPipeline(context, Pipeline);
        Layer = context.Resolve<IInputSystem>().AddLayer(context, new InputLayerOptions
        {
            Name = "game",
            Priority = InputLayerPriority.Game,
            Cursor = CursorRequest.Relative,
            ReceivesRawInput = true,
            Handler = this,
        });
    }

    public InputResult OnRawInput(in RawInputEvent e, IRawInput input)
    {
        Raw.Add(e);
        return InputResult.Pass;
    }

    public InputResult OnAction(InputAction action, ActionEdge edge)
    {
        Actions.Add((action.Id, edge));
        return InputResult.Pass;
    }

    /// <summary>Tells whether the game saw a press of the left button.</summary>
    public bool SawClick() => Raw.Any(e => e is { Kind: RawInputKind.MouseButtonDown, Button: MouseButton.Left });

    /// <summary>Forgets what the game saw.</summary>
    public void Clear()
    {
        Raw.Clear();
        Actions.Clear();
    }
}

/// <summary>A test module whose steps run the code a test gives it.</summary>
internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public Action<Probe>? OnRegister { get; set; }

    public Action<Probe>? OnInitialize { get; set; }

    public Action<Probe>? OnStart { get; set; }

    public Action<Probe>? OnStop { get; set; }

    public void Register(ModuleContext context)
    {
        Context = context;
        OnRegister?.Invoke(this);
    }

    public void Initialize(ModuleContext context) => OnInitialize?.Invoke(this);

    public void Start(ModuleContext context) => OnStart?.Invoke(this);

    public void Stop(ModuleContext context) => OnStop?.Invoke(this);
}

/// <summary>A pipeline that clears the target to <see cref="Background"/> and declares the insertion point of the user interface.</summary>
internal sealed class TestPipeline : IRenderPipeline, IRenderPass
{
    /// <summary>The color of the target before the user interface, linear.</summary>
    public static readonly ColorF Background = new(1, 0, 0, 1);

    public string Name => "test";

    public void Build(RenderGraphBuilder graph)
    {
        graph.AddRasterPass("clear", this).Color(graph.Target, LoadAction.Clear, Background);
        graph.InsertionPoint(RenderInsertionPoints.Ui, graph.Target);
    }

    public void Execute(RenderPassContext context)
    {
    }
}

/// <summary>A debug window that runs a function as its content.</summary>
internal sealed class DelegateWindow(Action<DebugWindow>? draw = null) : IDebugWindow
{
    public int Draws { get; private set; }

    public void Draw(DebugWindow window)
    {
        Draws++;
        draw?.Invoke(window);
    }
}
