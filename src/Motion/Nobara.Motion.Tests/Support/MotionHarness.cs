// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Items;
using Nobara.Physics;
using Nobara.Physics.Bepu;
using Nobara.Voxel;
using F = Nobara.Formats;

namespace Nobara.Motion.Tests.Support;

/// <summary>
/// Runs the real modules from nGame to nVoxel, nPhysics, the BEPUphysics
/// backend, and Motion on a server context, with a game module, <see cref="Probe"/>, that
/// starts a world session.
/// </summary>
internal sealed class MotionHarness : IDisposable
{
    public const string GameId = "example.game";
    private const string PlayStateId = "state:example.game.play";

    public MotionHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-motion-tests", Guid.NewGuid().ToString("N"));
    }

    public string Root { get; }

    public ManualClock Clock { get; } = new();

    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the values of the keys of nPhysics, by key.</summary>
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the area of the active ticket that the harness places around the origin once the session starts, so that the bodies there are simulated; <see langword="null"/> for none.</summary>
    public ChunkArea? ActiveArea { get; set; } = new ChunkArea(4, 2);

    /// <summary>Gets or sets the center of the active ticket of <see cref="ActiveArea"/>.</summary>
    public WorldPosition ActiveCenter { get; set; } = WorldPosition.FromBlocks(16, 16, 16);

    /// <summary>Gets or sets the compiled resources of the game module, such as its blocks; <see langword="null"/> for none.</summary>
    public byte[]? Resources { get; set; }

    /// <summary>Gets or sets what the game module does when it initializes, before nVoxel starts: register shapes, providers of chunks.</summary>
    public Action<MotionProbe>? Configure { get; set; }

    public MotionProbe Probe { get; private set; } = null!;

    public BepuModule Module { get; private set; } = null!;

    public IPhysicsSystem Physics => Probe.Physics;

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
        var ids = new List<string> { "nobara.game", "nobara.context", "nobara.entity", "nobara.items", "nobara.blocks", "nobara.voxel", PhysicsModule.ModuleId, BepuModule.ModuleId, MotionModule.ModuleId, GameId };
        foreach (string id in ids)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        var engine = Engine.Load(new EngineOptions
        {
            Side = Side.Server,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
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
        engine.MainContext.RunFrame();   // enters the play state, which starts the world session, without a tick
        if (ActiveArea is { } area)
        {
            Probe.Activate(ActiveCenter, area);
        }

        return engine;
    }

    /// <summary>Advances the clock by one tick and runs a frame, as many times as asked.</summary>
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
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        return loop;
    }

    private sealed class Source(MotionHarness harness) : IModuleSource
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
                PhysicsModule.ModuleId => Manifest(PhysicsModule.ModuleId, "Physics", "nobara.game", "nobara.context", "nobara.entity", "nobara.voxel", "nobara.blocks"),
                BepuModule.ModuleId => BackendManifest(),
                MotionModule.ModuleId => Manifest(MotionModule.ModuleId, "Motion", "nobara.game", "nobara.context", "nobara.entity", "nobara.blocks", "nobara.voxel", PhysicsModule.ModuleId),
                _ => GameManifest(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            "nobara.game" => new LoadedModule(typeof(GameModule).Assembly, () => new GameModule()),
            "nobara.context" => new LoadedModule(typeof(ContextModule).Assembly, () => new ContextModule()),
            "nobara.entity" => new LoadedModule(typeof(EntityModule).Assembly, () => new EntityModule()),
            "nobara.items" => new LoadedModule(typeof(ItemsModule).Assembly, () => new ItemsModule()),
            "nobara.blocks" => new LoadedModule(typeof(BlocksModule).Assembly, () => new BlocksModule()),
            "nobara.voxel" => new LoadedModule(typeof(VoxelModule).Assembly, () => new VoxelModule()),
            PhysicsModule.ModuleId => new LoadedModule(typeof(PhysicsModule).Assembly, () => new PhysicsModule()),
            BepuModule.ModuleId => new LoadedModule(typeof(BepuModule).Assembly, () => harness.Module = new BepuModule()),
            MotionModule.ModuleId => new LoadedModule(typeof(MotionModule).Assembly, () => new MotionModule()),
            _ => new LoadedModule(typeof(MotionProbe).Assembly, () => harness.Probe = new MotionProbe(harness.Configure), name => name == "nobara/resources.pb" && harness.Resources is not null ? new MemoryStream(harness.Resources) : null),
        };

        private F.ModuleManifest GameManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = GameId, Version = "1.0.0", Level = F.Level.Game, Side = F.Side.Both };
            foreach (string dependency in new[] { MotionModule.ModuleId, PhysicsModule.ModuleId, "nobara.voxel", "nobara.blocks", "nobara.items", "nobara.entity", "nobara.context", "nobara.game" })
            {
                manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
            }

            manifest.States.Add(new F.StateDeclaration { Id = PlayStateId });
            return manifest;
        }

        private static F.ModuleManifest BackendManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = BepuModule.ModuleId, ShortName = "PhysicsBepu", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both, BackendOf = PhysicsModule.ModuleId };
            manifest.Dependencies.Add(new F.Dependency { Id = PhysicsModule.ModuleId, Range = "^1.0" });
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.voxel", Range = "^1.0" });
            return manifest;
        }

        private F.ModuleManifest Manifest(string id, string shortName, params string[] dependencies)
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = id, ShortName = shortName, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both };
            foreach (string dependency in dependencies)
            {
                manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
            }

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
                    foreach ((string key, F.SettingDeclaration.Types.Type type, string value) in VoxelSettingsDeclared)
                    {
                        manifest.Settings.Add(Setting(key, type, value));
                    }

                    break;
                case MotionModule.ModuleId:
                    manifest.Settings.Add(Setting(MotionSettings.SmallGridBlocks, F.SettingDeclaration.Types.Type.Int, harness.Value(MotionSettings.SmallGridBlocks, "8")));
                    manifest.Settings.Add(Setting(MotionSettings.CornerMass, F.SettingDeclaration.Types.Type.Float, harness.Value(MotionSettings.CornerMass, "500000")));
                    manifest.Settings.Add(Setting(MotionSettings.LoadShare, F.SettingDeclaration.Types.Type.Float, harness.Value(MotionSettings.LoadShare, "0")));
                    manifest.Settings.Add(Setting(MotionSettings.PairBudget, F.SettingDeclaration.Types.Type.Int, harness.Value(MotionSettings.PairBudget, "1024")));
                    manifest.Settings.Add(Setting(MotionSettings.ImpactSpeed, F.SettingDeclaration.Types.Type.Float, harness.Value(MotionSettings.ImpactSpeed, "2")));
                    break;
                case PhysicsModule.ModuleId:
                    manifest.Reserves.Add(GridTransformWriter.Reservation);

                    manifest.Settings.Add(Setting(PhysicsSettings.Backend, F.SettingDeclaration.Types.Type.String, harness.Value(PhysicsSettings.Backend, "auto")));
                    manifest.Settings.Add(Setting(PhysicsSettings.Substeps, F.SettingDeclaration.Types.Type.Int, harness.Value(PhysicsSettings.Substeps, "8")));
                    manifest.Settings.Add(Setting(PhysicsSettings.VelocityIterations, F.SettingDeclaration.Types.Type.Int, harness.Value(PhysicsSettings.VelocityIterations, "1")));
                    break;
            }

            return manifest;
        }

        private static F.SettingDeclaration Setting(string key, F.SettingDeclaration.Types.Type type, string value) => new()
        {
            Key = key,
            Scope = F.SettingDeclaration.Types.Scope.Distribution,
            Type = type,
            DefaultValue = value,
        };
    }

    private string Value(string key, string fallback) => Settings.TryGetValue(key, out string? value) ? value : fallback;

    private static readonly (string Key, F.SettingDeclaration.Types.Type Type, string Default)[] VoxelSettingsDeclared =
    [
        (VoxelSettings.ViewRadius, F.SettingDeclaration.Types.Type.Int, "8"),
        (VoxelSettings.ViewHeight, F.SettingDeclaration.Types.Type.Int, "4"),
        (VoxelSettings.SimulationRadius, F.SettingDeclaration.Types.Type.Int, "4"),
        (VoxelSettings.SimulationHeight, F.SettingDeclaration.Types.Type.Int, "2"),
        (VoxelSettings.RandomTicksPerChunk, F.SettingDeclaration.Types.Type.Int, "3"),
        (VoxelSettings.ReducedTickInterval, F.SettingDeclaration.Types.Type.Int, "4"),
        (VoxelSettings.UnloadDelay, F.SettingDeclaration.Types.Type.Int, "300"),
        (VoxelSettings.FreezeInactiveDimensions, F.SettingDeclaration.Types.Type.Bool, "true"),
    ];
}

