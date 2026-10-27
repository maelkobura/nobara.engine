// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.ResourceCompiler.Manifests;
using F = Nobara.Formats;

namespace Nobara.Core.Loading;

/// <summary>A module accepted at discovery, with its manifest and where it was found.</summary>
internal sealed record DiscoveredModule(ModuleInfo Info, ModuleImage Image, int DistributionIndex);

/// <summary>
/// Step 0 of the lifecycle: finds every module of the distribution, reads
/// its manifest, and checks that the modules can run together. No module
/// code runs here.
/// </summary>
internal static class Composition
{
    private const uint SupportedFormat = 1;

    // Replaced in the messages of side checks by the sides where they apply.
    private const string SidePlaceholder = "{side}";

    /// <summary>Discovers and checks the modules, and returns them in load order.</summary>
    /// <param name="distribution">The distribution.</param>
    /// <param name="source">Where the modules are.</param>
    /// <param name="hostedSides">The sides the process may host: both for a client, the server for a dedicated server.</param>
    /// <exception cref="ModuleCompositionException">At least one problem was found.</exception>
    public static IReadOnlyList<DiscoveredModule> Compose(Distribution distribution, IModuleSource source, Side hostedSides)
    {
        var problems = new List<CompositionProblem>(
            source.CheckLibraries(distribution.Manifest.Libraries.Select(l => new ExpectedLibrary(l.Path, l.Hash.Memory)).ToArray()));

        var modules = new List<DiscoveredModule>();
        for (int index = 0; index < distribution.Manifest.Modules.Count; index++)
        {
            F.DistributionModule entry = distribution.Manifest.Modules[index];
            DiscoveredModule? module = Discover(distribution, source, entry, index, problems);
            if (module is not null)
            {
                modules.Add(module);
            }
        }

        // Missing or altered modules would only add noise to the graph checks.
        if (problems.Count > 0)
        {
            throw new ModuleCompositionException(problems);
        }

        var byId = modules.ToDictionary(m => m.Info.Id, StringComparer.Ordinal);
        CheckGraph(modules, byId, problems);

        // A problem found on both sides is reported once.
        var bySide = new Dictionary<CompositionProblem, Side>();
        foreach (Side side in new[] { Side.Client, Side.Server })
        {
            if ((hostedSides & side) != Side.None)
            {
                var found = new List<CompositionProblem>();
                CheckSide(modules, byId, side, found);
                foreach (CompositionProblem problem in found)
                {
                    bySide[problem] = bySide.GetValueOrDefault(problem) | side;
                }
            }
        }

        foreach ((CompositionProblem problem, Side sides) in bySide)
        {
            string where = sides == Side.Both ? "both sides" : sides == Side.Client ? "the client side" : "the server side";
            problems.Add(problem with { Message = problem.Message.Replace(SidePlaceholder, where, StringComparison.Ordinal) });
        }

        if (problems.Count > 0)
        {
            throw new ModuleCompositionException(problems);
        }

        return Order(modules, byId);
    }

    private static DiscoveredModule? Discover(
        Distribution distribution, IModuleSource source, F.DistributionModule entry, int index, List<CompositionProblem> problems)
    {
        var expected = new ExpectedModule(entry.Id, entry.Version, entry.Hash.Memory);
        ModuleImage image;
        try
        {
            image = source.Discover(expected);
        }
        catch (ModuleDiscoveryException exception)
        {
            problems.Add(new CompositionProblem(exception.Kind, entry.Id, exception.Message));
            return null;
        }

        F.ModuleManifest manifest;
        try
        {
            manifest = F.ModuleManifest.Parser.ParseFrom(image.Manifest.Span);
        }
        catch (InvalidProtocolBufferException exception)
        {
            problems.Add(Invalid(entry.Id, $"Module '{entry.Id}': the manifest in '{image.Location}' cannot be read: {exception.Message}"));
            return null;
        }

        string? error = manifest.Format != SupportedFormat ? $"its manifest has format {manifest.Format}; this core reads format {SupportedFormat}"
            : !string.Equals(manifest.Id, entry.Id, StringComparison.OrdinalIgnoreCase) ? $"'{image.Location}' holds the manifest of '{manifest.Id}'"
            : manifest.Version != entry.Version ? $"the distribution expects version {entry.Version}, but '{image.Location}' holds version {manifest.Version}"
            : manifest.Level == F.Level.Unspecified ? "its manifest has no level"
            : manifest.Side == F.Side.Unspecified ? "its manifest has no side"
            : null;
        if (error is not null)
        {
            problems.Add(Invalid(entry.Id, $"Module '{entry.Id}': {error}."));
            return null;
        }

        foreach (F.Dependency dependency in manifest.Dependencies)
        {
            if (!VersionRange.TryParse(dependency.Range, out _, out string rangeError))
            {
                problems.Add(Invalid(entry.Id, $"Module '{entry.Id}': the range of its dependency '{dependency.Id}' is invalid: {rangeError}."));
                return null;
            }
        }

        return new DiscoveredModule(distribution.CreateInfo(manifest, entry.Required), image, index);
    }

