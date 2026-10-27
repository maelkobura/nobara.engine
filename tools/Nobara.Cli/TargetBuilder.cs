// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Distribution;
using Nobara.ResourceCompiler.Manifests;
using F = Nobara.Formats;

namespace Nobara.Cli;

/// <summary>
/// <c>nobara build</c>: checks a target, publishes its modules and the entry
/// assembly, arranges the installed game in <c>out/&lt;target&gt;/</c>,
/// records the hashes in the distribution manifest, and packs it.
/// </summary>
internal sealed class TargetBuilder
{
    /// <summary>The platform that this version builds.</summary>
    public const string SupportedPlatform = "win-x64";

    private readonly GameFiles _files;
    private readonly Resolution _resolution;
    private readonly Reporter _reporter;

    public TargetBuilder(GameFiles files, Resolution resolution, Reporter reporter)
    {
        _files = files;
        _resolution = resolution;
        _reporter = reporter;
    }

    /// <summary>Gets or sets whether the build is a development build: JIT, debug configuration, hot reload, no archive.</summary>
    public bool Development { get; init; }

    public string OutputDirectory => Path.Combine(_files.Root, "out");

    /// <summary>Builds one target, or every target of <c>build.nobara</c>.</summary>
    /// <returns><see langword="true"/> when every target was built.</returns>
    public bool Build(string? targetName)
    {
        IEnumerable<BuildTarget> targets = _files.Build!.Targets;
        if (targetName is not null)
        {
            BuildTarget? target = targets.FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                _reporter.Error($"{_files.Build.File}: target '{targetName}' is not declared; targets: {string.Join(", ", _files.Build.Targets.Select(t => t.Name))}");
                return false;
            }

            targets = [target];
        }

        foreach (BuildTarget target in targets)
        {
            if (!BuildTarget(target))
            {
                _reporter.Error($"target '{target.Name}' was not built.");
                return false;
            }
        }

