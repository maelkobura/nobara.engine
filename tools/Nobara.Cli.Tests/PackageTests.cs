// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Cli.Tests;

/// <summary>Module packages, restored from a local feed, and the transitive modules they require.</summary>
public sealed class PackageTests : IDisposable
{
    private readonly GameFolder _game = new();

    public PackageTests()
    {
        Pack("example.colors", "1.0.0");
        Pack("example.colors", "1.1.0");
        Pack("example.lighting", "1.2.0", """<Dependencies><Module id="example.colors" version="^1.0" /></Dependencies>""");
        Pack("example.shades", "1.0.0", """<Dependencies><Module id="example.colors" version="&lt; 1.1" /></Dependencies>""");
        _game.Write("NuGet.config", "<configuration><packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources></configuration>");
        _game.Project("""<Local path="src/Example.Mobs" />""")
            .Module("src/Example.Mobs", "example.mobs", """<Dependencies><Module id="example.lighting" version="^1.2" /></Dependencies>""");
    }

    public void Dispose() => _game.Dispose();

    // A module package: an assembly named after the module that embeds its compiled manifest.
    private void Pack(string id, string version, string body = "")
    {
        string folder = Path.Combine(_game.Root, "packages", id + "-" + version);
        var diagnostics = new DiagnosticBag();
        ModuleManifest manifest = ModuleManifestReader.Read(
            $"""<NobaraModule format="1"><Id>{id}</Id><Version>{version}</Version><Level>platform</Level><Side>both</Side>{body}</NobaraModule>""",
            Path.Combine(folder, "module.nobara"),
            diagnostics)!;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "module.pb"), ManifestCompiler.Compile(manifest).ToByteArray());
        File.WriteAllText(Path.Combine(folder, "Package.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{id}</AssemblyName>
                <PackageId>{id}</PackageId>
                <Version>{version}</Version>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <EmbeddedResource Include="module.pb" LogicalName="nobara/module.pb" />
              </ItemGroup>
            </Project>
            """);
        (int exit, string output) = EngineFeed.Dotnet(folder, "pack", "-o", Path.Combine(_game.Root, "feed"), "--nologo");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Resolves_the_modules_that_packages_require_and_locks_their_versions()
    {
        CliResult result = _game.Run("generate");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("3 modules (1 local, 0 overrides, 2 packages)", result.Output, StringComparison.Ordinal);
        string lockFile = _game.Read("project.nobara.lock");
        Assert.Contains("""<Module id="example.lighting" version="1.2.0" source="package" transitive="true" />""", lockFile, StringComparison.Ordinal);
        Assert.Contains("""<Module id="example.colors" version="1.0.0" source="package" transitive="true" />""", lockFile, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_the_locked_version_while_it_satisfies_every_range()
    {
        _game.Write("project.nobara.lock", """
            <NobaraLock format="1">
              <Module id="example.colors" version="1.1.0" source="package" transitive="true" />
            </NobaraLock>
            """);

        CliResult result = _game.Run("generate");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("""<Module id="example.colors" version="1.1.0" source="package" transitive="true" />""", _game.Read("project.nobara.lock"), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_module_that_requires_another_version_than_the_resolved_one()
    {
        _game.Project("""<Local path="src/Example.Mobs" /><Package id="example.colors" version="~1.1" /><Package id="example.shades" version="^1.0" />""");

        CliResult result = _game.Run("generate");

        Assert.True(result.ExitCode == 1, result.ToString());
        Assert.Contains("error NRC202: 'example.shades' requires 'example.colors' < 1.1, but version 1.1.0 is already resolved", result.Errors, StringComparison.Ordinal);
    }
}
