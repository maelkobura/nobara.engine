// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Nobara.Sdk.Tests;

/// <summary>Builds sample modules with schemas, resources, and plugins through the SDK.</summary>
public sealed class ResourceBuildTests : IDisposable
{
    private const string GemSchema = """
        syntax = "proto3";
        package sample.gems;
        import "nobara/options.proto";
        option csharp_namespace = "Sample.Gems.Schemas";

        message Gem {
          option (nobara.resource_type) = "gem";
          option (nobara.accepts_tags) = true;
          string display_name = 1 [(nobara.i18n) = true, (nobara.required) = true];
          int32 hardness = 2 [(nobara.min) = 0, (nobara.max) = 10];
          string icon = 3 [(nobara.resource) = "texture"];
        }

        message Texture {
          option (nobara.resource_type) = "texture";
          string file = 1 [(nobara.file) = true, (nobara.required) = true];
        }
        """;

    private const string GemResources = """
        <Nobara version="1.0.0" namespace="Sample/Gems">
          <Gem>
            <Name>Ruby</Name>
            <Content>
              <DisplayName>gem.ruby</DisplayName>
              <Hardness>9</Hardness>
              <Icon>texture://Sample/Gems/RubyIcon</Icon>
            </Content>
            <Tags><Tag>#sample.precious</Tag></Tags>
          </Gem>
          <Texture><Name>RubyIcon</Name><Content><File>ruby.png</File></Content></Texture>
        </Nobara>
        """;

    private readonly SdkWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string WriteGems()
    {
        string project = _workspace.WriteModule(
            "Sample.Gems",
            "<Id>sample.gems</Id><Version>1.0.0</Version><Level>business</Level>",
            "namespace Sample.Gems; public static class Marker { public static object Gem => typeof(Sample.Gems.Schemas.Gem); }");
        _workspace.Write("Sample.Gems/Schemas/gem.proto", GemSchema);
        _workspace.Write("Sample.Gems/Resources/gems.nof", GemResources);
        _workspace.Write("Sample.Gems/Resources/ruby.png", "not really a png");
        return project;
    }

    private static string[] ManifestResources(string assembly)
    {
        using FileStream stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        return metadata.ManifestResources.Select(h => metadata.GetString(metadata.GetManifestResource(h).Name)).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Builds_a_module_with_schemas_and_resources_and_embeds_them()
    {
        string project = WriteGems();

        DotnetResult result = _workspace.Build(project);

        Assert.True(result.ExitCode == 0, result.Output);
        string assembly = Path.Combine(_workspace.Root, "Sample.Gems", "bin", "Debug", "net10.0", "Sample.Gems.dll");
        Assert.Equal(
            ["nobara/files/ruby.png", "nobara/module.pb", "nobara/resources.pb", "nobara/schemas.pb"],
            ManifestResources(assembly));
    }

    [Fact]
    public void Embeds_the_manifest_of_a_module_without_resources()
    {
        string project = _workspace.WriteModule("Nobara.Sample", "<Id>nobara.sample</Id><Version>1.0.0</Version>", "namespace Nobara.Sample; public static class Marker { }");

        DotnetResult result = _workspace.Build(project);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
            ["nobara/module.pb", "nobara/resources.pb"],
            ManifestResources(Path.Combine(_workspace.Root, "Nobara.Sample", "bin", "Debug", "net10.0", "Nobara.Sample.dll")));
    }

    [Fact]
    public void Fails_the_build_on_an_invalid_resource_with_its_file_line_and_column()
    {
        string project = WriteGems();
        _workspace.Write("Sample.Gems/Resources/broken.nof", "<Nobara version=\"1.0.0\" namespace=\"Sample/Gems\">\n  <Gem>\n    <Name>Opal</Name>\n    <Content><DisplayName>gem.opal</DisplayName><Hardness>11</Hardness></Content>\n  </Gem>\n</Nobara>");

        DotnetResult result = _workspace.Build(project);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("broken.nof(4,", result.Output);
        Assert.Contains("error NRC310", result.Output);
    }

