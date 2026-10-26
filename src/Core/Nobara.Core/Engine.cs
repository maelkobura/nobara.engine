// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using Nobara.Core.Loading;
using Nobara.Core.Logging;

namespace Nobara.Core;

/// <summary>How to start the engine.</summary>
public sealed class EngineOptions
{
    /// <summary>Gets the side of the main context: <see cref="Side.Client"/> for the game, <see cref="Side.Server"/> for a dedicated server.</summary>
    public required Side Side { get; init; }

    /// <summary>
    /// Gets the compiled distribution manifest. When it is empty, the manifest
    /// is read from <see cref="EntryAssembly"/>.
    /// </summary>
    public ReadOnlyMemory<byte> DistributionManifest { get; init; }

    /// <summary>
    /// Gets the entry assembly of the game, whose resource
    /// <see cref="DistributionInfo.ManifestResource"/> holds the distribution
    /// manifest. Defaults to the entry assembly of the process.
    /// </summary>
    public Assembly? EntryAssembly { get; init; }

    /// <summary>
    /// Gets the folder where the game is installed, which contains <c>bin/</c>,
    /// <c>system/</c>, <c>game/</c>, and <c>lib/</c>. Defaults to the parent of
    /// the folder of the entry assembly.
    /// </summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Gets where the modules are found. Defaults to an <see cref="AssemblyModuleSource"/> over <see cref="InstallDirectory"/>.</summary>
    public IModuleSource? ModuleSource { get; init; }

    /// <summary>
    /// Gets the data folders. Defaults to the user data of the operating
    /// system for a client, and to <see cref="InstallDirectory"/> for a
    /// dedicated server.
    /// </summary>
    public EnginePaths? Paths { get; init; }

    /// <summary>Gets the lowest level of the messages that are logged.</summary>
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    /// <summary>Gets whether messages are written to the console.</summary>
    public bool LogToConsole { get; init; } = true;

    /// <summary>Gets whether messages are written to a file of the logs folder.</summary>
    public bool LogToFile { get; init; } = true;

    /// <summary>Gets other destinations of the messages.</summary>
    public IReadOnlyList<ILogSink> LogSinks { get; init; } = [];
}

/// <summary>
/// The engine in one process: the distribution, its loaded modules, and the
/// contexts that run them.
/// </summary>
/// <remarks>
/// <see cref="Load"/> runs steps 0 and 1, once per process. Each context then
/// runs steps 2 to 7 for its own instances of the modules.
/// </remarks>
/// <example>
/// <code>
/// using Engine engine = Engine.Load(new EngineOptions { Side = Side.Client });
/// engine.MainContext.Start();
/// // ...
/// engine.MainContext.Stop();
/// </code>
/// </example>
public sealed class Engine : IDisposable
{
    private readonly LogDispatcher _logs;
    private readonly FileLogSink? _fileLog;
    private readonly Dictionary<Assembly, string?> _moduleOfAssembly = [];
    private readonly List<EngineContext> _contexts = [];
    private bool _disposed;

    private Engine(Distribution distribution, EnginePaths paths, LogDispatcher logs, FileLogSink? fileLog)
    {
        Distribution = distribution.Info;
        Paths = paths;
        _logs = logs;
        _fileLog = fileLog;
        Logger = CreateLogger(string.Empty, "core", null);
    }

    /// <summary>Gets the identity of the game.</summary>
    public DistributionInfo Distribution { get; }

    /// <summary>Gets the data folders.</summary>
    public EnginePaths Paths { get; }

    /// <summary>Gets the modules of the distribution, in load order.</summary>
    public IReadOnlyList<ModuleInfo> Modules { get; private set; } = [];

    /// <summary>Gets the logger of the core for the process.</summary>
    public Logger Logger { get; }

    /// <summary>Gets the context created at start-up, which lives as long as the process: the client, or the dedicated server.</summary>
    public EngineContext MainContext { get; private set; } = null!;

    internal IReadOnlyList<LoadedEntry> Loaded { get; private set; } = [];

    /// <summary>
    /// Reads the distribution, checks its modules (step 0), and loads them
    /// (step 1), then creates the main context without starting it.
    /// </summary>
    /// <param name="options">How to start.</param>
    /// <returns>The engine.</returns>
    /// <exception cref="ArgumentException"><see cref="EngineOptions.Side"/> is not a single side.</exception>
    /// <exception cref="ModuleCompositionException">The distribution cannot be read, or its modules cannot run together. No module code has run.</exception>
    /// <exception cref="ModuleLoadException">The code of an accepted module cannot be loaded.</exception>
    public static Engine Load(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Side is not (Side.Client or Side.Server))
        {
            throw new ArgumentException("The main context serves one side: Client or Server.", nameof(options));
        }

