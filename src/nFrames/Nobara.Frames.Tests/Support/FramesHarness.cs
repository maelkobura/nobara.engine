// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Event;
using F = Nobara.Formats;

namespace Nobara.Frames.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real nFrames, the real event
/// module when asked, a fake window backend, and modules of the test assembly
/// whose steps run the code a test gives them.
/// </summary>
internal sealed class FramesHarness : IDisposable
{
    private readonly List<(F.ModuleManifest Manifest, Action<Probe>? Behavior)> _probes = [];
    private readonly List<F.SettingValue> _settings = [];

    public FramesHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-frames-tests", Guid.NewGuid().ToString("N"));
        Backend = new FakeBackend("fake") { OnWait = Clock.Advance };
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the fake backend that the backend module registers.</summary>
    public FakeBackend Backend { get; }

    /// <summary>Gets or sets a second fake backend, registered after <see cref="Backend"/>; <see langword="null"/> for none.</summary>
    public FakeBackend? Other { get; set; }

    /// <summary>Gets or sets whether the distribution holds the event module.</summary>
    public bool WithEvents { get; set; }

    /// <summary>Gets or sets whether the distribution holds the backend module.</summary>
    public bool WithBackend { get; set; } = true;

    /// <summary>Gives a configuration key a default of the distribution.</summary>
    public FramesHarness Setting(string key, string value)
    {
        _settings.Add(new F.SettingValue { Key = key, Operation = F.SettingValue.Types.Operation.Default, Value = value });
        return this;
    }

    /// <summary>Adds a business module that depends on nFrames.</summary>
    /// <param name="id">The identifier of the module.</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    /// <param name="reserves">Exclusive resources it reserves.</param>
    public FramesHarness Module(string id, Action<Probe>? behavior = null, params string[] reserves)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Client };
        manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
        if (WithEvents)
        {
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.event", Range = "^1.0" });
        }

        manifest.Reserves.AddRange(reserves);
        _probes.Add((manifest, behavior));
        return this;
    }

    /// <summary>Loads the engine: steps 0 and 1.</summary>
    public Engine Load()
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
        };
        distribution.Settings.AddRange(_settings);
        foreach (string id in ModuleIds())
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        return Engine.Load(new EngineOptions
        {
            Side = Side.Client,
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

    /// <summary>Loads and starts the engine.</summary>
    public Engine Start()
    {
        Engine engine = Load();
        engine.MainContext.Start();
        return engine;
    }

    /// <summary>Runs one frame that lasts <paramref name="duration"/>, 1/60 s by default.</summary>
    public void Frame(Engine engine, TimeSpan? duration = null)
    {
        Clock.Advance(duration ?? TimeSpan.FromSeconds(1.0 / 60));
        engine.MainContext.RunFrame();
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private IEnumerable<string> ModuleIds()
    {
        if (WithEvents)
        {
            yield return "nobara.event";
        }

        yield return "nobara.frames";
        if (WithBackend)
        {
            yield return "example.window";
        }

        foreach ((F.ModuleManifest manifest, _) in _probes)
        {
            yield return manifest.Id;
        }
    }

    private sealed class Source(FramesHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                "nobara.event" => new F.ModuleManifest { Format = 1, Id = "nobara.event", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Both }.ToByteArray(),
                "nobara.frames" => FramesManifest(),
                "example.window" => BackendManifest().ToByteArray(),
                _ => harness._probes.Single(p => p.Manifest.Id == module.Id).Manifest.ToByteArray(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image)
        {
            switch (image.Id)
            {
                case "nobara.event":
                    return new LoadedModule(typeof(EventModule).Assembly, () => new EventModule());
                case "nobara.frames":
                    return new LoadedModule(typeof(FramesModule).Assembly, () => new FramesModule());
                case "example.window":
                    return new LoadedModule(typeof(BackendModule).Assembly, () => new BackendModule(harness));
            }

            Action<Probe>? behavior = harness._probes.Single(p => p.Manifest.Id == image.Id).Behavior;
            return new LoadedModule(typeof(Probe).Assembly, () =>
            {
                var probe = new Probe();
                behavior?.Invoke(probe);
                return probe;
            });
        }

        // The manifest that the SDK compiled from module.nobara, so the tests read the real keys.
        private static byte[] FramesManifest()
        {
            using Stream stream = typeof(FramesModule).Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)
                ?? throw new InvalidOperationException("The assembly of nFrames holds no compiled manifest.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static F.ModuleManifest BackendManifest()
        {
            var manifest = new F.ModuleManifest { Format = 1, Id = "example.window", Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Client, BackendOf = "nobara.frames" };
            manifest.Dependencies.Add(new F.Dependency { Id = "nobara.frames", Range = "^1.0" });
            return manifest;
        }
    }

    /// <summary>The module of the fake backends: it registers them during its initialization.</summary>
    private sealed class BackendModule(FramesHarness harness) : IModule
    {
        public void Initialize(ModuleContext context)
        {
            var backends = context.Resolve<IWindowBackends>();
            backends.Register(context, harness.Backend);
            if (harness.Other is not null)
            {
                backends.Register(context, harness.Other);
            }
        }
    }
}

/// <summary>A module of the test assembly whose steps run the code a test gives it.</summary>
internal sealed class Probe : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public Action<Probe>? OnRegister { get; set; }

    public Action<Probe>? OnInitialize { get; set; }

    public Action<Probe>? OnStart { get; set; }

    public IWindowSystem Windows => Context.Resolve<IWindowSystem>();

    public void Register(ModuleContext context)
    {
        Context = context;
        OnRegister?.Invoke(this);
    }

    public void Initialize(ModuleContext context) => OnInitialize?.Invoke(this);

    public void Start(ModuleContext context) => OnStart?.Invoke(this);
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
