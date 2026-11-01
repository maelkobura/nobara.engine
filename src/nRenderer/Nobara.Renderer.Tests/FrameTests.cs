// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

public sealed class FrameTests : IDisposable
{
    private readonly RendererHarness _harness = new() { DevelopmentMode = true };
    private readonly Engine _engine;

    public FrameTests()
    {
        _engine = _harness.WithGame(RendererHarness.Compiled(_harness.CopyResources())).Start();
    }

    public void Dispose()
    {
        _engine.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void AFrameAllocatesNothingOnceStable()
    {
        using var pipeline = new TestPipeline(_harness.Device);
        _harness.Render.RegisterPipeline(_harness.Context, pipeline);
        _harness.Render.Insert(_harness.Context, RenderInsertionPoints.Ui, new TestInsertion("hud", new RecordingPass()));
        RenderMesh crate = _harness.Resources.GetMesh("mesh:example.render.crate");
        IDebugDraw debug = _harness.DebugDraw;
        // Below the number of objects that nRenderer hands to jobs: the first wait of a thread for a job may allocate once, when the machine is busy.
        var ids = new RenderObjectId[200];
        int frame = 0;
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor(e =>
        {
            for (int i = 0; i < ids.Length; i++)
            {
                RenderTransform transform = RenderTransform.At(WorldPosition.FromBlocks(i, frame % 7, -10));
                if (!ids[i].IsValid)
                {
                    ids[i] = e.World.Create(new RenderObjectDescription(crate, transform));
                }
                else if (e.IsNewTick)
                {
                    e.World.Move(ids[i], transform);
                }
            }

            e.MainView.MoveCamera(new Camera { Position = WorldPosition.FromBlocks(frame % 3, 0, 0) });
            debug.Box(WorldPosition.FromBlocks(0, 0, -10), new Vector3(0.5f), new ColorF(0, 1, 0, 1));
            frame++;
        }));
        TimeSpan step = TimeSpan.FromSeconds(1.0 / 60);
        for (int i = 0; i < 30; i++)
        {
            _harness.Clock.Advance(step);
            _engine.MainContext.RunFrame();
        }

        _harness.Fake.Recording = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++)
        {
            _harness.Clock.Advance(step);
            _engine.MainContext.RunFrame();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(400, _harness.Render.MainView.Items.Length);
        Assert.Equal(1, pipeline.Builds);
    }

    [Fact]
    public void AnExceptionOfAnExtractorIsChargedToItsModule()
    {
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor(_ => throw new InvalidOperationException("broken")));

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _engine.MainContext.RunFrame());

        Assert.Equal(RendererHarness.Game, stop.ModuleId);
    }

    [Fact]
    public void ARemovedExtractorIsNoLongerCalled()
    {
        var extractor = new TestExtractor();
        RenderRegistration registration = _harness.Render.AddExtractor(_harness.Context, extractor);
        _engine.MainContext.RunFrame();

        registration.Dispose();
        _engine.MainContext.RunFrame();

        Assert.Equal(1, extractor.Runs);
        Assert.True(_harness.Render.IsCreated);
        Assert.False(registration.IsActive);
    }

    [Fact]
    public void TheGpuObjectsOfTheFramesAreReleasedWithTheEngine()
    {
        using var pipeline = new TestPipeline(_harness.Device);
        _harness.Render.RegisterPipeline(_harness.Context, pipeline);
        _engine.MainContext.RunFrame();
        pipeline.Dispose();

        _engine.Dispose();

        Assert.DoesNotContain(_harness.Log.Entries, e => e.Message.Contains("were not disposed", StringComparison.Ordinal));
    }
}
