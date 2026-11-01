// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Frames.SDL2;
using Nobara.Graphics;
using Nobara.Graphics.OpenGL;
using Nobara.Renderer;
using Nobara.Renderer.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Ungp.Tests.Support;

/// <summary>
/// Runs the engine with nFrames, nGraphics, nRenderer, UNGP, and a test game
/// whose resources the harness writes and compiles: a square mesh, and
/// materials of the standard shader of UNGP.
/// </summary>
internal sealed class UngpHarness : IDisposable
{
    public const string Game = "example.game";

    /// <summary>The color of the texture of <c>material:example.scene.red</c>, in sRGB.</summary>
    public static readonly (byte R, byte G, byte B) Red = (200, 40, 40);

    /// <summary>The color of the opaque half of the texture of <c>material:example.scene.leaf</c>, in sRGB.</summary>
    public static readonly (byte R, byte G, byte B) Leaf = (40, 160, 40);

    private static readonly Lazy<GameResources> s_resources = new(CompileGame);
    private readonly List<string> _folders = [];

    public UngpHarness(bool gpu = false)
    {
        Gpu = gpu;
        Root = Folder("data");
    }

    /// <summary>Gets whether the engine runs on the OpenGL backend.</summary>
    public bool Gpu { get; }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the values that the distribution gives to configuration keys.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the fake graphics backend.</summary>
    public FakeGraphicsBackend Backend { get; } = new("fake");

    /// <summary>Gets the fake window backend.</summary>
    public FakeWindowBackend Windows { get; } = new();

    public Probe Probe { get; private set; } = null!;

    public ModuleContext Context => Probe.Context;

    public IRenderer Render => Context.Resolve<IRenderer>();

    public IRenderResources Resources => Context.Resolve<IRenderResources>();

    public IUngp Ungp => Context.Resolve<IUngp>();

    public GraphicsDevice Device => Context.Resolve<IGraphicsSystem>().Device;

    public FakeDevice Fake => Backend.Device!;

    public ManualClock Clock { get; } = new();

    /// <summary>Gets UNGP, as the compiler reads it from its assembly.</summary>
    public static CompiledModule UngpModuleImage { get; } = CompiledModule.FromAssembly(typeof(UngpModule).Assembly.Location)!;

    /// <summary>Gets nRenderer, as the compiler reads it from its assembly.</summary>
    public static CompiledModule RendererModuleImage { get; } = CompiledModule.FromAssembly(typeof(RendererModule).Assembly.Location)!;

    /// <summary>Gets the warnings logged.</summary>
    public IEnumerable<string> Warnings => Log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

