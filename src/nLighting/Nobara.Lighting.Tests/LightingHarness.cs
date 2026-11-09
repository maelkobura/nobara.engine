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
using Nobara.Entity;
using Nobara.Game;
using Nobara.Items.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using Nobara.Voxel;
using F = Nobara.Formats;

namespace Nobara.Lighting.Tests;

/// <summary>Starts a server context with nVoxel, nLighting, and a module that owns test blocks and grids.</summary>
internal sealed class LightingHarness : IDisposable
{
    public const string OwnerId = "example.light";
    private const string PlayStateId = "state:example.light.play";

    /// <summary>The blocks of the tests: sources, filters, and walls.</summary>
    public const string Blocks = """
        <Block><Name>Stone</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>
        <Block><Name>Torch</Name><Content><Shape>shape:nobara.model</Shape><Collision>none</Collision></Content><Properties><LightEmission>14</LightEmission></Properties></Block>
        <Block><Name>Lamp</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><LightEmission>15</LightEmission></Properties></Block>
        <Block><Name>RedLamp</Name><Content><Shape>shape:nobara.model</Shape></Content><Properties><LightEmission>15 0 0</LightEmission></Properties></Block>
        <Block><Name>BlueGlass</Name><Content><Shape>shape:nobara.cube</Shape><Opacity>translucent</Opacity></Content><Properties><LightFilter>15 15 0</LightFilter></Properties></Block>
        <Block><Name>RedGlass</Name><Content><Shape>shape:nobara.cube</Shape><Opacity>translucent</Opacity></Content><Properties><LightFilter>0 15 15</LightFilter></Properties></Block>
        <Block><Name>Mist</Name><Content><Shape>shape:nobara.cube</Shape><Opacity>translucent</Opacity></Content><Properties><LightFilter>2</LightFilter></Properties></Block>
        """;

    public LightingHarness(bool colored = false, string blocks = Blocks, bool developmentMode = true)
    {
        DevelopmentMode = developmentMode;
        Root = Path.Combine(Path.GetTempPath(), "nobara-lighting-tests", Guid.NewGuid().ToString("N"));
        Colored = colored;
        OwnerResources = Bundle(blocks);
    }

    public string Root { get; }

    public bool Colored { get; }

    /// <summary>Gets whether the engine checks the accesses of jobs, which slows every read of a block.</summary>
    public bool DevelopmentMode { get; }

    public byte[] OwnerResources { get; }

    public ManualClock Clock { get; } = new();

    public MemoryLogSink Log { get; } = new();

    public Probe Owner { get; private set; } = null!;

