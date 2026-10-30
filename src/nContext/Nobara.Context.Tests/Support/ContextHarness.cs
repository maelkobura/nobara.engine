// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using F = Nobara.Formats;

namespace Nobara.Context.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real context module, and
/// modules of the test assembly that declare states and run the code a test
/// gives them.
/// </summary>
internal sealed class ContextHarness : IDisposable
{
    private readonly List<(F.ModuleManifest Manifest, Action<Probe>? Behavior)> _probes = [];
    private readonly Dictionary<string, F.Policy> _policies = [];

    public ContextHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-context-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets what the states and modules did, in order.</summary>
    public List<string> Journal { get; } = [];

    /// <summary>Gets or sets the initial client state, or <see langword="null"/> for none.</summary>
    public string? ClientState { get; set; }

    /// <summary>Gets or sets the initial server state, or <see langword="null"/> for none.</summary>
    public string? ServerState { get; set; }

    /// <summary>Adds a business module that depends on the context module and declares states.</summary>
    /// <param name="id">The identifier of the module.</param>
    /// <param name="states">The states its manifest declares, with their side; <see cref="F.Side.Unspecified"/> for every side of the module.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    /// <param name="side">The sides of the module.</param>
    /// <param name="policy">The error policy of the module, or <see langword="null"/> for the default.</param>
    public ContextHarness Module(string id, (string Id, F.Side Side)[] states, Action<Probe>? behavior = null, F.Side side = F.Side.Both, F.Policy? policy = null)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = side };
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.context", Range = "^1.0" });
        foreach ((string state, F.Side stateSide) in states)
        {
            manifest.States.Add(new F.StateDeclaration { Id = state, Side = stateSide });
        }

        _probes.Add((manifest, behavior));
        if (policy is { } value)
        {
            _policies[id] = value;
        }

        return this;
    }

    /// <summary>Gets the options of the engine, for <see cref="Engine.Load"/> or <see cref="Engine.Run"/>.</summary>
    public EngineOptions Options(Side side = Side.Client)
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
            Loop = StandardLoop(),
            States = new F.InitialStates { Client = ClientState ?? string.Empty, Server = ServerState ?? string.Empty },
        };
        distribution.Modules.Add(new F.DistributionModule { Id = "nobara.context", Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        foreach ((F.ModuleManifest manifest, _) in _probes)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = manifest.Id, Version = manifest.Version, Hash = ByteString.CopyFrom(new byte[32]) });
        }

        foreach ((string id, F.Policy policy) in _policies)
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = id, Policy = policy });
        }

        return new EngineOptions
        {
            Side = side,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(Root),
            TimeProvider = Clock,
            FramePacing = FramePacing.Free,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        };
    }

    /// <summary>Loads the engine: steps 0 and 1.</summary>
    public Engine Load(Side side = Side.Client) => Engine.Load(Options(side));

    /// <summary>Loads and starts the engine.</summary>
    public Engine Start(Side side = Side.Client)
    {
        Engine engine = Load(side);
        engine.MainContext.Start();
        return engine;
    }

    /// <summary>Runs frames of one tick at 30 ticks per second.</summary>
    public void Frame(Engine engine, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1.0 / 30));
            engine.MainContext.RunFrame();
        }
    }

    /// <summary>Writes a line in the journal.</summary>
    public void Add(string line)
    {
        lock (Journal)
        {
            Journal.Add(line);
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
        loop.Phases.Add(new F.Phase { Name = "Render", Side = F.Side.Client });
        return loop;
    }

    private sealed class Source(ContextHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            F.ModuleManifest manifest = module.Id == "nobara.context"
                ? ContextManifest()
                : harness._probes.Single(p => p.Manifest.Id == module.Id).Manifest;
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image)
        {
            if (image.Id == "nobara.context")
            {
                return new LoadedModule(typeof(ContextModule).Assembly, () => new ContextModule());
            }

            Action<Probe>? behavior = harness._probes.Single(p => p.Manifest.Id == image.Id).Behavior;
            return new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe(image.Id, harness);
                behavior?.Invoke(probe);
                return probe;
            });
        }

        private static F.ModuleManifest ContextManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "nobara.context", ShortName = "Context", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both };
            manifest.Reserves.Add("core.contexts");
            return manifest;
        }
    }
}

/// <summary>A module of the test assembly that registers states and runs the code a test gives it, with the state stack at hand.</summary>
internal sealed class Probe(string id, ContextHarness harness) : IModule
{
    public string Id { get; } = id;

    public ContextHarness Harness { get; } = harness;

    public ModuleContext Context { get; private set; } = null!;

    public IStateStack States { get; private set; } = null!;

    public Action<Probe>? OnRegister { get; set; }

    public Action<Probe>? OnInitialize { get; set; }

    public void Register(ModuleContext context)
    {
        Context = context;
        OnRegister?.Invoke(this);
    }

    public void Initialize(ModuleContext context)
    {
        States = context.Resolve<IStateStack>();
        OnInitialize?.Invoke(this);
    }

    /// <summary>Registers a state that writes its calls in the journal.</summary>
    public void State(string id, Action<JournalState>? setup = null, bool keepStatesBelowRunning = false) =>
        States.Register(Context, id, () =>
        {
            var state = new JournalState(Harness, States);
            setup?.Invoke(state);
            return state;
        }, new StateOptions { KeepStatesBelowRunning = keepStatesBelowRunning });
}

/// <summary>A state that writes each call in the journal, then runs the code a test gives it.</summary>
internal sealed class JournalState(ContextHarness harness, IStateStack states) : ApplicationState
{
    public IStateStack States { get; } = states;

    /// <summary>Gets or sets whether <see cref="Update"/> writes in the journal.</summary>
    public bool LogUpdates { get; set; }

    public Action<JournalState>? OnEnter { get; set; }

    public Action<JournalState>? OnExit { get; set; }

    public Action<JournalState, PhaseTime>? OnUpdate { get; set; }

    public Func<JournalState, GlobalEvent, bool>? OnEvent { get; set; }

    /// <summary>Gets the number of updates so far.</summary>
    public int Updates { get; private set; }

    public override void Enter()
    {
        harness.Add($"{Name}:Enter");
        OnEnter?.Invoke(this);
    }

    public override void Exit()
    {
        harness.Add($"{Name}:Exit");
        OnExit?.Invoke(this);
    }

    public override void Suspend() => harness.Add($"{Name}:Suspend");

    public override void Resume() => harness.Add($"{Name}:Resume");

    public override void Update(in PhaseTime time)
    {
        Updates++;
        if (LogUpdates)
        {
            harness.Add($"{Name}:Update@{time.Frame}");
        }

        OnUpdate?.Invoke(this, time);
    }

    public override bool OnGlobalEvent(GlobalEvent e)
    {
        harness.Add($"{Name}:{e.GetType().Name}");
        return OnEvent?.Invoke(this, e) ?? false;
    }

    // "state:example.menu" is written "menu".
    private string Name => Id[(Id.LastIndexOf('.') + 1)..];
}

/// <summary>A phase handler that runs the code a test gives it.</summary>
internal sealed class PhaseHandler(Action<PhaseTime> action) : IPhaseHandler
{
    public void Run(in PhaseTime time) => action(time);
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
