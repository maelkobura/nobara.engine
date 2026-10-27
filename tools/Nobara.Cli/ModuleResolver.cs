// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Manifests;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Cli;

/// <summary>Where the code of a module comes from.</summary>
internal enum ModuleOrigin
{
    /// <summary>A project of the game, listed with <c>&lt;Local&gt;</c>.</summary>
    Local,

    /// <summary>A project that replaces a package, listed with <c>&lt;Overrides&gt;</c>.</summary>
    Override,

    /// <summary>A NuGet package.</summary>
    Package,
}

/// <summary>A module of the game, with its manifest and where it comes from.</summary>
internal sealed class ResolvedModule
{
    public required ModuleManifest Manifest { get; init; }

    public required ModuleOrigin Origin { get; init; }

    /// <summary>Gets the project file of a local or overriding module.</summary>
    public string? ProjectFile { get; init; }

    /// <summary>Gets the assembly of a package module, in the global packages folder.</summary>
    public string? AssemblyFile { get; init; }

    /// <summary>Gets whether no entry of <c>&lt;Modules&gt;</c> names the module: another module requires it.</summary>
    public bool Transitive { get; init; }

    public string Id => Manifest.Id;

    public string Version => Manifest.Version.Text;

    public string? Folder => ProjectFile is null ? null : Path.GetDirectoryName(ProjectFile);
}

/// <summary>The modules of a game, and where the sources of the engine are, when the game builds against them.</summary>
internal sealed class Resolution
{
    public required IReadOnlyList<ResolvedModule> Modules { get; init; }

    /// <summary>Gets the root of a clone of the engine that an override points into, or <see langword="null"/>.</summary>
    public string? EngineRoot { get; init; }

    public ResolvedModule? Find(string id) => Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Finds every module of a game: local modules, overrides, packages, and the
/// modules they require, transitively. Package versions come from the lock
/// file when it still fits, from NuGet otherwise.
/// </summary>
internal sealed class ModuleResolver
{
    private readonly GameFiles _files;
    private readonly Reporter _reporter;
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, ResolvedModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageRequest> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ResolvedModule> _pending = new();

    private ModuleResolver(GameFiles files, Reporter reporter, DiagnosticBag diagnostics)
    {
        _files = files;
        _reporter = reporter;
        _diagnostics = diagnostics;
    }

    /// <summary>Resolves the modules of a game.</summary>
    /// <returns>The modules, or <see langword="null"/> when a problem was found; the problems are in <paramref name="diagnostics"/>.</returns>
    public static Resolution? Resolve(GameFiles files, IReadOnlyList<LockedModule>? locked, Reporter reporter, DiagnosticBag diagnostics) =>
        new ModuleResolver(files, reporter, diagnostics).Run(locked ?? []);

    private Resolution? Run(IReadOnlyList<LockedModule> locked)
    {
        ProjectManifest project = _files.Project;
        foreach (ProjectOverride entry in project.Overrides)
        {
            _overrides[entry.Id] = entry;
        }

        foreach (ModuleEntry entry in project.Modules.Where(e => e.Kind == ModuleEntryKind.Local))
        {
            AddProject(Path.GetFullPath(Path.Combine(_files.Root, entry.Path!)), ModuleOrigin.Local, expectedId: null, transitive: false, entry.Location);
        }

        foreach (ModuleEntry entry in project.Modules.Where(e => e.Kind == ModuleEntryKind.Package))
        {
            if (_overrides.TryGetValue(entry.Id!, out ProjectOverride? replacement))
            {
                AddOverride(replacement, transitive: false);
            }
            else
            {
                Request(entry.Id!, entry.Range!, $"project.nobara", transitive: false);
            }
        }

        // Modules required by the modules found, until nothing new appears.
        while (true)
        {
            while (_pending.TryDequeue(out ResolvedModule? module))
            {
                foreach (ModuleDependency dependency in module.Manifest.Dependencies.Where(d => !d.Optional))
                {
                    if (_modules.TryGetValue(dependency.Id, out ResolvedModule? present))
                    {
                        // The version of a package is chosen here; the build checks the others.
                        if (present.Origin == ModuleOrigin.Package && !dependency.Range.Contains(present.Manifest.Version))
                        {
                            _diagnostics.Error("NRC202", $"'{module.Id}' requires '{dependency.Id}' {dependency.Range}, but version {present.Version} is already resolved", dependency.Location);
                        }

                        continue;
                    }

                    if (_overrides.TryGetValue(dependency.Id, out ProjectOverride? replacement))
                    {
                        AddOverride(replacement, transitive: true);
                    }
                    else
                    {
                        Request(dependency.Id, dependency.Range, module.Id, transitive: true);
                    }
                }
            }

            List<PackageRequest> open = _packages.Values.Where(p => !p.Resolved).ToList();
            if (open.Count == 0 || _diagnostics.HasErrors)
            {
                break;
            }

            if (!RestorePackages(locked))
            {
                break;
            }
        }

        if (_diagnostics.HasErrors)
        {
            return null;
        }

        return new Resolution { Modules = _modules.Values.ToList(), EngineRoot = FindEngineRoot() };
    }

