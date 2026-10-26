// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Runtime.Loader;

namespace Nobara.Core.Loading;

/// <summary>
/// The load context of the module assemblies of one source. Modules resolve
/// each other by name among the modules already loaded, then in the folder
/// of shared libraries; everything else, the core included, comes from the
/// default context.
/// </summary>
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly string _libraryFolder;
    private readonly Dictionary<string, Assembly> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ModuleLoadContext(string libraryFolder)
        : base("Nobara modules", isCollectible: false)
    {
        _libraryFolder = libraryFolder;
    }

    public Assembly LoadModule(string path)
    {
        lock (_gate)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!_modules.TryGetValue(name, out Assembly? assembly))
            {
                assembly = LoadFromAssemblyPath(path);
                _modules.Add(name, assembly);
            }

            return assembly;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (name is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_modules.TryGetValue(name, out Assembly? module))
            {
                return module;
            }
        }

        // Share what the default context already holds, such as the core.
        foreach (Assembly loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        string library = Path.Combine(_libraryFolder, name + ".dll");
        return File.Exists(library) ? LoadFromAssemblyPath(library) : null;
    }
}
