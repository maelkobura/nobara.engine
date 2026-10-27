// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Formats;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using Nobara.ResourceCompiler.Plugins;

namespace Nobara.HotReload;

/// <summary>A module whose resources are watched.</summary>
/// <param name="Id">The identifier of the module.</param>
/// <param name="ResourcesDirectory">The folder of its sources: <c>Resources/</c> of its project.</param>
/// <param name="Plugins">The plugin assemblies of its dependencies, as its build uses them.</param>
public sealed record WatchedModule(string Id, string ResourcesDirectory, IReadOnlyList<string> Plugins);

/// <summary>
/// Watches the <c>.nof</c> files of modules, compiles the resources of a
/// module again when one changes, and hands them to the engine with
/// <see cref="Engine.ReloadResources"/>.
/// </summary>
/// <remarks>
/// <para>
/// A module is compiled against the schemas and the manifest of its loaded
/// assembly, the resources of its dependencies (reloaded ones included), and
/// the plugins of its dependencies. Errors are logged with their file, line,
/// and column; the engine keeps the previous resources.
/// </para>
/// <para>Compilations run on the thread pool, one at a time.</para>
/// </remarks>
public sealed class ResourceWatcher : IDisposable
{
    private readonly Engine _engine;
    private readonly Dictionary<string, WatchedModule> _modules;
    private readonly Func<string, CompiledModule?> _readModule;
    private readonly Dictionary<string, ResourceBundle> _reloaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _gate = new();
    private readonly Lock _compileGate = new();
    private bool _disposed;

    /// <summary>Initializes a watcher, without starting it.</summary>
    /// <param name="engine">The engine that receives the resources. It runs in development mode.</param>
    /// <param name="modules">The modules to watch.</param>
    /// <param name="readModule">
    /// Reads a loaded module by identifier: its compiled manifest, schemas, and
    /// resources, or <see langword="null"/> when it is not loaded.
    /// </param>
    public ResourceWatcher(Engine engine, IEnumerable<WatchedModule> modules, Func<string, CompiledModule?> readModule)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(readModule);
        _engine = engine;
        _modules = modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _readModule = readModule;
    }

    /// <summary>Gets or sets how long the watcher waits after the last change of a module before it compiles it.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Starts watching the resources folder of each module.</summary>
    public void Start()
    {
        foreach (WatchedModule module in _modules.Values)
        {
            if (!Directory.Exists(module.ResourcesDirectory))
            {
                _engine.Logger.Warning($"Hot reload: the resources of '{module.Id}' are not watched: '{module.ResourcesDirectory}' does not exist.");
                continue;
            }

            var watcher = new FileSystemWatcher(module.ResourcesDirectory, "*.nof")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            };
            string id = module.Id;
            watcher.Changed += (_, _) => Schedule(id);
            watcher.Created += (_, _) => Schedule(id);
            watcher.Deleted += (_, _) => Schedule(id);
            watcher.Renamed += (_, _) => Schedule(id);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        _engine.Logger.Info($"Hot reload: watching the resources of {_watchers.Count} modules.");
    }

    /// <summary>Compiles the resources of a watched module now and hands them to the engine.</summary>
    /// <param name="moduleId">The identifier of a watched module.</param>
    /// <returns><see langword="true"/> when the resources compiled and the engine received them.</returns>
    /// <exception cref="ArgumentException">The module is not watched.</exception>
    public bool Compile(string moduleId)
    {
        if (!_modules.TryGetValue(moduleId, out WatchedModule? module))
        {
            throw new ArgumentException($"Module '{moduleId}' is not watched.", nameof(moduleId));
        }

        lock (_compileGate)
        {
            return !_disposed && CompileLocked(module);
        }
    }

    // A compilation started by a change never ends the game: its failure is logged.
    private void CompileQuietly(string moduleId)
    {
        try
        {
            Compile(moduleId);
        }
        catch (Exception exception)
        {
            _engine.Logger.Error($"Hot reload: the resources of '{moduleId}' cannot be compiled: {exception.Message}", exception);
        }
    }

    /// <summary>Stops watching.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.Dispose();
            }

            foreach (Timer timer in _timers.Values)
            {
                timer.Dispose();
            }
        }
    }

    // Waits for the burst of events of one save to end before compiling.
    private void Schedule(string moduleId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_timers.TryGetValue(moduleId, out Timer? timer))
            {
                timer = new Timer(_ => CompileQuietly(moduleId));
                _timers[moduleId] = timer;
            }

            timer.Change(Delay, Timeout.InfiniteTimeSpan);
        }
    }

    private bool CompileLocked(WatchedModule module)
    {
        CompiledModule? self = _readModule(module.Id);
        if (self is null)
        {
            _engine.Logger.Warning($"Hot reload: '{module.Id}' is not loaded; its resources are not reloaded.");
            return false;
        }

        var dependencies = new List<CompiledModule>();
        foreach (Dependency dependency in self.Manifest.Dependencies)
        {
            if (_readModule(dependency.Id) is CompiledModule loaded)
            {
                dependencies.Add(_reloaded.TryGetValue(loaded.Id, out ResourceBundle? bundle) ? new CompiledModule(loaded.Manifest, loaded.Schemas, bundle) : loaded);
            }
        }

        var diagnostics = new DiagnosticBag();
        string[] probe = [AppContext.BaseDirectory];
        using PluginLoader plugins = PluginLoader.Load(module.Plugins.Where(File.Exists), probe, diagnostics);
        CompilationResult? result = null;
        if (!diagnostics.HasErrors)
        {
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = module.Id,
                ResourcesDirectory = module.ResourcesDirectory,
                Schemas = self.Schemas,
                Dependencies = dependencies,
                Plugins = plugins.Plugins,
            });
            string[] files = Directory.Exists(module.ResourcesDirectory)
                ? Directory.EnumerateFiles(module.ResourcesDirectory, "*.nof", SearchOption.AllDirectories).ToArray()
                : [];
            result = compiler.Compile(files);
        }

        foreach (Diagnostic diagnostic in diagnostics.Items.Concat(result?.Diagnostics ?? []))
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                _engine.Logger.Error($"Hot reload: {diagnostic}");
            }
            else
            {
                _engine.Logger.Warning($"Hot reload: {diagnostic}");
            }
        }

        if (result?.Bundle is not ResourceBundle compiled)
        {
            _engine.Logger.Error($"Hot reload: the resources of '{module.Id}' do not compile; the game keeps the previous ones.");
            return false;
        }

        if (result.Files.Any(f => f.Content is not null))
        {
            _engine.Logger.Warning($"Hot reload: binary files of '{module.Id}' are not reloaded; the game keeps those of the assembly.");
        }

        _reloaded[module.Id] = compiled;
        _engine.ReloadResources(module.Id, compiled.ToByteArray());
        _engine.Logger.Info($"Hot reload: compiled {result.CompiledFiles.Count + result.ReusedFiles.Count} files of '{module.Id}'; the game takes them at its next frame.");
        return true;
    }
}
