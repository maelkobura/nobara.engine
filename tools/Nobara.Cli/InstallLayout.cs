// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Cli;

/// <summary>Where the files of an installed game came from.</summary>
internal sealed class InstalledFiles
{
    /// <summary>Gets the assembly of each module, by identifier: <c>game/Example.Mobs.dll</c>.</summary>
    public Dictionary<string, string> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the files of <c>lib/</c>.</summary>
    public List<string> Libraries { get; } = [];
}

/// <summary>
/// Arranges the output of <c>dotnet publish</c> into the folders of an
/// installed game: <c>bin/</c> for the runtime, the core, and the entry
/// assembly; <c>system/</c> and <c>game/</c> for the modules of the target;
/// <c>lib/</c> for the libraries that only modules use.
/// </summary>
/// <remarks>
/// The dependency file of the entry assembly tells which library each file
/// comes from. Modules of the distribution that the target leaves out, and
/// build-time assemblies, are not installed. The dependency file in
/// <c>bin/</c> keeps only what <c>bin/</c> holds, so the .NET host does not
/// look for the moved files.
/// </remarks>
internal static class InstallLayout
{
    public static InstalledFiles? Arrange(
        string publish,
        string output,
        string entryName,
        IReadOnlyList<ModuleManifest> active,
        IReadOnlyCollection<string> knownModules,
        Reporter reporter)
    {
        string depsPath = Path.Combine(publish, entryName + ".deps.json");
        JsonObject deps = JsonNode.Parse(File.ReadAllText(depsPath))!.AsObject();
        // The target of the runtime identifier, such as .NETCoreApp,Version=v10.0/win-x64.
        JsonObject allTargets = deps["targets"]!.AsObject();
        JsonObject targets = (allTargets.FirstOrDefault(t => t.Key.Contains('/', StringComparison.Ordinal)).Value ?? allTargets.First().Value)!.AsObject();
        JsonObject libraries = deps["libraries"]!.AsObject();

        var modules = new HashSet<string>(knownModules, StringComparer.OrdinalIgnoreCase);
        var activeById = active.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        static string NameOf(string library) => library[..library.IndexOf('/', StringComparison.Ordinal)];

        // Which library each published file belongs to.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string library, JsonNode? node) in targets)
        {
            foreach (string kind in new[] { "runtime", "native" })
            {
                foreach ((string asset, _) in node?[kind]?.AsObject() ?? [])
                {
                    owners[Path.GetFileName(asset)] = library;
                }
            }

            foreach ((string asset, JsonNode? info) in node?["resources"]?.AsObject() ?? [])
            {
                owners[(string?)info?["locale"] + "/" + Path.GetFileName(asset)] = library;
            }
        }

        string entry = targets.Select(t => t.Key).First(k => string.Equals(NameOf(k), entryName, StringComparison.OrdinalIgnoreCase));
        HashSet<string> bin = Closure(targets, [entry], l => modules.Contains(NameOf(l)));
        HashSet<string> activeLibraries = targets.Select(t => t.Key).Where(k => activeById.ContainsKey(NameOf(k))).ToHashSet(StringComparer.Ordinal);
        HashSet<string> lib = Closure(targets, activeLibraries, l => bin.Contains(l) || modules.Contains(NameOf(l)));
        lib.ExceptWith(activeLibraries);

        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        var installed = new InstalledFiles();
        foreach (string file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories))
        {
            string relative = Names.Relative(publish, file);
            if (string.Equals(relative, entryName + ".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? owner = Owner(owners, relative);
            string name = Path.GetFileNameWithoutExtension(relative);
            string? destination;
            if (owner is not null && modules.Contains(NameOf(owner)))
            {
                destination = activeById.TryGetValue(NameOf(owner), out ModuleManifest? module)
                    ? (module.Level == ModuleLevel.Platform ? "system/" : "game/") + Path.GetFileName(relative)
                    : null;
                if (destination is not null && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && string.Equals(name, module!.Id, StringComparison.OrdinalIgnoreCase))
                {
                    installed.Modules[module.Id] = destination;
                }
            }
            else if (GameGenerator.AuxiliarySuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                destination = null;
            }
            else if (owner is not null && lib.Contains(owner))
            {
                destination = "lib/" + relative;
                installed.Libraries.Add(destination);
            }
            else if (owner is null || bin.Contains(owner))
            {
                destination = "bin/" + relative;
            }
            else
            {
                // A library that only modules left out of the target use.
                destination = null;
            }

            if (destination is not null)
            {
                string target = Path.Combine(output, destination);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        foreach (ModuleManifest module in active.Where(m => !installed.Modules.ContainsKey(m.Id)))
        {
            reporter.Error($"module '{module.Id}' was not published: no assembly '{module.Id}.dll' in '{publish}'");
        }

        if (reporter.Errors > 0)
        {
            return null;
        }

        // The host reads the dependency file and refuses missing files: keep bin/ only.
        foreach (string library in targets.Select(t => t.Key).Where(l => !bin.Contains(l)).ToList())
        {
            targets.Remove(library);
            libraries.Remove(library);
        }

        foreach ((_, JsonNode? node) in targets)
        {
            if (node?["dependencies"] is JsonObject dependencies)
            {
                foreach (string name in dependencies.Select(d => d.Key).Where(n => !bin.Any(l => string.Equals(NameOf(l), n, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    dependencies.Remove(name);
                }
            }
        }

        File.WriteAllText(Path.Combine(output, "bin", entryName + ".deps.json"), deps.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        installed.Libraries.Sort(StringComparer.Ordinal);
        return installed;
    }

    // The libraries reachable from the roots, without going through those that stop the walk.
    private static HashSet<string> Closure(JsonObject targets, IEnumerable<string> roots, Func<string, bool> stop)
    {
        var byName = targets.ToDictionary(t => t.Key[..t.Key.IndexOf('/', StringComparison.Ordinal)], t => t.Key, StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out string? library))
        {
            if (!result.Add(library))
            {
                continue;
            }

            foreach ((string name, _) in targets[library]?["dependencies"]?.AsObject() ?? [])
            {
                if (byName.TryGetValue(name, out string? dependency) && !stop(dependency) && !result.Contains(dependency))
                {
                    pending.Push(dependency);
                }
            }
        }

        return result;
    }

    // The library of a published file; a symbol or documentation file follows its assembly.
    private static string? Owner(Dictionary<string, string> owners, string relative)
    {
        if (owners.TryGetValue(relative.Contains('/', StringComparison.Ordinal) ? relative : Path.GetFileName(relative), out string? owner))
        {
            return owner;
        }

        string folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
        string assembly = (folder.Length > 0 ? folder + "/" : string.Empty) + Path.GetFileNameWithoutExtension(relative) + ".dll";
        return owners.GetValueOrDefault(assembly) ?? owners.GetValueOrDefault(Path.GetFileName(assembly));
    }
}
