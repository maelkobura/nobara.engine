// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using ImGuiNET;
using Nobara.Core;
using Nobara.Debug.Tests.Support;
using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Debug.Tests;

/// <summary>
/// Draws Dear ImGui to a texture with OpenGL, over a target that the test
/// pipeline clears to red, and reads its pixels. Rows are counted from the
/// bottom of the target.
/// </summary>
[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class OpenGLRenderTests : IDisposable
{
    private const int Size = 128;

    private readonly DebugHarness _harness = new(gpu: true);
    private Engine? _engine;
    private Texture? _color;

    public void Dispose()
    {
        _color?.Dispose();
        _engine?.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void A_fullscreen_screen_covers_the_target_with_its_background_and_its_text()
    {
        Start();
        _harness.Debug.AddWindow(
            _harness.GameModule.Context,
            new DebugWindowOptions { Title = "Menu", Kind = DebugWindowKind.Screen, Placement = DebugWindowPlacement.Fullscreen },
            new DelegateWindow(_ => ImGui.Text("WWWWWWWW")));

        byte[] pixels = Draw(2);

        // The background of a window, 94 % opaque, over red.
        (byte R, byte G, byte B) center = Pixel(pixels, Size / 2, Size / 4);
        Assert.InRange(center.R, 22, 38);
        Assert.InRange(center.G, 8, 22);
        Assert.Equal(center.G, center.B);

        // The text, white, near the top-left corner.
        bool text = false;
        for (int y = Size - 48; y < Size; y++)
        {
            for (int x = 0; x < Size / 2; x++)
            {
                text |= Pixel(pixels, x, y).G > 128;
            }
        }

        Assert.True(text, "No pixel of the text was drawn.");
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void A_window_draws_inside_its_rectangle_only()
    {
        Start();
        _harness.Debug.IsOpen = true;
        _harness.Debug.AddWindow(
            _harness.GameModule.Context,
            new DebugWindowOptions { Title = "Chunks", Placement = DebugWindowPlacement.Center, Size = new Vector2(80, 80) },
            new DelegateWindow());

        byte[] pixels = Draw(2);

        Assert.Equal((255, 0, 0), Pixel(pixels, 2, 2));
        Assert.Equal((255, 0, 0), Pixel(pixels, Size - 3, Size / 2));
        (byte R, byte G, byte B) inside = Pixel(pixels, Size / 2, Size / 2 - 20);
        Assert.InRange(inside.R, 22, 38);
        Assert.Empty(_harness.Errors);
    }

    private void Start()
    {
        _engine = _harness.Start();
        GraphicsDevice device = _harness.GameModule.Context.Resolve<IGraphicsSystem>().Device;
        _color = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, Size, Size, TextureUsage.RenderTarget | TextureUsage.Sampled));
        _harness.GameModule.Context.Resolve<IRenderer>().MainView.SetTarget(_color);
    }

    private byte[] Draw(int frames)
    {
        _harness.Frame(_engine!, frames);
        byte[] pixels = new byte[Size * Size * 4];
        _harness.GameModule.Context.Resolve<IGraphicsSystem>().Device.ReadTexture(_color!, TextureRegion.Whole(_color!), pixels);
        return pixels;
    }

    private static (byte R, byte G, byte B) Pixel(byte[] pixels, int x, int y)
    {
        int i = ((y * Size) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }
}
