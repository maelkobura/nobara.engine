// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Frames;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

public sealed class ViewTests : IDisposable
{
    private readonly RendererHarness _harness = new();
    private readonly Engine _engine;
    private readonly RecordingPass _scene = new();
    private int _builds;

    public ViewTests()
    {
        _engine = _harness.WithGame(RendererHarness.Compiled(_harness.CopyResources())).Start();
        _harness.Render.RegisterPipeline(_harness.Context, new DelegatePipeline("test", graph =>
        {
            _builds++;
            RenderGraphTexture scene = graph.CreateTexture("scene", new RenderGraphTextureDescription(PixelFormat.Rgba16Float));
            RenderGraphTexture half = graph.CreateTexture("half", new RenderGraphTextureDescription(PixelFormat.Rgba16Float) { Size = RenderTextureSize.TargetResolution, Scale = 0.5f });
            graph.AddRasterPass("scene", _scene).Color(scene, LoadAction.Clear);
            graph.AddRasterPass("bloom", new RecordingPass()).Read(scene).Color(half, LoadAction.DontCare);
            RenderPassBuilder present = graph.AddRasterPass("present", new RecordingPass()).Read(half).Color(graph.Target, LoadAction.Clear);
            if (graph.TargetDepth.IsValid)
            {
                present.Depth(graph.TargetDepth, LoadAction.Clear);
            }
        }));
    }

    public void Dispose()
    {
        _engine.Dispose();
        _harness.Dispose();
    }

    private RenderView View => _harness.Render.MainView;

    private List<string> Trace => _harness.Fake.Trace;

    private void Frame()
    {
        Trace.Clear();
        _engine.MainContext.RunFrame();
    }

    [Fact]
    public void TheProjectionReversesDepth()
    {
        var camera = new Camera { NearPlane = 0.1f, VerticalFieldOfView = MathF.PI / 2 };
        Matrix4x4 projection = camera.GetProjectionMatrix(2);

        Assert.Equal(1, Depth(projection, 0.1f), 5);
        Assert.Equal(0.01f, Depth(projection, 10), 5);
        Assert.True(Depth(projection, 1e9f) is > 0 and < 1e-9f);

        Matrix4x4 finite = (camera with { FarPlane = 100 }).GetProjectionMatrix(2);
        Assert.Equal(1, Depth(finite, 0.1f), 5);
        Assert.Equal(0, Depth(finite, 100), 5);
        Assert.Equal(0f, Camera.ClearDepth);
        Assert.Equal(CompareFunction.GreaterEqual, Camera.DepthCompare);

        // A point at the edge of the view, at the top: 45 degrees up, the height of the view is 1 in clip space.
        Vector4 top = Vector4.Transform(new Vector4(0, 1, -1, 1), projection);
        Assert.Equal(1, top.Y / top.W, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => (camera with { FarPlane = 0.05f }).GetProjectionMatrix(1));
    }

    [Fact]
    public void TheViewMatrixTurnsTheWorldAgainstTheCamera()
    {
        var camera = new Camera { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) };

        // Turned left by 90 degrees, the camera looks along -X: a point at -X is in front of it.
        Vector3 seen = Vector3.Transform(new Vector3(-5, 0, 0), camera.GetViewMatrix());

