// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text.Json;

namespace Nobara.Sdk.Tests;

/// <summary>Builds sample projects with the packed SDK.</summary>
public sealed class SdkBuildTests : IDisposable
{
    private readonly SdkWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static string[] Identities(IEnumerable<JsonElement> items) =>
        items.Select(i => i.GetProperty("Identity").GetString()!).ToArray();

    private static string VersionOf(JsonElement item) => item.GetProperty("Version").GetString()!;

    [Fact]
    public void Builds_a_module_from_a_project_file_that_only_names_the_sdk()
    {
        string project = _workspace.WriteModule(
            "Nobara.Sample",
            "<Id>nobara.sample</Id><Version>2.3.4</Version>",
            "namespace Nobara.Sample; public static class Marker { }");

        DotnetResult result = _workspace.Build(project);

        Assert.True(result.ExitCode == 0, result.Output);
        string assembly = Path.Combine(_workspace.Root, "Nobara.Sample", "bin", "Debug", "net10.0", "Nobara.Sample.dll");
        Assert.True(File.Exists(assembly), "The assembly is named after the module.");
        Assert.Equal(new Version(2, 3, 4, 0), System.Reflection.AssemblyName.GetAssemblyName(assembly).Version);
        Assert.Equal("2.3.4", FileVersionInfo.GetVersionInfo(assembly).ProductVersion);
    }

    [Fact]
    public void Takes_the_version_and_package_identity_from_the_manifest()
    {
        string project = _workspace.WriteModule("Nobara.Sample", "<Id>nobara.sample</Id><Version>1.4.0-beta.2</Version>");

        Evaluation evaluation = _workspace.Evaluate(project, ["Version", "PackageVersion", "AssemblyName", "PackageId", "NobaraProjectKind"], []);

        Assert.Equal("1.4.0-beta.2", evaluation.Property("Version"));
        Assert.Equal("1.4.0-beta.2", evaluation.Property("PackageVersion"));
        Assert.Equal("Nobara.Sample", evaluation.Property("AssemblyName"));
        Assert.Equal("Nobara.Sample", evaluation.Property("PackageId"));
        Assert.Equal("Module", evaluation.Property("NobaraProjectKind"));
    }

