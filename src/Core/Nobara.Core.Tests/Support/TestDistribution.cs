// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using F = Nobara.Formats;

namespace Nobara.Core.Tests.Support;

/// <summary>
/// A distribution whose modules live in the test assembly: manifests built in
/// memory, and instances created by factories. It records what the core does
/// with them.
/// </summary>
internal sealed class TestDistribution : IDisposable
{
    private readonly List<Entry> _modules = [];
    private readonly Dictionary<string, F.Policy> _policies = [];

    public TestDistribution()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-core-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets what modules did, in order: <c>id:Step</c>.</summary>
    public Journal Journal { get; } = new();

    /// <summary>Gets the messages logged by the engine.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the identifiers of the modules whose code was loaded.</summary>
    public List<string> Loaded { get; } = [];

    /// <summary>Gets the number of module instances created.</summary>
    public int Instances { get; private set; }

    public F.Policy DefaultPolicy { get; set; } = F.Policy.Stop;

    /// <summary>Gets or sets the loop of the distribution; <see langword="null"/> leaves it undeclared.</summary>
    public F.Loop? Loop { get; set; }

    /// <summary>Gets the clock of the loop, which only the test moves.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>Adds a module.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="configure">Changes to its manifest: dependencies, conflicts, side…</param>
    /// <param name="behavior">Sets up each instance of the module.</param>
    public TestDistribution Module(
        string id,
        Action<F.ModuleManifest>? configure = null,
        Action<TestModule>? behavior = null,
        bool required = false,
        string version = "1.0.0")
    {
        var manifest = new F.ModuleManifest
        {
            Format = 1,
            Id = id,
            Version = version,
            Level = F.Level.Business,
            Side = F.Side.Both,
        };
        configure?.Invoke(manifest);
        _modules.Add(new Entry(manifest, behavior, required));
        return this;
    }

    /// <summary>Sets the error policy of a module.</summary>
    public TestDistribution Policy(string id, F.Policy policy)
    {
        _policies[id] = policy;
        return this;
    }

    /// <summary>Builds the compiled distribution manifest.</summary>
    public byte[] Manifest()
    {
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = DefaultPolicy },
            Loop = Loop,
        };
        foreach (Entry entry in _modules)
        {
            distribution.Modules.Add(new F.DistributionModule
            {
                Id = entry.Manifest.Id,
                Version = entry.Manifest.Version,
                Required = entry.Required,
                Hash = ByteString.CopyFrom(new byte[32]),
            });
        }

        foreach ((string id, F.Policy policy) in _policies)
        {
            distribution.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = id, Policy = policy });
        }

        return distribution.ToByteArray();
    }

    /// <summary>Runs steps 0 and 1.</summary>
    public Engine Load(Side side = Side.Client, bool logToFile = false) => Engine.Load(Options(side, logToFile));

    /// <summary>Gets the options that load this distribution.</summary>
    public EngineOptions Options(Side side = Side.Client, bool logToFile = false, TimeProvider? clock = null, FramePacing pacing = FramePacing.Free) => new()
    {
        TimeProvider = clock ?? Clock,
        FramePacing = pacing,
        Side = side,
        DistributionManifest = Manifest(),
        ModuleSource = new Source(this),
        Paths = EnginePaths.At(Root),
        LogToConsole = false,
        LogToFile = logToFile,
        LogSinks = [Log],
        MinimumLogLevel = LogLevel.Debug,
    };

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record Entry(F.ModuleManifest Manifest, Action<TestModule>? Behavior, bool Required);

    private sealed class Source(TestDistribution distribution) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            Entry entry = distribution._modules.Single(m => m.Manifest.Id == module.Id);
            return new ModuleImage(module.Id, "memory:" + module.Id, entry.Manifest.ToByteArray());
        }

        public LoadedModule Load(ModuleImage image)
        {
            distribution.Loaded.Add(image.Id);
            Entry entry = distribution._modules.Single(m => m.Manifest.Id == image.Id);
            return new LoadedModule(typeof(TestModule).Assembly, () =>
            {
                distribution.Instances++;
                var module = new TestModule(image.Id, distribution.Journal);
                entry.Behavior?.Invoke(module);
                return module;
            });
        }
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

/// <summary>A phase handler that runs the code a test gives it.</summary>
internal sealed class PhaseHandler(Action<PhaseTime> action) : IPhaseHandler
{
    public void Run(in PhaseTime time) => action(time);
}

/// <summary>What modules did, in order.</summary>
internal sealed class Journal
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    /// <summary>Gets the lines of one step, as module identifiers.</summary>
    public string[] Of(string step) => Lines.Where(l => l.EndsWith(":" + step, StringComparison.Ordinal)).Select(l => l[..l.IndexOf(':')]).ToArray();
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

/// <summary>A module of the test assembly whose steps run the code a test gives it.</summary>
internal sealed class TestModule(string id, Journal journal) : IModule
{
    public string Id { get; } = id;

    public Action<ModuleContext>? OnRegister { get; set; }

    public Action<ModuleContext>? OnInitialize { get; set; }

    public Action<ModuleContext>? OnStart { get; set; }

    public Action<ModuleContext>? OnStop { get; set; }

    public Action<ModuleContext>? OnRelease { get; set; }

    public void Register(ModuleContext context) => Run("Registration", OnRegister, context);

    public void Initialize(ModuleContext context) => Run("Initialization", OnInitialize, context);

    public void Start(ModuleContext context) => Run("Start", OnStart, context);

    public void Stop(ModuleContext context) => Run("Stop", OnStop, context);

    public void Release(ModuleContext context) => Run("Release", OnRelease, context);

    public void OnDependencyDisabled(ModuleContext context, ModuleInfo dependency) => journal.Add($"{Id}:lost {dependency.Id}");

    private void Run(string step, Action<ModuleContext>? action, ModuleContext context)
    {
        journal.Add($"{Id}:{step}");
        action?.Invoke(context);
    }
}

/// <summary>Helpers for manifests.</summary>
internal static class ManifestExtensions
{
    public static F.ModuleManifest Requires(this F.ModuleManifest manifest, string id, string range = "^1.0")
    {
        manifest.Dependencies.Add(new F.Dependency { Id = id, Range = range });
        return manifest;
    }

    public static F.ModuleManifest Uses(this F.ModuleManifest manifest, string id, string range = "^1.0")
    {
        manifest.Dependencies.Add(new F.Dependency { Id = id, Range = range, Optional = true });
        return manifest;
    }
}
