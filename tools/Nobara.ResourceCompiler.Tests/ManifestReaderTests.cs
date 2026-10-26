// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler.Manifests;
using F = Nobara.Formats;

namespace Nobara.ResourceCompiler.Tests;

/// <summary>Structural validation of module.nobara, project.nobara, engine.nobara, and build.nobara.</summary>
public sealed class ManifestReaderTests
{
    private const string FullModule = """
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
            <Setting key="nobara.voxel.mode" scope="distribution" type="enum" values="fast,fancy" default="fast" />
          </Settings>
          <RequiresBackend side="client" />
        </NobaraModule>
        """;

    private static string Module(string body) => $"<NobaraModule format=\"1\">\n<Id>nobara.a</Id><Version>1.0.0</Version><Level>platform</Level><Side>both</Side>\n{body}\n</NobaraModule>";

    [Fact]
    public void Reads_every_element_of_a_module_manifest()
    {
        var diagnostics = new DiagnosticBag();

        ModuleManifest? manifest = ModuleManifestReader.Read(FullModule, "module.nobara", diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.NotNull(manifest);
        Assert.Equal("nobara.voxel", manifest.Id);
        Assert.Equal("Voxel", manifest.ShortName);
        Assert.Equal("1.2.0", manifest.Version.Text);
        Assert.Equal(ModuleLevel.Platform, manifest.Level);
        Assert.Equal(ModuleSide.Both, manifest.Side);
        Assert.Equal(["nobara.game", "nobara.lighting"], manifest.Dependencies.Select(d => d.Id));
        Assert.Equal("[1.0,2.0.0)", manifest.Dependencies[0].Range.NuGet);
        Assert.True(manifest.Dependencies[1].Optional);
        Assert.Equal("K4os.Compression.LZ4", manifest.Libraries.Single().Id);
        Assert.Equal("example.othervoxel", manifest.Conflicts.Single().Name);
        Assert.Equal("voxel.world", manifest.Reserves.Single().Name);
        Assert.Equal(ModuleSide.Client, manifest.States.Single().Side);
        Assert.Equal([32.0, 1024.0], [manifest.Settings[0].Min!.Value, manifest.Settings[0].Max!.Value]);
        Assert.Equal(["fast", "fancy"], manifest.Settings[1].Values);
        Assert.Equal(ModuleSide.Client, manifest.BackendRequired);
    }

    [Fact]
    public void Compiles_a_module_manifest()
    {
        ModuleManifest manifest = ModuleManifestReader.Read(FullModule, "module.nobara", new DiagnosticBag())!;

        F.ModuleManifest compiled = ManifestCompiler.Compile(manifest);

        Assert.Equal(1u, compiled.Format);
        Assert.Equal(F.Level.Platform, compiled.Level);
        Assert.Equal(F.Side.Client, compiled.BackendRequired);
        Assert.Equal("^1.0", compiled.Dependencies[0].Range);
        Assert.Equal(F.SettingDeclaration.Types.Type.Int, compiled.Settings[0].Type);
        Assert.Equal(32, compiled.Settings[0].Min);
        Assert.False(compiled.Settings[1].HasMin);
    }

    [Fact]
    public void Reads_a_backend_declaration()
    {
        var diagnostics = new DiagnosticBag();

        ModuleManifest? manifest = ModuleManifestReader.Read(Module("<Dependencies><Module id=\"nobara.audio\" version=\"^1.0\" /></Dependencies><BackendOf module=\"nobara.audio\" />"), "module.nobara", diagnostics);

        Assert.Equal("nobara.audio", manifest?.BackendOf);
    }

    [Fact]
    public void Ignores_commented_out_elements()
    {
        ModuleManifest? manifest = ModuleManifestReader.Read(Module("<!-- <Unknown /> -->"), "module.nobara", new DiagnosticBag());

        Assert.NotNull(manifest);
    }

    [Theory]
    [InlineData("<NobaraModule format=\"1\"><Version>1.0.0</Version><Level>platform</Level><Side>both</Side></NobaraModule>", "NRC102")]
    [InlineData("<NobaraModule format=\"1\"><Id> </Id><Version>1.0.0</Version><Level>platform</Level><Side>both</Side></NobaraModule>", "NRC102")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Level>platform</Level><Side>both</Side></NobaraModule>", "NRC102")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0.0</Version><Side>both</Side></NobaraModule>", "NRC102")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0.0</Version><Level>platform</Level></NobaraModule>", "NRC102")]
    [InlineData("<NobaraModule><Id>nobara.a</Id></NobaraModule>", "NRC102")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0</Version><Level>platform</Level><Side>both</Side></NobaraModule>", "NRC105")]
    [InlineData("<NobaraModule format=\"2\"><Id>nobara.a</Id></NobaraModule>", "NRC100")]
    [InlineData("<Other format=\"1\" />", "NRC100")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id>", "NRC100")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara a</Id><Version>1.0.0</Version><Level>platform</Level><Side>both</Side></NobaraModule>", "NRC104")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0.0</Version><Level>core</Level><Side>both</Side></NobaraModule>", "NRC107")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0.0</Version><Level>platform</Level><Side>everywhere</Side></NobaraModule>", "NRC107")]
    [InlineData("<NobaraModule format=\"1\"><Id>nobara.a</Id><Id>nobara.b</Id><Version>1.0.0</Version><Level>platform</Level><Side>both</Side></NobaraModule>", "NRC103")]
    public void Reports_an_invalid_identity(string text, string code)
    {
        var diagnostics = new DiagnosticBag();

        Assert.Null(ModuleManifestReader.Read(text, "module.nobara", diagnostics));
        Expect.Error(diagnostics.Items, code);
    }

