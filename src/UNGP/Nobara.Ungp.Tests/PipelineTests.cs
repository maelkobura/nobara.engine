// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer;
using Nobara.Ungp.Tests.Support;

namespace Nobara.Ungp.Tests;

public sealed class PipelineTests : IDisposable
{
    private readonly UngpHarness _harness = new();
    private Engine? _engine;

    public void Dispose()
    {
        _engine?.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void UngpIsRegisteredAndDrawsTheView()
    {
        Start();

        Assert.Same(_harness.Ungp.Pipeline, Assert.Single(_harness.Render.Pipelines));
        Assert.Equal(UngpModule.PipelineName, _harness.Ungp.Pipeline.Name);
        Assert.Equal(GraphicsFeatures.None, _harness.Ungp.Pipeline.RequiredFeatures);

        _engine!.MainContext.RunFrame();

        Assert.Equal(UngpModule.PipelineName, _harness.Render.LastGraph!.Pipeline);
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void TheModuleProvidesTheStandardShaderAndTheDefaultMaterial()
    {
        Start();

        RenderShader standard = _harness.Resources.GetShader(UngpModule.StandardShader);
        Assert.True(standard.TryGetProgram(UngpModule.CutoutVariant, out _));
        Assert.Contains(standard.Uniforms, u => u.Name == "View");
        RenderMaterial material = _harness.Resources.GetMaterial(UngpModule.DefaultMaterial);
        Assert.Same(standard, material.Shader);
        Assert.Equal(MaterialMode.Opaque, material.Mode);
        Assert.Empty(material.Textures);
    }

    [Fact]
    public void PassesRunInTheOrderOfThePipelineWithTheInsertionPoints()
    {
        Start();
        IRenderer render = _harness.Render;
        var afterOpaque = new TestInsertion("test.after-opaque");
        var beforePost = new TestInsertion("test.before-post");
        var ui = new TestInsertion("test.ui");
        render.Insert(_harness.Context, RenderInsertionPoints.Ui, ui);
        render.Insert(_harness.Context, RenderInsertionPoints.BeforePostProcess, beforePost);
        render.Insert(_harness.Context, RenderInsertionPoints.AfterOpaque, afterOpaque);

        _engine!.MainContext.RunFrame();

        Assert.Equal(
            [UngpPasses.DepthPrepass, UngpPasses.Opaque, "test.after-opaque", UngpPasses.Sky, "test.before-post", UngpPasses.Tonemap, "test.ui"],
            render.LastGraph!.Passes);
        Assert.Empty(render.LastGraph.CulledPasses);
        Assert.Equal(1, ui.Runs);

        // The scene points give the HDR color and the depth; the user interface draws on the target, without depth.
        Assert.True(afterOpaque.Point!.Value.Depth.IsValid);
        Assert.Equal(afterOpaque.Point.Value.Color, beforePost.Point!.Value.Color);
        Assert.NotEqual(afterOpaque.Point.Value.Color, ui.Point!.Value.Color);
        Assert.False(ui.Point.Value.Depth.IsValid);
        Assert.Equal(2, render.LastGraph.PhysicalTextures);
    }

    [Theory]
    [InlineData(false, true, true, new[] { UngpPasses.Opaque, UngpPasses.Sky, UngpPasses.Tonemap })]
    [InlineData(true, false, true, new[] { UngpPasses.DepthPrepass, UngpPasses.Opaque, UngpPasses.Tonemap })]
    [InlineData(true, true, false, new[] { UngpPasses.DepthPrepass, UngpPasses.Opaque, UngpPasses.Sky, UngpPasses.Output })]
    [InlineData(false, false, false, new[] { UngpPasses.Opaque, UngpPasses.Output })]
    public void TurningPassesOffChangesTheGraphWithoutBreakingIt(bool prepass, bool sky, bool tonemapping, string[] passes)
    {
        Start();
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor { Next = e => e.World.Create(_harness.Square("material:example.scene.red", 0, 0, -3)) });
        _engine!.MainContext.RunFrame();
        int draws = _harness.Fake.Executed.Count(k => k == CommandKind.DrawIndexed);
        Assert.Equal(2, draws);

        _harness.Ungp.DepthPrepass = prepass;
        _harness.Ungp.Sky = sky;
        _harness.Ungp.Tonemapping = tonemapping;
        _harness.Fake.Executed.Clear();
        _engine.MainContext.RunFrame();

        Assert.Equal(passes, _harness.Render.LastGraph!.Passes);
        Assert.Empty(_harness.Errors);

        // The square is drawn once by the opaque pass, and once more by the prepass when it runs.
        Assert.Equal(prepass ? 2 : 1, _harness.Fake.Executed.Count(k => k == CommandKind.DrawIndexed));

        // Turning the passes back on gives the first graph again.
        _harness.Ungp.DepthPrepass = _harness.Ungp.Sky = _harness.Ungp.Tonemapping = true;
        _engine.MainContext.RunFrame();
        Assert.Equal([UngpPasses.DepthPrepass, UngpPasses.Opaque, UngpPasses.Sky, UngpPasses.Tonemap], _harness.Render.LastGraph!.Passes);
    }

    [Fact]
    public void TheRenderScaleKeyScalesTheResolutionOfTheView()
    {
        _harness.Settings[UngpSettings.RenderScale] = "0.5";
        Start();

        _engine!.MainContext.RunFrame();

        RenderView view = _harness.Render.MainView;
        Assert.Equal(0.5f, _harness.Ungp.RenderScale);
        Assert.Equal(0.5f, view.RenderScale);
        Assert.Equal((640, 360), (view.RenderWidth, view.RenderHeight));
        Assert.Equal((1280, 720), (view.PixelViewport.Width, view.PixelViewport.Height));

        _harness.Ungp.RenderScale = 1;
        _engine.MainContext.RunFrame();
        Assert.Equal((1280, 720), (view.RenderWidth, view.RenderHeight));
    }

    [Fact]
    public void UngpLeavesTheScaleOfAViewThatAnotherPipelineDraws()
    {
        Start();
        _harness.Render.RegisterPipeline(_harness.Context, new EmptyPipeline());
        _harness.Render.MainView.Pipeline = "other";
        _harness.Ungp.RenderScale = 0.5f;

        _engine!.MainContext.RunFrame();

        Assert.Equal(1f, _harness.Render.MainView.RenderScale);
    }

    [Fact]
    public void TheKeysSetTheSkyColorAndTheExposure()
    {
        _harness.Settings[UngpSettings.SkyColor] = "#FF8000";
        _harness.Settings[UngpSettings.Exposure] = "2";
        Start();

        Assert.Equal(2f, _harness.Ungp.Exposure);
        ColorF sky = _harness.Ungp.SkyColor;
        Assert.Equal(1f, sky.R, 3);
        Assert.Equal(0.2159f, sky.G, 3);
        Assert.Equal(0f, sky.B, 3);
    }

    [Fact]
    public void ASkyColorThatIsNotAColorIsReportedAndIgnored()
    {
        _harness.Settings[UngpSettings.SkyColor] = "blue";
        Start();

        Assert.Contains(_harness.Warnings, w => w.Contains("'ungp.skyColor' is 'blue'", StringComparison.Ordinal));
        Assert.True(UngpSettings.TryParseColor(UngpSettings.DefaultSkyColor, out ColorF expected));
        Assert.Equal(expected, _harness.Ungp.SkyColor);
    }

    [Theory]
    [InlineData("#000000", true, 0f, 0f, 0f)]
    [InlineData("#ffffff", true, 1f, 1f, 1f)]
    [InlineData("#808080", true, 0.2158f, 0.2158f, 0.2158f)]
    [InlineData("808080", false, 0f, 0f, 0f)]
    [InlineData("#80808", false, 0f, 0f, 0f)]
    [InlineData("#GG0000", false, 0f, 0f, 0f)]
    [InlineData(null, false, 0f, 0f, 0f)]
    public void ColorsAreReadInSrgbAndGivenLinear(string? text, bool valid, float r, float g, float b)
    {
        Assert.Equal(valid, UngpSettings.TryParseColor(text, out ColorF color));
        Assert.Equal(r, color.R, 3);
        Assert.Equal(g, color.G, 3);
        Assert.Equal(b, color.B, 3);
    }

    [Fact]
    public void TheSettingsRejectValuesOutOfRange()
    {
        Start();

        Assert.Throws<ArgumentOutOfRangeException>(() => _harness.Ungp.Exposure = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => _harness.Ungp.Exposure = float.PositiveInfinity);
        Assert.Throws<ArgumentOutOfRangeException>(() => _harness.Ungp.RenderScale = 0.05f);
        Assert.Throws<ArgumentOutOfRangeException>(() => _harness.Ungp.RenderScale = 5);
    }

    [Fact]
    public void TransparentMaterialsAreLeftOutWithOneWarning()
    {
        Start();
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor
        {
            Next = e =>
            {
                e.World.Create(_harness.Square("material:example.scene.glass", 0, 0, -3));
                e.World.Create(_harness.Square("material:example.scene.glass", 1, 0, -3));
                e.World.Create(_harness.Square("material:example.scene.leaf", -1, 0, -3));
            },
        });

        for (int i = 0; i < 3; i++)
        {
            _engine!.MainContext.RunFrame();
        }

        Assert.Single(_harness.Warnings, w => w.Contains("material:example.scene.glass", StringComparison.Ordinal));

        // Only the cut-out square is drawn: by the prepass and the opaque pass, at each frame.
        Assert.Equal(6, _harness.Fake.Executed.Count(k => k == CommandKind.DrawIndexed));
    }