    public Action<Probe>? ConfigureOwner { get; set; }

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
            States = new F.InitialStates { Server = PlayStateId },
        };
        foreach (string id in new[] { "nobara.game", "nobara.context", "nobara.entity", "nobara.items", "nobara.blocks", "nobara.voxel", LightingModule.Id, OwnerId })
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        var owner = new F.ModuleManifest { Format = 1, Id = OwnerId, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both };
        foreach (string dependency in new[] { LightingModule.Id, "nobara.voxel", "nobara.items", "nobara.blocks", "nobara.context", "nobara.game" })
        {
            owner.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        owner.States.Add(new F.StateDeclaration { Id = PlayStateId });
        var engine = Engine.Load(new EngineOptions
        {
            Side = Side.Server,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this, owner),
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
        RunUntil(engine, () => Owner.Sessions.State == SessionState.Active);
        return engine;
    }

    public void Tick(Engine engine, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1.0 / 30));
            engine.MainContext.RunFrame();
        }
    }

    public void RunUntil(Engine engine, Func<bool> condition, int frames = 2000)
    {
        for (int i = 0; i < frames; i++)
        {
            if (condition()) return;
            Tick(engine);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte[] Bundle(string body)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-lighting-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "blocks.nof"), $"<Nobara version=\"1.0.0\" namespace=\"Example/Light\">{body}</Nobara>");
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = OwnerId,
                ResourcesDirectory = root,
                Dependencies = [CompiledModule.FromAssembly(typeof(Nobara.Items.ItemsModule).Assembly.Location)!, CompiledModule.FromAssembly(typeof(BlocksModule).Assembly.Location)!],
                Plugins = [new ItemPlugin(), new BlockPlugin()],
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

    private static F.Loop StandardLoop()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        return loop;
    }

    private sealed class Source(LightingHarness harness, F.ModuleManifest owner) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            F.ModuleManifest manifest = module.Id switch
            {
                "nobara.game" => Manifest("nobara.game", "Game"),
                "nobara.context" => Manifest("nobara.context", "Context"),
                "nobara.entity" => Manifest("nobara.entity", "Entity", "nobara.game", "nobara.context"),
                "nobara.items" => Manifest("nobara.items", "Items", "nobara.game"),
                "nobara.blocks" => Manifest("nobara.blocks", "Blocks", "nobara.items"),
                "nobara.voxel" => Manifest("nobara.voxel", "Voxel", "nobara.game", "nobara.items", "nobara.blocks", "nobara.entity", "nobara.context"),
                LightingModule.Id => Manifest(LightingModule.Id, "Lighting", "nobara.voxel", "nobara.blocks", "nobara.game", "nobara.context"),
                _ => owner,
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.game" => new LoadedModule(typeof(GameModule).Assembly, () => new GameModule()),
            "nobara.context" => new LoadedModule(typeof(ContextModule).Assembly, () => new ContextModule()),
            "nobara.entity" => new LoadedModule(typeof(EntityModule).Assembly, () => new EntityModule()),
            "nobara.items" => new LoadedModule(typeof(Nobara.Items.ItemsModule).Assembly, () => new Nobara.Items.ItemsModule()),
            "nobara.blocks" => new LoadedModule(typeof(BlocksModule).Assembly, () => new BlocksModule()),
            "nobara.voxel" => new LoadedModule(typeof(VoxelModule).Assembly, () => new VoxelModule()),
            LightingModule.Id => new LoadedModule(typeof(LightingModule).Assembly, () => new LightingModule()),
            _ => new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe(harness.ConfigureOwner);
                harness.Owner = probe;
                return probe;
            }, name => name == "nobara/resources.pb" ? new MemoryStream(harness.OwnerResources) : null),
        };

        private F.ModuleManifest Manifest(string id, string shortName, params string[] dependencies)
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = id, ShortName = shortName, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both };
            foreach (string dependency in dependencies) manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
            switch (id)
            {
                case "nobara.game":
                    manifest.Reserves.Add("loop.simulation");
                    break;
                case "nobara.context":
                    manifest.Reserves.Add("core.contexts");
                    break;
                case "nobara.items":
                    manifest.Settings.Add(Setting("items.defaultMaxStack", F.SettingDeclaration.Types.Type.Int, "64"));
                    break;
                case "nobara.blocks":
                    manifest.Settings.Add(Setting("blocks.maxStatesPerType", F.SettingDeclaration.Types.Type.Int, "1024"));
                    break;
                case "nobara.voxel":
                    manifest.Settings.Add(Setting(VoxelSettings.ViewRadius, F.SettingDeclaration.Types.Type.Int, "8", F.SettingDeclaration.Types.Scope.User));
                    manifest.Settings.Add(Setting(VoxelSettings.ViewHeight, F.SettingDeclaration.Types.Type.Int, "4", F.SettingDeclaration.Types.Scope.User));
                    manifest.Settings.Add(Setting(VoxelSettings.SimulationRadius, F.SettingDeclaration.Types.Type.Int, "4"));
                    manifest.Settings.Add(Setting(VoxelSettings.SimulationHeight, F.SettingDeclaration.Types.Type.Int, "2"));
                    manifest.Settings.Add(Setting(VoxelSettings.RandomTicksPerChunk, F.SettingDeclaration.Types.Type.Int, "3"));
                    manifest.Settings.Add(Setting(VoxelSettings.ReducedTickInterval, F.SettingDeclaration.Types.Type.Int, "4"));
                    manifest.Settings.Add(Setting(VoxelSettings.UnloadDelay, F.SettingDeclaration.Types.Type.Int, "0"));
                    manifest.Settings.Add(Setting(VoxelSettings.FreezeInactiveDimensions, F.SettingDeclaration.Types.Type.Bool, "true"));
                    break;
                case LightingModule.Id:
                    manifest.Settings.Add(Setting(LightingSettings.Color, F.SettingDeclaration.Types.Type.Bool, harness.Colored ? "true" : "false"));
                    break;
            }

            return manifest;
        }

        private static F.SettingDeclaration Setting(string key, F.SettingDeclaration.Types.Type type, string value, F.SettingDeclaration.Types.Scope scope = F.SettingDeclaration.Types.Scope.Distribution) =>
            new() { Key = key, Scope = scope, Type = type, DefaultValue = value };
    }
}