    /// <summary>Gets the errors logged.</summary>
    public IEnumerable<string> Errors => Log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message);

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
        if (Gpu)
        {
            distribution.Settings.Add(new F.SettingValue { Key = GraphicsSettings.VSync, Operation = F.SettingValue.Types.Operation.Default, Value = "off" });
        }

        // A distribution gives the value of its own keys, and the default of the keys of the player.
        foreach ((string key, string value) in Settings)
        {
            F.SettingValue.Types.Operation operation = key is UngpSettings.SkyColor or UngpSettings.Exposure ? F.SettingValue.Types.Operation.Set : F.SettingValue.Types.Operation.Default;
            distribution.Settings.Add(new F.SettingValue { Key = key, Operation = operation, Value = value });
        }

        string window = Gpu ? "nobara.frames.sdl2" : "example.window";
        string graphics = Gpu ? "nobara.graphics.opengl" : "example.graphics";
        foreach (string id in new[] { "nobara.frames", window, "nobara.graphics", graphics, RendererModule.ModuleId, UngpModule.ModuleId, Game })
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
            TimeProvider = Clock,
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

    /// <summary>Places a square of the test game, facing the camera, which stands at the origin and looks along -Z.</summary>
    public RenderObjectDescription Square(string material, double x, double y, double z, float scale = 1) =>
        new(Resources.GetMesh("mesh:example.scene.quad"), new RenderTransform(WorldPosition.FromBlocks(x, y, z), Quaternion.Identity, new Vector3(scale)))
        {
            Material = Resources.GetMaterial(material),
        };

    public void Dispose()
    {
        foreach (string folder in _folders.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still open; the temporary folder is left behind.
            }
        }
    }

    // The resources of the test game, written once per test run.
    private static GameResources CompileGame()
    {
        string resources = Path.Combine(Path.GetTempPath(), "nobara-ungp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(resources, "Textures"));
        Directory.CreateDirectory(Path.Combine(resources, "Meshes"));
        File.WriteAllBytes(Path.Combine(resources, "Meshes", "quad.glb"), Glb.Quad());
        File.WriteAllBytes(Path.Combine(resources, "Textures", "red.png"), Png.Solid(4, 4, Red.R, Red.G, Red.B, 255));
        File.WriteAllBytes(Path.Combine(resources, "Textures", "pure-red.png"), Png.Solid(4, 4, 255, 0, 0, 255));
        File.WriteAllBytes(Path.Combine(resources, "Textures", "glass.png"), Png.Solid(4, 4, 100, 100, 255, 128));

        // The left half is opaque, the right half fully transparent.
        File.WriteAllBytes(Path.Combine(resources, "Textures", "leaf.png"), Png.Image(4, 4, (x, _) => x < 2 ? (Leaf.R, Leaf.G, Leaf.B, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)));
        File.WriteAllText(Path.Combine(resources, "game.nof"), """
            <Nobara version="1.0.0" namespace="Example/Scene">
              <Texture><Name>Red</Name><Content><File>Textures/red.png</File></Content></Texture>
              <Texture><Name>PureRed</Name><Content><File>Textures/pure-red.png</File></Content></Texture>
              <Texture><Name>Leaf</Name><Content><File>Textures/leaf.png</File></Content></Texture>
              <Texture><Name>Glass</Name><Content><File>Textures/glass.png</File></Content></Texture>
              <Material>
                <Name>Red</Name>
                <Content>
                  <Shader>shader:nobara.ungp.standard</Shader>
                  <Texture name="Albedo" texture="texture:example.scene.red" />
                </Content>
              </Material>
              <Material>
                <Name>PureRed</Name>
                <Content>
                  <Shader>shader:nobara.ungp.standard</Shader>
                  <Texture name="Albedo" texture="texture:example.scene.purered" />
                </Content>
              </Material>
              <Material>
                <Name>Leaf</Name>
                <Content>
                  <Shader>shader:nobara.ungp.standard</Shader>
                  <Variant>Cutout</Variant>
                  <Mode>cutout</Mode>
                  <Texture name="Albedo" texture="texture:example.scene.leaf" />
                </Content>
              </Material>
              <Material>
                <Name>Glass</Name>
                <Content>
                  <Shader>shader:nobara.ungp.standard</Shader>
                  <Mode>transparent</Mode>
                  <Texture name="Albedo" texture="texture:example.scene.glass" />
                </Content>
              </Material>
              <Mesh>
                <Name>Quad</Name>
                <Content>
                  <File>Meshes/quad.glb</File>
                  <Material slot="default" material="material:nobara.ungp.default" />
                </Content>
              </Mesh>
            </Nobara>
            """);

        var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
        {
            ModuleId = Game,
            ResourcesDirectory = resources,
            Dependencies = [RendererModuleImage, UngpModuleImage],
            Plugins = [new RendererPlugin()],
        });
        CompilationResult result = compiler.Compile(Directory.EnumerateFiles(resources, "*.nof"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        // The embedded files are read now, so the folder can go.
        var game = new GameResources(result.Bundle!.ToByteArray(), result.Files.ToDictionary(f => f.Path, f => f.Content?.ToArray() ?? File.ReadAllBytes(f.SourcePath), StringComparer.Ordinal));
        Directory.Delete(resources, recursive: true);
        return game;
    }

    private string Folder(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "nobara-ungp-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(folder);
        _folders.Add(Path.GetDirectoryName(folder)!);
        return folder;
    }

    private static byte[] Embedded(Type module)
    {
        using Stream stream = module.Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)!;
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

    private static byte[] GameManifest()
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = Game, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
        manifest.Dependencies.Add(new F.Dependency { Id = RendererModule.ModuleId, Range = "^1.0" });
        manifest.Dependencies.Add(new F.Dependency { Id = UngpModule.ModuleId, Range = "^1.0" });
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.graphics", Range = "^1.0" });
        return manifest.ToByteArray();
    }

    private static Stream? OpenGameResource(string name)
    {
        GameResources game = s_resources.Value;
        if (name == "nobara/resources.pb")
        {
            return new MemoryStream(game.Bundle, writable: false);
        }

        const string Prefix = "nobara/files/";
        return name.StartsWith(Prefix, StringComparison.Ordinal) && game.Files.TryGetValue(name[Prefix.Length..], out byte[]? content) ? new MemoryStream(content, writable: false) : null;
    }

    /// <summary>The compiled resources of the test game: the bundle, and the embedded files by path.</summary>
    private sealed record GameResources(byte[] Bundle, Dictionary<string, byte[]> Files);

    private sealed class Source(UngpHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.frames" => Embedded(typeof(FramesModule)),
                "nobara.frames.sdl2" => Embedded(typeof(Sdl2Module)),
                "nobara.graphics" => Embedded(typeof(GraphicsModule)),
                "nobara.graphics.opengl" => Embedded(typeof(OpenGLModule)),
                RendererModule.ModuleId => Embedded(typeof(RendererModule)),
                UngpModule.ModuleId => Embedded(typeof(UngpModule)),
                "example.window" => BackendManifest("example.window", "nobara.frames"),
                "example.graphics" => BackendManifest("example.graphics", "nobara.graphics"),
                _ => GameManifest(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.frames" => new LoadedModule(typeof(FramesModule).Assembly, () => new FramesModule()),
            "nobara.frames.sdl2" => new LoadedModule(typeof(Sdl2Module).Assembly, () => new Sdl2Module()),
            "nobara.graphics" => new LoadedModule(typeof(GraphicsModule).Assembly, () => new GraphicsModule()),
            "nobara.graphics.opengl" => new LoadedModule(typeof(OpenGLModule).Assembly, () => new OpenGLModule()),
            RendererModule.ModuleId => new LoadedModule(typeof(RendererModule).Assembly, () => new RendererModule()),
            UngpModule.ModuleId => new LoadedModule(typeof(UngpModule).Assembly, () => new UngpModule()),
            "example.window" => new LoadedModule(null, () => new WindowBackendModule(harness.Windows)),
            "example.graphics" => new LoadedModule(null, () => new GraphicsBackendModule(harness.Backend)),
            _ => new LoadedModule(null, () => harness.Probe = new Probe(), OpenGameResource),
        };
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

internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
}

internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public void Register(ModuleContext context) => Context = context;
}

/// <summary>An extractor that runs a function at every frame.</summary>
internal sealed class TestExtractor(Action<RenderExtraction>? extract = null) : IRenderExtractor
{
    /// <summary>Gets or sets a function to run at the next frame only.</summary>
    public Action<RenderExtraction>? Next { get; set; }

    public void Extract(RenderExtraction extraction)
    {
        extract?.Invoke(extraction);
        Action<RenderExtraction>? next = Next;
        Next = null;
        next?.Invoke(extraction);
    }
}

/// <summary>An insertion that adds one raster pass that draws nothing at its point.</summary>
internal sealed class TestInsertion(string name) : IRenderInsertion, IRenderPass
{
    public int Runs { get; private set; }

    public RenderInsertionPoint? Point { get; private set; }

    public void Build(RenderGraphBuilder graph, in RenderInsertionPoint point)
    {
        Point = point;
        RenderPassBuilder pass = graph.AddRasterPass(name, this).Color(point.Color);
        if (point.Depth.IsValid)
        {
            pass.Depth(point.Depth, write: false);
        }
    }

    public void Execute(RenderPassContext context) => Runs++;
}
