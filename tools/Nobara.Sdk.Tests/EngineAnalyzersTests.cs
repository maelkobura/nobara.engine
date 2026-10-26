// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Sdk.Tests;

/// <summary>Builds sample projects to check that the SDK attaches the engine analyzers.</summary>
public sealed class EngineAnalyzersTests : IDisposable
{
    private const string MutableState = "namespace Nobara.Sample; public static class Counter { public static int Count; }";

    private const string IsolationAttribute = """
        namespace Nobara.Core
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
            internal sealed class UsesOptionalDependencyAttribute : System.Attribute
            {
                public UsesOptionalDependencyAttribute(string moduleId) { }
            }
        }
        """;

    private readonly SdkWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Checks_a_module_without_any_declaration()
    {
        string project = _workspace.WriteModule("Nobara.Sample", "<Id>nobara.sample</Id><Version>1.0.0</Version>", MutableState);

        DotnetResult result = _workspace.Build(project);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error NOB201", result.Output);
    }

    [Theory]
    [InlineData("Nobara.Sample.Scripting", true)]
    [InlineData("Nobara.Sample.Tests", false)]
    [InlineData("Nobara.Sample.Benchmarks", false)]
    [InlineData("Nobara.Sample.Build", false)]
    public void Checks_script_facades_but_not_tests_benchmarks_or_build_tools(string name, bool checkedByAnalyzers)
    {
        string project = _workspace.WriteAuxiliary(name, source: MutableState);

        DotnetResult result = _workspace.Build(project);

        Assert.Equal(checkedByAnalyzers, result.Output.Contains("error NOB201", StringComparison.Ordinal));
        Assert.Equal(checkedByAnalyzers, result.ExitCode != 0);
    }

    [Theory]
    [InlineData("class Renderer { Nobara.Lighting.Light _light; }", false)]
    [InlineData("[Nobara.Core.UsesOptionalDependency(\"nobara.lighting\")] class LightingBridge { Nobara.Lighting.Light _light; }", true)]
    public void Confines_an_optional_dependency_declared_in_the_manifest(string code, bool builds)
    {
        string lighting = _workspace.WriteModule(
            "Nobara.Lighting",
            "<Id>nobara.lighting</Id><Version>1.0.0</Version>",
            "namespace Nobara.Lighting; public sealed class Light { }");
        string voxel = _workspace.WriteModule(
            "Nobara.Voxel",
            """<Id>nobara.voxel</Id><Version>1.0.0</Version><Dependencies><Module id="nobara.lighting" version="^1.0" optional="true" /></Dependencies>""",
            IsolationAttribute + code);
        _workspace.MapModules(("nobara.lighting", lighting));

        DotnetResult result = _workspace.Build(voxel);

        Assert.True(builds == (result.ExitCode == 0), result.Output);
        Assert.Equal(!builds, result.Output.Contains("error NOB401", StringComparison.Ordinal));
    }
}
