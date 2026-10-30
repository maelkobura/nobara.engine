// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Event;
using Nobara.ResourceCompiler.Formats;
using F = Nobara.Formats;

namespace Nobara.Game.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real game module, the real
/// event module when asked, and modules of the test assembly whose steps run
/// the code a test gives them.
/// </summary>
internal sealed class GameHarness : IDisposable
{
    private readonly List<(F.ModuleManifest Manifest, Action<Probe>? Behavior)> _probes = [];
    private readonly Dictionary<string, F.Policy> _policies = [];

    public GameHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-game-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets what the modules did, in order.</summary>
    public List<string> Journal { get; } = [];

    /// <summary>Gets or sets whether the distribution holds the event module.</summary>
    public bool WithEvents { get; set; }

    /// <summary>Gets or sets whether the engine runs in development mode.</summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>Gets or sets whether the profiler measures from start-up.</summary>
    public bool Profiling { get; set; }

    /// <summary>Gets or sets the content of <c>&lt;ModuleConfig module="nobara.game"&gt;</c>, without the element itself; <see langword="null"/> for none.</summary>
    public string? Config { get; set; }

    /// <summary>Gets or sets the loop; the standard one at 30 ticks per second by default.</summary>
    public F.Loop Loop { get; set; } = Standard(30);

    /// <summary>Gets the standard loop: input, a fixed simulation, post-simulation, and rendering.</summary>
    public static F.Loop Standard(uint tickRate)
    {
        var loop = new F.Loop { TickRate = tickRate };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        loop.Phases.Add(new F.Phase { Name = "Render", Side = F.Side.Client });
        return loop;
    }

    /// <summary>Adds a business module that depends on the game module.</summary>
    /// <param name="id">The identifier of the module.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    /// <param name="policy">The error policy of the module, or <see langword="null"/> for the default.</param>
    /// <param name="side">The sides of the module.</param>
    /// <param name="dependsOn">Other modules of the test it depends on.</param>
    public GameHarness Module(string id, Action<Probe>? behavior = null, F.Policy? policy = null, F.Side side = F.Side.Both, params string[] dependsOn)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = side };
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.game", Range = "^1.0" });
        if (WithEvents)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.event", Range = "^1.0" });
        }

        foreach (string dependency in dependsOn)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = dependency, Range = "^1.0" });
        }

        _probes.Add((manifest, behavior));
        if (policy is { } value)
        {
            _policies[id] = value;
        }

        return this;
    }

    /// <summary>Loads the engine: steps 0 and 1.</summary>
    public Engine Load(Side side = Side.Client)
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
            Loop = Loop,
        };
        if (WithEvents)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = "nobara.event", Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        distribution.Modules.Add(new F.DistributionModule { Id = "nobara.game", Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        foreach ((F.ModuleManifest manifest, _) in _probes)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = manifest.Id, Version = manifest.Version, Hash = ByteString.CopyFrom(new byte[32]) });
        }

        foreach ((string id, F.Policy policy) in _policies)
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = id, Policy = policy });
        }

        if (Config is not null)
        {
            // Line 1 holds the opening tag, so the content starts on line 2.
            string text = "<ModuleConfig module=\"nobara.game\">\n" + Config + "\n</ModuleConfig>";
            XElement element = XElement.Parse(text, LoadOptions.SetLineInfo);
            distribution.ModuleConfigs.Add(new F.ModuleConfig { Module = "nobara.game", Config = GenericTree.FromElement(element, "engine.nobara") });
        }

        return Engine.Load(new EngineOptions
        {
            Side = side,
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
    }

    /// <summary>Loads and starts the engine.</summary>
    public Engine Start(Side side = Side.Client)
    {
        Engine engine = Load(side);
        engine.MainContext.Start();
        return engine;
    }

    /// <summary>Runs frames that each last <paramref name="frame"/>, for a total of <paramref name="duration"/> of real time.</summary>
    public void Run(Engine engine, TimeSpan duration, TimeSpan frame)
    {
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < duration; elapsed += frame)
        {
            Clock.Advance(frame);
            engine.MainContext.RunFrame();
        }
    }

    /// <summary>Runs one frame that lasts one tick at 30 ticks per second.</summary>
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

    private sealed class Source(GameHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            F.ModuleManifest manifest = module.Id switch
            {
                "nobara.event" => new F.ModuleManifest { Format = 1, Id = "nobara.event", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both },
                "nobara.game" => GameManifest(),
                _ => harness._probes.Single(p => p.Manifest.Id == module.Id).Manifest,
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image)
        {
            switch (image.Id)
            {
                case "nobara.event":
                    return new LoadedModule(typeof(EventModule).Assembly, () => new EventModule());
                case "nobara.game":
                    return new LoadedModule(typeof(GameModule).Assembly, () => new GameModule());
            }

            Action<Probe>? behavior = harness._probes.Single(p => p.Manifest.Id == image.Id).Behavior;
            return new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe(image.Id, harness.Journal);
                behavior?.Invoke(probe);
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
    }
}

/// <summary>A module of the test assembly whose steps run the code a test gives it, with the services of nGame at hand.</summary>
internal sealed class Probe(string id, List<string> journal) : IModule
{
    public string Id { get; } = id;

    public ModuleContext Context { get; private set; } = null!;

    public ITickSchedule Schedule { get; private set; } = null!;

    public IGameTime Time { get; private set; } = null!;

    public IScheduler Scheduler { get; private set; } = null!;

    public Action<Probe>? OnInitialize { get; set; }

    public Action<Probe>? OnStart { get; set; }

    public void Add(string line)
    {
        lock (journal)
        {
            journal.Add(line);
        }
    }

    public void Initialize(ModuleContext context)
    {
        Context = context;
        Schedule = context.Resolve<ITickSchedule>();
        Time = context.Resolve<IGameTime>();
        Scheduler = context.Resolve<IScheduler>();
        OnInitialize?.Invoke(this);
    }

    public void Start(ModuleContext context) => OnStart?.Invoke(this);

    /// <summary>Registers a system that writes its name, and the tick, in the journal.</summary>
    public TickSystemHandle System(string name, string stage, TickSystemOptions? options = null, Action<TickTime>? action = null) =>
        Schedule.AddSystem(Context, name, stage, new ActionSystem(this, name, action), options);

    private sealed class ActionSystem(Probe probe, string name, Action<TickTime>? action) : ITickSystem
    {
        public void Run(in TickTime time)
        {
            probe.Add($"{name}@{time.Tick}");
            action?.Invoke(time);
        }
    }
}

/// <summary>A task that writes a line in the journal, then runs optional code.</summary>
internal sealed class JournalTask(Probe probe, string name, Action<TaskRun>? action = null) : IScheduledTask
{
    public void Run(in TaskRun run)
    {
        probe.Add(run.IsRealTime ? $"{name}@{run.DueTime.TotalMilliseconds:0}ms" : $"{name}@{run.ClockTick}");
        action?.Invoke(run);
    }
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
