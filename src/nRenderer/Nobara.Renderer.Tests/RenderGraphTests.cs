// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

public sealed class RenderGraphTests : IDisposable
{
    private static readonly RenderGraphTextureDescription Color = new(PixelFormat.Rgba16Float);

    private readonly RendererHarness _harness = new();
    private readonly Engine _engine;

    public RenderGraphTests()
    {
        _engine = _harness.WithGame(RendererHarness.Compiled(_harness.CopyResources())).Start();
    }

    public void Dispose()
    {
        _engine.Dispose();
        _harness.Dispose();
    }

    private IRenderer Render => _harness.Render;

    private List<string> Trace => _harness.Fake.Trace;

    private List<string> Passes => [.. Trace.Where(t => t.StartsWith("pass ", StringComparison.Ordinal)).Select(t => t[5..])];

    private void Frame()
    {
        Trace.Clear();
        _engine.MainContext.RunFrame();
    }

    private RenderRegistration Pipeline(Action<RenderGraphBuilder> build, string name = "test") =>
        Render.RegisterPipeline(_harness.Context, new DelegatePipeline(name, build));

    [Fact]
    public void APipelineOfTwoPassesDrawsAndThePassWhoseResultNothingReadsIsCulled()
    {
        using var pipeline = new TestPipeline(_harness.Device);
        Render.RegisterPipeline(_harness.Context, pipeline);

        Frame();

        Assert.Equal(["opaque"], Passes);
        Assert.Equal(["unused"], Render.LastGraph!.CulledPasses);
        Assert.Equal(0, pipeline.Unused.Runs);
        Assert.Equal(0, Render.LastGraph.TransientTextures);
        Assert.Contains("begin swapchain 1280x720 Clear Clear", Trace);
        Assert.Contains("viewport 0 0 1280 720", Trace);
    }

    [Fact]
    public void PassesRunInOrderAndShareTransientTexturesThatDoNotOverlap()
    {
        var passes = Enumerable.Range(0, 4).Select(_ => new RecordingPass()).ToArray();
        Pipeline(graph =>
        {
            RenderGraphTexture first = graph.CreateTexture("first", Color);
            RenderGraphTexture second = graph.CreateTexture("second", Color);
            RenderGraphTexture third = graph.CreateTexture("third", Color);
            graph.AddRasterPass("a", passes[0]).Color(first, LoadAction.Clear);
            graph.AddRasterPass("b", passes[1]).Read(first).Color(second, LoadAction.DontCare);
            graph.AddRasterPass("c", passes[2]).Read(second).Color(third, LoadAction.DontCare);
            graph.AddRasterPass("d", passes[3]).Read(third).Color(graph.Target, LoadAction.DontCare);
        });

        Frame();

        Assert.Equal(["a", "b", "c", "d"], Passes);
        Assert.Equal(3, Render.LastGraph!.TransientTextures);

        // 'third' starts after 'first' ends: they share one texture.
        Assert.Equal(2, Render.LastGraph.PhysicalTextures);
        Assert.Same(passes[0].Framebuffer, passes[2].Framebuffer);
        Assert.NotSame(passes[0].Framebuffer, passes[1].Framebuffer);
        Assert.All(passes, p => Assert.Equal(1, p.Runs));
    }

    [Fact]
    public void TransientTexturesAreKeptFromOneBuildToTheNext()
    {
        Pipeline(graph =>
        {
            RenderGraphTexture scene = graph.CreateTexture("scene", Color);
            graph.AddRasterPass("scene", new RecordingPass()).Color(scene, LoadAction.Clear);
            graph.AddRasterPass("present", new RecordingPass()).Read(scene).Color(graph.Target);
        });
        Frame();
        Texture[] before = Transients();

        Render.RebuildGraph();
        Frame();

        Assert.Equal(before, Transients());
        Assert.Single(before);
        Assert.Equal((1280, 720, TextureUsage.RenderTarget | TextureUsage.Sampled), (before[0].Width, before[0].Height, before[0].Usage));
    }

    [Fact]
    public void ABarrierMakesWhatAComputePassWroteVisibleToThePassThatReadsIt()
    {
        Pipeline(graph =>
        {
            RenderGraphBuffer vertices = graph.CreateBuffer("vertices", 4096);
            RenderGraphTexture mask = graph.CreateTexture("mask", new RenderGraphTextureDescription(PixelFormat.R32Float));
            graph.AddComputePass("generate", new RecordingPass()).Write(vertices).WriteStorage(mask);
            graph.AddRasterPass("draw", new RecordingPass()).Read(vertices, RenderBufferAccess.Vertex).Read(mask).Color(graph.Target);
        });

        Frame();

        Assert.Equal(["pass generate", "barrier VertexInput, Texture", "pass draw", "begin swapchain 1280x720 Load Load"], Trace.Where(t => !t.StartsWith("viewport", StringComparison.Ordinal)).Take(4));
        Assert.Equal(1, Render.LastGraph!.Barriers);
        Assert.Equal(1, Render.LastGraph.PhysicalBuffers);
    }

    [Fact]
    public void InsertedPassesRunAtTheirPointInTheOrderOfTheirRank()
    {
        using var pipeline = new TestPipeline(_harness.Device);
        Render.RegisterPipeline(_harness.Context, pipeline);
        var menu = new TestInsertion("menu", new RecordingPass());
        Render.Insert(_harness.Context, RenderInsertionPoints.Ui, menu, order: 10);
        Render.Insert(_harness.Context, RenderInsertionPoints.Ui, new TestInsertion("hud", new RecordingPass()), order: -5);
        Render.Insert(_harness.Context, RenderInsertionPoints.AfterOpaque, new TestInsertion("outline", new RecordingPass()));

        Frame();

        Assert.Equal(["opaque", "outline", "hud", "menu"], Passes);
        Assert.Equal(RenderInsertionPoints.Ui, menu.Point!.Value.Name);
        Assert.False(menu.Point.Value.Depth.IsValid);
    }

