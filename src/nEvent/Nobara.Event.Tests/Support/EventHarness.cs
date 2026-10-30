// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using F = Nobara.Formats;

namespace Nobara.Event.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real event module, and
/// modules of the test assembly whose steps run the code a test gives them.
/// </summary>
internal sealed class EventHarness : IDisposable
{
    private readonly List<(F.ModuleManifest Manifest, Action<Probe>? Behavior)> _probes = [];
    private readonly Dictionary<string, F.Policy> _policies = [];

    public EventHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-event-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets what the modules did, in order.</summary>
    public List<string> Journal { get; } = [];

    /// <summary>Gets or sets the phases of the distribution; the standard four by default.</summary>
    public F.Loop Loop { get; set; } = Standard();

    /// <summary>Gets the standard loop: input, a fixed simulation, post-simulation, and rendering.</summary>
    public static F.Loop Standard()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        loop.Phases.Add(new F.Phase { Name = "Render", Side = F.Side.Client });
        return loop;
    }

    /// <summary>Adds a business module that depends on the event module.</summary>
    /// <param name="id">The identifier of the module.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    /// <param name="policy">The error policy of the module, or <see langword="null"/> for the default.</param>
    public EventHarness Module(string id, Action<Probe>? behavior = null, F.Policy? policy = null)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both };
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.event", Range = "^1.0" });
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
        distribution.Modules.Add(new F.DistributionModule { Id = "nobara.event", Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        foreach ((F.ModuleManifest manifest, _) in _probes)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = manifest.Id, Version = manifest.Version, Hash = ByteString.CopyFrom(new byte[32]) });
        }

        foreach ((string id, F.Policy policy) in _policies)
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = id, Policy = policy });
        }

        return Engine.Load(new EngineOptions
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
        });
    }

    /// <summary>Runs one frame that lasts one tick.</summary>
    public static void Frame(Engine engine, ManualClock clock)
    {
        clock.Advance(TimeSpan.FromSeconds(1.0 / 30));
        engine.MainContext.RunFrame();
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Source(EventHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            F.ModuleManifest manifest = module.Id == "nobara.event"
                ? new F.ModuleManifest { Format = 1, Id = "nobara.event", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both }
                : harness._probes.Single(p => p.Manifest.Id == module.Id).Manifest;
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image)
        {
            if (image.Id == "nobara.event")
            {
                return new LoadedModule(typeof(EventModule).Assembly, () => new EventModule());
            }

            Action<Probe>? behavior = harness._probes.Single(p => p.Manifest.Id == image.Id).Behavior;
            return new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe(image.Id, harness.Journal);
                behavior?.Invoke(probe);
                return probe;
            });
        }
    }
}

/// <summary>A module of the test assembly whose steps run the code a test gives it.</summary>
internal sealed class Probe(string id, List<string> journal) : IModule
{
    public string Id { get; } = id;

    public IEventBus Bus { get; private set; } = null!;

    public Action<ModuleContext>? OnRegister { get; set; }

    public Action<ModuleContext, IEventBus>? OnInitialize { get; set; }

    public Action<ModuleContext, IEventBus>? OnStart { get; set; }

    public Action<ModuleContext, IEventBus>? OnStop { get; set; }

    public void Add(string line) => journal.Add(line);

    public void Register(ModuleContext context) => OnRegister?.Invoke(context);

    public void Initialize(ModuleContext context)
    {
        Bus = context.Resolve<IEventBus>();
        OnInitialize?.Invoke(context, Bus);
    }

    public void Start(ModuleContext context) => OnStart?.Invoke(context, Bus);

    public void Stop(ModuleContext context) => OnStop?.Invoke(context, Bus);
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

/// <summary>Runs code on a thread of its own, so that no task is run inline by the thread that waits for it.</summary>
internal static class OtherThread
{
    /// <summary>Runs an action on a new thread and waits for it.</summary>
    /// <returns>The exception the action threw, or <see langword="null"/>.</returns>
    public static Exception? Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.Start();
        thread.Join();
        return error;
    }
}