    [Theory]
    [InlineData("<Dependencies><Module version=\"^1.0\" /></Dependencies>", "NRC102")]
    [InlineData("<Dependencies><Module id=\"nobara.b\" version=\"^1.0\" optional=\"maybe\" /></Dependencies>", "NRC107")]
    [InlineData("<Dependencies><Module id=\"nobara.a\" version=\"^1.0\" /></Dependencies>", "NRC109")]
    [InlineData("<Dependencies><Module id=\"nobara.b\" version=\"^1.0\" /><Module id=\"NOBARA.B\" version=\"^1.0\" /></Dependencies>", "NRC103")]
    [InlineData("<Dependencies><Module id=\"nobara.b\" /></Dependencies>", "NRC102")]
    [InlineData("<Dependencies><Module id=\"nobara.b\" version=\"1.0\" /></Dependencies>", "NRC106")]
    [InlineData("<Dependencies><Package id=\"nobara.b\" version=\"^1.0\" /></Dependencies>", "NRC101")]
    [InlineData("<Libraries><Package id=\"x\" /></Libraries>", "NRC102")]
    [InlineData("<Libraries><Package id=\"x\" version=\"1.0.0\" /><Package id=\"X\" version=\"2.0.0\" /></Libraries>", "NRC103")]
    [InlineData("<Conflicts><Module id=\"nobara.a\" /></Conflicts>", "NRC109")]
    [InlineData("<Reserves><Resource name=\"a b\" /></Reserves>", "NRC104")]
    [InlineData("<Reserves><Resource name=\"x\" /><Resource name=\"x\" /></Reserves>", "NRC103")]
    [InlineData("<ShortName>A.B</ShortName>", "NRC104")]
    [InlineData("<States><State id=\"menu\" /></States>", "NRC104")]
    [InlineData("<States><State id=\"state:a.menu\" side=\"both\" /></States>", "NRC107")]
    [InlineData("<States><State id=\"state:a.menu\" /><State id=\"state:a.menu\" /></States>", "NRC103")]
    [InlineData("<RequiresBackend />", "NRC102")]
    [InlineData("<RequiresBackend side=\"sometimes\" />", "NRC107")]
    [InlineData("<BackendOf module=\"nobara.audio\" />", "NRC109")]
    [InlineData("<BackendOf module=\"nobara.a\" />", "NRC109")]
    [InlineData("<Future />", "NRC101")]
    [InlineData("<Version>2.0.0</Version>", "NRC103")]
    public void Reports_an_invalid_declaration(string body, string code)
    {
        var diagnostics = new DiagnosticBag();

        Assert.Null(ModuleManifestReader.Read(Module(body), "module.nobara", diagnostics));
        Diagnostic error = Expect.Error(diagnostics.Items, code);
        Assert.True(error.Line >= 1);
        Assert.Equal("module.nobara", error.File);
    }