    [Fact]
    public void AFrameAllocatesNothingOnceStable()
    {
        Start();
        var ids = new RenderObjectId[200];
        int frame = 0;
        RenderObjectDescription[] squares =
        [
            _harness.Square("material:example.scene.red", 0, 0, -10),
            _harness.Square("material:example.scene.leaf", 0, 0, -10),
            _harness.Square(UngpModule.DefaultMaterial, 0, 0, -10),
            _harness.Square("material:example.scene.glass", 0, 0, -10),
        ];
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor(e =>
        {
            for (int i = 0; i < ids.Length; i++)
            {
                RenderTransform transform = RenderTransform.At(WorldPosition.FromBlocks(i % 20, frame % 7, -10 - (i / 20)));
                if (!ids[i].IsValid)
                {
                    ids[i] = e.World.Create(squares[i % squares.Length] with { Transform = transform });
                }
                else if (e.IsNewTick)
                {
                    e.World.Move(ids[i], transform);
                }
            }

            e.MainView.MoveCamera(new Camera { Position = WorldPosition.FromBlocks(frame % 3, 0, 0) });
            frame++;
        }));
        TimeSpan step = TimeSpan.FromSeconds(1.0 / 60);
        for (int i = 0; i < 30; i++)
        {
            _harness.Clock.Advance(step);
            _engine!.MainContext.RunFrame();
        }

        _harness.Fake.Recording = false;
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                _harness.Clock.Advance(step);
                _engine!.MainContext.RunFrame();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(200, _harness.Render.MainView.Items.Length);
        Assert.Empty(_harness.Errors);
    }

    private void Start() => _engine = _harness.Start();

    private sealed class EmptyPipeline : IRenderPipeline, IRenderPass
    {
        public string Name => "other";

        public void Build(RenderGraphBuilder graph) => graph.AddRasterPass("other.clear", this).Color(graph.Target, LoadAction.Clear);

        public void Execute(RenderPassContext context)
        {
        }
    }
}