    [Fact]
    public void References_a_dependency_by_project_when_the_module_map_names_a_project()
    {
        string game = _workspace.WriteModule(
            "Nobara.Game",
            "<Id>nobara.game</Id><Version>1.0.0</Version>",
            "namespace Nobara.Game; public static class Clock { public static int Ticks => 20; }");
        string voxel = _workspace.WriteModule(
            "Nobara.Voxel",
            """<Id>nobara.voxel</Id><Version>1.0.0</Version><Dependencies><Module id="nobara.game" version="^1.0" /></Dependencies>""",
            "namespace Nobara.Voxel; public static class Grid { public static int Rate => Nobara.Game.Clock.Ticks; }");
        _workspace.MapModules(("nobara.game", game));

        Evaluation evaluation = _workspace.Evaluate(voxel, [], ["ProjectReference", "PackageReference"]);
        DotnetResult result = _workspace.Build(voxel);

        Assert.Equal([game], Identities(evaluation.Items("ProjectReference")));
        Assert.Empty(evaluation.Items("PackageReference"));
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void References_a_dependency_by_package_when_no_project_is_mapped()
    {
        string project = _workspace.WriteModule(
            "Nobara.Voxel",
            """
            <Id>nobara.voxel</Id><Version>1.0.0</Version>
            <Dependencies>
              <Module id="nobara.game" version="^1.2" />
              <Module id="nobara.blocks" version="&gt;= 2.0" />
            </Dependencies>
            """);

        Evaluation evaluation = _workspace.Evaluate(project, [], ["ProjectReference", "PackageReference"]);

        Assert.Empty(evaluation.Items("ProjectReference"));
        var packages = evaluation.Items("PackageReference");
        Assert.Equal(["nobara.game", "nobara.blocks"], Identities(packages));
        Assert.Equal(["[1.2,2.0.0)", "[2.0,)"], packages.Select(VersionOf));
    }

    [Fact]
    public void Mixes_local_and_package_dependencies_and_references_libraries()
    {
        string game = _workspace.WriteModule("Nobara.Game", "<Id>nobara.game</Id><Version>1.0.0</Version>");
        string project = _workspace.WriteModule(
            "Nobara.Voxel",
            """
            <Id>nobara.voxel</Id><Version>1.0.0</Version>
            <Dependencies>
              <Module id="NOBARA.GAME" version="^1.0" />
              <Module id="nobara.blocks" version="^1.0" />
            </Dependencies>
            <Libraries><Package id="K4os.Compression.LZ4" version="1.3.8" /></Libraries>
            """);
        _workspace.MapModules(("nobara.game", game));

        Evaluation evaluation = _workspace.Evaluate(project, [], ["ProjectReference", "PackageReference"]);

        Assert.Equal([game], Identities(evaluation.Items("ProjectReference")));
        var packages = evaluation.Items("PackageReference");
        Assert.Equal(["nobara.blocks", "K4os.Compression.LZ4"], Identities(packages));
        Assert.Equal(["[1.0,2.0.0)", "1.3.8"], packages.Select(VersionOf));
    }

    [Fact]
    public void Finds_the_module_map_in_a_parent_directory()
    {
        string game = _workspace.WriteModule("src/Game/Nobara.Game", "<Id>nobara.game</Id><Version>1.0.0</Version>");
        string voxel = _workspace.WriteModule(
            "src/Voxel/Nobara.Voxel",
            """<Id>nobara.voxel</Id><Version>1.0.0</Version><Dependencies><Module id="nobara.game" version="^1.0" /></Dependencies>""");
        _workspace.MapModules(("nobara.game", game));

        Evaluation evaluation = _workspace.Evaluate(voxel, [], ["ProjectReference"]);

        Assert.Equal([game], Identities(evaluation.Items("ProjectReference")));
    }

    [Fact]
    public void Uses_the_module_map_named_in_a_directory_build_props_file()
    {
        string game = _workspace.WriteModule("Nobara.Game", "<Id>nobara.game</Id><Version>1.0.0</Version>");
        string map = _workspace.Write(
            "maps/custom.props",
            $"""<Project><ItemGroup><NobaraLocalModule Include="nobara.game" Project="{game}" /></ItemGroup></Project>""");
        _workspace.Write("Directory.Build.props", $"<Project><PropertyGroup><NobaraModulesFile>{map}</NobaraModulesFile></PropertyGroup></Project>");
        string voxel = _workspace.WriteModule(
            "Nobara.Voxel",
            """<Id>nobara.voxel</Id><Version>1.0.0</Version><Dependencies><Module id="nobara.game" version="^1.0" /></Dependencies>""");

        Evaluation evaluation = _workspace.Evaluate(voxel, [], ["ProjectReference"]);

        Assert.Equal([game], Identities(evaluation.Items("ProjectReference")));
    }

    [Fact]
    public void Gives_the_optional_dependencies_to_the_compiler_as_a_build_property()
    {
        string lighting = _workspace.WriteModule(
            "Nobara.Lighting",
            "<Id>nobara.lighting</Id><Version>1.0.0</Version>",
            "namespace Nobara.Lighting; public static class Light { }");
        string voxel = _workspace.WriteModule(
            "Nobara.Voxel",
            """
            <Id>nobara.voxel</Id><Version>1.0.0</Version>
            <Dependencies>
              <Module id="nobara.lighting" version="^1.0" optional="true" />
              <Module id="nobara.game" version="^1.0" />
              <Module id="nobara.debug" version="^1.0" optional="true" />
            </Dependencies>
            """,
            "namespace Nobara.Voxel; public static class Grid { }");
        _workspace.MapModules(("nobara.lighting", lighting));

        Evaluation evaluation = _workspace.Evaluate(voxel, ["NobaraOptionalModules"], ["ProjectReference", "CompilerVisibleProperty"]);

        Assert.Equal("nobara.lighting;nobara.debug", evaluation.Property("NobaraOptionalModules"));
        Assert.Equal([lighting], Identities(evaluation.Items("ProjectReference")));
        Assert.Contains("NobaraOptionalModules", Identities(evaluation.Items("CompilerVisibleProperty")));
    }

    [Fact]
    public void Passes_the_optional_dependencies_to_analyzers_through_the_analyzer_config()
    {
        string lighting = _workspace.WriteModule("Nobara.Lighting", "<Id>nobara.lighting</Id><Version>1.0.0</Version>");
        string voxel = _workspace.WriteModule(
            "Nobara.Voxel",
            """<Id>nobara.voxel</Id><Version>1.0.0</Version><Dependencies><Module id="nobara.lighting" version="^1.0" optional="true" /></Dependencies>""",
            "namespace Nobara.Voxel; public static class Grid { }");
        _workspace.MapModules(("nobara.lighting", lighting));

        DotnetResult result = _workspace.Build(voxel);

        Assert.True(result.ExitCode == 0, result.Output);
        string config = Directory.EnumerateFiles(Path.Combine(_workspace.Root, "Nobara.Voxel", "obj"), "*.GeneratedMSBuildEditorConfig.editorconfig", SearchOption.AllDirectories).Single();
        Assert.Contains("build_property.NobaraOptionalModules = nobara.lighting", File.ReadAllText(config));
    }

    [Fact]
    public void Treats_an_extension_as_a_module_with_its_own_manifest()
    {
        string voxel = _workspace.WriteModule("Nobara.Voxel", "<Id>nobara.voxel</Id><Version>1.0.0</Version>", "namespace Nobara.Voxel; public static class Grid { }");
        string renderer = _workspace.WriteModule(
            "Nobara.Voxel.Renderer",
            """<Id>nobara.voxel.renderer</Id><Version>1.1.0</Version><Dependencies><Module id="nobara.voxel" version="^1.0" /></Dependencies>""",
            "namespace Nobara.Voxel.Renderer; public static class Mesher { public static object Grid => typeof(Nobara.Voxel.Grid); }");
        _workspace.MapModules(("nobara.voxel", voxel));

        Evaluation evaluation = _workspace.Evaluate(renderer, ["NobaraProjectKind", "AssemblyName", "Version"], ["ProjectReference"]);
        DotnetResult result = _workspace.Build(renderer);

        Assert.Equal("Module", evaluation.Property("NobaraProjectKind"));
        Assert.Equal("Nobara.Voxel.Renderer", evaluation.Property("AssemblyName"));
        Assert.Equal("1.1.0", evaluation.Property("Version"));
        Assert.Equal([voxel], Identities(evaluation.Items("ProjectReference")));
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void Requires_a_manifest_for_an_extension()
    {
        string renderer = _workspace.WriteAuxiliary("Nobara.Voxel.Renderer");

        string output = _workspace.EvaluateFailure(renderer);

        Assert.Contains("NSDK001", output);
    }

    [Fact]
    public void Builds_a_test_project_without_a_manifest_that_references_its_module()
    {
        string voxel = _workspace.WriteModule("Nobara.Voxel", "<Id>nobara.voxel</Id><Version>1.0.0</Version>", "namespace Nobara.Voxel; public static class Grid { public static int Size => 16; }");
        string tests = _workspace.WriteAuxiliary(
            "Nobara.Voxel.Tests",
            """<ItemGroup><ProjectReference Include="..\Nobara.Voxel\Nobara.Voxel.csproj" /></ItemGroup>""",
            "namespace Nobara.Voxel.Tests; public static class Check { public static int Size => Nobara.Voxel.Grid.Size; }");

        Evaluation evaluation = _workspace.Evaluate(tests, ["NobaraProjectKind", "IsPackable", "TargetFramework", "Version"], ["ProjectReference"]);
        DotnetResult result = _workspace.Build(tests);

        Assert.Equal("Auxiliary", evaluation.Property("NobaraProjectKind"));
        Assert.Equal("false", evaluation.Property("IsPackable"));
        Assert.Equal("net10.0", evaluation.Property("TargetFramework"));
        Assert.Equal("1.0.0", evaluation.Property("Version"));
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Theory]
    [InlineData("Nobara.Blocks.Build")]
    [InlineData("Nobara.Network.Generators")]
    [InlineData("Nobara.World.Analyzers")]
    [InlineData("Nobara.Network.Scripting")]
    [InlineData("Nobpen.TypeGen")]
    [InlineData("Nobara.Voxel.Benchmarks")]
    [InlineData("Nobara.Voxel.Renderer.Tests")]
    public void Recognizes_every_reserved_suffix_as_an_auxiliary_project(string name)
    {
        string project = _workspace.WriteAuxiliary(name);

        Evaluation evaluation = _workspace.Evaluate(project, ["NobaraProjectKind"], []);

        Assert.Equal("Auxiliary", evaluation.Property("NobaraProjectKind"));
    }

    [Fact]
    public void Ignores_a_manifest_next_to_an_auxiliary_project()
    {
        string project = _workspace.WriteAuxiliary("Nobara.Voxel.Tests");
        _workspace.Write("Nobara.Voxel.Tests/module.nobara", "not even xml");

        Evaluation evaluation = _workspace.Evaluate(project, ["NobaraProjectKind"], []);

        Assert.Equal("Auxiliary", evaluation.Property("NobaraProjectKind"));
    }

    [Fact]
    public void Lets_the_project_file_set_ordinary_properties()
    {
        string project = _workspace.WriteModule(
            "Nobara.Sample",
            "<Id>nobara.sample</Id><Version>1.0.0</Version>",
            projectBody: "<PropertyGroup><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>");

        Evaluation evaluation = _workspace.Evaluate(project, ["TargetFramework", "Nullable"], []);

        Assert.Equal("net9.0", evaluation.Property("TargetFramework"));
        Assert.Equal("enable", evaluation.Property("Nullable"));
    }

    [Fact]
    public void Runs_the_extension_points_in_order_without_changing_the_build()
    {
        string project = _workspace.WriteModule(
            "Nobara.Sample",
            "<Id>nobara.sample</Id><Version>1.0.0</Version>",
            "namespace Nobara.Sample; public static class Marker { }");

        DotnetResult result = _workspace.Dotnet("build", project, "--nologo", "-nodeReuse:false", "-v:detailed");

        Assert.True(result.ExitCode == 0, result.Output);
        int analyzers = result.Output.IndexOf("Target \"NobaraAnalyzers", StringComparison.Ordinal);
        int schemas = result.Output.IndexOf("Target \"NobaraSchemas", StringComparison.Ordinal);
        int resources = result.Output.IndexOf("Target \"NobaraResources", StringComparison.Ordinal);
        int compile = result.Output.IndexOf("Target \"CoreCompile", StringComparison.Ordinal);
        Assert.InRange(analyzers, 0, schemas - 1);
        Assert.InRange(schemas, 0, resources - 1);
        Assert.InRange(resources, 0, compile - 1);
    }

    [Fact]
    public void Reports_a_missing_manifest()
    {
        string project = _workspace.WriteAuxiliary("Nobara.Sample");

        string output = _workspace.EvaluateFailure(project);

        Assert.Contains("module.nobara", output);
        Assert.Contains("error NSDK001", output);
    }

    [Fact]
    public void Reports_a_missing_id_and_a_missing_version()
    {
        string noId = _workspace.WriteModule("Nobara.NoId", "<Version>1.0.0</Version>");
        string noVersion = _workspace.WriteModule("Nobara.NoVersion", "<Id>nobara.noversion</Id>");

        Assert.Contains("error NRC102", _workspace.EvaluateFailure(noId));
        Assert.Contains("error NRC102", _workspace.EvaluateFailure(noVersion));
    }

    [Fact]
    public void Reports_an_invalid_version_range_with_the_file_and_line()
    {
        string project = _workspace.WriteModule(
            "Nobara.Sample",
            "\n<Id>nobara.sample</Id><Version>1.0.0</Version>\n<Dependencies>\n<Module id=\"nobara.game\" version=\"soon\" />\n</Dependencies>\n");

        string output = _workspace.EvaluateFailure(project);

        Assert.Contains("module.nobara(4,", output);
        Assert.Contains("error NRC106", output);
        Assert.Contains("nobara.game", output);
    }

    [Fact]
    public void Reports_an_id_that_does_not_match_the_project_name()
    {
        string project = _workspace.WriteModule("Nobara.Sample", "<Id>nobara.other</Id><Version>1.0.0</Version>");

        string output = _workspace.EvaluateFailure(project);

        Assert.Contains("error NSDK005", output);
    }

    [Fact]
    public void Reports_a_manifest_that_is_not_well_formed()
    {
        string project = _workspace.WriteModule("Nobara.Sample", "<Id>nobara.sample</Id><Version>1.0.0");

        string output = _workspace.EvaluateFailure(project);

        Assert.Contains("error NRC100", output);
    }
}
