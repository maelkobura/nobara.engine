// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;
using Nobara.HotReload;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

/// <summary>The resources uploaded to a real GPU, through nGraphics.OpenGL. Needs an OpenGL 4.6 driver that loads SPIR-V.</summary>
[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class OpenGLTests : IDisposable
{
    private readonly RendererHarness _harness = new(gpu: true) { DevelopmentMode = true };
    private readonly string _resources;

    public OpenGLTests()
    {
        _resources = _harness.CopyResources();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TheDriverLoadsTheShadersOfEachVariantAndTheTexelsAreOnTheGpu()
    {
        using Engine engine = _harness.WithGame(RendererHarness.Compiled(_resources)).Start();
        IRenderResources resources = _harness.Resources;

        Assert.Equal("opengl", _harness.Probe.Context.Resolve<IGraphicsSystem>().BackendName);
        Assert.Equal(2, resources.GetShader("shader:example.render.lit").Programs.Count);
        Assert.Equal(3, resources.Materials.Count);

        foreach (RenderTexture texture in resources.Textures)
        {
            string file = texture.ObjectId switch
            {
                "texture:example.render.dirt" => "dirt.png",
                "texture:example.render.leaves" => "leaves.png",
                "texture:example.render.stone" => "stone.png",
                _ => "stone_normal.png",
            };
            TextureImage expected = TextureImage.Decode(File.ReadAllBytes(Path.Combine(_resources, "Textures", file)), 0, texture.Array.ColorSpace == TextureColorSpace.Srgb);
            for (int level = 0; level < texture.Array.MipLevels; level++)
            {
                int width = Math.Max(1, texture.Array.Width >> level);
                int height = Math.Max(1, texture.Array.Height >> level);
                var read = new byte[width * height * 4];
                _harness.Device.ReadTexture(texture.Array.Texture, new TextureRegion(level, 0, 0, texture.Location.Layer, width, height, 1), read);
                Assert.Equal(expected.Levels[level], read);
            }
        }

        var vertices = new byte[RenderMesh.VertexSize];
        RenderMesh crate = resources.GetMesh("mesh:example.render.crate");
        _harness.Device.ReadBuffer(crate.VertexBuffer, 0, vertices);
        Assert.Equal(-0.5f, BitConverter.ToSingle(vertices, 0));
        Assert.Equal(0.5f, BitConverter.ToSingle(vertices, 4));
    }

    [Fact]
    public void AChangedShaderIsLoadedAgainByTheDriver()
    {
        using Engine engine = _harness.WithGame(RendererHarness.Compiled(_resources)).Start();
        using ResourceWatcher watcher = _harness.Watcher(engine, _resources);
        RenderShader lit = _harness.Resources.GetShader("shader:example.render.lit");

        string path = Path.Combine(_resources, "Shaders", "lit.frag");
        File.WriteAllText(path, File.ReadAllText(path).Replace("0.5 + 0.5 * normal.z", "0.25 + 0.75 * normal.z", StringComparison.Ordinal));
        Assert.True(watcher.Compile(RendererHarness.Game));
        engine.MainContext.RunFrame();

        Assert.Equal(1, lit.Version);
        Assert.Equal(1, _harness.Resources.GetMaterial("material:example.render.leaves").Version);
    }
}