    // Checks that do not depend on the side: versions, cycles, and the level rule.
    private static void CheckGraph(List<DiscoveredModule> modules, Dictionary<string, DiscoveredModule> byId, List<CompositionProblem> problems)
    {
        foreach (DiscoveredModule module in modules)
        {
            foreach (ModuleDependency dependency in module.Info.Dependencies)
            {
                if (!byId.TryGetValue(dependency.ModuleId, out DiscoveredModule? target))
                {
                    continue;
                }

                VersionRange.TryParse(dependency.Range, out VersionRange? range, out _);
                if (SemanticVersion.TryParse(target.Info.Version, out SemanticVersion? version) && !range!.Contains(version!))
                {
                    problems.Add(new CompositionProblem(
                        CompositionProblemKind.VersionOutOfRange,
                        module.Info.Id,
                        $"Module '{module.Info.Id}' depends on '{dependency.ModuleId}' {dependency.Range}, but the distribution has version {target.Info.Version}."));
                }

                if (module.Info.Level == ModuleLevel.Platform && target.Info.Level == ModuleLevel.Business)
                {
                    problems.Add(new CompositionProblem(
                        CompositionProblemKind.PlatformDependsOnBusiness,
                        module.Info.Id,
                        $"Platform module '{module.Info.Id}' depends on business module '{dependency.ModuleId}'."));
                }
            }
        }

        foreach (IReadOnlyList<string> cycle in FindCycles(modules, byId))
        {
            problems.Add(new CompositionProblem(
                CompositionProblemKind.DependencyCycle,
                cycle[0],
                $"Modules depend on each other in a cycle: {string.Join(" -> ", cycle)} -> {cycle[0]}."));
        }
    }

