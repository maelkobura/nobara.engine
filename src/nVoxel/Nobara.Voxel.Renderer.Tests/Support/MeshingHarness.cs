// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Blocks;
using Nobara.Blocks.Build;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Frames;
using Nobara.Game;
using Nobara.Graphics;
using Nobara.Items;
using Nobara.Items.Build;
using Nobara.Renderer;
using Nobara.Renderer.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Voxel.Renderer.Tests.Support;

/// <summary>
/// A client engine with nVoxel, nRenderer on fake window and graphics
/// backends, nVoxel.Renderer, and a test game: its blocks, textures, and a
/// square mesh, a flat terrain of stone below Y = 0, and a viewer at the
/// origin that holds the chunks around it. A pipeline culls and draws the
/// chunk geometry, as a real one would.
/// </summary>
internal sealed class MeshingHarness : IDisposable
{
    public const string Game = "example.world";
    public const string PlayState = "state:example.world.play";

    /// <summary>The blocks of the test game, in the namespace <c>Example/World</c>.</summary>
    public const string StandardBlocks = """
        <Block><Name>Stone</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><RenderTexture>texture://Example/World/Stone</RenderTexture></Properties></Block>
        <Block>
          <Name>Grass</Name>
          <Content><Shape>shape:nobara.cube</Shape></Content>
          <Properties>
            <RenderTexture>texture://Example/World/Dirt</RenderTexture>
            <RenderTextureTop>texture://Example/World/Grass</RenderTextureTop>
            <RenderTint>#7CBD6B</RenderTint>
          </Properties>
        </Block>
        <Block><Name>Glass</Name><Content><Shape>shape:nobara.cube</Shape><Opacity>translucent</Opacity></Content><Properties><RenderTexture>texture://Example/World/Glass</RenderTexture></Properties></Block>
        <Block><Name>Leaves</Name><Content><Shape>shape:nobara.cube</Shape><Opacity>cutout</Opacity></Content><Properties><RenderTexture>texture://Example/World/Leaves</RenderTexture></Properties></Block>
        <Block>
          <Name>Sign</Name>
          <Content><Shape>shape:nobara.model</Shape><Orientations>horizontal</Orientations><Opacity>cutout</Opacity></Content>
          <Properties><RenderModel>mesh://Example/World/Quad</RenderModel><RenderTexture>texture://Example/World/Leaves</RenderTexture></Properties>
        </Block>
        <Block>
          <Name>Bench</Name>
          <Content><Shape>shape:nobara.model</Shape><Footprint><Cell x="1"/></Footprint></Content>
          <Properties><RenderModel>mesh://Example/World/Wide</RenderModel><RenderTexture>texture://Example/World/Stone</RenderTexture></Properties>
        </Block>
        """;

    private readonly List<string> _folders = [];

    public MeshingHarness()
    {
        Root = Folder("data");
        Settings[VoxelSettings.ViewRadius] = "2";
        Settings[VoxelSettings.ViewHeight] = "1";
        Settings[VoxelSettings.SimulationRadius] = "1";
        Settings[VoxelSettings.SimulationHeight] = "1";
    }

    public string Root { get; }

    /// <summary>Gets the defaults that the distribution gives to configuration keys; a small view area unless a test changes it.</summary>
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the blocks of the test game; <see cref="StandardBlocks"/> by default.</summary>
    public string Blocks { get; set; } = StandardBlocks;

    /// <summary>Gets or sets whether the provider fills the chunks below Y = 0 with stone.</summary>
    public bool FlatTerrain { get; set; } = true;

    /// <summary>Gets or sets what the game does when it initializes, before the world session.</summary>
    public Action<WorldProbe>? Configure { get; set; }

    public MemoryLogSink Log { get; } = new();

    public FakeGraphicsBackend Backend { get; } = new("fake");

    public FakeWindowBackend Windows { get; } = new();

    public ManualClock Clock { get; } = new();

