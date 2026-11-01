// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Renderer.Build;
using Nobara.Renderer.Schemas;
using Nobara.Renderer.Tests.Support;
using Nobara.ResourceCompiler;

namespace Nobara.Renderer.Tests;

/// <summary>The build of the resources: shaders compiled by shaderc, and the checks of the plugin.</summary>
public sealed class CompilationTests : IDisposable
{
    private readonly RendererHarness _harness = new();
    private readonly string _resources;

    public CompilationTests()
    {
        _resources = _harness.CopyResources();
    }

    public void Dispose() => _harness.Dispose();

    private string Nof => Path.Combine(_resources, "game.nof");

    private void Edit(string relative, Func<string, string> change)
    {
        string path = Path.Combine(_resources, relative);
        File.WriteAllText(path, change(File.ReadAllText(path)));
    }

    private void Add(string resources) => Edit("game.nof", text => text.Replace("</Nobara>", resources + "\n</Nobara>", StringComparison.Ordinal));

    private Diagnostic Error(string code)
    {
        CompilationResult result = RendererHarness.Compile(_resources);
        Assert.False(result.Success);
        Diagnostic? error = result.Diagnostics.FirstOrDefault(d => d.Code == code && d.Severity == DiagnosticSeverity.Error);
        Assert.True(error is not null, $"Expected error {code}, got:{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics)}");
        return error;
    }