        return true;
    }

    /// <summary>The folder of the installed game of a target.</summary>
    public string InstallDirectory(BuildTarget target) => Path.Combine(OutputDirectory, target.Name);

    /// <summary>The archive of a target.</summary>
    public string Archive(BuildTarget target) => Path.Combine(OutputDirectory, target.Name + ".zip");

    /// <summary>The name of the root folder and of the executable of a target: <c>Name</c> or <c>NameServer</c>.</summary>
    public string ExecutableName(BuildTarget target) => _files.FileName + (target.Side == ModuleSide.Server ? "Server" : string.Empty);

    private bool BuildTarget(BuildTarget target)
    {
        _reporter.Info($"Building target '{target.Name}' ({target.Platform}, {target.Side.ToString().ToLowerInvariant()}{(Development ? ", development" : string.Empty)}).");

        // Steps 3 and 4: the modules of the target and the engine file.
        if (!string.Equals(target.Platform, SupportedPlatform, StringComparison.OrdinalIgnoreCase))
        {
            _reporter.Report([new Diagnostic(CliCodes.Platform, DiagnosticSeverity.Error, $"target '{target.Name}' is for '{target.Platform}'; this version builds '{SupportedPlatform}' only", target.Location.File, target.Location.Line, target.Location.Column)]);
            return false;
        }

        DistributionCheckResult check = DistributionChecker.Check(new DistributionInput
        {
            Project = _files.Project,
            Engine = _files.Engine!,
            Build = _files.Build,
            Target = target.Name,
            Modules = _resolution.Modules.Select(m => m.Manifest).ToList(),
        });
        if (!_reporter.Report(check.Diagnostics) || !CheckLibraries(check.ActiveModules))
        {
            return false;
        }

        IReadOnlyList<ModuleManifest> active = check.ActiveModules;
        List<ResolvedModule> modules = active.Select(m => _resolution.Find(m.Id)!).ToList();
        string staging = Path.Combine(_files.Generated, "targets", target.Name);
        Directory.CreateDirectory(staging);
        string? hotReloadMap = Development ? WriteHotReloadMap(staging, modules) : null;

        // Steps 5 and 6: compile and publish, self-contained, ReadyToRun outside development.
        string publish = Path.Combine(staging, "publish");
        if (!Publish(target, modules, publish, manifest: null, hotReloadMap))
        {
            return false;
        }

        // Step 7: the folders of the installed game.
        string output = InstallDirectory(target);
        InstalledFiles? installed = InstallLayout.Arrange(publish, output, _files.EntryName, active, _resolution.Modules.Select(m => m.Id).ToList(), _reporter);
        if (installed is null)
        {
            return false;
        }

        // Step 8: hashes, distribution manifest, entry assembly that embeds it.
        var hashes = installed.Modules.ToDictionary(m => m.Key, m => SHA256.HashData(File.ReadAllBytes(Path.Combine(output, m.Value))), StringComparer.OrdinalIgnoreCase);
        var libraries = installed.Libraries.ToDictionary(l => l, l => SHA256.HashData(File.ReadAllBytes(Path.Combine(output, l))), StringComparer.Ordinal);
        F.DistributionManifest manifest = ManifestCompiler.Compile(_files.Project, _files.Engine!, active, check.RequiredModules, hashes, _files.Root, libraries);
        string manifestFile = Path.Combine(staging, "distribution.pb");
        File.WriteAllBytes(manifestFile, manifest.ToByteArray());
        string entryPublish = Path.Combine(staging, "entry");
        if (!Publish(target, modules, entryPublish, manifestFile, hotReloadMap))
        {
            return false;
        }

        foreach (string extension in new[] { ".dll", ".pdb" })
        {
            string file = Path.Combine(entryPublish, _files.EntryName + extension);
            if (File.Exists(file))
            {
                File.Copy(file, Path.Combine(output, "bin", _files.EntryName + extension), overwrite: true);
            }
        }

        // Step 9: the executable at the root.
        string executable = ExecutableName(target) + ".exe";
        string? problem = AppHost.Install(Path.Combine(entryPublish, _files.EntryName + ".exe"), Path.Combine(output, executable), _files.EntryName + ".dll");
        if (problem is not null)
        {
            _reporter.Error(problem);
            return false;
        }

        _reporter.Info($"Installed {active.Count} modules and {installed.Libraries.Count} library files in {output}.");

        // Step 10: the archive.
        if (Development)
        {
            _reporter.Info($"Development build: run {Path.Combine(output, executable)} {"--hot-reload"} to reload the .nof files of local modules while it runs.");
            return true;
        }

        return Package(target);
    }

    /// <summary>Packs the installed game of a target into its archive.</summary>
    public bool Package(BuildTarget target)
    {
        string output = InstallDirectory(target);
        if (!Directory.Exists(Path.Combine(output, "bin")))
        {
            _reporter.Error($"target '{target.Name}' is not built: run nobara build {target.Name} first.");
            return false;
        }

        string archive = Archive(target);
        Packager.Zip(output, archive, ExecutableName(target));
        _reporter.Info($"Packed {archive}.");
        return true;
    }

    private bool Publish(BuildTarget target, List<ResolvedModule> modules, string output, string? manifest, string? hotReloadMap)
    {
        var settings = new TargetSettings
        {
            Platform = target.Platform,
            Server = target.Side == ModuleSide.Server,
            Development = Development,
            Projects = modules.Where(m => m.ProjectFile is not null).Select(m => m.ProjectFile!).ToList(),
            Packages = modules.Where(m => m.Origin == ModuleOrigin.Package).Select(m => (m.Id, m.Version)).ToList(),
            DistributionManifest = manifest,
            HotReloadMap = hotReloadMap,
        };
        File.WriteAllText(Path.Combine(_files.EntryDirectory, EntryAssembly.TargetFile), EntryAssembly.Target(settings));
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        string configuration = Development ? "Debug" : "Release";
        int exit = Dotnet.Run(_reporter, _files.Root, "publish", _files.EntryProject, "-c", configuration, "-o", output, "--nologo", "-v:q", "-nodeReuse:false");
        if (exit != 0)
        {
            _reporter.Error($"dotnet publish failed for target '{target.Name}'; see the errors above.");
            return false;
        }

        return true;
    }

    // One version of each third-party library: the versions that the modules declare must meet.
    private bool CheckLibraries(IReadOnlyList<ModuleManifest> active)
    {
        bool success = true;
        foreach (IGrouping<string, (ModuleManifest Module, ModuleLibrary Library)> group in active
            .SelectMany(m => m.Libraries.Select(l => (Module: m, Library: l)))
            .GroupBy(x => x.Library.Id, StringComparer.OrdinalIgnoreCase))
        {
            var declared = group.ToList();
            for (int i = 0; i < declared.Count; i++)
            {
                for (int j = i + 1; j < declared.Count; j++)
                {
                    if (!NuGetRange.Overlap(declared[i].Library.Version, declared[j].Library.Version))
                    {
                        ModuleLibrary second = declared[j].Library;
                        _reporter.Report([new Diagnostic(
                            CliCodes.LibraryVersion,
                            DiagnosticSeverity.Error,
                            $"modules '{declared[i].Module.Id}' and '{declared[j].Module.Id}' need incompatible versions of library '{group.Key}': {declared[i].Library.Version} and {second.Version}; lib/ holds one version of each library",
                            second.Location.File,
                            second.Location.Line,
                            second.Location.Column)]);
                        success = false;
                    }
                }
            }
        }

        return success;
    }

    // The modules whose sources hot reload watches, for the entry assembly of a development build.
    private string WriteHotReloadMap(string staging, List<ResolvedModule> modules)
    {
        string path = Path.Combine(staging, "hot-reload.json");
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartArray("modules");
            foreach (ResolvedModule module in modules.Where(m => m.Folder is not null))
            {
                json.WriteStartObject();
                json.WriteString("id", module.Id);
                json.WriteString("resources", Path.Combine(module.Folder!, "Resources"));
                json.WriteStartArray("plugins");
                foreach (ModuleDependency dependency in module.Manifest.Dependencies)
                {
                    if (_resolution.Find(dependency.Id)?.ProjectFile is string project)
                    {
                        string name = Path.GetFileNameWithoutExtension(project) + ".Build";
                        string folder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(project)!)!, name);
                        if (File.Exists(Path.Combine(folder, name + ".csproj")))
                        {
                            json.WriteStringValue(Path.Combine(folder, "bin", "Debug", "net10.0", name + ".dll"));
                        }
                    }
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        Files.WriteIfChanged(path, stream.ToArray());
        return path;
    }
}

