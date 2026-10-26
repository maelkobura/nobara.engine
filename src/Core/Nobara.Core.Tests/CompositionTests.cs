// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core.Loading;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>Step 0: the core refuses compositions that cannot run, before any module code runs.</summary>
public sealed class CompositionTests : IDisposable
{
    private readonly TestDistribution _distribution = new();

    public void Dispose() => _distribution.Dispose();

    private CompositionProblem Refused(CompositionProblemKind kind, Side side = Side.Client)
    {
        var exception = Assert.Throws<ModuleCompositionException>(() => _distribution.Load(side));

        // No module was loaded, so no module code ran.
        Assert.Empty(_distribution.Loaded);
        Assert.Equal(0, _distribution.Instances);
        return Assert.Single(exception.Problems, p => p.Kind == kind);
    }

    [Fact]
    public void Refuses_a_missing_required_dependency_and_names_both_modules()
    {
        _distribution.Module("example.mobs", m => m.Requires("example.creatures"));

        CompositionProblem problem = Refused(CompositionProblemKind.MissingDependency);

        Assert.Equal("example.mobs", problem.ModuleId);
        Assert.Contains("'example.mobs' requires 'example.creatures'", problem.Message);
        Assert.Contains("not in the distribution", problem.Message);
    }

    [Fact]
    public void Refuses_a_required_dependency_that_does_not_run_on_a_side_of_its_dependent()
    {
        _distribution
            .Module("example.sky", m => m.Side = F.Side.Client)
            .Module("example.weather", m => m.Requires("example.sky"));

        CompositionProblem problem = Refused(CompositionProblemKind.MissingDependency);

        Assert.Equal("example.weather", problem.ModuleId);
        Assert.Contains("does not run on the server side", problem.Message);
    }

    [Fact]
    public void Accepts_a_missing_optional_dependency()
    {
        _distribution.Module("example.mobs", m => m.Uses("example.particles"));

        using Engine engine = _distribution.Load();

        Assert.Equal(["example.mobs"], engine.Modules.Select(m => m.Id));
    }

    [Fact]
    public void Refuses_a_dependency_outside_its_range()
    {
        _distribution
            .Module("example.creatures", version: "2.1.0")
            .Module("example.mobs", m => m.Requires("example.creatures", "^1.0"));

        CompositionProblem problem = Refused(CompositionProblemKind.VersionOutOfRange);

        Assert.Equal("example.mobs", problem.ModuleId);
        Assert.Contains("'example.creatures' ^1.0", problem.Message);
        Assert.Contains("version 2.1.0", problem.Message);
    }

    [Fact]
    public void Refuses_an_optional_dependency_present_outside_its_range()
    {
        _distribution
            .Module("example.particles", version: "3.0.0")
            .Module("example.mobs", m => m.Uses("example.particles", "^2.0"));

        Assert.Equal("example.mobs", Refused(CompositionProblemKind.VersionOutOfRange).ModuleId);
    }

    [Fact]
    public void Refuses_a_dependency_cycle_and_names_the_modules_of_the_cycle()
    {
        _distribution
            .Module("example.a", m => m.Requires("example.b"))
            .Module("example.b", m => m.Requires("example.c"))
            .Module("example.c", m => m.Uses("example.a"));

        CompositionProblem problem = Refused(CompositionProblemKind.DependencyCycle);

        Assert.Contains("example.a -> example.b -> example.c -> example.a", problem.Message);
    }

    [Fact]
    public void Refuses_a_platform_module_that_depends_on_a_business_module()
    {
        _distribution
            .Module("example.mobs")
            .Module("example.physics", m => m.Uses("example.mobs").Level = F.Level.Platform);

        CompositionProblem problem = Refused(CompositionProblemKind.PlatformDependsOnBusiness);

        Assert.Equal("example.physics", problem.ModuleId);
        Assert.Contains("'example.mobs'", problem.Message);
    }

    [Fact]
    public void Refuses_two_active_modules_in_conflict()
    {
        _distribution
            .Module("example.terrain")
            .Module("example.flatworld", m => m.Conflicts.Add("example.terrain"));

        CompositionProblem problem = Refused(CompositionProblemKind.Conflict);

        Assert.Equal("example.flatworld", problem.ModuleId);
        Assert.Contains("conflicts with 'example.terrain'", problem.Message);
    }

    [Fact]
    public void Accepts_a_conflict_between_modules_that_never_run_on_the_same_side()
    {
        _distribution
            .Module("example.hud", m => m.Side = F.Side.Client)
            .Module("example.console", m =>
            {
                m.Side = F.Side.Server;
                m.Conflicts.Add("example.hud");
            });

        using Engine engine = _distribution.Load();

        Assert.Equal(2, engine.Modules.Count);
    }

    [Fact]
    public void Refuses_a_disputed_exclusive_resource_and_names_both_holders()
    {
        _distribution
            .Module("example.window", m => m.Reserves.Add("window.main"))
            .Module("example.otherwindow", m => m.Reserves.Add("window.main"));

        CompositionProblem problem = Refused(CompositionProblemKind.DisputedReservation);

        Assert.Contains("'example.window' and 'example.otherwindow' both reserve 'window.main'", problem.Message);
    }

