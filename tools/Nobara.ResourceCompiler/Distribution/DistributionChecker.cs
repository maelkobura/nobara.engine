// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler.Manifests;

namespace Nobara.ResourceCompiler.Distribution;

/// <summary>What <see cref="DistributionChecker.Check"/> checks: the sources of a distribution and the manifests of its modules.</summary>
public sealed class DistributionInput
{
    /// <summary>Gets the content of <c>project.nobara</c>.</summary>
    public required ProjectManifest Project { get; init; }

    /// <summary>Gets the content of <c>engine.nobara</c>.</summary>
    public required EngineManifest Engine { get; init; }

    /// <summary>
    /// Gets the manifests of every module of the distribution: the modules
    /// listed in <c>&lt;Modules&gt;</c> and the dependencies resolved from
    /// them. The manifest of a local module is matched to its entry by the
    /// folder of its file.
    /// </summary>
    public required IReadOnlyList<ModuleManifest> Modules { get; init; }

    /// <summary>Gets the content of <c>build.nobara</c>, or <see langword="null"/>.</summary>
    public BuildManifest? Build { get; init; }

    /// <summary>
    /// Gets the name of the target to check, or <see langword="null"/> to
    /// check the distribution without a target: then every module is active
    /// and each side is checked.
    /// </summary>
    public string? Target { get; init; }
}

/// <summary>The outcome of <see cref="DistributionChecker.Check"/>.</summary>
public sealed class DistributionCheckResult
{
    internal DistributionCheckResult(IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<ModuleManifest> activeModules, IReadOnlySet<string> requiredModules)
    {
        Diagnostics = diagnostics;
        ActiveModules = activeModules;
        RequiredModules = requiredModules;
    }

    /// <summary>Gets the problems found.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Gets whether no error was found.</summary>
    public bool Success => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);

    /// <summary>
    /// Gets the active modules in load order: platform modules, then business
    /// modules, each group sorted by dependencies. Empty when a cycle was found.
    /// </summary>
    public IReadOnlyList<ModuleManifest> ActiveModules { get; }

    /// <summary>Gets the identifiers of the required modules, compared without case.</summary>
    public IReadOnlySet<string> RequiredModules { get; }
}

/// <summary>
/// Checks that the modules of a distribution fit together: dependencies,
/// cycles, conflicts, reservations, names, states, backends, configuration,
/// and error policies.
/// </summary>
public static class DistributionChecker
{
    private const string MissingManifest = "NRC200";
    private const string MissingDependency = "NRC201";
    private const string VersionOutOfRange = "NRC202";
    private const string PlatformOnBusiness = "NRC203";
    private const string Cycle = "NRC204";
    private const string Conflict = "NRC205";
    private const string Reservation = "NRC206";
    private const string ShortNameTaken = "NRC207";
    private const string KeyTaken = "NRC208";
    private const string StateTaken = "NRC209";
    private const string InitialState = "NRC210";
    private const string MissingBackend = "NRC211";
    private const string UnknownKey = "NRC212";
    private const string InvalidSetting = "NRC213";
    private const string ImposedPolicy = "NRC214";
    private const string CascadePolicy = "NRC215";
    private const string UnknownModule = "NRC216";
    private const string TargetProblem = "NRC217";

    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;