    public WorldProbe Probe { get; private set; } = null!;

    public Engine Engine { get; private set; } = null!;

    public IVoxelMeshing Meshing => Probe.Context.Resolve<IVoxelMeshing>();

    public ChunkRemesher Remesher => (ChunkRemesher)Meshing;

    public ChunkGeometry Chunks => Probe.Context.Resolve<IRenderer>().Chunks;

    public Grid Terrain
    {
        get
        {
            Assert.True(Probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
            return terrain!;
        }
    }

    public Engine Start()
    {
        (byte[] bundle, Dictionary<string, byte[]> files) = Compile(Blocks);
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
            Loop = Loop(),
            States = new F.InitialStates { Client = PlayState },
        };
        foreach ((string key, string value) in Settings)
        {
            // A user key gets a default; a distribution key gets its value.
            F.SettingValue.Types.Operation operation = key is VoxelSettings.ViewRadius or VoxelSettings.ViewHeight ? F.SettingValue.Types.Operation.Default : F.SettingValue.Types.Operation.Set;
            distribution.Settings.Add(new F.SettingValue { Key = key, Operation = operation, Value = value });
        }

        foreach (string id in ModuleIds)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        Engine = Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this, bundle, files),
            Paths = EnginePaths.At(Root),
            TimeProvider = Clock,
            FramePacing = FramePacing.Free,
            DevelopmentMode = true,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        try
        {
            Engine.MainContext.Start();
        }
        catch
        {
            Engine.Dispose();
            throw;
        }

