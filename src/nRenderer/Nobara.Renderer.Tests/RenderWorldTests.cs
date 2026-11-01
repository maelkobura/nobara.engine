// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

public sealed class RenderWorldTests : IDisposable
{
    private readonly RendererHarness _harness = new();
    private readonly Engine _engine;
    private readonly TestPipeline _pipeline;
    private readonly TestExtractor _extractor = new();
    private readonly RenderMesh _crate;

    public RenderWorldTests()
    {
        _engine = _harness.WithGame(RendererHarness.Compiled(_harness.CopyResources())).Start();
        _pipeline = new TestPipeline(_harness.Device);
        _harness.Render.RegisterPipeline(_harness.Context, _pipeline);
        _harness.Render.AddExtractor(_harness.Context, _extractor);
        _crate = _harness.Resources.GetMesh("mesh:example.render.crate");
    }

    public void Dispose()
    {
        _engine.Dispose();
        _pipeline.Dispose();
        _harness.Dispose();
    }

    private TimeSpan Tick => _harness.Context.Loop.TickDuration;

    private RenderWorld World => _harness.Render.World;

    private RenderView View => _harness.Render.MainView;

    private void Frame(TimeSpan elapsed, Action<RenderExtraction>? extract = null)
    {
        _harness.Clock.Advance(elapsed);
        _extractor.Next = extract;
        _engine.MainContext.RunFrame();
    }

    private static RenderTransform At(float x) => RenderTransform.At(WorldPosition.FromBlocks(x, 0, -5));

    private float DrawnX(RenderObjectId id) => FirstItem(id).Model.Translation.X;

    private RenderItem FirstItem(RenderObjectId id)
    {
        foreach (RenderItem item in View.Items)
        {
            if (item.Object == id)
            {
                return item;
            }
        }

        throw new InvalidOperationException($"{id} is not drawn.");
    }

    [Fact]
    public void AnObjectThatTheSimulationMovesIsInterpolatedBetweenItsLastTwoStates()
    {
        RenderObjectId crate = default;
        Frame(TimeSpan.Zero, e => crate = e.World.Create(new RenderObjectDescription(_crate, At(0))));

        bool newTick = false;
        Frame(Tick, e =>
        {
            newTick = e.IsNewTick;
            e.World.Move(crate, At(10));
        });
        Assert.True(newTick);
        Assert.Equal(0, DrawnX(crate), 4);

        Frame(Tick / 2, e => newTick = e.IsNewTick);
        Assert.False(newTick);
        Assert.Equal(5, DrawnX(crate), 3);

        // The next tick does not move it: it stays at its last state.
        Frame(Tick - (Tick / 2));
        Assert.Equal(10, DrawnX(crate), 4);
        Assert.Equal(_pipeline.Opaque.Draws, View.Items.Length * 4);
    }

    [Fact]
    public void ATeleportedObjectIsNotInterpolated()
    {
        RenderObjectId crate = default;
        Frame(TimeSpan.Zero, e => crate = e.World.Create(new RenderObjectDescription(_crate, At(0))));

        Frame(Tick + (Tick / 2), e => e.World.Teleport(crate, At(10)));

        Assert.Equal(10, DrawnX(crate), 4);
    }

    [Fact]
    public void AnObjectThatAppearsIsNotInterpolatedAndOneThatDisappearsIsNotDrawn()
    {
        RenderObjectId crate = default;
        Frame(Tick + (Tick / 2), e => crate = e.World.Create(new RenderObjectDescription(_crate, At(7))));
        Assert.Equal(7, DrawnX(crate), 4);
        Assert.Equal(2, View.Items.Length);

        Frame(Tick, e => e.World.Destroy(crate));

        Assert.Equal(0, View.Items.Length);
        Assert.False(World.Contains(crate));
        Assert.Equal(0, World.Count);
    }