    [Fact]
    public void AnInsertionAtAPointThePipelineLacksIsNotDrawnAndAWarningSaysItOnce()
    {
        Pipeline(graph => graph.AddRasterPass("only", new RecordingPass()).Color(graph.Target, LoadAction.Clear));
        Render.Insert(_harness.Context, RenderInsertionPoints.Ui, new TestInsertion("hud", new RecordingPass()));

        Frame();
        Render.RebuildGraph();
        Frame();

        Assert.Equal(["only"], Passes);
        Assert.Single(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no insertion point 'Ui'", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovingAnInsertionBuildsTheGraphAgainWithoutIt()
    {
        using var pipeline = new TestPipeline(_harness.Device);
        Render.RegisterPipeline(_harness.Context, pipeline);
        RenderRegistration hud = Render.Insert(_harness.Context, RenderInsertionPoints.Ui, new TestInsertion("hud", new RecordingPass()));
        Frame();
        Frame();

        hud.Dispose();
        Frame();

        Assert.Equal(["opaque"], Passes);
        Assert.Equal(2, pipeline.Builds);
        Assert.False(hud.IsActive);
    }

    [Fact]
    public void ThePipelineBuildsTheGraphOnceUntilSomethingChanges()
    {
        using var pipeline = new TestPipeline(_harness.Device);
        Render.RegisterPipeline(_harness.Context, pipeline);

        Frame();
        Frame();
        Frame();
        Assert.Equal(1, pipeline.Builds);

        Render.RebuildGraph();
        Frame();
        Assert.Equal(2, pipeline.Builds);
    }

    [Fact]
    public void APassThatNeedsAMissingFeatureIsRefusedAndNothingIsDrawn()
    {
        var traced = new RecordingPass();
        Pipeline(graph => graph.AddRasterPass("traced", traced).Color(graph.Target).Require(GraphicsFeatures.RayTracing));

        Frame();
        Frame();

        Assert.Equal(0, traced.Runs);
        LogEntry error = Assert.Single(Errors());
        Assert.Contains("'traced' needs the graphics features RayTracing", error.Message, StringComparison.Ordinal);
        Assert.Contains($"module '{RendererHarness.Game}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APipelineThatNeedsAMissingFeatureIsRefused()
    {
        var pipeline = new DelegatePipeline("traced", _ => { }) { RequiredFeatures = GraphicsFeatures.RayTracing };

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => Render.RegisterPipeline(_harness.Context, pipeline));

        Assert.Contains("traced", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APassThatReadsWhatNoEarlierPassWritesIsRefused()
    {
        Pipeline(graph =>
        {
            RenderGraphTexture history = graph.CreateTexture("history", Color);
            graph.AddRasterPass("resolve", new RecordingPass()).Read(history).Color(graph.Target);
        });

        Frame();

        Assert.Contains("'resolve' reads 'history', which no earlier pass writes", Assert.Single(Errors()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScreenIsDrawnWithItsOwnDepthOnly()
    {
        Pipeline(graph =>
        {
            RenderGraphTexture depth = graph.CreateTexture("depth", new RenderGraphTextureDescription(PixelFormat.Depth32Float));
            graph.AddRasterPass("scene", new RecordingPass()).Color(graph.Target, LoadAction.Clear).Depth(depth, LoadAction.Clear);
        });

        Frame();

        Assert.Contains("the screen is drawn with its own depth", Assert.Single(Errors()).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CommandKind.BeginRenderPass, _harness.Fake.Executed);
    }

    [Fact]
    public void WithoutPipelineNothingIsDrawnAndAWarningSaysItOnce()
    {
        Frame();
        Frame();
        Frame();

        Assert.DoesNotContain(CommandKind.BeginRenderPass, _harness.Fake.Executed);
        Assert.Single(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("No render pipeline is registered", StringComparison.Ordinal));
        Assert.Contains(_harness.Fake.Calls, c => c.EndsWith("present", StringComparison.Ordinal));
        Assert.Null(Render.LastGraph);
    }

    [Fact]
    public void AViewIsDrawnByThePipelineItNames()
    {
        Pipeline(graph => graph.AddRasterPass("first", new RecordingPass()).Color(graph.Target), "first");
        Pipeline(graph => graph.AddRasterPass("second", new RecordingPass()).Color(graph.Target), "second");

        Frame();
        Assert.Equal(["first"], Passes);

        Render.MainView.Pipeline = "second";
        Frame();
        Assert.Equal(["second"], Passes);

        Render.MainView.Pipeline = "missing";
        Frame();
        Assert.Equal(["first"], Passes);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'missing'", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => Pipeline(_ => { }, "First"));
    }

    [Fact]
    public void AnExceptionOfAPassIsChargedToTheModuleThatDeclaredIt()
    {
        var broken = new RecordingPass { OnExecute = _ => throw new InvalidOperationException("broken") };
        Pipeline(graph => graph.AddRasterPass("broken", broken).Color(graph.Target));

        EngineStopException stop = Assert.Throws<EngineStopException>(Frame);

        Assert.Equal(RendererHarness.Game, stop.ModuleId);
    }

    private IEnumerable<LogEntry> Errors() => _harness.Log.Entries.Where(e => e.Level == LogLevel.Error);

    private Texture[] Transients() =>
        [.. _harness.Device.GetLiveObjects().OfType<Texture>().Where(t => t.Name?.StartsWith("nobara.renderer.transient", StringComparison.Ordinal) == true)];
}
