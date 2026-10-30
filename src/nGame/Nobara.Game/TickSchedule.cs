// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Profiling;

namespace Nobara.Game;

/// <summary>
/// The stages and systems of the tick of one context: their registration,
/// the resolution of their order, and the run of each tick.
/// </summary>
internal sealed partial class TickSchedule : ITickSchedule
{
    private readonly ModuleContext _game;
    private readonly Dictionary<string, StageEntry> _stages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SystemEntry> _systems = new(StringComparer.Ordinal);
    private readonly List<ModuleGroup> _groups = [];
    private StageEntry[] _order = [];

    // The systems that run, per stage of the order; revoked systems are
    // removed in place at the start of the next tick.
    private SystemEntry[][] _running = [];
    private int[] _runningCount = [];
    private bool _revoked;

    public TickSchedule(ModuleContext game)
    {
        _game = game;
        string? previous = null;
        foreach (string name in TickStages.Standard)
        {
            _stages.Add(name, new StageEntry(name, game.Module.Id, game.LoadOrder, previous, null, null, IsStandard: true));
            previous = name;
        }
    }

    public bool IsResolved { get; private set; }

    public void AddStage(ModuleContext module, string name, string? after = null, string? before = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);
        RequireOpen($"add stage '{name}'");
        CheckName(module.Module.Id, name, "stage");
        if (after is null && before is null)
        {
            throw new ArgumentException($"Stage '{name}' needs a stage to run after, before, or both.", nameof(after));
        }