    [Fact]
    public void Refuses_a_base_module_without_a_backend_on_a_side_where_it_requires_one()
    {
        _distribution
            .Module("example.audio", m => m.BackendRequired = F.Side.Client)
            .Module("example.audio.null", m =>
            {
                m.Side = F.Side.Server;
                m.BackendOf = "example.audio";
                m.Requires("example.audio");
            });

        CompositionProblem problem = Refused(CompositionProblemKind.MissingBackend);

        Assert.Equal("example.audio", problem.ModuleId);
        Assert.Contains("client side", problem.Message);
    }

    [Fact]
    public void Accepts_a_base_module_with_a_backend_on_the_side_where_it_requires_one()
    {
        _distribution
            .Module("example.audio", m => m.BackendRequired = F.Side.Client)
            .Module("example.audio.openal", m =>
            {
                m.Side = F.Side.Client;
                m.BackendOf = "example.audio";
                m.Requires("example.audio");
            });

        using Engine engine = _distribution.Load();

        Assert.Equal(2, engine.Modules.Count);
    }

    [Fact]
    public void Checks_only_the_server_side_for_a_dedicated_server()
    {
        _distribution
            .Module("example.audio", m => m.BackendRequired = F.Side.Client)
            .Module("example.window", m =>
            {
                m.Side = F.Side.Client;
                m.Requires("example.missing");
            });

        using Engine engine = _distribution.Load(Side.Server);

        // A client module is not even loaded by a dedicated server.
        Assert.Equal(["example.audio"], engine.Modules.Select(m => m.Id));
        Assert.Equal(["example.audio"], engine.MainContext.Modules.Select(m => m.Id));
        Assert.Equal(["example.audio"], _distribution.Loaded);
    }

    [Fact]
    public void Refuses_a_manifest_whose_version_differs_from_the_distribution()
    {
        byte[] manifest = _distribution.Module("example.mobs", version: "1.0.0").Manifest();
        var distribution = F.DistributionManifest.Parser.ParseFrom(manifest);
        distribution.Modules[0].Version = "1.1.0";

        var exception = Assert.Throws<ModuleCompositionException>(() => Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = distribution.ToByteArray(),
            ModuleSource = new FixedSource(_distribution),
            Paths = EnginePaths.At(_distribution.Root),
            LogToConsole = false,
            LogToFile = false,
        }));

        CompositionProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(CompositionProblemKind.InvalidManifest, problem.Kind);
        Assert.Contains("expects version 1.1.0", problem.Message);
    }

    [Fact]
    public void Refuses_an_unreadable_distribution_manifest()
    {
        var exception = Assert.Throws<ModuleCompositionException>(() => Engine.Load(new EngineOptions
        {
            Side = Side.Client,
            DistributionManifest = new byte[] { 0xFF, 0xFF, 0xFF },
            Paths = EnginePaths.At(_distribution.Root),
            LogToConsole = false,
            LogToFile = false,
        }));

        Assert.Equal(CompositionProblemKind.InvalidDistribution, Assert.Single(exception.Problems).Kind);
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        _distribution
            .Module("example.a", m => m.Requires("example.missing"))
            .Module("example.b", m => m.Conflicts.Add("example.a"));

        var exception = Assert.Throws<ModuleCompositionException>(() => _distribution.Load());

        Assert.Contains(exception.Problems, p => p.Kind == CompositionProblemKind.MissingDependency);
        Assert.Contains(exception.Problems, p => p.Kind == CompositionProblemKind.Conflict);
        Assert.Contains("example.missing", exception.Message);
    }

    [Fact]
    public void Loads_platform_modules_first_then_dependencies_before_dependents()
    {
        _distribution
            .Module("example.mobs", m => m.Requires("example.creatures"))
            .Module("example.creatures", m => m.Requires("example.physics"))
            .Module("example.loot")
            .Module("example.physics", m => m.Requires("example.time").Level = F.Level.Platform)
            .Module("example.time", m => m.Level = F.Level.Platform)
            .Module("example.particles", m => m.Level = F.Level.Business)
            .Module("example.sparks", m => m.Uses("example.particles"));

        using Engine engine = _distribution.Load();

        Assert.Equal(
            ["example.time", "example.physics", "example.creatures", "example.mobs", "example.loot", "example.particles", "example.sparks"],
            engine.Modules.Select(m => m.Id));
        Assert.Equal(engine.Modules.Select(m => m.Id), _distribution.Loaded);
    }

    // Returns the manifests of a test distribution without its bookkeeping.
    private sealed class FixedSource(TestDistribution distribution) : IModuleSource
    {
        public ModuleImage Discover(ExpectedModule module) =>
            new(module.Id, "memory:" + module.Id, new F.ModuleManifest { Format = 1, Id = module.Id, Version = "1.0.0", Level = F.Level.Business, Side = F.Side.Both }.ToByteArray());

        public LoadedModule Load(ModuleImage image)
        {
            distribution.Loaded.Add(image.Id);
            return new LoadedModule(null, null);
        }
    }
}
