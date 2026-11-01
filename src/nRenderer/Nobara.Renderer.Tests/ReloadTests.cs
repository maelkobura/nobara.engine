// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.HotReload;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

/// <summary>
/// Hot reload in a development build: the watcher compiles the resources
/// again, GLSL sources and binary files included, and nRenderer creates the
/// GPU objects again at the next frame.
/// </summary>
public sealed class ReloadTests : IDisposable
{
    private readonly RendererHarness _harness = new() { DevelopmentMode = true };
    private readonly string _resources;
    private readonly Engine _engine;
    private readonly ResourceWatcher _watcher;

    public ReloadTests()
    {
        _resources = _harness.CopyResources();
        _engine = _harness.WithGame(RendererHarness.Compiled(_resources)).Start();
        _watcher = _harness.Watcher(_engine, _resources);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _engine.Dispose();
        _harness.Dispose();
    }

    private IRenderResources Resources => _harness.Resources;

    private void Edit(string relative, Func<string, string> change)
    {
        string path = Path.Combine(_resources, relative);
        File.WriteAllText(path, change(File.ReadAllText(path)));
    }

    private string Logs() => string.Join(Environment.NewLine, _harness.Log.Entries.Select(e => $"{e.Level}: {e.Message}"));

    private bool Reload()
    {
        bool compiled = _watcher.Compile(RendererHarness.Game);
        _engine.MainContext.RunFrame();
        return compiled;
    }

    [Fact]
    public void AChangedGlslSourceIsCompiledAgainAndItsGpuObjectsAreCreatedAgainAtTheNextFrame()
    {
        RenderShader lit = Resources.GetShader("shader:example.render.lit");
        RenderMaterial leaves = Resources.GetMaterial("material:example.render.leaves");
        Graphics.Shader before = lit.Programs[1].Fragment!;
        Graphics.ResourceSet set = leaves.ResourceSet!;

        Edit("Shaders/lit.frag", text => text.Replace("0.5 + 0.5 * normal.z", "0.25 + 0.75 * normal.z", StringComparison.Ordinal));
        Assert.True(Reload());

        Assert.Same(lit, Resources.GetShader("shader:example.render.lit"));
        Assert.Equal(1, lit.Version);
        Assert.NotSame(before, lit.Programs[1].Fragment);
        Assert.True(before.IsDisposed);

        // The materials of the shader are built again, on its new layout.
        Assert.Equal(1, leaves.Version);
        Assert.NotSame(set, leaves.ResourceSet);
        Assert.Same(lit.MaterialLayout, leaves.ResourceSet!.Layout);
        Assert.False(_harness.Log.Entries.Any(e => e.Level >= LogLevel.Warning && !e.Message.Contains("the loop catches up", StringComparison.Ordinal) && !e.Message.StartsWith("No render pipeline", StringComparison.Ordinal)), Logs());
    }