    private void AddOverride(ProjectOverride entry, bool transitive) =>
        AddProject(Path.GetFullPath(Path.Combine(_files.Root, entry.Path)), ModuleOrigin.Override, entry.Id, transitive, entry.Location);

    private void AddProject(string folder, ModuleOrigin origin, string? expectedId, bool transitive, Location at)
    {
        if (!Directory.Exists(folder))
        {
            _diagnostics.Error("NRC200", $"the module folder '{folder}' does not exist", at);
            return;
        }

        ModuleManifest? manifest = GameFiles.ReadModule(folder, _diagnostics);
        if (manifest is null)
        {
            return;
        }

        if (expectedId is not null && !string.Equals(manifest.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Error("NRC200", $"the override of '{expectedId}' points to module '{manifest.Id}'", at);
            return;
        }

        string? projectFile = Directory.EnumerateFiles(folder, "*.csproj")
            .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (projectFile is null)
        {
            _diagnostics.Error(CliCodes.ProjectFile, $"module '{manifest.Id}' has no project file '{manifest.Id}.csproj' in '{folder}'", at);
            return;
        }

        if (_modules.TryGetValue(manifest.Id, out ResolvedModule? existing))
        {
            _diagnostics.Error("NRC200", $"module '{manifest.Id}' is provided twice: '{existing.Folder ?? existing.AssemblyFile}' and '{folder}'", at);
            return;
        }

        var module = new ResolvedModule { Manifest = manifest, Origin = origin, ProjectFile = Path.GetFullPath(projectFile), Transitive = transitive };
        _modules[manifest.Id] = module;
        _pending.Enqueue(module);
    }

    private void Request(string id, VersionRange range, string requiredBy, bool transitive)
    {
        if (!_packages.TryGetValue(id, out PackageRequest? request))
        {
            request = new PackageRequest(id, transitive);
            _packages[id] = request;
        }
        else if (!transitive)
        {
            request.Transitive = false;
        }

        request.Ranges.Add((range, requiredBy));
        if (request.Resolved && !range.Contains(SemanticVersion.TryParse(request.Version!, out SemanticVersion? version) ? version! : SemanticVersion.Of(0, 0, 0)))
        {
            _diagnostics.Error("NRC202", $"'{requiredBy}' requires '{id}' {range}, but version {request.Version} is already resolved", null);
        }
    }

    // Restores the requested packages that are not resolved yet, then reads their manifests.
    private bool RestorePackages(IReadOnlyList<LockedModule> locked)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageRequest request in _packages.Values)
        {
            if (request.Resolved)
            {
                versions[request.Id] = $"[{request.Version}]";
                continue;
            }

            string? nuget = Intersect(request);
            if (nuget is null)
            {
                return false;
            }

            LockedModule? pin = locked.FirstOrDefault(l => l.Origin == ModuleOrigin.Package && string.Equals(l.Id, request.Id, StringComparison.OrdinalIgnoreCase));
            bool fits = pin is not null && SemanticVersion.TryParse(pin.Version, out SemanticVersion? pinned) && request.Ranges.All(r => r.Range.Contains(pinned!));
            versions[request.Id] = fits ? $"[{pin!.Version}]" : nuget;
        }

        _reporter.Info($"Restoring {versions.Count} module packages.");
        IReadOnlyDictionary<string, (string Version, string? Assembly)>? restored = PackageRestorer.Restore(_files, versions, _reporter, _diagnostics);
        if (restored is null)
        {
            foreach (PackageRequest request in _packages.Values.Where(p => !p.Resolved))
            {
                string requiredBy = string.Join(", ", request.Ranges.Select(r => $"'{r.RequiredBy}' requires {r.Range}"));
                _diagnostics.Error(CliCodes.Restore, $"package '{request.Id}' cannot be restored ({requiredBy}); see the NuGet errors above", null);
            }

            return false;
        }

        foreach (PackageRequest request in _packages.Values.Where(p => !p.Resolved).ToList())
        {
            if (!restored.TryGetValue(request.Id, out var package))
            {
                _diagnostics.Error(CliCodes.Restore, $"package '{request.Id}' was not restored", null);
                continue;
            }

            if (package.Assembly is null || CompiledModule.FromAssembly(package.Assembly) is not CompiledModule compiled)
            {
                _diagnostics.Error("NRC200", $"package '{request.Id}' {package.Version} is not a Nobara module: it has no assembly '{request.Id}.dll' with a module manifest", null);
                continue;
            }

            request.Version = package.Version;
            ModuleManifest manifest = FromCompiled(compiled.Manifest, package.Assembly);
            var module = new ResolvedModule { Manifest = manifest, Origin = ModuleOrigin.Package, AssemblyFile = package.Assembly, Transitive = request.Transitive };
            _modules[manifest.Id] = module;
            _pending.Enqueue(module);
        }

        return !_diagnostics.HasErrors;
    }

