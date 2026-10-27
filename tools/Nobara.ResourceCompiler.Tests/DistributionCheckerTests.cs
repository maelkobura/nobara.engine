// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler.Distribution;
using Nobara.ResourceCompiler.Manifests;
using F = Nobara.Formats;

namespace Nobara.ResourceCompiler.Tests;

/// <summary>Checks of a whole distribution, on sets of manifests.</summary>
public sealed class DistributionCheckerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "nobara-distribution");

    private readonly List<ModuleManifest> _modules = [];
    private readonly List<string> _entries = [];
    private string _engine = string.Empty;
    private string? _build;

    /// <summary>Adds a module listed as a package of the project.</summary>
    private DistributionCheckerTests Module(string id, string body = "", string level = "platform", string side = "both", string version = "1.0.0", bool required = false, bool listed = true)
    {
        string text = $"<NobaraModule format=\"1\"><Id>{id}</Id><Version>{version}</Version><Level>{level}</Level><Side>{side}</Side>{body}</NobaraModule>";
        var diagnostics = new DiagnosticBag();
        ModuleManifest? manifest = ModuleManifestReader.Read(text, Path.Combine(Root, id, "module.nobara"), diagnostics);
        Assert.True(manifest is not null, string.Join(Environment.NewLine, diagnostics.Items));
        _modules.Add(manifest);
        if (listed)
        {
            _entries.Add($"<Package id=\"{id}\" version=\">= 0.0\" required=\"{(required ? "true" : "false")}\" />");
        }

        return this;
    }

    private static string Depends(string id, string range = "^1.0", bool optional = false) =>
        $"<Dependencies><Module id=\"{id}\" version=\"{range}\" optional=\"{(optional ? "true" : "false")}\" /></Dependencies>";

    private DistributionCheckResult Check(string? target = null)
    {
        var diagnostics = new DiagnosticBag();
        ProjectManifest project = ProjectManifestReader.Read(
            $"<NobaraProject format=\"1\"><Id>example</Id><Name>Example</Name><Version>1.0.0</Version><Modules>{string.Concat(_entries)}</Modules></NobaraProject>",
            Path.Combine(Root, "project.nobara"),
            diagnostics)!;
        EngineManifest engine = EngineManifestReader.Read($"<NobaraEngine format=\"1\">{_engine}</NobaraEngine>", Path.Combine(Root, "engine.nobara"), diagnostics)!;
        BuildManifest? build = _build is null ? null : BuildManifestReader.Read($"<NobaraBuild format=\"1\">{_build}</NobaraBuild>", Path.Combine(Root, "build.nobara"), diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items));
        return DistributionChecker.Check(new DistributionInput { Project = project, Engine = engine, Build = build, Target = target, Modules = _modules });
    }

    private static void Refuses(DistributionCheckResult result, string code, params string[] words)
    {
        Diagnostic error = Expect.Error(result.Diagnostics, code);
        foreach (string word in words)
        {
            Assert.Contains(word, error.Message);
        }
    }

    [Fact]
    public void Accepts_a_coherent_distribution_and_orders_its_modules()
    {
        Module("nobara.game")
            .Module("nobara.blocks", Depends("nobara.game"))
            .Module("example.mobs", Depends("nobara.blocks"), level: "business")
            .Module("example.zombie", Depends("example.mobs") + "<States><State id=\"state:example.boot\" /></States>", level: "business", required: true);
        _engine = "<States client=\"state:example.boot\" server=\"state:example.boot\" />";

        DistributionCheckResult result = Check();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(["nobara.game", "nobara.blocks", "example.mobs", "example.zombie"], result.ActiveModules.Select(m => m.Id));
        Assert.Contains("example.zombie", result.RequiredModules);
    }

    [Fact]
    public void Refuses_a_dependency_cycle()
    {
        Module("nobara.a", Depends("nobara.b")).Module("nobara.b", Depends("nobara.c")).Module("nobara.c", Depends("nobara.a", optional: true));

        Refuses(Check(), "NRC204", "nobara.a -> nobara.b -> nobara.c -> nobara.a");
    }

    [Fact]
    public void Refuses_a_missing_required_dependency_but_not_a_missing_optional_one()
    {
        Module("nobara.a", "<Dependencies><Module id=\"nobara.missing\" version=\"^1.0\" /><Module id=\"nobara.lighting\" version=\"^1.0\" optional=\"true\" /></Dependencies>");

        DistributionCheckResult result = Check();

        Refuses(result, "NRC201", "nobara.a", "nobara.missing");
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("nobara.lighting", StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_a_required_dependency_disabled_by_the_target()
    {
        Module("nobara.a", Depends("nobara.b")).Module("nobara.b");
        _build = "<Target name=\"server\"><Platform>win-x64</Platform><Side>server</Side><Disable module=\"nobara.b\" /></Target>";

        Refuses(Check("server"), "NRC201", "nobara.a", "nobara.b", "server");
    }

    [Fact]
    public void Refuses_a_required_dependency_that_does_not_run_on_a_side()
    {
        Module("nobara.a", Depends("nobara.b")).Module("nobara.b", side: "client");

        Refuses(Check(), "NRC201", "server side");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refuses_a_dependency_outside_its_version_range(bool optional)
    {
        Module("nobara.a", Depends("nobara.b", "^2.0", optional)).Module("nobara.b", version: "1.4.0");

        Refuses(Check(), "NRC202", "nobara.b", "1.4.0");
    }

    [Fact]
    public void Refuses_a_platform_module_that_depends_on_a_business_module()
    {
        Module("nobara.a", Depends("example.b", optional: true)).Module("example.b", level: "business");

        Refuses(Check(), "NRC203", "nobara.a", "example.b");
    }

    [Fact]
    public void Refuses_a_conflict_between_active_modules()
    {
        Module("nobara.a", "<Conflicts><Module id=\"nobara.b\" /></Conflicts>").Module("nobara.b");

        Refuses(Check(), "NRC205", "nobara.a", "nobara.b");
    }

    [Fact]
    public void Accepts_a_conflict_with_a_module_disabled_by_the_target()
    {
        Module("nobara.a", "<Conflicts><Module id=\"nobara.b\" /></Conflicts>").Module("nobara.b");
        _build = "<Target name=\"client\"><Platform>win-x64</Platform><Side>client</Side><Disable module=\"nobara.b\" /></Target>";

        Assert.True(Check("client").Success);
    }

    [Fact]
    public void Ships_both_sides_in_a_client_target_and_the_server_side_in_a_server_target()
    {
        Module("nobara.game")
            .Module("nobara.renderer", Depends("nobara.game"), side: "client")
            .Module("nobara.saves", Depends("nobara.game"), side: "server");
        _build = """
            <Target name="client"><Platform>win-x64</Platform><Side>client</Side></Target>
            <Target name="server"><Platform>win-x64</Platform><Side>server</Side></Target>
            """;

        DistributionCheckResult client = Check("client");
        DistributionCheckResult server = Check("server");

        Assert.True(client.Success && server.Success, string.Join(Environment.NewLine, client.Diagnostics.Concat(server.Diagnostics)));
        Assert.Equal(["nobara.game", "nobara.renderer", "nobara.saves"], client.ActiveModules.Select(m => m.Id));
        Assert.Equal(["nobara.game", "nobara.saves"], server.ActiveModules.Select(m => m.Id));
    }

    [Fact]
    public void Checks_the_server_side_of_a_client_target()
    {
        Module("nobara.saves", Depends("nobara.storage"), side: "server").Module("nobara.storage", side: "client");
        _build = "<Target name=\"client\"><Platform>win-x64</Platform><Side>client</Side></Target>";

        Refuses(Check("client"), "NRC201", "nobara.saves", "nobara.storage", "server side of target 'client'");
    }

    [Fact]
    public void Refuses_a_disputed_reservation()
    {
        Module("nobara.a", "<Reserves><Resource name=\"window.main\" /></Reserves>").Module("nobara.b", "<Reserves><Resource name=\"window.main\" /></Reserves>");

        Refuses(Check(), "NRC206", "window.main", "nobara.a", "nobara.b");
    }

    [Fact]
    public void Refuses_a_disabled_or_logged_module_required_by_a_module_that_stops()
    {
        Module("example.stats", level: "business").Module("example.hud", Depends("example.stats"), level: "business");
        _engine = "<ErrorPolicy default=\"stop\"><Module id=\"example.stats\" policy=\"disable\" /></ErrorPolicy>";

        Refuses(Check(), "NRC215", "example.stats", "example.hud");
    }

    [Fact]
    public void Accepts_a_cascade_whose_dependents_are_disabled_too()
    {
        Module("example.stats", level: "business").Module("example.hud", Depends("example.stats"), level: "business");
        _engine = "<ErrorPolicy default=\"disable\" />";

        Assert.True(Check().Success);
    }

    [Theory]
    [InlineData(true, "business")]
    [InlineData(false, "platform")]
    public void Refuses_a_policy_other_than_stop_for_a_required_or_platform_module(bool required, string level)
    {
        Module("example.core", level: level, required: required);
        _engine = "<ErrorPolicy><Module id=\"example.core\" policy=\"log\" /></ErrorPolicy>";

        Refuses(Check(), "NRC214", "example.core");
    }

    [Fact]
    public void Refuses_a_base_module_without_a_backend_where_it_requires_one()
    {
        Module("nobara.audio", "<RequiresBackend side=\"client\" />")
            .Module("nobara.audio.openal", Depends("nobara.audio") + "<BackendOf module=\"nobara.audio\" />", side: "client");
        _build = """
            <Target name="client"><Platform>win-x64</Platform><Side>client</Side><Disable module="nobara.audio.openal" /></Target>
            <Target name="server"><Platform>win-x64</Platform><Side>server</Side></Target>
            """;

        Refuses(Check("client"), "NRC211", "nobara.audio", "client");
        Assert.True(Check("server").Success);
    }

    [Fact]
    public void Accepts_a_base_module_with_an_active_backend()
    {
        Module("nobara.audio", "<RequiresBackend side=\"client\" />")
            .Module("nobara.audio.openal", Depends("nobara.audio") + "<BackendOf module=\"nobara.audio\" />", side: "client");

        Assert.True(Check().Success, string.Join(Environment.NewLine, Check().Diagnostics));
    }

    [Fact]
    public void Refuses_an_initial_state_that_no_active_module_provides()
    {
        Module("example.menu", "<States><State id=\"state:example.menu\" side=\"client\" /></States>", level: "business");
        _engine = "<States client=\"state:example.menu\" server=\"state:example.menu\" />";

        DistributionCheckResult result = Check();

        Refuses(result, "NRC210", "server", "example.menu");
        Assert.Single(result.Diagnostics, d => d.Code == "NRC210");
    }

    [Fact]
    public void Refuses_a_state_declared_twice()
    {
        Module("example.a", "<States><State id=\"state:example.menu\" /></States>", level: "business")
            .Module("example.b", "<States><State id=\"state:example.menu\" /></States>", level: "business");

        Refuses(Check(), "NRC209", "state:example.menu", "example.a", "example.b");
    }

    [Theory]
    [InlineData("<Set key=\"blocks.maxStates\" value=\"2048\" />", null)]
    [InlineData("<Set key=\"blocks.unknown\" value=\"1\" />", "NRC212")]
    [InlineData("<Set key=\"blocks.maxstates\" value=\"1\" />", "NRC212")]
    [InlineData("<Set key=\"blocks.maxStates\" value=\"many\" />", "NRC213")]
    [InlineData("<Set key=\"blocks.maxStates\" value=\"99999\" />", "NRC213")]
    [InlineData("<Set key=\"blocks.viewRadius\" value=\"64\" />", "NRC213")]
    [InlineData("<Default key=\"blocks.maxStates\" value=\"64\" />", "NRC213")]
    [InlineData("<Lock key=\"blocks.viewRadius\" value=\"64\" />", null)]
    [InlineData("<Default key=\"blocks.mode\" value=\"fancy\" />", null)]
    [InlineData("<Default key=\"blocks.mode\" value=\"ugly\" />", "NRC213")]
    public void Checks_the_configuration_values_of_the_engine_file(string setting, string? code)
    {
        Module("nobara.blocks", """
            <ShortName>Blocks</ShortName>
            <Settings>
              <Setting key="blocks.maxStates" scope="distribution" type="int" min="1" max="4096" default="1024" />
              <Setting key="blocks.viewRadius" scope="user" type="int" default="128" />
              <Setting key="blocks.mode" scope="user" type="enum" values="fast,fancy" default="fast" />
            </Settings>
            """);
        _engine = $"<Settings>{setting}</Settings>";

        DistributionCheckResult result = Check();

        if (code is null)
        {
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        }
        else
        {
            Expect.Error(result.Diagnostics, code);
        }
    }

    [Fact]
    public void Refuses_a_short_name_or_a_key_declared_twice()
    {
        Module("nobara.a", "<ShortName>Motion</ShortName><Settings><Setting key=\"motion.speed\" scope=\"user\" type=\"int\" default=\"1\" /></Settings>")
            .Module("nobara.b", "<ShortName>Motion</ShortName><Settings><Setting key=\"motion.speed\" scope=\"user\" type=\"int\" default=\"1\" /></Settings>");

        DistributionCheckResult result = Check();

        Refuses(result, "NRC207", "Motion");
        Refuses(result, "NRC208", "motion.speed");
    }

    [Fact]
    public void Refuses_a_module_config_or_policy_meant_for_an_absent_module()
    {
        Module("nobara.game");
        _engine = "<ErrorPolicy><Module id=\"example.ghost\" policy=\"log\" /></ErrorPolicy><ModuleConfig module=\"nobara.physics\"><Step /></ModuleConfig>";

        DistributionCheckResult result = Check();

        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "NRC216"));
    }

    [Fact]
    public void Refuses_an_entry_without_manifest_and_a_required_module_disabled_by_the_target()
    {
        Module("nobara.a", required: true);
        _entries.Add("<Package id=\"nobara.ghost\" version=\"^1.0\" />");
        _build = "<Target name=\"client\"><Platform>win-x64</Platform><Side>client</Side><Disable module=\"nobara.a\" /></Target>";

        DistributionCheckResult result = Check("client");

        Refuses(result, "NRC200", "nobara.ghost");
        Refuses(result, "NRC217", "nobara.a");
    }

    [Fact]
    public void Matches_a_local_module_by_the_folder_of_its_manifest()
    {
        Module("example.mobs", level: "business", listed: false);
        _entries.Add("<Local path=\"example.mobs\" required=\"true\" />");

        DistributionCheckResult result = Check();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("example.mobs", result.RequiredModules);
    }

    [Fact]
    public void Refuses_an_unknown_target()
    {
        Module("nobara.a");
        _build = "<Target name=\"client\"><Platform>win-x64</Platform><Side>client</Side></Target>";

        Refuses(Check("server"), "NRC217", "server");
    }

    [Fact]
    public void Compiles_the_distribution_manifest()
    {
        Module("nobara.game", required: true);
        _engine = """
            <Loop tickRate="20"><Phase name="Simulation" fixed="true" /><Phase name="Render" side="client" /></Loop>
            <ModuleConfig module="nobara.game"><Stage name="example.combat" after="Entities" /></ModuleConfig>
            """;
        DistributionCheckResult result = Check();
        var diagnostics = new DiagnosticBag();
        ProjectManifest project = ProjectManifestReader.Read($"<NobaraProject format=\"1\"><Id>example</Id><Name>Example</Name><Version>1.0.0</Version><Modules>{string.Concat(_entries)}</Modules></NobaraProject>", Path.Combine(Root, "project.nobara"), diagnostics)!;
        EngineManifest engine = EngineManifestReader.Read($"<NobaraEngine format=\"1\">{_engine}</NobaraEngine>", Path.Combine(Root, "engine.nobara"), diagnostics)!;

        F.DistributionManifest compiled = ManifestCompiler.Compile(project, engine, result.ActiveModules, result.RequiredModules, new Dictionary<string, byte[]> { ["nobara.game"] = [1, 2, 3] }, Root);

        F.DistributionModule module = compiled.Modules.Single();
        Assert.True(module.Required);
        Assert.Equal([1, 2, 3], module.Hash.ToByteArray());
        Assert.Equal(20u, compiled.Loop.TickRate);
        Assert.Equal(F.Side.Client, compiled.Loop.Phases[1].Side);
        F.ModuleConfig config = compiled.ModuleConfigs.Single();
        Assert.Equal("Stage", config.Config.Children.Single().Name);
        Assert.Equal("engine.nobara", config.Config.Source.File);
        Assert.Equal(2u, config.Config.Source.Line);
    }
}