    [Fact]
    public void Reports_a_state_or_backend_on_a_side_where_the_module_does_not_run()
    {
        string text = "<NobaraModule format=\"1\"><Id>nobara.a</Id><Version>1.0.0</Version><Level>platform</Level><Side>server</Side><States><State id=\"state:a.menu\" side=\"client\" /></States><RequiresBackend side=\"both\" /></NobaraModule>";
        var diagnostics = new DiagnosticBag();

        ModuleManifestReader.Read(text, "module.nobara", diagnostics);

        Assert.Equal(2, diagnostics.Items.Count(d => d.Code == "NRC109"));
    }

    [Theory]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" default=\"4\" />", true)]
    [InlineData("<Setting key=\"nobara.a.size\" scope=\"user\" type=\"float\" min=\"0.5\" default=\"1.5\" />", true)]
    [InlineData("<Setting key=\"a.name\" scope=\"distribution\" type=\"string\" default=\"\" />", true)]
    [InlineData("<Setting key=\"a.on\" scope=\"user\" type=\"bool\" default=\"true\" side=\"server\" />", true)]
    [InlineData("<Setting key=\"other.size\" scope=\"user\" type=\"int\" default=\"4\" />", false)]
    [InlineData("<Setting key=\"size\" scope=\"user\" type=\"int\" default=\"4\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"everyone\" type=\"int\" default=\"4\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"long\" default=\"4\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" default=\"four\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" default=\"1.5\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" min=\"8\" default=\"4\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" min=\"8\" max=\"2\" default=\"4\" />", false)]
    [InlineData("<Setting key=\"a.on\" scope=\"user\" type=\"bool\" min=\"0\" default=\"true\" />", false)]
    [InlineData("<Setting key=\"a.mode\" scope=\"user\" type=\"enum\" default=\"x\" />", false)]
    [InlineData("<Setting key=\"a.mode\" scope=\"user\" type=\"enum\" values=\"x,y\" default=\"z\" />", false)]
    [InlineData("<Setting key=\"a.mode\" scope=\"user\" type=\"enum\" values=\"x,x\" default=\"x\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" values=\"1,2\" default=\"1\" />", false)]
    [InlineData("<Setting key=\"a.size\" scope=\"user\" type=\"int\" default=\"1\" /><Setting key=\"a.size\" scope=\"user\" type=\"int\" default=\"1\" />", false)]
    public void Validates_configuration_keys(string settings, bool valid)
    {
        var diagnostics = new DiagnosticBag();
        string text = $"<NobaraModule format=\"1\"><Id>nobara.a</Id><ShortName>A</ShortName><Version>1.0.0</Version><Level>platform</Level><Side>both</Side><Settings>{settings}</Settings></NobaraModule>";

        ModuleManifest? manifest = ModuleManifestReader.Read(text, "module.nobara", diagnostics);

        Assert.True(valid == (manifest is not null), string.Join(Environment.NewLine, diagnostics.Items));
    }

    [Fact]
    public void Reads_a_project_file()
    {
        const string text = """
            <NobaraProject format="1">
              <Id>example.game</Id>
              <Name>Example Game</Name>
              <Version>0.1.0</Version>
              <Icon>assets/icon.png</Icon>
              <Modules>
                <Local path="src/Example.Mobs" />
                <Local path="src\Example.Mobs.Zombie" required="true" />
                <Package id="nobara.voxel" version="^1.2" required="true" />
              </Modules>
              <Overrides><Project id="nobara.voxel" path="../engine/src/nVoxel/Nobara.Voxel" /></Overrides>
              <ProtectedNamespaces><Namespace>Example/*</Namespace></ProtectedNamespaces>
              <Modding enabled="false" />
            </NobaraProject>
            """;
        var diagnostics = new DiagnosticBag();

        ProjectManifest? project = ProjectManifestReader.Read(text, "project.nobara", diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.NotNull(project);
        Assert.Equal("Example Game", project.Name);
        Assert.Equal(["src/Example.Mobs", "src/Example.Mobs.Zombie", null], project.Modules.Select(m => m.Path));
        Assert.Equal([false, true, true], project.Modules.Select(m => m.Required));
        Assert.Equal("Example/*", project.ProtectedNamespaces.Single().Name);
    }

    [Theory]
    [InlineData("<Id>Example</Id><Name>A</Name><Version>1.0.0</Version><Modules/>", "NRC104")]
    [InlineData("<Id>example</Id><Version>1.0.0</Version><Modules/>", "NRC102")]
    [InlineData("<Id>example</Id><Name>A</Name><Version>1.0.0</Version>", "NRC102")]
    [InlineData("<Id>example</Id><Name>A</Name><Version>1.0.0</Version><Modules><Local /></Modules>", "NRC102")]
    [InlineData("<Id>example</Id><Name>A</Name><Version>1.0.0</Version><Modules><Local path=\"a\" /><Local path=\"a/\" /></Modules>", "NRC103")]
    [InlineData("<Id>example</Id><Name>A</Name><Version>1.0.0</Version><Modules><Package id=\"a\" version=\"1.0\" /></Modules>", "NRC106")]
    [InlineData("<Id>example</Id><Name>A</Name><Version>1.0.0</Version><Modules/><ProtectedNamespaces><Namespace>Example.*</Namespace></ProtectedNamespaces>", "NRC104")]
    [InlineData("<Id>example</Id><Name>A</Name><Version>1.0.0</Version><Modules/><Mods/>", "NRC101")]
    public void Reports_an_invalid_project_file(string body, string code)
    {
        var diagnostics = new DiagnosticBag();

        Assert.Null(ProjectManifestReader.Read($"<NobaraProject format=\"1\">{body}</NobaraProject>", "project.nobara", diagnostics));
        Expect.Error(diagnostics.Items, code);
    }

    [Fact]
    public void Reads_an_engine_file()
    {
        const string text = """
            <NobaraEngine format="1">
              <Loop tickRate="30">
                <Phase name="Input" />
                <Phase name="Simulation" fixed="true" />
                <Phase name="Render" side="client" />
              </Loop>
              <States client="state:example.boot" server="state:example.server_boot" />
              <ErrorPolicy default="disable">
                <Module id="example.stats" policy="log" />
              </ErrorPolicy>
              <Settings>
                <Default key="voxel.viewRadius" value="192" />
                <Lock key="network.tickRate" value="30" />
                <Set key="blocks.maxStatesPerType" value="2048" />
              </Settings>
              <ModuleConfig module="nobara.game">
                <Stage name="example.combat" after="Entities" before="Physics" />
              </ModuleConfig>
            </NobaraEngine>
            """;
        var diagnostics = new DiagnosticBag();

        EngineManifest? engine = EngineManifestReader.Read(text, "engine.nobara", diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.NotNull(engine);
        Assert.Equal(30, engine.TickRate);
        Assert.Equal([false, true, false], engine.Phases.Select(p => p.Fixed));
        Assert.Equal(ModuleSide.Client, engine.Phases[2].Side);
        Assert.Equal("state:example.boot", engine.ClientState!.Name);
        Assert.Equal(ErrorPolicyKind.Disable, engine.DefaultPolicy);
        Assert.Equal([SettingOperation.Default, SettingOperation.Lock, SettingOperation.Set], engine.Settings.Select(s => s.Operation));
        Assert.Equal("nobara.game", engine.ModuleConfigs.Single().Module);
    }

    [Theory]
    [InlineData("<Loop tickRate=\"0\"><Phase name=\"A\" /></Loop>", "NRC107")]
    [InlineData("<Loop tickRate=\"30\" />", "NRC102")]
    [InlineData("<Loop tickRate=\"30\"><Phase name=\"A\" /><Phase name=\"A\" /></Loop>", "NRC103")]
    [InlineData("<Loop tickRate=\"30\"><Phase name=\"A\" side=\"both\" /></Loop>", "NRC107")]
    [InlineData("<States client=\"boot\" />", "NRC104")]
    [InlineData("<States />", "NRC102")]
    [InlineData("<ErrorPolicy default=\"crash\" />", "NRC107")]
    [InlineData("<ErrorPolicy><Module id=\"a\" policy=\"log\" /><Module id=\"a\" policy=\"stop\" /></ErrorPolicy>", "NRC103")]
    [InlineData("<Settings><Set key=\"a.b\" /></Settings>", "NRC102")]
    [InlineData("<Settings><Set key=\"a.b\" value=\"1\" /><Lock key=\"a.b\" value=\"2\" /></Settings>", "NRC103")]
    [InlineData("<Settings><Change key=\"a.b\" value=\"1\" /></Settings>", "NRC101")]
    [InlineData("<ModuleConfig><Stage /></ModuleConfig>", "NRC102")]
    [InlineData("<ModuleConfig module=\"a\" /><ModuleConfig module=\"a\" />", "NRC103")]
    public void Reports_an_invalid_engine_file(string body, string code)
    {
        var diagnostics = new DiagnosticBag();

        Assert.Null(EngineManifestReader.Read($"<NobaraEngine format=\"1\">{body}</NobaraEngine>", "engine.nobara", diagnostics));
        Expect.Error(diagnostics.Items, code);
    }

    [Fact]
    public void Reads_a_build_file()
    {
        const string text = """
            <NobaraBuild format="1">
              <Target name="client-win"><Platform>win-x64</Platform><Side>Client</Side></Target>
              <Target name="server-win">
                <Platform>win-x64</Platform>
                <Side>server</Side>
                <Disable module="example.debug.tools" />
              </Target>
            </NobaraBuild>
            """;
        var diagnostics = new DiagnosticBag();

        BuildManifest? build = BuildManifestReader.Read(text, "build.nobara", diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal([ModuleSide.Client, ModuleSide.Server], build!.Targets.Select(t => t.Side));
        Assert.Equal("example.debug.tools", build.Targets[1].Disabled.Single().Name);
    }

    [Theory]
    [InlineData("<Target><Platform>win-x64</Platform><Side>client</Side></Target>", "NRC102")]
    [InlineData("<Target name=\"a\"><Side>client</Side></Target>", "NRC102")]
    [InlineData("<Target name=\"a\"><Platform>windows</Platform><Side>client</Side></Target>", "NRC107")]
    [InlineData("<Target name=\"a\"><Platform>win-x64</Platform><Side>both</Side></Target>", "NRC107")]
    [InlineData("<Target name=\"a\"><Platform>win-x64</Platform><Side>client</Side></Target><Target name=\"A\"><Platform>win-x64</Platform><Side>client</Side></Target>", "NRC103")]
    [InlineData("<Target name=\"a\"><Platform>win-x64</Platform><Side>client</Side><Disable module=\"x\" /><Disable module=\"x\" /></Target>", "NRC103")]
    [InlineData("<Target name=\"a\"><Platform>win-x64</Platform><Side>client</Side><Mode>aot</Mode></Target>", "NRC101")]
    public void Reports_an_invalid_build_file(string body, string code)
    {
        var diagnostics = new DiagnosticBag();

        Assert.Null(BuildManifestReader.Read($"<NobaraBuild format=\"1\">{body}</NobaraBuild>", "build.nobara", diagnostics));
        Expect.Error(diagnostics.Items, code);
    }

    [Fact]
    public void Warns_that_variants_are_not_built()
    {
        var diagnostics = new DiagnosticBag();

        BuildManifest? build = BuildManifestReader.Read("<NobaraBuild format=\"1\"><Target name=\"a\"><Platform>win-x64</Platform><Side>client</Side><Variants><Play /></Variants></Target></NobaraBuild>", "build.nobara", diagnostics);

        Assert.NotNull(build);
        Expect.Warning(diagnostics.Items, "NRC110");
    }
}