        Assert.Equal(0, seen.X, 5);
        Assert.Equal(-5, seen.Z, 5);
    }

    [Fact]
    public void APositionHasTheSamePrecisionFarFromTheOrigin()
    {
        var position = new WorldPosition(0, 0, 0, new Vector3(-0.5f, 33, 64));
        Assert.Equal((-1, 1, 2), (position.ChunkX, position.ChunkY, position.ChunkZ));
        Assert.Equal(new Vector3(31.5f, 1, 0), position.Local);

        WorldPosition far = WorldPosition.FromBlocks(-1_000_000_000.75, 1_000_000_000.25, 0);
        Assert.Equal(new Vector3(31.25f, 0.25f, 0), far.Local);
        Assert.Equal((-31_250_001, 31_250_000), (far.ChunkX, far.ChunkY));
        Assert.Equal(new Vector3(1.5f, -0.25f, 2), far.Offset(new Vector3(1.5f, -0.25f, 2)).RelativeTo(far));
        Assert.Equal(new Vector3(0.5f, 0, 0), WorldPosition.Lerp(far, far.Offset(Vector3.UnitX), 0.5f).RelativeTo(far));
        Assert.Equal(far, WorldPosition.FromBlocks(-1_000_000_000.75, 1_000_000_000.25, 0));
    }

    [Fact]
    public void TheViewFollowsTheSizeOfTheWindowInPixels()
    {
        Frame();
        Assert.Equal((1280, 720), (View.RenderWidth, View.RenderHeight));

        _harness.Windows.Queue(WindowEventKind.Resized, w => w.Size = w.PixelSize = new WindowSize(800, 600));
        Frame();

        Assert.Equal((800, 600, 800, 600), (View.TargetWidth, View.TargetHeight, View.RenderWidth, View.RenderHeight));
        Assert.Contains("begin swapchain 800x600 Clear Clear", Trace);
        Assert.Contains("viewport 0 0 800 600", Trace);
        Assert.Contains(Trace, t => t.StartsWith("begin nobara.renderer.scene 800x600", StringComparison.Ordinal));
        Assert.Contains(Trace, t => t.StartsWith("begin nobara.renderer.bloom 400x300", StringComparison.Ordinal));
        Assert.Equal(2, _builds);
    }

    [Fact]
    public void NothingIsDrawnWhileTheWindowIsMinimized()
    {
        Frame();
        int runs = _scene.Runs;

        _harness.Windows.Queue(WindowEventKind.Minimized, w => w.IsMinimized = true);
        Frame();
        Frame();
        Assert.Equal(runs, _scene.Runs);
        Assert.Empty(Trace);
        Assert.Contains(_harness.Fake.Calls, c => c.StartsWith("end ", StringComparison.Ordinal) && !c.EndsWith("present", StringComparison.Ordinal));

        _harness.Windows.Queue(WindowEventKind.Restored, w => w.IsMinimized = false);
        Frame();
        Assert.Equal(runs + 1, _scene.Runs);
    }

    [Fact]
    public void AViewportAndARenderScaleSizeTheFrame()
    {
        View.Viewport = new ViewRect(0.5f, 0, 0.5f, 1);
        View.RenderScale = 0.5f;

        Frame();

        Assert.Equal(new PixelRect(640, 0, 640, 720), View.PixelViewport);
        Assert.Equal((320, 360), (View.RenderWidth, View.RenderHeight));
        Assert.Contains(Trace, t => t.StartsWith("begin nobara.renderer.scene 320x360", StringComparison.Ordinal));
        Assert.Contains(Trace, t => t.StartsWith("begin nobara.renderer.bloom 320x360", StringComparison.Ordinal));
        Assert.Contains("viewport 640 0 640 720", Trace);
        Assert.Throws<ArgumentOutOfRangeException>(() => View.RenderScale = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => View.Viewport = new ViewRect(0.5f, 0, 0.6f, 1));
    }

    [Fact]
    public void AViewDrawsToATextureInsteadOfTheScreen()
    {
        Texture color = _harness.Device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, 256, 128, TextureUsage.RenderTarget | TextureUsage.Sampled) with { Name = "capture" });
        Texture depth = _harness.Device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Depth32Float, 256, 128, TextureUsage.DepthStencil));
        View.SetTarget(color, depth);

        Frame();

        Assert.Contains(Trace, t => t.StartsWith("begin nobara.renderer.present 256x128", StringComparison.Ordinal));
        Assert.DoesNotContain(Trace, t => t.StartsWith("begin swapchain", StringComparison.Ordinal));
        Assert.Equal((256, 128), (View.TargetWidth, View.TargetHeight));

        View.SetScreenTarget();
        Frame();
        Assert.Contains(Trace, t => t.StartsWith("begin swapchain", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => View.SetTarget(depth));
    }

    private static float Depth(Matrix4x4 projection, float distance)
    {
        Vector4 clip = Vector4.Transform(new Vector4(0, 0, -distance, 1), projection);
        return clip.Z / clip.W;
    }
}
