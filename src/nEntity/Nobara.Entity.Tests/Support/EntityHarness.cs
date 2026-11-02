// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using Nobara.Game;
using F = Nobara.Formats;

namespace Nobara.Entity.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real game, context, and entity
/// modules, a module that owns the dimensions and opens a world session, and
/// modules of the test assembly that run the code a test gives them.
/// </summary>
internal sealed class EntityHarness : IDisposable
{
    /// <summary>The identifier of the module that owns the dimensions.</summary>
    public const string OwnerId = "example.world";

    private const string PlayStateId = "state:example.world.play";
    private readonly List<(F.ModuleManifest Manifest, Action<Probe>? Behavior)> _probes = [];
    private readonly Dictionary<string, F.Policy> _policies = [];

    public EntityHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-entity-tests", Guid.NewGuid().ToString("N"));
        Module(OwnerId, null);
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets or sets whether the engine runs in development mode.</summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>Gets or sets whether the profiler is on.</summary>
    public bool Profiling { get; set; }

    /// <summary>Gets or sets the error policy of the owner module.</summary>
    public F.Policy? OwnerPolicy { get; set; }

    /// <summary>Gets the dimensions whose worlds the owner creates when the session starts.</summary>
    public List<DimensionId> Dimensions { get; } = [DimensionId.Default];

    /// <summary>Gets or sets whether the owner leaves its worlds after the end of the session.</summary>
    public bool LeakWorlds { get; set; }

    /// <summary>Gets the owner module of the last engine loaded.</summary>
    public Probe Owner { get; private set; } = null!;

    /// <summary>Gets the instances of the modules of the test, by identifier, for the last engine loaded.</summary>
    public Dictionary<string, Probe> Probes { get; } = [];