    [Fact]
    public void ObjectsStayInTheWorldFromFrameToFrame()
    {
        RenderObjectId first = default;
        RenderObjectId second = default;
        Frame(TimeSpan.Zero, e =>
        {
            first = e.World.Create(new RenderObjectDescription(_crate, At(1)));
            second = e.World.Create(new RenderObjectDescription(_crate, At(2)) { Visible = false });
        });

        for (int i = 0; i < 5; i++)
        {
            Frame(Tick);
        }

        Assert.Equal(2, World.Count);
        Assert.Equal([first, first], View.Items.ToArray().Select(i => i.Object));
        Assert.Equal(["Wood", "Leaves"], View.Items.ToArray().Select(i => _crate.Submeshes[i.Submesh].Slot));

        // A destroyed slot is reused with a new generation: the old id names nothing.
        Frame(TimeSpan.Zero, e => e.World.Destroy(first));
        RenderObjectId third = default;
        Frame(TimeSpan.Zero, e => third = e.World.Create(new RenderObjectDescription(_crate, At(3))));
        Assert.Equal(first.Index, third.Index);
        Assert.NotEqual(first, third);
        Assert.False(World.Contains(first));
        Assert.True(World.Contains(second));
    }

    [Fact]
    public void AMaterialOfTheObjectReplacesTheMaterialsOfTheSlots()
    {
        RenderMaterial stone = _harness.Resources.GetMaterial("material:example.render.stone");
        Frame(TimeSpan.Zero, e => e.World.Create(new RenderObjectDescription(_crate, At(1)) { Material = stone }));

        Assert.All(View.Items.ToArray(), i => Assert.Same(stone, i.Material));
    }

    [Fact]
    public void TheWorldAndTheCameraChangeDuringExtractionOnly()
    {
        Assert.False(World.IsWritable);
        Assert.Throws<InvalidOperationException>(() => World.Create(new RenderObjectDescription(_crate, At(0))));
        Assert.Throws<InvalidOperationException>(() => View.TeleportCamera(new Camera()));

        RenderObjectId stale = default;
        Frame(TimeSpan.Zero, e =>
        {
            stale = e.World.Create(new RenderObjectDescription(_crate, At(0)));
            e.World.Destroy(stale);
        });
        Frame(TimeSpan.Zero, e => Assert.Throws<ArgumentException>(() => e.World.Move(stale, At(1))));
    }

    [Fact]
    public void TheCameraIsInterpolatedLikeAnObject()
    {
        Frame(TimeSpan.Zero, e => e.MainView.TeleportCamera(new Camera { Position = WorldPosition.FromBlocks(0, 0, 0) }));
        Frame(Tick, e => e.MainView.MoveCamera(new Camera { Position = WorldPosition.FromBlocks(4, 0, 0) }));

        Frame(Tick / 4);

        Assert.Equal(1, View.FrameCamera.Position.RelativeTo(default).X, 4);
        Assert.Equal(4, View.Camera.Position.RelativeTo(default).X, 4);
    }

    [Fact]
    public void ObjectsFarFromTheOriginAreDrawnRelativeToTheCamera()
    {
        WorldPosition camera = WorldPosition.FromBlocks(1_000_000_000.5, -2_000_000.25, 750_000_000);
        RenderObjectId crate = default;
        Frame(TimeSpan.Zero, e =>
        {
            e.MainView.TeleportCamera(new Camera { Position = camera });
            crate = e.World.Create(new RenderObjectDescription(_crate, RenderTransform.At(camera.Offset(new Vector3(1.25f, -0.5f, -3)))));
        });

        Assert.Equal(new Vector3(1.25f, -0.5f, -3), FirstItem(crate).Model.Translation);
    }

    [Fact]
    public void ManyObjectsAreTransformedByJobs()
    {
        var ids = new RenderObjectId[1000];
        Frame(TimeSpan.Zero, e =>
        {
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = e.World.Create(new RenderObjectDescription(_crate, At(i)));
            }
        });
        Frame(Tick, e =>
        {
            for (int i = 0; i < ids.Length; i++)
            {
                e.World.Move(ids[i], At(i + 2));
            }
        });

        Frame(Tick / 2);

        Assert.Equal(2000, View.Items.Length);
        Assert.Equal(1, DrawnX(ids[0]), 3);
        Assert.Equal(1000, DrawnX(ids[999]), 3);
    }
}
