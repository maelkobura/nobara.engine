// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer;
using Nobara.Ungp.Tests.Support;

namespace Nobara.Ungp.Tests;

/// <summary>
/// Draws a test scene to a texture with OpenGL and reads its pixels: the camera
/// stands at the origin and looks along -Z; a red square sits on the left, a
/// cut-out square on the right, whose left half is opaque and right half
/// transparent, and the sky fills the rest.
/// </summary>
[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class OpenGLRenderTests : IDisposable
{
    private const int Size = 64;

    // #4080C0: linear values under the start of the highlight compression, which tonemapping keeps.
    private static readonly (byte R, byte G, byte B) SkyColor = (0x40, 0x80, 0xC0);

    private readonly UngpHarness _harness = new(gpu: true);
    private Engine? _engine;
    private Texture? _color;
    private Texture? _depth;

    public void Dispose()
    {
        _color?.Dispose();
        _depth?.Dispose();
        _engine?.Dispose();
        _harness.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTestSceneHasTheExpectedPixels(bool prepass)
    {
        Start();
        _harness.Ungp.DepthPrepass = prepass;
        AddScene();

        byte[] pixels = Draw();

        Assert.Equal(prepass ? 4 : 3, _harness.Render.LastGraph!.Passes.Count);
        AssertColor(SkyColor, pixels, 2, 2, "the sky in a corner");
        AssertColor(SkyColor, pixels, 32, 60, "the sky at the top");
        AssertColor(UngpHarness.Red, pixels, 17, 32, "the opaque square");
        AssertColor(UngpHarness.Leaf, pixels, 43, 32, "the opaque half of the cut-out square");
        AssertColor(SkyColor, pixels, 51, 32, "the sky through the transparent half of the cut-out square");
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void WithoutTheSkyTheBackgroundIsBlack()
    {
        Start();
        _harness.Ungp.Sky = false;
        AddScene();

        byte[] pixels = Draw();

        AssertColor((0, 0, 0), pixels, 2, 2, "the background");
        AssertColor((0, 0, 0), pixels, 51, 32, "the background through the transparent half");
        AssertColor(UngpHarness.Red, pixels, 17, 32, "the opaque square");
    }

    [Fact]
    public void TonemappingCompressesHighlightsThatClampingCuts()
    {
        Start();
        _harness.Ungp.Exposure = 2;
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor { Next = e => e.World.Create(_harness.Square("material:example.scene.purered", 0, 0, -3, scale: 10)) });

        // A linear red of 2: tonemapped, it stays under 1 and turns toward white.
        (byte R, byte G, byte B) tonemapped = Pixel(Draw(), 32, 32);
        Assert.InRange(tonemapped.R, 240, 254);
        Assert.InRange(tonemapped.G, 80, 120);
        Assert.Equal(tonemapped.G, tonemapped.B);

        // Clamped, it is pure red.
        _harness.Ungp.Tonemapping = false;
        (byte R, byte G, byte B) clamped = Pixel(Draw(), 32, 32);
        Assert.Equal(UngpPasses.Output, _harness.Render.LastGraph!.Passes[^1]);
        Assert.Equal(255, clamped.R);
        Assert.InRange(clamped.G, 0, 1);
    }

    [Fact]
    public void AHalfRenderResolutionFillsTheTarget()
    {
        _harness.Settings[UngpSettings.RenderScale] = "0.5";
        Start();
        AddScene();

        byte[] pixels = Draw();

        RenderView view = _harness.Render.MainView;
        Assert.Equal((32, 32), (view.RenderWidth, view.RenderHeight));
        AssertColor(SkyColor, pixels, 1, 1, "the sky in the bottom-left corner");
        AssertColor(SkyColor, pixels, 62, 62, "the sky in the top-right corner");
        AssertColor(UngpHarness.Red, pixels, 17, 32, "the opaque square");
        AssertColor(UngpHarness.Leaf, pixels, 43, 32, "the opaque half of the cut-out square");
        AssertColor(SkyColor, pixels, 51, 32, "the sky through the transparent half of the cut-out square");
    }

    private void Start()
    {
        _harness.Settings[UngpSettings.SkyColor] = "#4080C0";
        _engine = _harness.Start();
        GraphicsDevice device = _harness.Device;
        _color = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, Size, Size, TextureUsage.RenderTarget | TextureUsage.Sampled));
        _depth = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Depth32Float, Size, Size, TextureUsage.DepthStencil));
        _harness.Render.MainView.SetTarget(_color, _depth);
    }

    private void AddScene() => _harness.Render.AddExtractor(_harness.Context, new TestExtractor
    {
        Next = e =>
        {
            e.World.Create(_harness.Square("material:example.scene.red", -1, 0, -3));
            e.World.Create(_harness.Square("material:example.scene.leaf", 1, 0, -3));
        },
    });

    private byte[] Draw()
    {
        _engine!.MainContext.RunFrame();
        byte[] pixels = new byte[Size * Size * 4];
        _harness.Device.ReadTexture(_color!, TextureRegion.Whole(_color!), pixels);
        return pixels;
    }

    // Rows are counted from the bottom of the target.
    private static (byte R, byte G, byte B) Pixel(byte[] pixels, int x, int y)
    {
        int i = ((y * Size) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    private static void AssertColor((byte R, byte G, byte B) expected, byte[] pixels, int x, int y, string what)
    {
        (byte R, byte G, byte B) actual = Pixel(pixels, x, y);
        Assert.True(
            Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2 && Math.Abs(actual.B - expected.B) <= 2,
            $"Pixel ({x}, {y}), {what}, is {actual}; expected {expected}.");
    }
}