/// <summary>The game module of the tests: it resolves the services and starts a world session.</summary>
internal sealed class MotionProbe(Action<MotionProbe>? configure = null) : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public IPhysicsSystem Physics { get; private set; } = null!;

    public IPhysicsBackends Backends { get; private set; } = null!;

    public IEntityWorlds Entities { get; private set; } = null!;

    public IVoxelWorlds Voxels { get; private set; } = null!;

    public IGameTime Time { get; private set; } = null!;

    public ITickSchedule Schedule { get; private set; } = null!;

    public IBlockRegistry Blocks { get; private set; } = null!;

    public IVoxelBatches Batches { get; private set; } = null!;

    public IShapeRegistry Shapes { get; private set; } = null!;

    public IMotionSystem Motion { get; private set; } = null!;

    public EntityWorld World => Entities.TryGet(DimensionId.Default, out EntityWorld? world) ? world : throw new InvalidOperationException("No entity world.");

    public void Register(ModuleContext context) => Context = context;

    public void Initialize(ModuleContext context)
    {
        Physics = context.Resolve<IPhysicsSystem>();
        Backends = context.Resolve<IPhysicsBackends>();
        Entities = context.Resolve<IEntityWorlds>();
        Voxels = context.Resolve<IVoxelWorlds>();
        Time = context.Resolve<IGameTime>();
        Schedule = context.Resolve<ITickSchedule>();
        Loading = context.Resolve<IChunkLoading>();
        Blocks = context.Resolve<IBlockRegistry>();
        Batches = context.Resolve<IVoxelBatches>();
        Shapes = context.Resolve<IShapeRegistry>();
        Motion = context.Resolve<IMotionSystem>();
        configure?.Invoke(this);
        IWorldSessions sessions = context.Resolve<IWorldSessions>();
        sessions.SubscribeEnding(context, _ =>
        {
            foreach (AreaTicket ticket in Tickets)
            {
                ticket.Dispose();
            }

            Tickets.Clear();
        });
        context.Resolve<IStateStack>().Register(context, "state:example.game.play", () => new PlayState(sessions));
    }

    public IChunkLoading Loading { get; private set; } = null!;

    /// <summary>Gets the active tickets this module placed; they are disposed when the session ends.</summary>
    public List<AreaTicket> Tickets { get; } = [];

    /// <summary>Holds an area of the terrain of the default dimension at the level <see cref="TicketLevel.Active"/>: nPhysics simulates the bodies there.</summary>
    public AreaTicket Activate(in WorldPosition center, ChunkArea area)
    {
        if (!Voxels.TryGetTerrain(DimensionId.Default, out Grid? terrain))
        {
            throw new InvalidOperationException("No terrain.");
        }

        AreaTicket ticket = Loading.AddAreaTicket(Context, terrain!, center, area, TicketLevel.Active);
        Tickets.Add(ticket);
        return ticket;
    }

    /// <summary>Creates an entity with a world transform, outside entity systems.</summary>
    public EntityId CreateEntity(in WorldTransform transform)
    {
        EntityWorld world = World;
        EntityId entity = world.Create(world.GetArchetype(Entities.WorldTransform));
        world.Set(entity, Entities.WorldTransform, transform);
        return entity;
    }

    /// <summary>Creates an entity and makes it a body.</summary>
    public BodyHandle CreateBody(in WorldPosition position, BodyKind kind = BodyKind.Dynamic, PhysicsShape? shape = null, float mass = 1f)
    {
        EntityId entity = CreateEntity(WorldTransform.At(position));
        return Physics.AddBody(DimensionId.Default, entity, new BodyDescription { Kind = kind, Shape = shape ?? Physics.CreateSphere(0.5f), Mass = mass });
    }

    public WorldTransform TransformOf(BodyHandle body) => Physics.GetState(body).Transform;

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