/// <summary>The module of the tests: it resolves the services, starts the session, and owns the grids it creates.</summary>
internal sealed class Probe(Action<Probe>? configure) : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public IVoxelWorlds Worlds { get; private set; } = null!;

    public IBlockRegistry Blocks { get; private set; } = null!;

    public IVoxelBatches Batches { get; private set; } = null!;

    public IChunkLoading Loading { get; private set; } = null!;

    public IWorldSessions Sessions { get; private set; } = null!;

    public ILighting Lighting { get; private set; } = null!;

    public ILightSources Sources { get; private set; } = null!;

    public List<Grid> Grids { get; } = [];

    public List<IDisposable> Tickets { get; } = [];

    internal Lighting Internal => (Lighting)Lighting;

    public void Register(ModuleContext context) => Context = context;

    public void Initialize(ModuleContext context)
    {
        Worlds = context.Resolve<IVoxelWorlds>();
        Blocks = context.Resolve<IBlockRegistry>();
        Batches = context.Resolve<IVoxelBatches>();
        Loading = context.Resolve<IChunkLoading>();
        Sessions = context.Resolve<IWorldSessions>();
        Lighting = context.Resolve<ILighting>();
        Sources = context.Resolve<ILightSources>();
        configure?.Invoke(this);
        context.Resolve<IStateStack>().Register(context, "state:example.light.play", () => new PlayState(Sessions));
        Sessions.SubscribeEnding(context, _ =>
        {
            foreach (IDisposable ticket in Tickets) ticket.Dispose();
            Tickets.Clear();
        });
        Sessions.SubscribeEnded(context, _ =>
        {
            foreach (Grid grid in Grids) Worlds.Destroy(context, grid);
            Grids.Clear();
        });
    }

    public uint State(string name) => Blocks.Get($"block:example.light.{name}").DefaultState;

    /// <summary>Creates a grid that keeps every chunk it writes, where an absent chunk is air.</summary>
    public Grid CreateGrid()
    {
        Grid grid = Worlds.CreateGrid(Context, DimensionId.Default, new GridOptions());
        Grids.Add(grid);
        return grid;
    }

    public Grid Terrain => Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain) ? terrain! : throw new InvalidOperationException("No terrain.");

    /// <summary>Writes blocks, applied at the next tick.</summary>
    public void Set(Grid grid, params (int X, int Y, int Z, uint State)[] blocks)
    {
        var batch = new BlockBatch(blocks.Length) { Cause = BlockCause.Command };
        foreach ((int x, int y, int z, uint state) in blocks) batch.Add(new BlockChange(new BlockPosition(grid.Identifier, x, y, z), 0, state));
        Batches.SubmitTechnical(batch);
    }

    /// <summary>Reads the light of a block.</summary>
    public LightLevel Light(Grid grid, int x, int y, int z)
    {
        Assert.True(Internal.TryGetLight(grid, new BlockCoordinate(x, y, z), out LightLevel light), $"No light at {x} {y} {z}.");
        return light;
    }

    public int Block(Grid grid, int x, int y, int z) => Light(grid, x, y, z).Block;

    /// <summary>Copies the light of every chunk of a grid, by chunk coordinate, as 16-bit words.</summary>
    public SortedDictionary<(int, int, int), ushort[]> Dump(Grid grid)
    {
        var dump = new SortedDictionary<(int, int, int), ushort[]>();
        for (int x = -2; x <= 2; x++)
        {
            for (int y = -2; y <= 2; y++)
            {
                for (int z = -2; z <= 2; z++)
                {
                    if (!Internal.TryGetChunk(grid, new ChunkCoordinate(x, y, z), out LightChunk? chunk)) continue;
                    var values = new ushort[LightChunk.Volume];
                    chunk!.CopyTo(values);
                    dump.Add((x, y, z), values);
                }
            }
        }

        return dump;
    }

    private sealed class PlayState(IWorldSessions sessions) : ApplicationState
    {
        public override void Enter() => sessions.Start();
    }
}

internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
}

internal sealed class MemoryLogSink : ILogSink
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Write(in LogEntry entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    public void Flush()
    {
    }
}