    // The NuGet range that satisfies every range of a request.
    private string? Intersect(PackageRequest request)
    {
        SemanticVersion? lower = null;
        bool lowerInclusive = true;
        SemanticVersion? upper = null;
        bool upperInclusive = true;
        foreach ((VersionRange range, _) in request.Ranges)
        {
            if (range.Lower is not null && (lower is null || range.Lower.CompareTo(lower) > 0 || (range.Lower.CompareTo(lower) == 0 && !range.LowerInclusive)))
            {
                lower = range.Lower;
                lowerInclusive = range.LowerInclusive;
            }

            if (range.Upper is not null && (upper is null || range.Upper.CompareTo(upper) < 0 || (range.Upper.CompareTo(upper) == 0 && !range.UpperInclusive)))
            {
                upper = range.Upper;
                upperInclusive = range.UpperInclusive;
            }
        }

        int order = lower is null || upper is null ? -1 : lower.CompareTo(upper);
        if (order > 0 || (order == 0 && !(lowerInclusive && upperInclusive)))
        {
            string ranges = string.Join(", ", request.Ranges.Select(r => $"'{r.RequiredBy}' requires {r.Range}"));
            _diagnostics.Error("NRC202", $"no version of '{request.Id}' satisfies every range: {ranges}", null);
            return null;
        }

        return (lower is null ? "(" : lowerInclusive ? "[" : "(") + (lower?.Text ?? string.Empty) + ","
            + (upper?.Text ?? string.Empty) + (upper is null ? ")" : upperInclusive ? "]" : ")");
    }

    // The clone of the engine that an override points into: the folder above
    // it that holds the project of the core.
    private string? FindEngineRoot()
    {
        foreach (ResolvedModule module in _modules.Values.Where(m => m.Origin == ModuleOrigin.Override))
        {
            for (DirectoryInfo? folder = new(module.Folder!); folder is not null; folder = folder.Parent)
            {
                if (File.Exists(Path.Combine(folder.FullName, "src", "Core", "Nobara.Core", "Nobara.Core.csproj")))
                {
                    return folder.FullName;
                }
            }
        }

        return null;
    }

    /// <summary>Turns the compiled manifest of a package into the form that the distribution checks read.</summary>
    internal static ModuleManifest FromCompiled(F.ModuleManifest compiled, string file)
    {
        var at = new Location(file, 0, 0);
        static ModuleSide Side(F.Side side) => side switch
        {
            F.Side.Client => ModuleSide.Client,
            F.Side.Server => ModuleSide.Server,
            F.Side.Both => ModuleSide.Both,
            _ => ModuleSide.None,
        };

        return new ModuleManifest
        {
            File = file,
            Id = compiled.Id,
            ShortName = compiled.ShortName.Length > 0 ? compiled.ShortName : null,
            Version = SemanticVersion.TryParse(compiled.Version, out SemanticVersion? version) ? version! : SemanticVersion.Of(0, 0, 0),
            Level = compiled.Level == F.Level.Platform ? ModuleLevel.Platform : ModuleLevel.Business,
            Side = Side(compiled.Side),
            Dependencies = compiled.Dependencies
                .Select(d => VersionRange.TryParse(d.Range, out VersionRange? range, out _) ? new ModuleDependency { Id = d.Id, Range = range!, Optional = d.Optional, Location = at } : null)
                .OfType<ModuleDependency>()
                .ToArray(),
            Libraries = compiled.Libraries.Select(l => new ModuleLibrary { Id = l.Id, Version = l.Version, Location = at }).ToArray(),
            Conflicts = compiled.Conflicts.Select(c => new Declared { Name = c, Location = at }).ToArray(),
            Reserves = compiled.Reserves.Select(r => new Declared { Name = r, Location = at }).ToArray(),
            States = compiled.States.Select(s => new StateDeclaration { Id = s.Id, Side = Side(s.Side), Location = at }).ToArray(),
            Settings = compiled.Settings.Select(s => new SettingDeclaration
            {
                Key = s.Key,
                Scope = s.Scope == F.SettingDeclaration.Types.Scope.User ? SettingScope.User : SettingScope.Distribution,
                Type = s.Type switch
                {
                    F.SettingDeclaration.Types.Type.Bool => SettingType.Bool,
                    F.SettingDeclaration.Types.Type.Int => SettingType.Int,
                    F.SettingDeclaration.Types.Type.Float => SettingType.Float,
                    F.SettingDeclaration.Types.Type.String => SettingType.String,
                    _ => SettingType.Enum,
                },
                Default = s.DefaultValue,
                Min = s.HasMin ? s.Min : null,
                Max = s.HasMax ? s.Max : null,
                Values = s.Values.ToArray(),
                Side = Side(s.Side),
                Location = at,
            }).ToArray(),
            BackendRequired = Side(compiled.BackendRequired),
            BackendOf = compiled.BackendOf.Length > 0 ? compiled.BackendOf : null,
            Location = at,
        };
    }

    private sealed class PackageRequest(string id, bool transitive)
    {
        public string Id { get; } = id;

        public bool Transitive { get; set; } = transitive;

        public List<(VersionRange Range, string RequiredBy)> Ranges { get; } = [];

        public string? Version { get; set; }

        public bool Resolved => Version is not null;
    }
}