        Assembly? entryAssembly = options.EntryAssembly ?? Assembly.GetEntryAssembly();
        Distribution distribution = !options.DistributionManifest.IsEmpty ? Loading.Distribution.Read(options.DistributionManifest)
            : entryAssembly is not null ? Loading.Distribution.Read(entryAssembly)
            : throw new ModuleCompositionException(
                [new CompositionProblem(CompositionProblemKind.InvalidDistribution, string.Empty, "No distribution manifest and no entry assembly were given.")]);

        string install = Path.GetFullPath(options.InstallDirectory ?? DefaultInstallDirectory(entryAssembly));
        EnginePaths paths = options.Paths ?? (options.Side == Side.Server
            ? EnginePaths.ForServer(install)
            : EnginePaths.ForClient(distribution.Info.Name.Length > 0 ? distribution.Info.Name : distribution.Info.Id));

        var sinks = new List<ILogSink>(options.LogSinks);
        if (options.LogToConsole)
        {
            sinks.Add(new ConsoleLogSink());
        }

        FileLogSink? fileLog = options.LogToFile ? new FileLogSink(paths.Logs) : null;
        if (fileLog is not null)
        {
            sinks.Add(fileLog);
        }

        var engine = new Engine(distribution, paths, new LogDispatcher(sinks, options.MinimumLogLevel), fileLog);
        try
        {
            engine.LoadModules(distribution, options.ModuleSource ?? new AssemblyModuleSource(install), options.Side);
            engine.MainContext = engine.CreateContext(options.Side);
            return engine;
        }
        catch (Exception exception) when (exception is ModuleCompositionException or ModuleLoadException)
        {
            engine.Logger.Critical(exception.Message);
            engine.Dispose();
            throw;
        }
    }

    /// <summary>Stops every running context, then closes the logs.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (EngineContext context in Enumerable.Reverse(_contexts))
        {
            context.Stop();
        }

        _logs.Flush();
        _fileLog?.Dispose();
    }

    internal Logger CreateLogger(string context, string source, PhaseTracker? phase) => new(_logs, context, source, phase);

    internal string? ModuleOfType(Type type) => _moduleOfAssembly.GetValueOrDefault(type.Assembly);

    internal EngineContext CreateContext(Side side)
    {
        var context = new EngineContext(this, side);
        _contexts.Add(context);
        return context;
    }

    private void LoadModules(Distribution distribution, IModuleSource source, Side side)
    {
        Logger.Info($"Loading {distribution.Info.Name} {distribution.Info.Version}: {distribution.Manifest.Modules.Count} modules.");

        // A client may host an integrated server, so both sides must hold together.
        Side hosted = side == Side.Client ? Side.Both : Side.Server;
        IReadOnlyList<DiscoveredModule> accepted = Composition.Compose(distribution, source, hosted);

        var loaded = new List<LoadedEntry>(accepted.Count);
        foreach (DiscoveredModule module in accepted.Where(m => (m.Info.Side & hosted) != Side.None))
        {
            Logger.Debug($"Loading {module.Info.Id} {module.Info.Version} from {module.Image.Location}.");
            LoadedModule code = source.Load(module.Image);
            loaded.Add(new LoadedEntry(module.Info, code));
            if (code.Assembly is not null)
            {
                // An assembly shared by several modules belongs to none of them.
                _moduleOfAssembly[code.Assembly] = _moduleOfAssembly.ContainsKey(code.Assembly) ? null : module.Info.Id;
            }
        }

        Loaded = loaded;
        Modules = loaded.Select(e => e.Info).ToArray();
        Logger.Info($"Loaded {loaded.Count} modules.");
    }

    private static string DefaultInstallDirectory(Assembly? entryAssembly)
    {
        string bin = entryAssembly is not null && entryAssembly.Location.Length > 0
            ? Path.GetDirectoryName(entryAssembly.Location)!
            : AppContext.BaseDirectory;
        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(bin)) ?? bin;
    }

    internal sealed record LoadedEntry(ModuleInfo Info, LoadedModule Module);
}
