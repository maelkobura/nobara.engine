// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

public sealed class DebugDrawTests
{
    private static readonly ColorF Red = new(1, 0, 0, 1);

    [Fact]
    public void InDevelopmentModeTheShapesAreDrawnBeforePostProcessing()
    {
        using var test = new Session(development: true);
        IDebugDraw draw = test.Harness.DebugDraw;
        Assert.True(draw.IsEnabled);
        test.Harness.Render.Insert(test.Harness.Context, RenderInsertionPoints.Ui, new TestInsertion("hud", new RecordingPass()));

        WorldPosition center = WorldPosition.FromBlocks(0, 0, -5);
        draw.Line(center, center.Offset(Vector3.UnitX), Red);
        draw.Box(center, new Vector3(0.5f), Red);
        draw.Sphere(center, 2, Red);
        Assert.Equal(1 + 12 + (3 * 24), draw.LineCount);

        test.Frame();

        Assert.Equal(["opaque", DebugDrawPass.PassName, "hud"], test.Passes);
        Assert.Contains($"draw {2 * (1 + 12 + (3 * 24))}", test.Harness.Fake.Trace);

        // A shape of no duration is drawn once.
        Assert.Equal(0, draw.LineCount);
        test.Frame();
        Assert.DoesNotContain(test.Harness.Fake.Trace, t => t.StartsWith("draw 2", StringComparison.Ordinal));
    }

    [Fact]
    public void AShapeStaysForItsDuration()
    {
        using var test = new Session(development: true);
        IDebugDraw draw = test.Harness.DebugDraw;
        WorldPosition origin = default;
        draw.Line(origin, origin.Offset(Vector3.UnitY), Red, duration: 1);
        draw.Line(origin, origin.Offset(Vector3.UnitZ), Red);

        test.Frame(TimeSpan.FromSeconds(0.2));
        Assert.Contains("draw 4", test.Harness.Fake.Trace);
        Assert.Equal(1, draw.LineCount);

        for (int i = 0; i < 3; i++)
        {
            test.Frame(TimeSpan.FromSeconds(0.2));
            Assert.Contains("draw 2", test.Harness.Fake.Trace);
        }

        test.Frame(TimeSpan.FromSeconds(0.2));
        Assert.Equal(0, draw.LineCount);

        draw.Line(origin, origin.Offset(Vector3.UnitY), Red, duration: 10);
        draw.Clear();
        Assert.Equal(0, draw.LineCount);
    }

    [Fact]
    public void OutsideDevelopmentModeDebugDrawingCostsNothing()
    {
        using var test = new Session(development: false);
        IDebugDraw draw = test.Harness.DebugDraw;
        WorldPosition center = WorldPosition.FromBlocks(0, 0, -5);
        draw.Sphere(center, 1, Red);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            draw.Line(center, center.Offset(Vector3.UnitX), Red, duration: 5);
            draw.Box(center, Vector3.One, Red);
            draw.Sphere(center, 1, Red);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        test.Frame();

        Assert.False(draw.IsEnabled);
        Assert.Equal(0, allocated);
        Assert.Equal(0, draw.LineCount);
        Assert.Equal(["opaque"], test.Passes);
        Assert.DoesNotContain(test.Harness.Device.GetLiveObjects(), o => o.Name?.Contains("debug-draw", StringComparison.Ordinal) == true);
    }

    private sealed class Session : IDisposable
    {
        private readonly Engine _engine;
        private readonly TestPipeline _pipeline;

        public Session(bool development)
        {
            Harness = new RendererHarness { DevelopmentMode = development };
            _engine = Harness.WithGame(RendererHarness.Compiled(Harness.CopyResources())).Start();
            _pipeline = new TestPipeline(Harness.Device);
            Harness.Render.RegisterPipeline(Harness.Context, _pipeline);
        }

        public RendererHarness Harness { get; }

        public List<string> Passes => [.. Harness.Fake.Trace.Where(t => t.StartsWith("pass ", StringComparison.Ordinal)).Select(t => t[5..])];

        public void Frame(TimeSpan elapsed = default)
        {
            Harness.Fake.Trace.Clear();
            Harness.Clock.Advance(elapsed);
            _engine.MainContext.RunFrame();
        }

        public void Dispose()
        {
            _engine.Dispose();
            _pipeline.Dispose();
            Harness.Dispose();
        }
    }
}