        return Engine;
    }

    /// <summary>Runs frames, one tick each.</summary>
    public void Frame(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1.0 / 30));
            Engine.MainContext.RunFrame();
        }
    }

    /// <summary>
    /// Runs frames until a condition holds. Frames take no time on the clock
    /// of the test, but the remeshes run in jobs: the harness waits a little
    /// between frames, for 30 seconds at most.
    /// </summary>
    public void RunUntil(Func<bool> condition, Func<string>? describe = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), describe?.Invoke() ?? "The condition did not hold after 30 seconds.");
            Frame();
            Thread.Sleep(1);
        }
    }

    /// <summary>Runs frames until the session is active and every loaded chunk is meshed.</summary>
    public void Settle() => RunUntil(
        () => Probe.Sessions.State == SessionState.Active
            && Probe.Loading.LoadedChunks >= Probe.Loading.ViewArea.Count
            && Probe.Loading.PendingChunks == 0
            && Meshing.Statistics is { Dirty: 0, Running: 0 },
        () => $"Not settled: session {Probe.Sessions.State}, {Probe.Loading.LoadedChunks} of {Probe.Loading.ViewArea.Count} chunks loaded, {Probe.Loading.PendingChunks} pending, {Meshing.Statistics}. Log: {string.Join(" | ", Log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message))}");

    public uint State(string block) => Probe.Blocks.Get(block).DefaultState;

    /// <summary>Places blocks with a hand batch, applied at the next tick.</summary>
    public VoxelBatchHandle Place(Grid grid, params (BlockCoordinate Position, uint State)[] blocks) =>
        Probe.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, [.. blocks.Select(b => new BlockPlacement(b.Position, b.State))]);

    public void Dispose()
    {
        Engine?.Dispose();
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

    private static readonly string[] ModuleIds =
    [
        "nobara.game", "nobara.context", "nobara.entity", "nobara.items", "nobara.blocks", "nobara.voxel",
        "nobara.frames", "example.window", "nobara.graphics", "example.graphics", "nobara.renderer",
        VoxelRendererModule.ModuleId, Game,
    ];

    private static F.Loop Loop()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        loop.Phases.Add(new F.Phase { Name = "Render" });
        return loop;
    }

    private (byte[] Bundle, Dictionary<string, byte[]> Files) Compile(string blocks)
    {
        string resources = Folder("resources");
        Directory.CreateDirectory(Path.Combine(resources, "Textures"));
        Directory.CreateDirectory(Path.Combine(resources, "Meshes"));
        File.WriteAllBytes(Path.Combine(resources, "Meshes", "quad.glb"), Glb.Quad());
        File.WriteAllBytes(Path.Combine(resources, "Meshes", "wide.glb"), Glb.Quad(width: 2));
        File.WriteAllBytes(Path.Combine(resources, "Textures", "stone.png"), Png.Solid(16, 16, 128, 128, 128, 255));
        File.WriteAllBytes(Path.Combine(resources, "Textures", "dirt.png"), Png.Solid(16, 16, 120, 80, 40, 255));
        File.WriteAllBytes(Path.Combine(resources, "Textures", "grass.png"), Png.Solid(16, 16, 200, 200, 200, 255));
        File.WriteAllBytes(Path.Combine(resources, "Textures", "glass.png"), Png.Solid(16, 16, 180, 200, 255, 100));
        File.WriteAllBytes(Path.Combine(resources, "Textures", "leaves.png"), Png.Image(16, 16, (x, y) => ((x + y) & 1) == 0 ? ((byte)40, (byte)160, (byte)40, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)));
        File.WriteAllText(Path.Combine(resources, "world.nof"), $"""
            <Nobara version="1.0.0" namespace="Example/World">
              <Texture><Name>Stone</Name><Content><File>Textures/stone.png</File></Content></Texture>
              <Texture><Name>Dirt</Name><Content><File>Textures/dirt.png</File></Content></Texture>
              <Texture><Name>Grass</Name><Content><File>Textures/grass.png</File></Content></Texture>
              <Texture><Name>Glass</Name><Content><File>Textures/glass.png</File></Content></Texture>
              <Texture><Name>Leaves</Name><Content><File>Textures/leaves.png</File></Content></Texture>
              <Mesh><Name>Quad</Name><Content><File>Meshes/quad.glb</File></Content></Mesh>
              <Mesh><Name>Wide</Name><Content><File>Meshes/wide.glb</File></Content></Mesh>
              {blocks}
            </Nobara>
            """);
        var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
        {
            ModuleId = Game,
            ResourcesDirectory = resources,
            Dependencies =
            [
                CompiledModule.FromAssembly(typeof(ItemsModule).Assembly.Location)!,
                CompiledModule.FromAssembly(typeof(BlocksModule).Assembly.Location)!,
                CompiledModule.FromAssembly(typeof(RendererModule).Assembly.Location)!,
            ],
            Plugins = [new ItemPlugin(), new BlockPlugin(), new RendererPlugin()],
        });
        CompilationResult result = compiler.Compile(Directory.EnumerateFiles(resources, "*.nof"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return (result.Bundle!.ToByteArray(), result.Files.ToDictionary(f => f.Path, f => f.Content?.ToArray() ?? File.ReadAllBytes(f.SourcePath), StringComparer.Ordinal));
    }

    private string Folder(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "nobara-voxel-renderer-tests", Guid.NewGuid().ToString("N"), name);
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
        foreach (string dependency in new[] { "nobara.game", "nobara.context", "nobara.items", "nobara.blocks", "nobara.voxel", "nobara.graphics", "nobara.renderer", VoxelRendererModule.ModuleId })
        {
            manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        manifest.States.Add(new F.StateDeclaration { Id = PlayState });
        return manifest.ToByteArray();
    }

    private sealed class Source(MeshingHarness harness, byte[] bundle, Dictionary<string, byte[]> files) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.game" => Embedded(typeof(GameModule)),
                "nobara.context" => Embedded(typeof(ContextModule)),
                "nobara.entity" => Embedded(typeof(EntityModule)),
                "nobara.items" => Embedded(typeof(ItemsModule)),
                "nobara.blocks" => Embedded(typeof(BlocksModule)),
                "nobara.voxel" => Embedded(typeof(VoxelModule)),
                "nobara.frames" => Embedded(typeof(FramesModule)),
                "nobara.graphics" => Embedded(typeof(GraphicsModule)),
                "nobara.renderer" => Embedded(typeof(RendererModule)),
                VoxelRendererModule.ModuleId => Embedded(typeof(VoxelRendererModule)),
                "example.window" => BackendManifest("example.window", "nobara.frames"),
                "example.graphics" => BackendManifest("example.graphics", "nobara.graphics"),
                _ => GameManifest(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.game" => new LoadedModule(typeof(GameModule).Assembly, () => new GameModule()),
            "nobara.context" => new LoadedModule(typeof(ContextModule).Assembly, () => new ContextModule()),
            "nobara.entity" => new LoadedModule(typeof(EntityModule).Assembly, () => new EntityModule()),
            "nobara.items" => new LoadedModule(typeof(ItemsModule).Assembly, () => new ItemsModule()),
            "nobara.blocks" => new LoadedModule(typeof(BlocksModule).Assembly, () => new BlocksModule()),
            "nobara.voxel" => new LoadedModule(typeof(VoxelModule).Assembly, () => new VoxelModule()),
            "nobara.frames" => new LoadedModule(typeof(FramesModule).Assembly, () => new FramesModule()),
            "nobara.graphics" => new LoadedModule(typeof(GraphicsModule).Assembly, () => new GraphicsModule()),
            "nobara.renderer" => new LoadedModule(typeof(RendererModule).Assembly, () => new RendererModule()),
            VoxelRendererModule.ModuleId => new LoadedModule(typeof(VoxelRendererModule).Assembly, () => new VoxelRendererModule()),
            "example.window" => new LoadedModule(null, () => new WindowBackendModule(harness.Windows)),
            "example.graphics" => new LoadedModule(null, () => new GraphicsBackendModule(harness.Backend)),
            _ => new LoadedModule(null, () => harness.Probe = new WorldProbe(harness), Open),
        };

        private Stream? Open(string name)
        {
            if (name == "nobara/resources.pb")
            {
                return new MemoryStream(bundle, writable: false);
            }

            const string Prefix = "nobara/files/";
            return name.StartsWith(Prefix, StringComparison.Ordinal) && files.TryGetValue(name[Prefix.Length..], out byte[]? file) ? new MemoryStream(file, writable: false) : null;
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

/// <summary>The test game: a flat terrain, a viewer at the origin, and a pipeline that draws chunk geometry.</summary>
internal sealed class WorldProbe(MeshingHarness harness) : IModule
{
    private ViewerTicket? _viewer;

    public ModuleContext Context { get; private set; } = null!;

    public IVoxelWorlds Worlds { get; private set; } = null!;

    public IBlockRegistry Blocks { get; private set; } = null!;

    public IVoxelBatches Batches { get; private set; } = null!;

    public IChunkLoading Loading { get; private set; } = null!;

    public IWorldSessions Sessions { get; private set; } = null!;

    public IShapeRegistry Shapes { get; private set; } = null!;

    public ChunkDrawPipeline Pipeline { get; private set; } = null!;

    /// <summary>Gets or sets where the viewer stands.</summary>
    public WorldPosition ViewerPosition { get; set; } = WorldPosition.FromBlocks(0.5, 0.5, 0.5);

    public void Register(ModuleContext context) => Context = context;

    public void Initialize(ModuleContext context)
    {
        Worlds = context.Resolve<IVoxelWorlds>();
        Blocks = context.Resolve<IBlockRegistry>();
        Batches = context.Resolve<IVoxelBatches>();
        Loading = context.Resolve<IChunkLoading>();
        Sessions = context.Resolve<IWorldSessions>();
        Shapes = context.Resolve<IShapeRegistry>();
        harness.Configure?.Invoke(this);
        if (harness.FlatTerrain)
        {
            Loading.AddProvider(context, new FlatProvider(() => Blocks.Get("block:example.world.stone").DefaultState));
        }

        context.Resolve<IStateStack>().Register(context, MeshingHarness.PlayState, () => new PlayState(Sessions));
        Sessions.SubscribeStarting(context, _ =>
        {
            Assert.True(Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
            _viewer = Loading.AddViewer(context, terrain!, ViewerPosition);
        });
        Sessions.SubscribeEnding(context, _ => _viewer?.Dispose());
    }

    public void Start(ModuleContext context)
    {
        IRenderer renderer = context.Resolve<IRenderer>();
        Pipeline = new ChunkDrawPipeline(renderer, context.Resolve<IGraphicsSystem>().Device);
        renderer.RegisterPipeline(context, Pipeline);
    }

    private sealed class PlayState(IWorldSessions sessions) : ApplicationState
    {
        public override void Enter() => sessions.Start();
    }

    private sealed class FlatProvider(Func<uint> ground) : IChunkProvider
    {
        private uint _ground = uint.MaxValue;

        public bool Accepts(in ChunkRequest request)
        {
            if (_ground == uint.MaxValue)
            {
                _ground = ground();
            }

            return request.IsTerrain;
        }

        [HotPath]
        public void Produce(in ChunkRequest request, Span<uint> states)
        {
            if (request.Chunk.Y < 0)
            {
                states.Fill(_ground);
            }
        }
    }
}

/// <summary>
/// A pipeline that culls the chunk geometry and draws its opaque, cut-out,
/// and transparent faces to the target of the view, as a real pipeline does,
/// with shaders that the fake device does not run.
/// </summary>
internal sealed class ChunkDrawPipeline(IRenderer renderer, GraphicsDevice device) : IRenderPipeline, IRenderPass
{
    private GraphicsPipeline? _pipeline;

    public string Name => "chunks";

    public GraphicsFeatures RequiredFeatures => ChunkGeometry.RequiredFeatures;

    /// <summary>Gets the number of frames that drew chunk geometry.</summary>
    public int Draws { get; private set; }

    public void Build(RenderGraphBuilder graph)
    {
        renderer.Chunks.AddCullingPass(graph);
        graph.AddRasterPass("chunks", this).Color(graph.Target, LoadAction.Clear).Depth(graph.TargetDepth, LoadAction.Clear);
        graph.InsertionPoint(RenderInsertionPoints.AfterOpaque, graph.Target, graph.TargetDepth);
        graph.InsertionPoint(RenderInsertionPoints.BeforePostProcess, graph.Target, graph.TargetDepth);
        graph.InsertionPoint(RenderInsertionPoints.Ui, graph.Target);
    }

    public void Prepare(RenderPassContext context)
    {
    }

    public void Execute(RenderPassContext context)
    {
        ChunkGeometry chunks = renderer.Chunks;
        _pipeline ??= device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            VertexShader = device.CreateShader(new ShaderDescription(ShaderStages.Vertex, FakeSpirV())),
            FragmentShader = device.CreateShader(new ShaderDescription(ShaderStages.Fragment, FakeSpirV())),
            VertexBuffers = chunks.VertexLayouts,
            ResourceLayouts = [chunks.DrawLayout],
            Depth = new DepthState { TestEnabled = true, WriteEnabled = true, Compare = Camera.DepthCompare },
            Outputs = new OutputDescription([.. context.Framebuffer!.ColorFormats], context.Framebuffer.DepthFormat),
        });
        context.Commands.SetPipeline(_pipeline);
        chunks.Draw(context, ChunkCategory.Opaque, 0);
        chunks.Draw(context, ChunkCategory.Cutout, 0);
        chunks.Draw(context, ChunkCategory.Transparent, 0);
        Draws++;
    }

    // The header of a SPIR-V module, which is all that the fake device reads.
    private static byte[] FakeSpirV()
    {
        var code = new byte[20];
        BitConverter.TryWriteBytes(code, 0x07230203u);
        return code;
    }
}

internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
}
