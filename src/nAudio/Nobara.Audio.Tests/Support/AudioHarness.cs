// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Audio.Build;
using Nobara.Core;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.HotReload;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Audio.Tests.Support;

/// <summary>
/// An engine whose modules live in memory: the real nAudio, an empty audio
/// backend on a client, and a game module whose resources the tests compile
/// from a copy of <c>Resources/</c>.
/// </summary>
internal sealed class AudioHarness : IDisposable
{
    public const string Game = "example.game";

    private const string Backend = "example.audio";

    private readonly List<string> _folders = [];
    private CompilationResult? _game;

    public AudioHarness()
    {
        Root = Folder("data");
    }

    /// <summary>Gets the scratch folder of the data folders.</summary>
    public string Root { get; }

    /// <summary>Gets or sets whether the engine runs in development mode, which reloads resources.</summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>Gets the messages the engine logged.</summary>
    public MemoryLogSink Log { get; } = new();

    /// <summary>Gets nAudio, as the compiler reads it from its assembly.</summary>
    public static CompiledModule Audio { get; } = CompiledModule.FromAssembly(typeof(AudioModule).Assembly.Location)!;

    /// <summary>Gets the game module, once loaded.</summary>
    public Probe Probe { get; private set; } = null!;

    public ISoundRegistry Sounds => Probe.Context.Resolve<ISoundRegistry>();

    /// <summary>Copies the resources of the tests to a new folder, which a test may change.</summary>
    public string CopyResources()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Resources");
        string target = Folder("resources");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        return target;
    }

    /// <summary>Compiles the resources of a module that depends on nAudio.</summary>
    public static CompilationResult Compile(string resources, string? cacheFile = null)
    {
        var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
        {
            ModuleId = Game,
            ResourcesDirectory = resources,
            Dependencies = [Audio],
            Plugins = [new AudioPlugin()],
            CacheFile = cacheFile,
        });
        return compiler.Compile(Directory.EnumerateFiles(resources, "*.nof", SearchOption.AllDirectories));
    }

    /// <summary>Compiles resources that must compile.</summary>
    public static CompilationResult Compiled(string resources, string? cacheFile = null)
    {
        CompilationResult result = Compile(resources, cacheFile);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }

    /// <summary>Gets the embedded files of a compilation, by path, as the compiler embeds them.</summary>
    public static Dictionary<string, byte[]> Files(CompilationResult result) =>
        result.Files.ToDictionary(f => f.Path, f => f.Content ?? File.ReadAllBytes(f.SourcePath), StringComparer.Ordinal);

    /// <summary>Gives the game the resources of a compilation.</summary>
    public AudioHarness WithGame(CompilationResult resources)
    {
        _game = resources;
        return this;
    }

    /// <summary>
    /// Creates the watcher of a development build for the game: it compiles the
    /// resources of the game again from a folder, with the plugin of nAudio as
    /// the build gives it, and hands them to the engine.
    /// </summary>
    public ResourceWatcher Watcher(Engine engine, string resources) => new(
        engine,
        [new WatchedModule(Game, resources, [Path.Combine(AppContext.BaseDirectory, "Nobara.Audio.Build.dll")])],
        id => id switch
        {
            Game => new CompiledModule(F.ModuleManifest.Parser.ParseFrom(GameManifest()), null, _game!.Bundle!),
            AudioModule.ModuleId => Audio,
            _ => null,
        });

    /// <summary>Loads and starts the engine.</summary>
    /// <param name="side">The side of the main context.</param>
    /// <param name="bundle">Changes the compiled resources of the game before they load, or <see langword="null"/>.</param>
    public Engine Start(Side side = Side.Client, Action<F.ResourceBundle>? bundle = null)
    {
        if (bundle is not null)
        {
            bundle(_game!.Bundle!);
        }

        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
        };
        string[] modules = side == Side.Client ? [AudioModule.ModuleId, Backend, Game] : [AudioModule.ModuleId, Game];
        foreach (string id in modules)
        {
            distribution.Modules.Add(new F.DistributionModule { Id = id, Version = "1.0.0", Hash = ByteString.CopyFrom(new byte[32]) });
        }

        Engine engine = Engine.Load(new EngineOptions
        {
            Side = side,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new Source(this),
            Paths = EnginePaths.At(Root),
            FramePacing = FramePacing.Free,
            LogToConsole = false,
            LogToFile = false,
            LogSinks = [Log],
            MinimumLogLevel = LogLevel.Debug,
            DevelopmentMode = DevelopmentMode,
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
        foreach (string folder in _folders.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file still open; the temporary folder is left behind.
            }
        }
    }

    private string Folder(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "nobara-audio-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(folder);
        _folders.Add(Path.GetDirectoryName(folder)!);
        return folder;
    }

    private static byte[] Embedded(Type module)
    {
        using Stream stream = module.Assembly.GetManifestResourceStream(AssemblyModuleSource.ManifestResource)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] BackendManifest()
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = Backend, Version = "1.0.0", Level = F.Level.Platform, Side = F.Side.Client, BackendOf = AudioModule.ModuleId };
        manifest.Dependencies.Add(new F.Dependency { Id = AudioModule.ModuleId, Range = "^1.0" });
        return manifest.ToByteArray();
    }

    private static byte[] GameManifest()
    {
        var manifest = new F.ModuleManifest { Format = 1, Id = Game, Version = "1.0.0", Level = F.Level.Game, Side = F.Side.Both };
        manifest.Dependencies.Add(new F.Dependency { Id = AudioModule.ModuleId, Range = "^1.0" });
        return manifest.ToByteArray();
    }

    private sealed class Source(AudioHarness harness) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module)
        {
            byte[] manifest = module.Id switch
            {
                AudioModule.ModuleId => Embedded(typeof(AudioModule)),
                Backend => BackendManifest(),
                _ => GameManifest(),
            };
            return new ModuleImage(module.Id, "memory:" + module.Id, manifest);
        }

        public LoadedModule Load(ModuleImage image) => image.Id switch
        {
            AudioModule.ModuleId => new LoadedModule(typeof(AudioModule).Assembly, () => new AudioModule()),
            Backend => new LoadedModule(null, () => new EmptyModule()),
            _ => new LoadedModule(null, () => harness.Probe = new Probe(), harness.OpenGameResource),
        };
    }

    private Stream? OpenGameResource(string name)
    {
        if (_game?.Bundle is null)
        {
            return null;
        }

        if (name == "nobara/resources.pb")
        {
            return new MemoryStream(_game.Bundle.ToByteArray(), writable: false);
        }

        const string Prefix = "nobara/files/";
        EmbeddedFile? file = name.StartsWith(Prefix, StringComparison.Ordinal) ? _game.Files.FirstOrDefault(f => f.Path == name[Prefix.Length..]) : null;
        return file is null ? null : new MemoryStream(file.Content ?? File.ReadAllBytes(file.SourcePath), writable: false);
    }

    /// <summary>The audio backend: nothing until nAudio has a registry of backends.</summary>
    private sealed class EmptyModule : IModule
    {
    }
}

/// <summary>The game module: it keeps its context.</summary>
internal sealed class Probe : IModule
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