    [Fact]
    public void ABrokenShaderKeepsThePreviousVersionAndTheErrorIsLoggedWithItsLine()
    {
        RenderShader unlit = Resources.GetShader("shader:example.render.unlit");
        Graphics.Shader before = unlit.Programs[0].Fragment!;

        Edit("Shaders/unlit.frag", text => text.Replace("outColor = texture(", "outColor = broken + texture(", StringComparison.Ordinal));
        int line = File.ReadAllLines(Path.Combine(_resources, "Shaders", "unlit.frag")).ToList().FindIndex(l => l.Contains("broken", StringComparison.Ordinal)) + 1;
        Assert.False(Reload());

        Assert.Equal(0, unlit.Version);
        Assert.Same(before, unlit.Programs[0].Fragment);
        Assert.False(before.IsDisposed);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($"unlit.frag({line},", StringComparison.Ordinal) && e.Message.Contains("NREN002", StringComparison.Ordinal));
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("keeps the previous ones", StringComparison.Ordinal));

        // Fixed, it reloads.
        Edit("Shaders/unlit.frag", text => text.Replace("broken + texture(", "0.5 * texture(", StringComparison.Ordinal));
        Assert.True(Reload(), Logs());
        Assert.True(unlit.Version == 1, Logs());
    }

    [Fact]
    public void AChangedTextureIsWrittenAgainInItsLayer()
    {
        RenderTexture dirt = Resources.GetTexture("texture:example.render.dirt");
        File.WriteAllBytes(Path.Combine(_resources, "Textures", "dirt.png"), Png.Solid(16, 16, 10, 20, 30, 255));
        Assert.True(Reload());

        Assert.Equal(1, dirt.Version);
        Assert.Same(dirt, Resources.GetTexture("texture:example.render.dirt"));
        Assert.Equal([10, 20, 30, 255], _harness.Fake.Texels[(dirt.Array.Texture, 0, dirt.Location.Layer)][..4]);
        Assert.Equal([10, 20, 30, 255], _harness.Fake.Texels[(dirt.Array.Texture, 4, dirt.Location.Layer)]);
    }

    [Fact]
    public void ATextureThatChangesSizeIsRefusedAndKeepsItsLayer()
    {
        RenderTexture dirt = Resources.GetTexture("texture:example.render.dirt");
        File.WriteAllBytes(Path.Combine(_resources, "Textures", "dirt.png"), Png.Solid(32, 32, 10, 20, 30, 255));
        Assert.True(Reload());

        Assert.Equal(0, dirt.Version);
        Assert.Equal(new TextureLocation(0, 0), dirt.Location);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("texture:example.render.dirt", StringComparison.Ordinal) && e.Message.Contains("restart", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangedMaterialIsBuiltAgain()
    {
        RenderMaterial stone = Resources.GetMaterial("material:example.render.stone");
        Edit("game.nof", text => text.Replace("value=\"0.9 0.9 1 1\"", "value=\"0.1 0.2 0.3 1\"", StringComparison.Ordinal));
        Assert.True(Reload());

        Assert.Equal(1, stone.Version);
        Assert.Equal(0.1f, BinaryPrimitives.ReadSingleLittleEndian(stone.ParameterData.Span));
        var read = new byte[4];
        _harness.Device.ReadBuffer(stone.ParameterBuffer!, 0, read);
        Assert.Equal(0.1f, BinaryPrimitives.ReadSingleLittleEndian(read));
    }

    [Fact]
    public void AMaterialThatNoLongerFitsItsShaderIsRefusedAndKeepsItsPreviousForm()
    {
        RenderMaterial stone = Resources.GetMaterial("material:example.render.stone");
        Edit("game.nof", text => text.Replace("<Parameter name=\"Tint\" value=\"0.9 0.9 1 1\" />", "<Parameter name=\"Gloss\" value=\"1\" />", StringComparison.Ordinal));
        Assert.True(Reload());

        Assert.Equal(0, stone.Version);
        Assert.Equal(0.9f, BinaryPrimitives.ReadSingleLittleEndian(stone.ParameterData.Span));
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("has no parameter 'Gloss'", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewMaterialAndANewShaderAreAdded()
    {
        Edit("game.nof", text => text.Replace("</Nobara>", """
            <Shader><Name>Flat</Name><Content><Vertex>Shaders/lit.vert</Vertex><Fragment>Shaders/unlit.frag</Fragment><Texture name="Albedo" binding="0" /><Parameters binding="1"><Parameter name="AlbedoLayer" type="layer" texture="Albedo" /></Parameters></Content></Shader>
            <Material><Name>Flat</Name><Content><Shader>shader:example.render.flat</Shader><Texture name="Albedo" texture="texture:example.render.stone" /></Content></Material>
            </Nobara>
            """, StringComparison.Ordinal));
        Assert.True(Reload());

        Assert.Same(Resources.GetShader("shader:example.render.flat"), Resources.GetMaterial("material:example.render.flat").Shader);
        Assert.Equal(3, Resources.Shaders.Count);
        Assert.Equal(4, Resources.Materials.Count);
    }
}
