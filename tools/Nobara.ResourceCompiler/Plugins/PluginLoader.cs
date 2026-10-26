// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Runtime.Loader;
using Google.Protobuf;

namespace Nobara.ResourceCompiler.Plugins;

/// <summary>
/// Loads plugin assemblies in a context of their own, from memory, so that
/// the files stay free to be rebuilt.
/// </summary>
public sealed class PluginLoader : IDisposable
{
    private readonly PluginContext _context;

    private PluginLoader(PluginContext context, IReadOnlyList<ResourcePlugin> plugins)
    {
        _context = context;
        Plugins = plugins;
    }

    /// <summary>Gets the plugins created from the loaded assemblies.</summary>
    public IReadOnlyList<ResourcePlugin> Plugins { get; }

    /// <summary>Loads plugin assemblies and creates the plugins they declare with <see cref="ResourcePluginAttribute"/>.</summary>
    /// <param name="assemblies">The full paths of the plugin assemblies.</param>
    /// <param name="probeDirectories">Folders searched for the dependencies of the plugins, after the folder of each plugin.</param>
    /// <param name="diagnostics">Receives a problem for each assembly or type that cannot be used.</param>
    /// <returns>The loader; dispose it once the compilation ends.</returns>
    public static PluginLoader Load(IEnumerable<string> assemblies, IEnumerable<string> probeDirectories, DiagnosticBag diagnostics)
    {
        List<string> paths = assemblies.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var context = new PluginContext(paths.Select(p => Path.GetDirectoryName(p)!).Concat(probeDirectories).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var plugins = new List<ResourcePlugin>();
        foreach (string path in paths)
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadFromPath(path);
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException)
            {
                diagnostics.Error("NRC601", $"the plugin assembly '{path}' cannot be loaded: {exception.Message}", new Location(path, 0, 0));
                continue;
            }

            foreach (ResourcePluginAttribute attribute in GetAttributes(assembly, path, diagnostics))
            {
                Type type = attribute.PluginType;
                if (!typeof(ResourcePlugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    diagnostics.Error("NRC601", $"{type.FullName} is declared as a plugin but is not a concrete ResourcePlugin with a parameterless constructor", new Location(path, 0, 0));
                    continue;
                }

                try
                {
                    plugins.Add((ResourcePlugin)Activator.CreateInstance(type)!);
                }
                catch (TargetInvocationException exception)
                {
                    diagnostics.Error("NRC601", $"the plugin {type.FullName} cannot be created: {exception.InnerException?.Message}", new Location(path, 0, 0));
                }
            }
        }

        return new PluginLoader(context, plugins);
    }

    /// <summary>Unloads the plugin assemblies.</summary>
    public void Dispose() => _context.Unload();

    private static IEnumerable<ResourcePluginAttribute> GetAttributes(Assembly assembly, string path, DiagnosticBag diagnostics)
    {
        try
        {
            return assembly.GetCustomAttributes<ResourcePluginAttribute>().ToList();
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            diagnostics.Error("NRC601", $"the plugins of '{path}' cannot be read: {exception.Message}", new Location(path, 0, 0));
            return [];
        }
    }

    private sealed class PluginContext : AssemblyLoadContext
    {
        // The plugins share these assemblies with the compiler, so that the
        // plugin types and the protobuf types are the compiler's own.
        private static readonly Assembly[] Shared = [typeof(ResourcePlugin).Assembly, typeof(IMessage).Assembly];

        private readonly IReadOnlyList<string> _directories;

        public PluginContext(IReadOnlyList<string> directories)
            : base("Nobara resource plugins", isCollectible: true)
        {
            _directories = directories;
        }

        public Assembly LoadFromPath(string path)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            return LoadFromStream(stream);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            foreach (Assembly shared in Shared)
            {
                if (string.Equals(shared.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return shared;
                }
            }

            foreach (string directory in _directories)
            {
                string candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromPath(candidate);
                }
            }

            return null;
        }
    }
}
