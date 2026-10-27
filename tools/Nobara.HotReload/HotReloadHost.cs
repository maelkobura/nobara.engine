// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Text.Json;
using Nobara.Core;
using Nobara.ResourceCompiler.Nof;

namespace Nobara.HotReload;

/// <summary>
/// Hot reload in a development build: reads the watched modules from the
/// entry assembly and starts a <see cref="ResourceWatcher"/> once the engine
/// is loaded.
/// </summary>
/// <remarks>
/// The entry assembly that the command-line tool generates for a development
/// build uses it when the game is launched with <see cref="Option"/>:
/// <code>
/// using var hotReload = new HotReloadHost(typeof(Program).Assembly);
/// return Engine.Run(new EngineOptions
/// {
///     Side = Side.Client,
///     DevelopmentMode = true,
///     OnLoaded = hotReload.Attach,
/// });
/// </code>
/// </remarks>
public sealed class HotReloadHost : IDisposable
{
    /// <summary>The launch option that turns hot reload on.</summary>
    public const string Option = "--hot-reload";

    /// <summary>The name of the manifest resource of the entry assembly that lists the watched modules.</summary>
    public const string MapResource = "nobara/hot-reload.json";

    private readonly IReadOnlyList<WatchedModule> _modules;
    private readonly string _installDirectory;
    private ResourceWatcher? _watcher;

    /// <summary>Reads the watched modules from the entry assembly of a development build.</summary>
    /// <param name="entryAssembly">The entry assembly, in <c>bin/</c> of the installed game.</param>
    /// <exception cref="InvalidOperationException">The assembly lists no watched module: it was not built for development.</exception>
    public HotReloadHost(Assembly entryAssembly)
    {
        ArgumentNullException.ThrowIfNull(entryAssembly);
        using Stream stream = entryAssembly.GetManifestResourceStream(MapResource)
            ?? throw new InvalidOperationException($"The entry assembly '{entryAssembly.GetName().Name}' has no {MapResource}: hot reload needs a development build.");
        _modules = ReadMap(stream);
        string bin = Path.GetDirectoryName(entryAssembly.Location) is { Length: > 0 } folder ? folder : AppContext.BaseDirectory;
        _installDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(bin)) ?? bin;
    }

    /// <summary>Gets the modules whose resources are watched.</summary>
    public IReadOnlyList<WatchedModule> Modules => _modules;

    /// <summary>Tells whether the launch arguments ask for hot reload.</summary>
    /// <param name="arguments">The arguments of <c>main()</c>.</param>
    /// <returns><see langword="true"/> when <see cref="Option"/> is one of them.</returns>
    public static bool IsRequested(IEnumerable<string> arguments) => arguments.Contains(Option, StringComparer.Ordinal);

    /// <summary>Starts watching the resources for an engine. Pass it as <see cref="EngineOptions.OnLoaded"/>.</summary>
    /// <param name="engine">The loaded engine, in development mode.</param>
    /// <exception cref="InvalidOperationException">The engine does not run in development mode, or a watcher is already attached.</exception>
    public void Attach(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!engine.DevelopmentMode)
        {
            throw new InvalidOperationException("Hot reload needs an engine in development mode.");
        }

        if (_watcher is not null)
        {
            throw new InvalidOperationException("Hot reload is already attached to an engine.");
        }

        _watcher = new ResourceWatcher(engine, _modules, ReadInstalledModule);
        _watcher.Start();
    }

    /// <summary>Stops watching.</summary>
    public void Dispose() => _watcher?.Dispose();

    // A module of the installed game, read from its assembly in system/ or game/.
    private CompiledModule? ReadInstalledModule(string moduleId)
    {
        foreach (string folder in new[] { "system", "game" })
        {
            string directory = Path.Combine(_installDirectory, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            string? path = Directory.EnumerateFiles(directory, "*.dll")
                .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), moduleId, StringComparison.OrdinalIgnoreCase));
            if (path is not null)
            {
                return CompiledModule.FromAssembly(path);
            }
        }

        return null;
    }

    /// <summary>Reads the list of watched modules.</summary>
    /// <remarks>
    /// The format: <c>{"modules": [{"id": "…", "resources": "…", "plugins": ["…"]}]}</c>,
    /// with absolute paths.
    /// </remarks>
    internal static IReadOnlyList<WatchedModule> ReadMap(Stream stream)
    {
        using JsonDocument document = JsonDocument.Parse(stream);
        var modules = new List<WatchedModule>();
        foreach (JsonElement module in document.RootElement.GetProperty("modules").EnumerateArray())
        {
            modules.Add(new WatchedModule(
                module.GetProperty("id").GetString()!,
                module.GetProperty("resources").GetString()!,
                module.TryGetProperty("plugins", out JsonElement plugins) ? plugins.EnumerateArray().Select(p => p.GetString()!).ToArray() : []));
        }

        return modules;
    }
}