    [Fact]
    public void CompilesEachGlslSourceOnceForEachSetOfMacrosOfItsShaders()
    {
        CompilationResult result = RendererHarness.Compiled(_resources);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
        Dictionary<string, ReadOnlyMemory<byte>> files = RendererHarness.Files(result);

        // The vertex source is shared by Lit, which has a variant, and by Unlit.
        CompiledStage vertex = CompiledStage.Parser.ParseFrom(files["Shaders/lit.vert"].Span);
        Assert.Equal("vertex", vertex.Stage);
        Assert.Equal(["", "ALPHA_CUTOUT"], vertex.Variant.Select(v => v.Defines));
        Assert.All(vertex.Variant, v => Assert.Equal(0x07230203u, BitConverter.ToUInt32(v.Spirv.Span)));

        CompiledStage unlit = CompiledStage.Parser.ParseFrom(files["Shaders/unlit.frag"].Span);
        Assert.Equal("fragment", unlit.Stage);
        Assert.Equal([""], unlit.Variant.Select(v => v.Defines));

        // Textures and meshes are embedded as they are.
        Assert.Equal(File.ReadAllBytes(Path.Combine(_resources, "Textures", "dirt.png")), files["Textures/dirt.png"].ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(_resources, "Meshes", "crate.glb")), files["Meshes/crate.glb"].ToArray());
    }

    [Fact]
    public void AGlslErrorFailsTheBuildWithTheFileAndTheLineOfTheSource()
    {
        Edit("Shaders/unlit.frag", text => text.Replace("outColor = texture(", "outColor = undeclared + texture(", StringComparison.Ordinal));
        int line = File.ReadAllLines(Path.Combine(_resources, "Shaders", "unlit.frag")).ToList().FindIndex(l => l.Contains("undeclared", StringComparison.Ordinal)) + 1;

        Diagnostic error = Error(RendererPlugin.GlslError);

        Assert.EndsWith(Path.Combine("Shaders", "unlit.frag"), error.File, StringComparison.Ordinal);
        Assert.Equal(line, error.Line);
        Assert.Contains("undeclared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorInOneVariantNamesTheVariant()
    {
        Edit("Shaders/lit.frag", text => text.Replace("discard;", "discard\n", StringComparison.Ordinal));

        Diagnostic error = Error(RendererPlugin.GlslError);

        Assert.EndsWith(Path.Combine("Shaders", "lit.frag"), error.File, StringComparison.Ordinal);
        Assert.True(error.Line > 0);
        Assert.Contains("(variant Cutout)", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex>", "needs a <Vertex> and a <Fragment>")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Compute>Shaders/unlit.frag</Compute>", "either a compute shader")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Texture name=\"A\" binding=\"0\" /><Texture name=\"B\" binding=\"0\" />", "two textures use the binding 0")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Texture name=\"A\" binding=\"0\" fallback=\"pink\" optional=\"true\" />", "'pink' is not a fallback")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Parameters binding=\"1\"><Parameter name=\"L\" type=\"layer\" texture=\"Missing\" /></Parameters>", "names the texture 'Missing'")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Parameters binding=\"1\"><Parameter name=\"Tint\" type=\"vec4\" default=\"1 1 1\" /></Parameters>", "takes 4 numbers; got 3")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Parameters binding=\"1\"><Parameter name=\"Tint\" type=\"color\" /></Parameters>", "'color' is not a parameter type")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Parameters binding=\"0\"><Parameter name=\"A\" type=\"float\" /></Parameters><Uniform name=\"Camera\" binding=\"0\" />", "two uniform blocks use the binding 0")]
    [InlineData("<Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Variant name=\"Fog\"><Define>GL_FOG</Define></Variant>", "'GL_FOG' is not a macro")]
    public void AnInvalidShaderDeclarationFailsTheBuild(string content, string message)
    {
        Add($"<Shader><Name>Broken</Name><Content>{content}</Content></Shader>");

        Diagnostic error = Error(RendererPlugin.InvalidShader);

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.EndsWith("game.nof", error.File, StringComparison.Ordinal);
        Assert.True(error.Line > 0);
    }

    [Fact]
    public void AGlslSourceIsOneStage()
    {
        Add("<Shader><Name>Twisted</Name><Content><Vertex>Shaders/unlit.frag</Vertex><Fragment>Shaders/lit.frag</Fragment></Content></Shader>");

        Assert.Contains("used as fragment and vertex stages", Error(RendererPlugin.SharedFile).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMaterialNamesALoadedShaderAndLoadedTextures()
    {
        Add("""
            <Material><Name>NoShader</Name><Content><Shader>shader:example.render.missing</Shader></Content></Material>
            <Material><Name>NoTexture</Name><Content><Shader>shader:example.render.unlit</Shader><Texture name="Albedo" texture="texture:example.render.missing" /></Content></Material>
            """);

        CompilationResult result = RendererHarness.Compile(_resources);

        Diagnostic[] unknown = [.. result.Diagnostics.Where(d => d.Code == "NRC401")];
        Assert.Contains(unknown, d => d.Message.Contains("shader:example.render.missing", StringComparison.Ordinal));
        Assert.Contains(unknown, d => d.Message.Contains("texture:example.render.missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<Mode>glass</Mode>", "'glass' is not a render mode")]
    [InlineData("<Parameter name=\"Tint\" value=\"red\" />", "'red' is not a list of numbers")]
    [InlineData("<Texture name=\"Albedo\" texture=\"texture:example.render.dirt\" /><Texture name=\"Albedo\" texture=\"texture:example.render.stone\" />", "the texture 'Albedo' is given twice")]
    public void AnInvalidMaterialFailsTheBuild(string content, string message)
    {
        Add($"<Material><Name>Broken</Name><Content><Shader>shader:example.render.lit</Shader>{content}</Content></Material>");

        Assert.Contains(message, Error(RendererPlugin.InvalidMaterial).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeshGivesMaterialsToTheSlotsOfItsFile()
    {
        Add("<Mesh><Name>Wrong</Name><Content><File>Meshes/crate.glb</File><Material slot=\"Glass\" material=\"material:example.render.dirt\" /></Content></Mesh>");

        Diagnostic error = Error(RendererPlugin.InvalidMesh);

        Assert.Contains("slot 'Glass'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Wood, Leaves", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeshFileIsAGltfBinaryFile()
    {
        File.WriteAllText(Path.Combine(_resources, "Meshes", "fake.glb"), "not a model");
        Add("<Mesh><Name>Fake</Name><Content><File>Meshes/fake.glb</File></Content></Mesh>");

        Diagnostic error = Error(RendererPlugin.InvalidMesh);

        Assert.Contains("not a glTF binary file", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("fake.glb", error.File, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextureFileIsAPngImage()
    {
        File.WriteAllText(Path.Combine(_resources, "Textures", "fake.png"), "not an image");
        Add("<Texture><Name>Fake</Name><Content><File>Textures/fake.png</File></Content></Texture>");

        Assert.Contains("not a PNG image", Error(RendererPlugin.InvalidTexture).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Filter>bilinear</Filter>", "'bilinear' is not a filter")]
    [InlineData("<ColorSpace>rgb</ColorSpace>", "'rgb' is not a color space")]
    public void AnInvalidTextureSettingFailsTheBuild(string setting, string message)
    {
        Add($"<Texture><Name>Odd</Name><Content><File>Textures/dirt.png</File>{setting}</Content></Texture>");

        Assert.Contains(message, Error(RendererPlugin.InvalidTexture).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesTheMessagesOfShaderc()
    {
        List<GlslMessage> messages = GlslCompiler.Parse(
            "Shaders/a.frag:12: error: 'x' : undeclared identifier\nShaders/a.frag:3: warning: version 460 is unknown\nerror: no entry point\n2 errors generated.\n",
            "Shaders/a.frag");

        Assert.Equal(
            [new GlslMessage(true, 12, "'x' : undeclared identifier"), new GlslMessage(false, 3, "version 460 is unknown"), new GlslMessage(true, 0, "no entry point")],
            messages);
    }
}
