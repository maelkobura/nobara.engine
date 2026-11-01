// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Frames;
using Nobara.Frames.SDL2;
using Nobara.Graphics;
using Nobara.Graphics.OpenGL;
using Nobara.HotReload;
using Nobara.Renderer.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Renderer.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real nFrames, nGraphics, and
/// nRenderer, on fake window and graphics backends, or on SDL2 and OpenGL for
/// the tests of the category GPU, and a game module whose resources the tests
/// compile from a copy of <c>Resources/</c>.
/// </summary>
internal sealed class RendererHarness : IDisposable
{
    public const string Game = "example.game";

    private readonly List<string> _folders = [];
    private CompilationResult? _game;

    public RendererHarness(bool gpu = false)
    {
        Gpu = gpu;
        Root = Folder("data");
    }

    /// <summary>Gets whether the engine runs on the OpenGL backend.</summary>
    public bool Gpu { get; }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the fake graphics backend.</summary>
    public FakeGraphicsBackend Backend { get; } = new("fake");

    /// <summary>Gets the fake window backend.</summary>
    public FakeWindowBackend Windows { get; } = new();

    /// <summary>Gets or sets whether the engine runs in development mode, which reloads resources.</summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>Gets the game module, once loaded.</summary>
    public Probe Probe { get; private set; } = null!;

    /// <summary>Gets nRenderer, as the compiler reads it from its assembly.</summary>
    public static CompiledModule Renderer { get; } = CompiledModule.FromAssembly(typeof(RendererModule).Assembly.Location)!;

    /// <summary>Gets the resources of the game.</summary>
    public IRenderResources Resources => Probe.Context.Resolve<IRenderResources>();

    /// <summary>Gets the device.</summary>
    public GraphicsDevice Device => Probe.Context.Resolve<IGraphicsSystem>().Device;

    /// <summary>Gets the fake device.</summary>
    public FakeDevice Fake => Backend.Device!;

    /// <summary>Copies the resources of the tests to a new folder, which a test may change.</summary>
    /// <returns>The folder: the <c>Resources/</c> of a module.</returns>
    public string CopyResources()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Resources");
        string target = Folder("resources");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        return target;
    }

    /// <summary>Compiles the resources of a module that depends on nRenderer.</summary>
    /// <param name="resources">Its <c>Resources/</c> folder.</param>
    /// <param name="moduleId">The module.</param>
    public static CompilationResult Compile(string resources, string moduleId = Game)
    {
        var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
        {
            ModuleId = moduleId,
            ResourcesDirectory = resources,
            Dependencies = [Renderer],
            Plugins = [new RendererPlugin()],
        });
        return compiler.Compile(Directory.EnumerateFiles(resources, "*.nof", SearchOption.AllDirectories));
    }

    /// <summary>Compiles resources that must compile.</summary>
    public static CompilationResult Compiled(string resources, string moduleId = Game)
    {
        CompilationResult result = Compile(resources, moduleId);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }

    /// <summary>Gets the embedded files of a compilation, by path, as the compiler embeds them.</summary>
    public static Dictionary<string, ReadOnlyMemory<byte>> Files(CompilationResult result) =>
        result.Files.ToDictionary(f => f.Path, f => (ReadOnlyMemory<byte>)(f.Content ?? File.ReadAllBytes(f.SourcePath)), StringComparer.Ordinal);

    /// <summary>Gives the game the resources of a compilation.</summary>
    public RendererHarness WithGame(CompilationResult resources)
    {
        _game = resources;
        return this;
    }

    /// <summary>
    /// Creates the watcher of a development build for the game: it compiles the
    /// resources of the game again from a folder, with the plugin of nRenderer
    /// as the build gives it, and hands them to the engine.
    /// </summary>
    /// <param name="engine">The engine, in development mode.</param>
    /// <param name="resources">The resources folder of the game.</param>
    public ResourceWatcher Watcher(Engine engine, string resources) => new(
        engine,
        [new WatchedModule(Game, resources, [Path.Combine(AppContext.BaseDirectory, "Nobara.Renderer.Build.dll")])],
        id => id switch
        {
            Game => new CompiledModule(F.ModuleManifest.Parser.ParseFrom(GameManifest()), null, _game!.Bundle!),
            RendererModule.ModuleId => Renderer,
            _ => null,
        });

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

        string window = Gpu ? "nobara.frames.sdl2" : "example.window";
        string graphics = Gpu ? "nobara.graphics.opengl" : "example.graphics";
        foreach (string id in new[] { "nobara.frames", window, "nobara.graphics", graphics, RendererModule.ModuleId, Game })
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
            DevelopmentMode = DevelopmentMode,
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
                // A file still open by a watcher; the temporary folder is left behind.
            }
        }
    }

    private string Folder(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "nobara-renderer-tests", Guid.NewGuid().ToString("N"), name);
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
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.graphics", Range = "^1.0" });
        return manifest.ToByteArray();
    }

    private sealed class Source(RendererHarness harness) : IModuleSource
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
            "example.window" => new LoadedModule(null, () => new WindowBackendModule(harness.Windows)),
            "example.graphics" => new LoadedModule(null, () => new GraphicsBackendModule(harness.Backend)),
            _ => new LoadedModule(null, () => harness.Probe = new Probe(), harness.OpenGameResource),
        };
    }

    private Stream? OpenGameResource(string name)
    {
        if (_game?.Bundle is null)
        {
            return null;
        }

        if (name == "nobara/resources.pb")
        {
            return new MemoryStream(_game.Bundle.ToByteArray(), writable: false);
        }

        const string Prefix = "nobara/files/";
        EmbeddedFile? file = name.StartsWith(Prefix, StringComparison.Ordinal) ? _game.Files.FirstOrDefault(f => f.Path == name[Prefix.Length..]) : null;
        return file is null ? null : new MemoryStream(file.Content ?? File.ReadAllBytes(file.SourcePath), writable: false);
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

/// <summary>The game module: it keeps its context.</summary>
internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public void Register(ModuleContext context) => Context = context;
}