/// <summary>Versions in NuGet notation, as <c>&lt;Libraries&gt;</c> declares them.</summary>
internal static class NuGetRange
{
    /// <summary>Tells whether one version satisfies two ranges: <c>1.2</c> (at least), <c>[1.2]</c> (exactly), <c>[1.0,2.0)</c>.</summary>
    public static bool Overlap(string a, string b)
    {
        if (!TryParse(a, out var first) || !TryParse(b, out var second))
        {
            return true;
        }

        (SemanticVersion? lower, bool lowerInclusive) = Max(first.Lower, first.LowerInclusive, second.Lower, second.LowerInclusive);
        (SemanticVersion? upper, bool upperInclusive) = Min(first.Upper, first.UpperInclusive, second.Upper, second.UpperInclusive);
        if (lower is null || upper is null)
        {
            return true;
        }

        int order = lower.CompareTo(upper);
        return order < 0 || (order == 0 && lowerInclusive && upperInclusive);
    }

    private static bool TryParse(string text, out (SemanticVersion? Lower, bool LowerInclusive, SemanticVersion? Upper, bool UpperInclusive) range)
    {
        text = text.Trim();
        range = default;
        if (text.Length == 0)
        {
            return false;
        }

        if (text[0] is not ('[' or '('))
        {
            bool parsed = SemanticVersion.TryParsePartial(text, out SemanticVersion? minimum);
            range = (minimum, true, null, false);
            return parsed;
        }

        if (text[^1] is not (']' or ')'))
        {
            return false;
        }

        string[] parts = text[1..^1].Split(',');
        SemanticVersion? lower = null;
        SemanticVersion? upper = null;
        if (parts.Length == 1)
        {
            if (!SemanticVersion.TryParsePartial(parts[0].Trim(), out lower))
            {
                return false;
            }

            range = (lower, true, lower, true);
            return true;
        }

        if (parts.Length != 2
            || (parts[0].Trim().Length > 0 && !SemanticVersion.TryParsePartial(parts[0].Trim(), out lower))
            || (parts[1].Trim().Length > 0 && !SemanticVersion.TryParsePartial(parts[1].Trim(), out upper)))
        {
            return false;
        }

        range = (lower, text[0] == '[', upper, text[^1] == ']');
        return true;
    }

    private static (SemanticVersion?, bool) Max(SemanticVersion? a, bool aInclusive, SemanticVersion? b, bool bInclusive) =>
        a is null ? (b, bInclusive) : b is null ? (a, aInclusive)
        : a.CompareTo(b) > 0 ? (a, aInclusive) : a.CompareTo(b) < 0 ? (b, bInclusive) : (a, aInclusive && bInclusive);

    private static (SemanticVersion?, bool) Min(SemanticVersion? a, bool aInclusive, SemanticVersion? b, bool bInclusive) =>
        a is null ? (b, bInclusive) : b is null ? (a, aInclusive)
        : a.CompareTo(b) < 0 ? (a, aInclusive) : a.CompareTo(b) > 0 ? (b, bInclusive) : (a, aInclusive && bInclusive);
}