    // Checks among the modules that run on one side.
    private static void CheckSide(List<DiscoveredModule> modules, Dictionary<string, DiscoveredModule> byId, Side side, List<CompositionProblem> problems)
    {
        var active = modules.Where(m => m.Info.RunsOn(side)).ToList();
        var activeIds = active.Select(m => m.Info.Id).ToHashSet(StringComparer.Ordinal);
        var holders = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DiscoveredModule module in active)
        {
            ModuleInfo info = module.Info;
            foreach (ModuleDependency dependency in info.Dependencies.Where(d => !d.Optional && !activeIds.Contains(d.ModuleId)))
            {
                string where = byId.ContainsKey(dependency.ModuleId)
                    ? $"which does not run on {SidePlaceholder}"
                    : "which is not in the distribution";
                problems.Add(new CompositionProblem(
                    CompositionProblemKind.MissingDependency,
                    info.Id,
                    $"Module '{info.Id}' requires '{dependency.ModuleId}' {dependency.Range}, {where}."));
            }

            foreach (string other in info.Conflicts.Where(activeIds.Contains))
            {
                // Report a mutual conflict once.
                bool mutual = byId[other].Info.Conflicts.Contains(info.Id, StringComparer.Ordinal);
                if (!mutual || string.CompareOrdinal(info.Id, other) < 0)
                {
                    problems.Add(new CompositionProblem(
                        CompositionProblemKind.Conflict,
                        info.Id,
                        $"Module '{info.Id}' conflicts with '{other}', and both run on {SidePlaceholder}."));
                }
            }

            foreach (string reservation in info.Reservations)
            {
                if (holders.TryGetValue(reservation, out string? holder))
                {
                    problems.Add(new CompositionProblem(
                        CompositionProblemKind.DisputedReservation,
                        info.Id,
                        $"Modules '{holder}' and '{info.Id}' both reserve '{reservation}' on {SidePlaceholder}."));
                }
                else
                {
                    holders.Add(reservation, info.Id);
                }
            }

            if ((info.BackendRequired & side) != Side.None
                && !active.Any(m => string.Equals(m.Info.BackendOf, info.Id, StringComparison.Ordinal)))
            {
                problems.Add(new CompositionProblem(
                    CompositionProblemKind.MissingBackend,
                    info.Id,
                    $"Module '{info.Id}' requires a backend on {SidePlaceholder}, and no active module there is one."));
            }
        }
    }

    private static List<IReadOnlyList<string>> FindCycles(List<DiscoveredModule> modules, Dictionary<string, DiscoveredModule> byId)
    {
        var cycles = new List<IReadOnlyList<string>>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1: on the path, 2: done
        var path = new List<string>();

        void Visit(string id)
        {
            state[id] = 1;
            path.Add(id);
            foreach (ModuleDependency dependency in byId[id].Info.Dependencies)
            {
                if (!byId.ContainsKey(dependency.ModuleId))
                {
                    continue;
                }

                state.TryGetValue(dependency.ModuleId, out int mark);
                if (mark == 1)
                {
                    cycles.Add(path.Skip(path.IndexOf(dependency.ModuleId)).ToList());
                }
                else if (mark == 0)
                {
                    Visit(dependency.ModuleId);
                }
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }

        foreach (DiscoveredModule module in modules)
        {
            if (!state.ContainsKey(module.Info.Id))
            {
                Visit(module.Info.Id);
            }
        }

        return cycles;
    }

    // Platform modules first, then business modules; within a level, a module
    // comes after its present dependencies, required or optional, and ties
    // keep the order of the distribution.
    private static List<DiscoveredModule> Order(List<DiscoveredModule> modules, Dictionary<string, DiscoveredModule> byId)
    {
        var order = new List<DiscoveredModule>(modules.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModuleLevel level in new[] { ModuleLevel.Platform, ModuleLevel.Business })
        {
            var group = modules.Where(m => m.Info.Level == level).ToList();

            // A dependency counts when it is present and not placed yet. The
            // checks guarantee that a platform module only depends on
            // platform modules, so every pending dependency is in the group.
            var remaining = group.ToDictionary(
                m => m.Info.Id,
                m => m.Info.Dependencies.Count(d => byId.ContainsKey(d.ModuleId) && !placed.Contains(d.ModuleId)),
                StringComparer.Ordinal);
            var dependents = group.ToDictionary(m => m.Info.Id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (DiscoveredModule module in group)
            {
                foreach (ModuleDependency dependency in module.Info.Dependencies.Where(d => dependents.ContainsKey(d.ModuleId)))
                {
                    dependents[dependency.ModuleId].Add(module.Info.Id);
                }
            }

            var ready = new PriorityQueue<DiscoveredModule, int>();
            foreach (DiscoveredModule module in group.Where(m => remaining[m.Info.Id] == 0))
            {
                ready.Enqueue(module, module.DistributionIndex);
            }

            while (ready.TryDequeue(out DiscoveredModule? module, out _))
            {
                order.Add(module);
                placed.Add(module.Info.Id);
                foreach (string dependent in dependents[module.Info.Id])
                {
                    if (--remaining[dependent] == 0)
                    {
                        DiscoveredModule next = byId[dependent];
                        ready.Enqueue(next, next.DistributionIndex);
                    }
                }
            }
        }

        return order;
    }

    private static CompositionProblem Invalid(string moduleId, string message) =>
        new(CompositionProblemKind.InvalidManifest, moduleId, message);
}