    /// <summary>Checks a distribution.</summary>
    /// <param name="input">The sources and the module manifests.</param>
    /// <returns>The diagnostics and the active modules.</returns>
    public static DistributionCheckResult Check(DistributionInput input)
    {
        var bag = new DiagnosticBag();
        var modules = new Dictionary<string, ModuleManifest>(Ids);
        foreach (ModuleManifest module in input.Modules)
        {
            if (!modules.TryAdd(module.Id, module))
            {
                bag.Error(MissingManifest, $"module '{module.Id}' is provided twice: '{modules[module.Id].File}' and '{module.File}'", module.Location);
            }
        }

        HashSet<string> required = MatchEntries(input.Project, modules, bag);

        BuildTarget? target = null;
        if (input.Target is not null)
        {
            target = input.Build?.Targets.FirstOrDefault(t => string.Equals(t.Name, input.Target, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                bag.Error(TargetProblem, $"target '{input.Target}' is not declared in build.nobara", input.Build?.Location);
                return new DistributionCheckResult(Distinct(bag), [], required);
            }
        }

        var disabled = new HashSet<string>(Ids);
        foreach (Declared disable in target?.Disabled ?? [])
        {
            if (!modules.ContainsKey(disable.Name))
            {
                bag.Error(TargetProblem, $"target '{target!.Name}' disables '{disable.Name}', which is not a module of the distribution", disable.Location);
            }
            else if (required.Contains(disable.Name))
            {
                bag.Error(TargetProblem, $"target '{target!.Name}' disables the required module '{disable.Name}'", disable.Location);
            }

            disabled.Add(disable.Name);
        }

        CheckDeclaredDependencies(modules, bag);
        CheckUnique(modules.Values, m => m.ShortName is null ? [] : [new Declared { Name = m.ShortName, Location = m.Location }], ShortNameTaken, "short name", StringComparer.Ordinal, bag);
        CheckUnique(modules.Values, m => m.Settings.Select(s => new Declared { Name = s.Key, Location = s.Location }), KeyTaken, "configuration key", StringComparer.Ordinal, bag);
        CheckUnique(modules.Values, m => m.States.Select(s => new Declared { Name = s.Id, Location = s.Location }), StateTaken, "state", StringComparer.Ordinal, bag);
        CheckSettings(input.Engine, modules, bag);
        CheckPolicies(input.Engine, modules, required, bag);
        CheckModuleConfigs(input.Engine, modules, bag);

        // A client hosts an integrated server, so a client target ships the
        // modules of both sides and is checked on both; a server target ships
        // the modules of the server side only.
        ModuleSide hosted = target is null || target.Side == ModuleSide.Client ? ModuleSide.Both : ModuleSide.Server;
        foreach (ModuleSide side in new[] { ModuleSide.Client, ModuleSide.Server }.Where(s => (hosted & s) != 0))
        {
            List<ModuleManifest> active = modules.Values.Where(m => (m.Side & side) != 0 && !disabled.Contains(m.Id)).ToList();
            string where = target is null ? $"on the {Name(side)} side" : $"on the {Name(side)} side of target '{target.Name}'";
            CheckActive(active, modules, input.Engine, side, where, bag);
            Sort(active, where, bag);
        }

        List<ModuleManifest> shipped = modules.Values.Where(m => (m.Side & hosted) != 0 && !disabled.Contains(m.Id)).ToList();
        IReadOnlyList<ModuleManifest> order = Sort(shipped, target is null ? "in the distribution" : $"in target '{target!.Name}'", new DiagnosticBag()) ?? [];
        return new DistributionCheckResult(Distinct(bag), order, required);
    }

    private static HashSet<string> MatchEntries(ProjectManifest project, Dictionary<string, ModuleManifest> modules, DiagnosticBag bag)
    {
        var required = new HashSet<string>(Ids);
        string projectFolder = Path.GetDirectoryName(Path.GetFullPath(project.File)) ?? string.Empty;
        foreach (ModuleEntry entry in project.Modules)
        {
            ModuleManifest? module;
            if (entry.Kind == ModuleEntryKind.Local)
            {
                string folder = Normalize(Path.Combine(projectFolder, entry.Path!));
                module = modules.Values.FirstOrDefault(m => Normalize(Path.GetDirectoryName(Path.GetFullPath(m.File)) ?? string.Empty) == folder);
                if (module is null)
                {
                    bag.Error(MissingManifest, $"no module manifest was provided for the local module '{entry.Path}'", entry.Location);
                    continue;
                }
            }
            else
            {
                if (!modules.TryGetValue(entry.Id!, out module))
                {
                    bag.Error(MissingManifest, $"no module manifest was provided for the package '{entry.Id}'", entry.Location);
                    continue;
                }

                if (!entry.Range!.Contains(module.Version))
                {
                    bag.Error(VersionOutOfRange, $"package '{entry.Id}' is version {module.Version}, outside the range '{entry.Range}'", entry.Location);
                }
            }

            if (entry.Required)
            {
                required.Add(module.Id);
            }
        }

        return required;
    }

    private static void CheckDeclaredDependencies(Dictionary<string, ModuleManifest> modules, DiagnosticBag bag)
    {
        foreach (ModuleManifest module in modules.Values)
        {
            foreach (ModuleDependency dependency in module.Dependencies)
            {
                if (!modules.TryGetValue(dependency.Id, out ModuleManifest? target))
                {
                    if (!dependency.Optional)
                    {
                        bag.Error(MissingDependency, $"module '{module.Id}' requires '{dependency.Id}', which is not a module of the distribution", dependency.Location);
                    }

                    continue;
                }

                if (!dependency.Range.Contains(target.Version))
                {
                    string kind = dependency.Optional ? "optional dependency" : "dependency";
                    bag.Error(VersionOutOfRange, $"module '{module.Id}' accepts '{dependency.Id}' in '{dependency.Range}' ({kind}), but the distribution has version {target.Version}", dependency.Location);
                }

                if (module.Level == ModuleLevel.Platform && target.Level == ModuleLevel.Business)
                {
                    bag.Error(PlatformOnBusiness, $"platform module '{module.Id}' depends on business module '{dependency.Id}'; a platform module never depends on a business module, even optionally", dependency.Location);
                }
            }
        }
    }

    private static void CheckActive(List<ModuleManifest> active, Dictionary<string, ModuleManifest> modules, EngineManifest engine, ModuleSide side, string where, DiagnosticBag bag)
    {
        var activeIds = new HashSet<string>(active.Select(m => m.Id), Ids);
        foreach (ModuleManifest module in active)
        {
            foreach (ModuleDependency dependency in module.Dependencies)
            {
                if (!dependency.Optional && modules.ContainsKey(dependency.Id) && !activeIds.Contains(dependency.Id))
                {
                    bag.Error(MissingDependency, $"module '{module.Id}' requires '{dependency.Id}', which is not active {where}", dependency.Location);
                }
            }

            foreach (Declared conflict in module.Conflicts)
            {
                if (activeIds.Contains(conflict.Name))
                {
                    bag.Error(Conflict, $"module '{module.Id}' conflicts with '{conflict.Name}', and both are active {where}", conflict.Location);
                }
            }

            if ((module.BackendRequired & side) != 0)
            {
                bool hasBackend = active.Any(b => string.Equals(b.BackendOf, module.Id, StringComparison.OrdinalIgnoreCase) && (b.Side & side) != 0);
                if (!hasBackend)
                {
                    bag.Error(MissingBackend, $"module '{module.Id}' requires a backend on the {Name(side)} side, and no backend of it is active {where}", module.Location);
                }
            }
        }

        var holders = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
        foreach (ModuleManifest module in active)
        {
            foreach (Declared reserve in module.Reserves)
            {
                if (holders.TryGetValue(reserve.Name, out ModuleManifest? holder))
                {
                    bag.Error(Reservation, $"modules '{holder.Id}' and '{module.Id}' both reserve '{reserve.Name}' {where}", reserve.Location);
                }
                else
                {
                    holders[reserve.Name] = module;
                }
            }
        }

        Declared? initial = side == ModuleSide.Client ? engine.ClientState : engine.ServerState;
        if (initial is not null)
        {
            bool provided = active.Any(m => m.States.Any(s => s.Id == initial.Name && (s.Side == ModuleSide.None || (s.Side & side) != 0)));
            if (!provided)
            {
                ModuleManifest? declaring = modules.Values.FirstOrDefault(m => m.States.Any(s => s.Id == initial.Name));
                string detail = declaring is null ? "no module declares it" : $"it is declared by '{declaring.Id}', which does not provide it {where}";
                bag.Error(InitialState, $"the initial {Name(side)} state '{initial.Name}' is not provided by an active module: {detail}", initial.Location);
            }
        }
    }

    private static IReadOnlyList<ModuleManifest>? Sort(List<ModuleManifest> active, string where, DiagnosticBag bag)
    {
        var byId = active.ToDictionary(m => m.Id, Ids);
        var state = new Dictionary<string, int>(Ids);
        var order = new List<ModuleManifest>();
        var stack = new List<string>();
        bool cycle = false;

        void Visit(ModuleManifest module)
        {
            if (cycle)
            {
                return;
            }

            state[module.Id] = 1;
            stack.Add(module.Id);
            foreach (ModuleDependency dependency in module.Dependencies.OrderBy(d => d.Id, Ids))
            {
                if (!byId.TryGetValue(dependency.Id, out ModuleManifest? next))
                {
                    continue;
                }

                state.TryGetValue(next.Id, out int mark);
                if (mark == 1)
                {
                    int start = stack.FindIndex(id => Ids.Equals(id, next.Id));
                    string path = string.Join(" -> ", stack.Skip(start).Append(next.Id));
                    bag.Error(Cycle, $"the dependencies form a cycle {where}: {path}", dependency.Location);
                    cycle = true;
                    return;
                }

                if (mark == 0)
                {
                    Visit(next);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[module.Id] = 2;
            order.Add(module);
        }

        foreach (ModuleManifest module in active.OrderBy(m => m.Id, Ids))
        {
            if (!state.ContainsKey(module.Id))
            {
                Visit(module);
            }
        }

        if (cycle)
        {
            return null;
        }

        // A platform module never depends on a business module, so putting
        // the platform modules first keeps the dependency order.
        return order.Where(m => m.Level == ModuleLevel.Platform).Concat(order.Where(m => m.Level == ModuleLevel.Business)).ToList();
    }

    private static void CheckUnique(IEnumerable<ModuleManifest> modules, Func<ModuleManifest, IEnumerable<Declared>> names, string code, string what, StringComparer comparer, DiagnosticBag bag)
    {
        var owners = new Dictionary<string, ModuleManifest>(comparer);
        foreach (ModuleManifest module in modules.OrderBy(m => m.Id, Ids))
        {
            foreach (Declared name in names(module))
            {
                if (owners.TryGetValue(name.Name, out ModuleManifest? owner))
                {
                    bag.Error(code, $"the {what} '{name.Name}' is declared by both '{owner.Id}' and '{module.Id}'", name.Location);
                }
                else
                {
                    owners[name.Name] = module;
                }
            }
        }
    }

    private static void CheckSettings(EngineManifest engine, Dictionary<string, ModuleManifest> modules, DiagnosticBag bag)
    {
        var declarations = new Dictionary<string, SettingDeclaration>(StringComparer.Ordinal);
        foreach (SettingDeclaration declaration in modules.Values.SelectMany(m => m.Settings))
        {
            declarations.TryAdd(declaration.Key, declaration);
        }

        foreach (SettingValue setting in engine.Settings)
        {
            string element = setting.Operation.ToString();
            if (!declarations.TryGetValue(setting.Key, out SettingDeclaration? declaration))
            {
                string? hint = declarations.Keys.FirstOrDefault(k => string.Equals(k, setting.Key, StringComparison.OrdinalIgnoreCase));
                bag.Error(UnknownKey, $"<{element}> names the unknown key '{setting.Key}'" + (hint is null ? string.Empty : $"; keys are case-sensitive, did you mean '{hint}'?"), setting.Location);
                continue;
            }

            if (setting.Operation == SettingOperation.Set && declaration.Scope != SettingScope.Distribution)
            {
                bag.Error(InvalidSetting, $"<Set> gives a value to a distribution key, but '{setting.Key}' is a user key; use <Default> or <Lock>", setting.Location);
                continue;
            }

            if (setting.Operation != SettingOperation.Set && declaration.Scope != SettingScope.User)
            {
                bag.Error(InvalidSetting, $"<{element}> applies to a user key, but '{setting.Key}' is a distribution key; use <Set>", setting.Location);
                continue;
            }

            if (!declaration.Accepts(setting.Value, out string error))
            {
                bag.Error(InvalidSetting, $"invalid value for '{setting.Key}' ({declaration.Type.ToString().ToLowerInvariant()}): {error}", setting.Location);
            }
        }
    }

    private static void CheckPolicies(EngineManifest engine, Dictionary<string, ModuleManifest> modules, HashSet<string> required, DiagnosticBag bag)
    {
        var explicitPolicies = new Dictionary<string, ModulePolicy>(Ids);
        foreach (ModulePolicy policy in engine.Policies)
        {
            if (!modules.TryGetValue(policy.Module, out ModuleManifest? module))
            {
                bag.Error(UnknownModule, $"<ErrorPolicy> names '{policy.Module}', which is not a module of the distribution", policy.Location);
                continue;
            }

            if (policy.Policy != ErrorPolicyKind.Stop && (required.Contains(module.Id) || module.Level == ModuleLevel.Platform))
            {
                string why = required.Contains(module.Id) ? "a required module" : "a platform module";
                bag.Error(ImposedPolicy, $"'{module.Id}' is {why}, so its error policy is always 'stop', not '{policy.Policy.ToString().ToLowerInvariant()}'", policy.Location);
                continue;
            }

            explicitPolicies[module.Id] = policy;
        }

        ErrorPolicyKind Effective(ModuleManifest module)
        {
            if (required.Contains(module.Id) || module.Level == ModuleLevel.Platform)
            {
                return ErrorPolicyKind.Stop;
            }

            return explicitPolicies.TryGetValue(module.Id, out ModulePolicy? policy) ? policy.Policy : engine.DefaultPolicy;
        }

        foreach (ModuleManifest module in modules.Values.OrderBy(m => m.Id, Ids))
        {
            ErrorPolicyKind policy = Effective(module);
            if (policy == ErrorPolicyKind.Stop)
            {
                continue;
            }

            foreach (ModuleManifest dependent in modules.Values.OrderBy(m => m.Id, Ids))
            {
                bool requiresIt = dependent.Dependencies.Any(d => !d.Optional && Ids.Equals(d.Id, module.Id));
                if (requiresIt && Effective(dependent) == ErrorPolicyKind.Stop)
                {
                    Location at = explicitPolicies.TryGetValue(module.Id, out ModulePolicy? entry) ? entry.Location : engine.Location;
                    bag.Error(CascadePolicy, $"'{module.Id}' has the error policy '{policy.ToString().ToLowerInvariant()}', but '{dependent.Id}' requires it and has the policy 'stop': disabling '{module.Id}' would stop the game", at);
                }
            }
        }
    }

    private static void CheckModuleConfigs(EngineManifest engine, Dictionary<string, ModuleManifest> modules, DiagnosticBag bag)
    {
        foreach (ModuleConfigBlock block in engine.ModuleConfigs)
        {
            if (!modules.ContainsKey(block.Module))
            {
                bag.Error(UnknownModule, $"<ModuleConfig> is meant for '{block.Module}', which is not a module of the distribution", block.Location);
            }
        }
    }

    private static string Name(ModuleSide side) => side == ModuleSide.Client ? "client" : "server";

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/').ToUpperInvariant();

    // Checking each side reports a problem common to both sides twice.
    private static List<Diagnostic> Distinct(DiagnosticBag bag) =>
        bag.Items.DistinctBy(d => (d.Code, d.Message, d.File, d.Line, d.Column)).ToList();
}
