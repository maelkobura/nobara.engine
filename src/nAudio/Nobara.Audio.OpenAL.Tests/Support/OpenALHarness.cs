// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using F = Nobara.Formats;

namespace Nobara.Audio.OpenAL.Tests.Support;

/// <summary>
/// An engine with the real nAudio, a probe module that keeps its context, and
/// either the real OpenAL module (on the default output) or a module that
/// registers the same backend on the loopback device, which needs no output.
/// </summary>
internal sealed class OpenALHarness : IDisposable
{
    private const string Probe = "example.probe";
    private const string LoopbackModule = "example.loopback";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nobara-openal-tests", Guid.NewGuid().ToString("N"));
    private readonly List<F.SettingValue> _settings = [];

    /// <param name="realModule">Loads the real module, which opens the default output.</param>
    /// <param name="defaultOutput">With the harness backend, opens the default output instead of the loopback device.</param>
    /// <param name="library">With the harness backend, the full path of the native library to load.</param>
    public OpenALHarness(bool realModule = false, bool defaultOutput = false, string? library = null)
    {
        RealModule = realModule;
        Backend = new OpenALBackend(loopback: !defaultOutput, library);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Gets whether the real module of the backend is loaded, which opens the default output.</summary>
    public bool RealModule { get; }

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets the loopback backend of the harness.</summary>
    public OpenALBackend Backend { get; }

    /// <summary>Gets the module that keeps its context, once loaded.</summary>
    public ProbeModule Module { get; private set; } = null!;

    public IAudioSystem System => Module.Context.Resolve<IAudioSystem>();

    /// <summary>Gives a user key its default value.</summary>
    public OpenALHarness Setting(string key, string value)
    {
        _settings.Add(new F.SettingValue { Key = key, Operation = F.SettingValue.Types.Operation.Default, Value = value });
        return this;
    }

    /// <summary>Loads and starts the engine.</summary>
    public Engine Start()
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
        string backend = RealModule ? OpenALModule.ModuleId : LoopbackModule;
        foreach (string id in new[] { AudioModule.ModuleId, backend, Probe })
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        Engine engine = Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(_root),
            FramePacing = FramePacing.Free,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
        });
        try
        {
            engine.MainContext.Start();
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        return engine;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file still open; the temporary folder is left behind.
        }
    }

    private static byte[] Embedded(Type module)
    {
        using Stream stream = module.Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] Manifest(string id, string? backendOf)
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = id, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Client };
        manifest.Dependencies.Add(new F.Dependency { Id = AudioModule.ModuleId, Range = "^1.0" });
        if (backendOf is not null)
        {
            manifest.BackendOf = backendOf;
        }

        return manifest.ToByteArray();
    }

    private sealed class Source(OpenALHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                AudioModule.ModuleId => Embedded(typeof(AudioModule)),
                OpenALModule.ModuleId => Embedded(typeof(OpenALModule)),
                LoopbackModule => Manifest(LoopbackModule, AudioModule.ModuleId),
                _ => Manifest(Probe, null),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            AudioModule.ModuleId => new LoadedModule(typeof(AudioModule).Assembly, () => new AudioModule()),
            OpenALModule.ModuleId => new LoadedModule(typeof(OpenALModule).Assembly, () => new OpenALModule()),
            LoopbackModule => new LoadedModule(null, () => new LoopbackBackendModule(harness.Backend)),
            _ => new LoadedModule(typeof(ProbeModule).Assembly, () => harness.Module = new ProbeModule()),
        };
    }

    private sealed class LoopbackBackendModule(OpenALBackend backend) : IModule
    {
        public void Initialize(ModuleContext context) => context.Resolve<IAudioBackends>().Register(context, backend);
    }
}

/// <summary>A module that keeps its context.</summary>
internal sealed class ProbeModule : IModule
{
    public ModuleContext Context { get; private set; } = null!;

    public void Register(ModuleContext context) => Context = context;
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
