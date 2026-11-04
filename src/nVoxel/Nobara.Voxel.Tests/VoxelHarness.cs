// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using System.Numerics;
using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Items;
using F = Nobara.Formats;

namespace Nobara.Voxel.Tests;

internal sealed class VoxelHarness : IDisposable
{
    public const string OwnerId = "example.world";
    internal const string PlayStateId = "state:example.world.play";

    public VoxelHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-voxel-tests", Guid.NewGuid().ToString("N"));
    }

    public string Root { get; }

    public ManualClock Clock { get; } = new();

    public MemoryLogSink Log { get; } = new();

    public Probe Owner { get; private set; } = null!;

    public byte[]? OwnerResources { get; set; }

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
        foreach (string id in new[] { "nobara.game", "nobara.context", "nobara.entity", "nobara.items", "nobara.blocks", "nobara.voxel" })
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }
        var ownerManifest = new F.ModuleManifest { Format = 1, Id = OwnerId, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both };
        ownerManifest.Dependencies.Add(new F.Dependency { Id = "nobara.voxel", Range = "^1.0" });
        ownerManifest.Dependencies.Add(new F.Dependency { Id = "nobara.items", Range = "^1.0" });
        ownerManifest.Dependencies.Add(new F.Dependency { Id = "nobara.blocks", Range = "^1.0" });
        ownerManifest.Dependencies.Add(new F.Dependency { Id = "nobara.context", Range = "^1.0" });
        ownerManifest.States.Add(new F.StateDeclaration { Id = PlayStateId });
        distribution.Modules.Add(new F.DistributionModule { Id = OwnerId, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });

        var engine = Engine.Load(new EngineOptions
        {
            Side = Side.Server,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this, ownerManifest),
            Paths = EnginePaths.At(Root),
            TimeProvider = Clock,
            FramePacing = FramePacing.Free,
            DevelopmentMode = true,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        engine.MainContext.Start();
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

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static F.Loop StandardLoop()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        return loop;
    }

    private sealed class Source(VoxelHarness harness, F.ModuleManifest ownerManifest) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            F.ModuleManifest manifest = module.Id switch
            {
                "nobara.game" => Manifest("nobara.game", "Game"),
                "nobara.context" => ContextManifest(),
                "nobara.entity" => Manifest("nobara.entity", "Entity", "nobara.game", "nobara.context"),
                "nobara.items" => Manifest("nobara.items", "Items", "nobara.game"),
                "nobara.blocks" => Manifest("nobara.blocks", "Blocks", "nobara.items"),
                "nobara.voxel" => Manifest("nobara.voxel", "Voxel", "nobara.game", "nobara.items", "nobara.blocks", "nobara.entity", "nobara.context"),
                _ => ownerManifest,
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.game" => new LoadedModule(typeof(GameModule).Assembly, () => new GameModule()),
            "nobara.context" => new LoadedModule(typeof(ContextModule).Assembly, () => new ContextModule()),
            "nobara.entity" => new LoadedModule(typeof(EntityModule).Assembly, () => new EntityModule()),
            "nobara.items" => new LoadedModule(typeof(Nobara.Items.ItemsModule).Assembly, () => new Nobara.Items.ItemsModule()),
            "nobara.blocks" => new LoadedModule(typeof(Nobara.Blocks.BlocksModule).Assembly, () => new Nobara.Blocks.BlocksModule()),
            "nobara.voxel" => new LoadedModule(typeof(VoxelModule).Assembly, () => new VoxelModule()),
            _ => new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe(harness.ConfigureOwner);
                harness.Owner = probe;
                return probe;
            }, name => name == "nobara/resources.pb" && harness.OwnerResources is not null ? new MemoryStream(harness.OwnerResources) : null),
        };

        private static F.ModuleManifest ContextManifest()
        {
            var manifest = Manifest("nobara.context", "Context");
            manifest.Reserves.Add("core.contexts");
            return manifest;
        }

        private static F.ModuleManifest Manifest(string id, string shortName, params string[] dependencies)
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = id, ShortName = shortName, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both };
            foreach (string dependency in dependencies) manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
            if (id == "nobara.game") manifest.Reserves.Add("loop.simulation");
            if (id == "nobara.items") manifest.Settings.Add(new F.SettingDeclaration
            {
                Key = "items.defaultMaxStack",
                Scope = F.SettingDeclaration.Types.Scope.Distribution,
                Type = F.SettingDeclaration.Types.Type.Int,
                DefaultValue = "64",
            });
            if (id == "nobara.blocks") manifest.Settings.Add(new F.SettingDeclaration
            {
                Key = "blocks.maxStatesPerType",
                Scope = F.SettingDeclaration.Types.Scope.Distribution,
                Type = F.SettingDeclaration.Types.Type.Int,
                DefaultValue = "1024",
            });
            return manifest;
        }
    }
}

internal sealed class Probe(Action<Probe>? configure = null) : IModule
{
    private readonly Action<Probe>? _configure = configure;
    public ModuleContext Context { get; private set; } = null!;

    public IVoxelWorlds Worlds { get; private set; } = null!;

    public IBlockRegistry Blocks { get; private set; } = null!;

    public IItemRegistry Items { get; private set; } = null!;

    public IItemUsage Usage { get; private set; } = null!;

    public IBlockHooks Hooks { get; private set; } = null!;

    public IVoxelBatches Batches { get; private set; } = null!;

    public IShapeRegistry Shapes { get; private set; } = null!;

    public IMultiBlockRegistry MultiBlocks { get; private set; } = null!;

    public IWorldSessions Sessions { get; private set; } = null!;

    public List<Grid> CreatedGrids { get; } = [];

    public void Register(ModuleContext context)
    {
        Context = context;
    }

    public void Initialize(ModuleContext context)
    {
        Worlds = context.Resolve<IVoxelWorlds>();
        Blocks = context.Resolve<IBlockRegistry>();
        Items = context.Resolve<IItemRegistry>();
        Usage = context.Resolve<IItemUsage>();
        Hooks = context.Resolve<IBlockHooks>();
        Batches = context.Resolve<IVoxelBatches>();
        Shapes = context.Resolve<IShapeRegistry>();
        MultiBlocks = context.Resolve<IMultiBlockRegistry>();
        _configure?.Invoke(this);
        Sessions = context.Resolve<IWorldSessions>();
        IStateStack states = context.Resolve<IStateStack>();
        states.Register(context, VoxelHarness.PlayStateId, () => new PlayState(Sessions));
        Sessions.SubscribeStarted(context, _ =>
        {
            foreach (int x in new[] { 0, 10, 100 })
            {
                Grid grid = Worlds.CreateGrid(context, DimensionId.Default, new GridOptions
                {
                    Bounds = new GridBounds(new BlockCoordinate(-2, -2, -2), new BlockCoordinate(2, 2, 2)),
                    Transform = new WorldTransform(WorldPosition.FromBlocks(x, 0, 0), x == 100 ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6) : Quaternion.Identity, Vector3.One),
                });
                CreatedGrids.Add(grid);
            }
        });
        Sessions.SubscribeEnded(context, _ =>
        {
            foreach (Grid grid in CreatedGrids.ToArray()) Worlds.Destroy(context, grid);
            CreatedGrids.Clear();
        });
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

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public void Write(in LogEntry entry) => _entries.Add(entry);

    public void Flush()
    {
    }
}