    [Fact]
    public void Validates_the_whole_module_manifest()
    {
        string project = _workspace.WriteModule("Nobara.Sample", "<Id>nobara.sample</Id><Version>1.0.0</Version><Settings><Setting key=\"other.size\" scope=\"user\" type=\"int\" default=\"1\" /></Settings>");

        Assert.Contains("error NRC108", _workspace.EvaluateFailure(project));
    }

    [Fact]
    public void Resolves_references_to_a_dependency_and_loads_its_plugin()
    {
        string gems = WriteGems();
        _workspace.Write("Sample.Gems.Build/Sample.Gems.Build.csproj", $$"""
            {{SdkWorkspace.ProjectOpen}}
              <ItemGroup>
                <ProjectReference Include="../Sample.Gems/Sample.Gems.csproj" />
                <Reference Include="$([MSBuild]::EnsureTrailingSlash('$(NuGetPackageRoot)'))nobara.sdk/{{SdkWorkspace.SdkVersion}}/tasks/net10.0/Nobara.ResourceCompiler.dll" Private="false" />
              </ItemGroup>
            </Project>
            """);
        _workspace.Write("Sample.Gems.Build/GemPlugin.cs", """
            using System.Collections.Generic;
            using Nobara.ResourceCompiler.Plugins;

            [assembly: ResourcePlugin(typeof(Sample.Gems.Build.GemPlugin))]

            namespace Sample.Gems.Build;

            public sealed class GemPlugin : ResourcePlugin
            {
                public override IReadOnlyCollection<string> ResourceTypes => ["Gem"];

                public override void Validate(ResourceContext context)
                {
                    if (context.Parse(Sample.Gems.Schemas.Gem.Parser).Hardness == 7)
                    {
                        context.Error("GEM001", "hardness 7 is reserved");
                    }
                }
            }
            """);
        string shop = _workspace.WriteModule(
            "Sample.Shop",
            """<Id>sample.shop</Id><Version>1.0.0</Version><Level>business</Level><Dependencies><Module id="sample.gems" version="^1.0" /></Dependencies>""");
        _workspace.Write("Sample.Shop/Resources/shop.nof", """
            <Nobara version="1.0.0" namespace="Sample/Shop">
              <Gem><Name>Emerald</Name><Content><DisplayName>gem.emerald</DisplayName><Hardness>7</Hardness><Icon>texture://Sample/Gems/RubyIcon</Icon></Content></Gem>
              <Gem override="true" optional="true"><Name>Ruby</Name><Content><Hardness>10</Hardness></Content></Gem>
              <Tag registry="gem"><Name>Precious</Name><Content><Member>gem:sample.shop.emerald</Member></Content></Tag>
            </Nobara>
            """);
        _workspace.MapModules(("sample.gems", gems));

        DotnetResult failed = _workspace.Build(shop);
        _workspace.Write("Sample.Shop/Resources/shop.nof", File.ReadAllText(Path.Combine(_workspace.Root, "Sample.Shop/Resources/shop.nof")).Replace("<Hardness>7</Hardness>", "<Hardness>8</Hardness>", StringComparison.Ordinal));
        DotnetResult built = _workspace.Build(shop);

        Assert.True(failed.Output.Contains("error GEM001", StringComparison.Ordinal), failed.Output);
        Assert.Contains("shop.nof(2,", failed.Output);
        Assert.True(built.ExitCode == 0, built.Output);
    }

    [Fact]
    public void Compiles_nothing_again_when_nothing_changed()
    {
        string project = WriteGems();
        DotnetResult first = _workspace.Build(project);

        DotnetResult second = _workspace.Dotnet("build", project, "--nologo", "-nodeReuse:false", "-v:detailed");

        Assert.True(first.ExitCode == 0, first.Output);
        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Contains("Skipping target \"NobaraCompileResources\" because all output files are up-to-date", second.Output);
        Assert.Contains("Skipping target \"CoreCompile\"", second.Output);
    }
}