        _stages.Add(name, new StageEntry(name, module.Module.Id, module.LoadOrder, after, before, null, IsStandard: false));
    }

    public TickSystemHandle AddSystem(ModuleContext module, string name, string stage, ITickSystem system, TickSystemOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(system);
        options ??= new TickSystemOptions();
        RequireOpen($"add system '{name}'");
        CheckName(module.Module.Id, name, "system");
        LoopAccess access = module.Jobs.DeclareLoopAccess($"tick system '{name}'", [.. options.Reads], [.. options.Writes]);
        return Add(module, name, stage, system, options, access);
    }

    /// <summary>Adds the system of nGame that runs the tasks of the scheduler, which declare no accesses.</summary>
    public void AddBuiltIn(string name, string stage, ITickSystem system) =>
        Add(_game, name, stage, system, new TickSystemOptions(), access: null);

    public void Revoke(string name)
    {
        if (_systems.TryGetValue(name, out SystemEntry? entry) && !entry.Revoked)
        {
            entry.Revoked = true;
            _revoked = true;
        }
    }

    public IReadOnlyList<TickStageInfo> GetOrder()
    {
        if (!IsResolved)
        {
            throw new InvalidOperationException("The order of the tick is resolved when nobara.game starts; it is not resolved yet.");
        }

        return _order
            .Select(s => new TickStageInfo(
                s.Name,
                s.ModuleId,
                s.Systems.Select(e => new TickSystemInfo(
                    e.Name,
                    s.Name,
                    e.ModuleId,
                    StateOf(e),
                    e.Options.Reads.Select(d => d.Name).ToArray(),
                    e.Options.Writes.Select(d => d.Name).ToArray())).ToArray()))
            .ToArray();
    }

    public string FormatOrder()
    {
        var text = new StringBuilder();
        int number = 0;
        foreach (TickStageInfo stage in GetOrder())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{++number}. {stage.Name} ({stage.ModuleId})");
            foreach (TickSystemInfo system in stage.Systems)
            {
                string state = system.State == TickSystemState.Runs ? string.Empty : $" [{system.State}]";
                text.AppendLine(CultureInfo.InvariantCulture, $"   {system.Name} ({system.ModuleId}){state}");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Resolves the order of the stages and of the systems, applies the
    /// configuration of the distribution, and fixes the order.
    /// </summary>
    /// <exception cref="TickScheduleException">A stage cannot be placed, a constraint forms a cycle, or the configuration names what does not exist.</exception>
    public void Resolve(GameConfig config, string distributionId, Side side)
    {
        var problems = new List<string>();
        ApplyConfig(config, distributionId, side, problems);
        List<StageEntry> stages = PlaceStages(problems);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < stages.Count; i++)
        {
            index.Add(stages[i].Name, i);
        }

        foreach (SystemEntry system in _systems.Values.OrderBy(s => s.LoadOrder).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            if (!index.TryGetValue(system.StageName, out int position))
            {
                problems.Add($"System '{system.Name}' of module '{system.ModuleId}' runs in stage '{system.StageName}', which no module adds.");
                continue;
            }

            system.StageIndex = position;
            system.OtherSide = (system.Options.Side & side) == Side.None;
        }

        if (problems.Count == 0)
        {
            foreach (StageEntry stage in stages)
            {
                stage.Systems = OrderSystems(stage, index, problems);
            }
        }

        if (problems.Count > 0)
        {
            throw new TickScheduleException(problems);
        }

        _order = [.. stages];
        _running = new SystemEntry[_order.Length][];
        _runningCount = new int[_order.Length];
        for (int i = 0; i < _order.Length; i++)
        {
            _order[i].Marker = _game.Profiler.CreateMarker("Tick stage " + _order[i].Name);
            _running[i] = _order[i].Systems.Where(s => !s.OtherSide && !s.Disabled && !s.Revoked).ToArray();
            _runningCount[i] = _running[i].Length;
        }

        _revoked = false;
        IsResolved = true;
    }

    /// <summary>Runs every system of one tick, stage after stage.</summary>
    [HotPath]
    public void RunTick(in TickTime time)
    {
        if (_revoked)
        {
            RemoveRevoked();
        }

        RefreshModules();
        for (int stage = 0; stage < _running.Length; stage++)
        {
            SystemEntry[] systems = _running[stage];
            int count = _runningCount[stage];
            ProfilerScope scope = _order[stage].Marker!.Begin();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    SystemEntry system = systems[i];
                    if (system.Group.Active)
                    {
                        RunSystem(system, time);
                    }
                }
            }
            finally
            {
                scope.Dispose();
            }
        }
    }

    /// <summary>Updates whether the module of each system is still active.</summary>
    [HotPath]
    public void RefreshModules()
    {
        foreach (ModuleGroup group in _groups)
        {
            group.Active = group.Context.IsModuleActive(group.ModuleId);
        }
    }

    [HotPath]
    private void RunSystem(SystemEntry system, in TickTime time)
    {
        Exception? failure = null;
        ProfilerScope scope = system.Marker.Begin();
        LoopAccessScope access = system.Access is null ? default : system.Access.Enter();
        try
        {
            system.System.Run(time);
        }
        catch (Exception thrown) when (thrown is not EngineStopException)
        {
            failure = thrown;
        }
        finally
        {
            access.Dispose();
            scope.Dispose();
        }

        if (failure is not null)
        {
            // The policy of the module applies; a module disabled now skips its next systems.
            system.Group.Context.ReportFault(failure);
            RefreshModules();
        }
    }

    [HotPath]
    private void RemoveRevoked()
    {
        _revoked = false;
        for (int stage = 0; stage < _running.Length; stage++)
        {
            SystemEntry[] systems = _running[stage];
            int kept = 0;
            for (int i = 0; i < _runningCount[stage]; i++)
            {
                if (!systems[i].Revoked)
                {
                    systems[kept++] = systems[i];
                }
            }

            Array.Clear(systems, kept, _runningCount[stage] - kept);
            _runningCount[stage] = kept;
        }
    }

    private TickSystemHandle Add(ModuleContext module, string name, string stage, ITickSystem system, TickSystemOptions options, LoopAccess? access)
    {
        ModuleGroup? group = _groups.Find(g => g.ModuleId == module.Module.Id);
        if (group is null)
        {
            group = new ModuleGroup(module, module.Module.Id);
            _groups.Add(group);
        }

        ProfilerMarker marker = module.Profiler.CreateMarker("Tick system " + name);
        var entry = new SystemEntry(name, stage, module.Module.Id, module.LoadOrder, system, options, access, marker, group);
        foreach (string target in options.After)
        {
            entry.Constraints.Add(new Constraint(target, After: true, Source: null));
        }

        foreach (string target in options.Before)
        {
            entry.Constraints.Add(new Constraint(target, After: false, Source: null));
        }

        _systems.Add(name, entry);
        return new TickSystemHandle(this, name);
    }

    private void RequireOpen(string action)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException(
                $"Cannot {action}: the order of the tick is fixed once nobara.game starts. Add stages and systems while the module initializes.");
        }
    }

    private void CheckName(string moduleId, string name, string what)
    {
        if (!name.StartsWith(moduleId + ".", StringComparison.Ordinal) || !QualifiedName().IsMatch(name))
        {
            throw new ArgumentException(
                $"The {what} name '{name}' must be qualified by its module: '{moduleId}.' then lowercase names of letters, digits, and underscores, separated by dots.",
                nameof(name));
        }

        if (_stages.ContainsKey(name) || _systems.ContainsKey(name))
        {
            throw new ArgumentException($"The name '{name}' is already used by a {(_stages.ContainsKey(name) ? "stage" : "system")} of the tick.", nameof(name));
        }
    }

    private TickSystemState StateOf(SystemEntry entry) =>
        entry.OtherSide ? TickSystemState.OtherSide
        : entry.Disabled ? TickSystemState.Disabled
        : entry.Revoked ? TickSystemState.Revoked
        : !entry.Group.Context.IsModuleActive(entry.ModuleId) ? TickSystemState.ModuleDisabled
        : TickSystemState.Runs;

    private void ApplyConfig(GameConfig config, string distributionId, Side side, List<string> problems)
    {
        foreach (ConfigStage stage in config.Stages.Where(s => (s.Side & side) != Side.None))
        {
            if (!stage.Name.StartsWith(distributionId + ".", StringComparison.Ordinal) || !QualifiedName().IsMatch(stage.Name))
            {
                problems.Add($"{stage.Source}: the stage name '{stage.Name}' must be qualified by the game: '{distributionId}.' then lowercase names separated by dots.");
            }
            else if (_stages.ContainsKey(stage.Name) || _systems.ContainsKey(stage.Name))
            {
                problems.Add($"{stage.Source}: the name '{stage.Name}' is already used by a stage or a system of the tick.");
            }
            else
            {
                _stages.Add(stage.Name, new StageEntry(stage.Name, distributionId, int.MaxValue, stage.After, stage.Before, stage.Source, IsStandard: false));
            }
        }

        foreach (ConfigConstraint constraint in config.Constraints.Where(c => (c.Side & side) != Side.None))
        {
            if (!KnownSystem(constraint.System, constraint.Source, problems, out SystemEntry? system))
            {
                continue;
            }

            foreach ((string? target, bool after) in new[] { (constraint.After, true), (constraint.Before, false) })
            {
                if (target is not null && KnownSystem(target, constraint.Source, problems, out _))
                {
                    system.Constraints.Add(new Constraint(target, after, constraint.Source));
                }
            }
        }

        foreach (ConfigDisable disable in config.Disables.Where(d => (d.Side & side) != Side.None))
        {
            if (KnownSystem(disable.System, disable.Source, problems, out SystemEntry? system))
            {
                system.Disabled = true;
            }
        }
    }

    private bool KnownSystem(string name, SourcePosition source, List<string> problems, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SystemEntry? system)
    {
        if (_systems.TryGetValue(name, out system))
        {
            return true;
        }

        problems.Add(_stages.ContainsKey(name)
            ? $"{source}: '{name}' is a stage, not a system; a constraint orders systems."
            : $"{source}: no module registers a tick system named '{name}' on this side.");
        return false;
    }

    // Each inserted stage goes right after its 'after' stage, or right before
    // its 'before' stage when it has only that one. Stages that go to the same
    // place keep the load order of their modules, then their names.
    private List<StageEntry> PlaceStages(List<string> problems)
    {
        var placed = _stages.Values.Where(s => s.IsStandard).ToList();
        var pending = _stages.Values.Where(s => !s.IsStandard).OrderBy(s => s.LoadOrder).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        bool progress = true;
        while (progress && pending.Count > 0)
        {
            progress = false;
            foreach (StageEntry stage in pending.ToArray())
            {
                int position;
                if (stage.After is not null)
                {
                    StageEntry? anchor = placed.Find(s => s.Name == stage.After);
                    if (anchor is null)
                    {
                        continue;
                    }

                    // Past the stages already inserted after the anchor, and those inserted after them.
                    position = placed.IndexOf(anchor) + 1;
                    while (position < placed.Count && FollowsAfter(placed[position], anchor.Name))
                    {
                        position++;
                    }
                }
                else
                {
                    StageEntry? anchor = placed.Find(s => s.Name == stage.Before);
                    if (anchor is null)
                    {
                        continue;
                    }

                    position = placed.IndexOf(anchor);
                }

                placed.Insert(position, stage);
                pending.Remove(stage);
                progress = true;
            }
        }

        foreach (StageEntry stage in pending)
        {
            string anchor = stage.After ?? stage.Before!;
            string reason = _stages.ContainsKey(anchor)
                ? $"stage '{anchor}' cannot be placed first; the stages wait on each other"
                : $"no module adds stage '{anchor}'";
            problems.Add($"{Where(stage)}Stage '{stage.Name}' of {Owner(stage)} cannot be placed: {reason}.");
        }

        foreach (StageEntry stage in placed.Where(s => s.Before is not null && s.After is not null))
        {
            int before = placed.FindIndex(s => s.Name == stage.Before);
            if (before < 0)
            {
                problems.Add($"{Where(stage)}Stage '{stage.Name}' of {Owner(stage)} runs before stage '{stage.Before}', which no module adds.");
            }
            else if (before < placed.IndexOf(stage))
            {
                problems.Add($"{Where(stage)}Stage '{stage.Name}' of {Owner(stage)} runs after '{stage.After}' and before '{stage.Before}', but '{stage.Before}' runs before '{stage.After}': the constraints form a cycle.");
            }
        }

        return placed;
    }

    // Whether an inserted stage goes after the anchor through a chain of 'after'.
    private bool FollowsAfter(StageEntry stage, string anchor)
    {
        StageEntry? current = stage;
        while (current is { IsStandard: false, After: not null })
        {
            if (current.After == anchor)
            {
                return true;
            }

            current = _stages.GetValueOrDefault(current.After);
        }

        return false;
    }

    // Orders the systems of one stage: a topological sort of their
    // constraints, ties broken by the load order of their modules, then by
    // their names.
    private List<SystemEntry> OrderSystems(StageEntry stage, Dictionary<string, int> stageIndex, List<string> problems)
    {
        int here = stageIndex[stage.Name];
        var members = _systems.Values.Where(s => s.StageIndex == here).ToList();
        var edges = members.ToDictionary(s => s, _ => new List<(SystemEntry To, SourcePosition? Source)>());
        var incoming = members.ToDictionary(s => s, _ => 0);
        foreach (SystemEntry system in members)
        {
            foreach (Constraint constraint in system.Constraints)
            {
                if (!_systems.TryGetValue(constraint.Target, out SystemEntry? target))
                {
                    _game.Logger.Debug($"System '{system.Name}' is ordered against '{constraint.Target}', which no module registers here; the constraint is ignored.");
                    continue;
                }

                if (target.StageIndex != here)
                {
                    bool satisfied = constraint.After ? target.StageIndex < here : target.StageIndex > here;
                    if (!satisfied)
                    {
                        problems.Add($"{Where(constraint.Source)}System '{system.Name}' (stage '{stage.Name}') runs {(constraint.After ? "after" : "before")} '{target.Name}', which runs in stage '{target.StageName}', {(constraint.After ? "later" : "earlier")} in the tick: the constraint and the stages form a cycle.");
                    }

                    continue;
                }

                (SystemEntry from, SystemEntry to) = constraint.After ? (target, system) : (system, target);
                edges[from].Add((to, constraint.Source));
                incoming[to]++;
            }
        }

        var ready = new PriorityQueue<SystemEntry, (int, string)>(Comparer<(int LoadOrder, string Name)>.Create((a, b) =>
            a.LoadOrder != b.LoadOrder ? a.LoadOrder.CompareTo(b.LoadOrder) : string.CompareOrdinal(a.Name, b.Name)));
        foreach (SystemEntry system in members.Where(s => incoming[s] == 0))
        {
            ready.Enqueue(system, (system.LoadOrder, system.Name));
        }

        var order = new List<SystemEntry>(members.Count);
        while (ready.TryDequeue(out SystemEntry? system, out _))
        {
            order.Add(system);
            foreach ((SystemEntry to, _) in edges[system])
            {
                if (--incoming[to] == 0)
                {
                    ready.Enqueue(to, (to.LoadOrder, to.Name));
                }
            }
        }

        if (order.Count < members.Count)
        {
            problems.Add($"The tick systems of stage '{stage.Name}' form a cycle: {DescribeCycle(members.Where(s => incoming[s] > 0).ToList(), edges)}.");
        }

        return order;
    }

    private static string DescribeCycle(List<SystemEntry> remaining, Dictionary<SystemEntry, List<(SystemEntry To, SourcePosition? Source)>> edges)
    {
        // Every remaining system has a remaining predecessor: walking back
        // from any of them meets a system twice, on a cycle.
        var inCycle = remaining.ToHashSet();
        var predecessors = remaining.ToDictionary(s => s, _ => new List<(SystemEntry From, SourcePosition? Source)>());
        foreach ((SystemEntry from, List<(SystemEntry To, SourcePosition? Source)> targets) in edges)
        {
            foreach ((SystemEntry to, SourcePosition? source) in targets)
            {
                if (inCycle.Contains(from) && inCycle.Contains(to))
                {
                    predecessors[to].Add((from, source));
                }
            }
        }

        var path = new List<SystemEntry>();
        var via = new List<SourcePosition?>();
        var seen = new Dictionary<SystemEntry, int>();
        SystemEntry current = remaining.OrderBy(s => s.Name, StringComparer.Ordinal).First();
        while (!seen.ContainsKey(current))
        {
            seen[current] = path.Count;
            path.Add(current);
            (SystemEntry previous, SourcePosition? source) = predecessors[current][0];
            via.Add(source);
            current = previous;
        }

        // The path goes backward; the cycle reads forward, from its first name.
        int start = seen[current];
        List<SystemEntry> cycle = path.GetRange(start, path.Count - start);
        cycle.Reverse();
        int first = cycle.IndexOf(cycle.MinBy(s => s.Name, StringComparer.Ordinal)!);
        cycle = [.. cycle.Skip(first), .. cycle.Take(first)];
        cycle.Add(cycle[0]);
        string text = string.Join(" -> ", cycle.Select(s => s.Name));
        string[] sources = via.Skip(start).OfType<SourcePosition>().Select(p => p.ToString()).Distinct().ToArray();
        return sources.Length == 0 ? text : text + $" (constraints of {string.Join(", ", sources)})";
    }

    private static string Where(StageEntry stage) => Where(stage.Source);

    private static string Where(SourcePosition? source) => source is { } position ? position + ": " : string.Empty;

    private static string Owner(StageEntry stage) => stage.Source is null ? $"module '{stage.ModuleId}'" : "engine.nobara";

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedName();

    private sealed record Constraint(string Target, bool After, SourcePosition? Source);

    private sealed record StageEntry(string Name, string ModuleId, int LoadOrder, string? After, string? Before, SourcePosition? Source, bool IsStandard)
    {
        public List<SystemEntry> Systems { get; set; } = [];

        public ProfilerMarker? Marker { get; set; }
    }

    /// <summary>The modules that have systems, and whether each is still active.</summary>
    private sealed class ModuleGroup(ModuleContext context, string moduleId)
    {
        public ModuleContext Context { get; } = context;

        public string ModuleId { get; } = moduleId;

        public bool Active { get; set; } = true;
    }

    private sealed class SystemEntry(
        string name,
        string stageName,
        string moduleId,
        int loadOrder,
        ITickSystem system,
        TickSystemOptions options,
        LoopAccess? access,
        ProfilerMarker marker,
        ModuleGroup group)
    {
        public string Name { get; } = name;

        public string StageName { get; } = stageName;

        public string ModuleId { get; } = moduleId;

        public int LoadOrder { get; } = loadOrder;

        public ITickSystem System { get; } = system;

        public TickSystemOptions Options { get; } = options;

        public LoopAccess? Access { get; } = access;

        public ProfilerMarker Marker { get; } = marker;

        public ModuleGroup Group { get; } = group;

        public List<Constraint> Constraints { get; } = [];

        public int StageIndex { get; set; } = -1;

        public bool OtherSide { get; set; }

        public bool Disabled { get; set; }

        public bool Revoked { get; set; }
    }
}
