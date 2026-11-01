// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class OpenGLFrameTests : IDisposable
{
    private const int Size = 64;
    private readonly RendererHarness _harness = new(gpu: true) { DevelopmentMode = true };
    private readonly Engine _engine;
    private readonly TestPipeline _pipeline;
    private readonly DrawPass _ui;
    private readonly Texture _color;
    private readonly Texture _depth;

    public OpenGLFrameTests()
    {
        _engine = _harness.WithGame(RendererHarness.Compiled(_harness.CopyResources())).Start();
        GraphicsDevice device = _harness.Device;
        _pipeline = new TestPipeline(device);
        _ui = new DrawPass(device);
        _color = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, Size, Size, TextureUsage.RenderTarget | TextureUsage.Sampled));
        _depth = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Depth32Float, Size, Size, TextureUsage.DepthStencil));
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _ui.Dispose();
        _color.Dispose();
        _depth.Dispose();
        _engine.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void AnExtractedObjectIsDrawnWithTheUserInterfaceAndTheDebugLinesOnTop()
    {
        IRenderer render = _harness.Render;
        RenderMesh crate = _harness.Resources.GetMesh("mesh:example.render.crate");
        RenderMaterial dirt = _harness.Resources.GetMaterial("material:example.render.dirt");
        render.RegisterPipeline(_harness.Context, _pipeline);
        render.MainView.SetTarget(_color, _depth);
        // The crate spans 0.5 to 1.5 blocks up: one block down, it is centered in the view.
        render.AddExtractor(_harness.Context, new TestExtractor { Next = e => e.World.Create(new RenderObjectDescription(crate, RenderTransform.At(WorldPosition.FromBlocks(0, -1, -3)))) });

        // The user interface draws a small crate in the top-right corner, straight in clip space.
        Matrix4x4 corner = Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateTranslation(0.7f, 0.7f, 0.5f);
        _ui.FixedItems = [new RenderItem(default, crate, 0, dirt, corner)];
        render.Insert(_harness.Context, RenderInsertionPoints.Ui, new TestInsertion("test.ui", _ui));

        _harness.DebugDraw.Line(WorldPosition.FromBlocks(-10, -1, -3), WorldPosition.FromBlocks(10, -1, -3), new ColorF(1, 0, 0, 1));

        _engine.MainContext.RunFrame();

        byte[] pixels = new byte[Size * Size * 4];
        _harness.Device.ReadTexture(_color, TextureRegion.Whole(_color), pixels);

        Assert.Equal(["opaque", DebugDrawPass.PassName, "test.ui"], render.LastGraph!.Passes);
        Assert.Equal(["unused"], render.LastGraph.CulledPasses);
        Assert.True(IsBackground(Pixel(pixels, 2, 2)), $"The background is {Pixel(pixels, 2, 2)}.");
        Assert.False(IsBackground(Pixel(pixels, 32, 32)), "The crate is not drawn in the center.");
        Assert.False(IsBackground(Pixel(pixels, 56, 61)), "The user interface is not drawn in the top-right corner.");
        Assert.True(Enumerable.Range(12, 10).Any(y => IsRed(Pixel(pixels, 5, y))), "The debug line is not drawn below the crate.");
    }

    private static (byte R, byte G, byte B, byte A) Pixel(byte[] pixels, int x, int y)
    {
        int i = ((y * Size) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    private static bool IsBackground((byte R, byte G, byte B, byte A) p) =>
        Math.Abs(p.R - 26) <= 2 && Math.Abs(p.G - 51) <= 2 && Math.Abs(p.B - 77) <= 2;

    private static bool IsRed((byte R, byte G, byte B, byte A) p) => p.R > 200 && p.G < 60 && p.B < 60;
}