    /// <summary>Adds a business module that depends on the entity module.</summary>
    public EntityHarness Module(string id, Action<Probe>? behavior, F.Policy? policy = null)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both };
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.entity", Range = "^1.0" });
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.game", Range = "^1.0" });
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.context", Range = "^1.0" });
        if (id == OwnerId)
        {
            manifest.States.Add(new F.StateDeclaration { Id = PlayStateId });
        }
        else
        {
            manifest.Dependencies.Add(new F.Dependency { Id = OwnerId, Range = "^1.0" });
        }

        _probes.Add((manifest, behavior));
        if (policy is { } value)
        {
            _policies[id] = value;
        }

        return this;
    }

    /// <summary>Loads and starts an engine. The first frame enters the play state, which starts the session.</summary>
    public Engine Start(Side side = Side.Client)
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
            Loop = StandardLoop(),
            States = new F.InitialStates { Client = PlayStateId, Server = PlayStateId },
        };
        foreach (string id in new[] { "nobara.game", "nobara.context", "nobara.entity" })
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        foreach ((F.ModuleManifest manifest, _) in _probes)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = manifest.Id, Version = manifest.Version, Hash = ByteString.CopyFrom(new byte[32]) });
        }

        if (OwnerPolicy is { } owner)
        {
            _policies[OwnerId] = owner;
        }

        foreach ((string id, F.Policy policy) in _policies)
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = id, Policy = policy });
        }

        Probes.Clear();
        Engine engine = Engine.Load(new EngineOptions
        {
            Side = side,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(Path.Combine(Root, Guid.NewGuid().ToString("N"))),
            TimeProvider = Clock,
            FramePacing = FramePacing.Free,
            DevelopmentMode = DevelopmentMode,
            Profiling = Profiling,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        engine.MainContext.Start();
        return engine;
    }

    /// <summary>Runs frames of one tick at 30 ticks per second.</summary>
    public void Tick(Engine engine, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1.0 / 30));
            engine.MainContext.RunFrame();
        }
    }

    /// <summary>Starts an engine and runs the first frame: the session is active and the worlds exist.</summary>
    public Engine StartSession(Side side = Side.Client)
    {
        Engine engine = Start(side);
        Tick(engine);
        Assert.Equal(SessionState.Active, Owner.Sessions.State);
        return engine;
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

    private void SetUpOwner(Probe owner)
    {
        Owner = owner;
        owner.OnInitialize += p =>
        {
            p.States.Register(p.Context, PlayStateId, () => new PlayState(p.Sessions));
            p.Sessions.SubscribeStarting(p.Context, _ =>
            {
                foreach (DimensionId dimension in Dimensions)
                {
                    if (!p.Time.HasDimension(dimension))
                    {
                        p.Time.AddDimension(dimension);
                    }

                    p.Worlds.Create(p.Context, dimension);
                }
            });
            p.Sessions.SubscribeEnded(p.Context, _ =>
            {
                if (!LeakWorlds)
                {
                    foreach (EntityWorld world in p.Worlds.Worlds.ToArray())
                    {
                        p.Worlds.Destroy(p.Context, world.Dimension);
                    }
                }
            });
        };
    }

    private sealed class Source(EntityHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            F.ModuleManifest manifest = module.Id switch
            {
                "nobara.game" => GameManifest(),
                "nobara.context" => ContextManifest(),
                "nobara.entity" => EntityManifest(),
                _ => harness._probes.Single(p => p.Manifest.Id == module.Id).Manifest,
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image)
        {
            switch (image.Id)
            {
                case "nobara.game":
                    return new LoadedModule(typeof(GameModule).Assembly, () => new GameModule());
                case "nobara.context":
                    return new LoadedModule(typeof(ContextModule).Assembly, () => new ContextModule());
                case "nobara.entity":
                    return new LoadedModule(typeof(EntityModule).Assembly, () => new EntityModule());
            }

            Action<Probe>? behavior = harness._probes.Single(p => p.Manifest.Id == image.Id).Behavior;
            return new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe(image.Id);
                if (image.Id == OwnerId)
                {
                    harness.SetUpOwner(probe);
                }

                behavior?.Invoke(probe);
                harness.Probes[image.Id] = probe;
                return probe;
            });
        }

        private static F.ModuleManifest GameManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "nobara.game", ShortName = "Game", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both };
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.event", Range = "^1.0", Optional = true });
            manifest.Reserves.Add("loop.simulation");
            return manifest;
        }

        private static F.ModuleManifest ContextManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "nobara.context", ShortName = "Context", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both };
            manifest.Reserves.Add("core.contexts");
            return manifest;
        }

        private static F.ModuleManifest EntityManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "nobara.entity", ShortName = "Entity", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both };
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.game", Range = "^1.0" });
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.context", Range = "^1.0" });
            return manifest;
        }
    }

    /// <summary>The state of the owner: it starts a session when it enters.</summary>
    private sealed class PlayState(IWorldSessions sessions) : ApplicationState
    {
        public override void Enter() => sessions.Start();
    }
}

/// <summary>A module of the test assembly, with the services of the entity, game, and context modules at hand.</summary>
internal sealed class Probe(string id) : IModule
{
    public string Id { get; } = id;

    public ModuleContext Context { get; private set; } = null!;

    public IEntityWorlds Worlds { get; private set; } = null!;

    public IGameTime Time { get; private set; } = null!;

    public ITickSchedule Schedule { get; private set; } = null!;

    public IWorldSessions Sessions { get; private set; } = null!;

    public IStateStack States { get; private set; } = null!;

    public Action<Probe>? OnInitialize { get; set; }

    public void Register(ModuleContext context) => Context = context;

    public void Initialize(ModuleContext context)
    {
        Worlds = context.Resolve<IEntityWorlds>();
        Time = context.Resolve<IGameTime>();
        Schedule = context.Resolve<ITickSchedule>();
        Sessions = context.Resolve<IWorldSessions>();
        States = context.Resolve<IStateStack>();
        OnInitialize?.Invoke(this);
    }

    /// <summary>Gets the world of a dimension.</summary>
    public EntityWorld World(DimensionId dimension = default)
    {
        Assert.True(Worlds.TryGet(dimension, out EntityWorld? world));
        return world;
    }
}

/// <summary>An entity system that runs the code a test gives it.</summary>
internal sealed class ActionSystem(Action<EntityWorld, uint> action) : IEntitySystem
{
    public void Run(in EntitySystemRun run) => action(run.World, run.LastRunVersion);
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
