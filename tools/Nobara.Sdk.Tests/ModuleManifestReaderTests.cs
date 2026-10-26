// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Sdk.Tasks;

namespace Nobara.Sdk.Tests;

public class ModuleManifestReaderTests
{
    private const string Full = """
        <NobaraModule format="1">
          <Id>nobara.voxel</Id>
          <ShortName>Voxel</ShortName>
          <Version>1.2.0</Version>
          <Level>platform</Level>
          <Side>both</Side>
          <Dependencies>
            <Module id="nobara.game" version="^1.0" />
            <Module id="nobara.lighting" version="^1.0" optional="true" />
          </Dependencies>
          <Libraries>
            <Package id="K4os.Compression.LZ4" version="1.3.8" />
          </Libraries>
          <Conflicts><Module id="example.othervoxel" /></Conflicts>
          <Reserves><Resource name="voxel.world" /></Reserves>
          <States><State id="state:nobara.voxel.debug_world" side="client" /></States>
          <Settings>
            <Setting key="voxel.viewRadius" scope="user" type="int" min="32" max="1024" default="256" side="client" />
          </Settings>
          <FutureElement attribute="ignored" />
        </NobaraModule>
        """;

    [Fact]
    public void Reads_the_fields_the_sdk_needs_and_ignores_the_others()
    {
        var diagnostics = new List<ManifestDiagnostic>();
        ModuleManifest? manifest = ModuleManifestReader.Parse(Full, diagnostics);

        Assert.Empty(diagnostics);
        Assert.NotNull(manifest);
        Assert.Equal("nobara.voxel", manifest.Id);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal(
            [new ModuleDependency("nobara.game", "^1.0", "[1.0,2.0.0)", false), new ModuleDependency("nobara.lighting", "^1.0", "[1.0,2.0.0)", true)],
            manifest.Dependencies);
        Assert.Equal([new ModuleLibrary("K4os.Compression.LZ4", "1.3.8")], manifest.Libraries);
    }

    [Fact]
    public void Ignores_commented_out_elements()
    {
        var diagnostics = new List<ManifestDiagnostic>();
        ModuleManifest? manifest = ModuleManifestReader.Parse(
            "<NobaraModule format=\"1\"><!-- <Id>wrong</Id> --><Id>nobara.a</Id><Version>1.0.0</Version></NobaraModule>", diagnostics);

        Assert.Equal("nobara.a", manifest?.Id);
    }

    [Theory]
    [InlineData("<NobaraModule format=\"1\"><Version>1.0.0</Version></NobaraModule>", "NSDK003")]
    [InlineData("<NobaraModule format=\"1\"><Id> </Id><Version>1.0.0</Version></NobaraModule>", "NSDK003")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id></NobaraModule>", "NSDK004")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0</Version></NobaraModule>", "NSDK004")]
    [InlineData("<NobaraModule format=\"2\"><Id>nobara.a</Id><Version>1.0.0</Version></NobaraModule>", "NSDK002")]
    [InlineData("<Other><Id>nobara.a</Id><Version>1.0.0</Version></Other>", "NSDK002")]
    [InlineData("<NobaraModule><Id>nobara.a</Id>", "NSDK002")]
    public void Reports_an_unreadable_manifest(string text, string code)
    {
        var diagnostics = new List<ManifestDiagnostic>();

        Assert.Null(ModuleManifestReader.Parse(text, diagnostics));
        Assert.Contains(diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("""<Module version="^1.0" />""", "NSDK006")]
    [InlineData("""<Module id="nobara.b" version="^1.0" optional="maybe" />""", "NSDK006")]
    [InlineData("""<Module id="nobara.a" version="^1.0" />""", "NSDK006")]
    [InlineData("""<Module id="nobara.b" version="^1.0" /><Module id="NOBARA.B" version="^1.0" />""", "NSDK006")]
    [InlineData("""<Module id="nobara.b" />""", "NSDK007")]
    [InlineData("""<Module id="nobara.b" version="1.0" />""", "NSDK007")]
    public void Reports_an_invalid_dependency(string dependency, string code)
    {
        var diagnostics = new List<ManifestDiagnostic>();
        string text = $"<NobaraModule format=\"1\">\n<Id>nobara.a</Id><Version>1.0.0</Version>\n<Dependencies>\n{dependency}\n</Dependencies></NobaraModule>";

        Assert.Null(ModuleManifestReader.Parse(text, diagnostics));
        ManifestDiagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == code);
        Assert.Equal(4, diagnostic.Line);
    }

    [Theory]
    [InlineData("""<Package id="x" />""")]
    [InlineData("""<Package version="1.0.0" />""")]
    [InlineData("""<Package id="x" version="1.0.0" /><Package id="X" version="2.0.0" />""")]
    public void Reports_an_invalid_library(string library)
    {
        var diagnostics = new List<ManifestDiagnostic>();
        string text = $"<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0.0</Version><Libraries>{library}</Libraries></NobaraModule>";

        Assert.Null(ModuleManifestReader.Parse(text, diagnostics));
        Assert.Contains(diagnostics, d => d.Code == "NSDK008");
    }
}
