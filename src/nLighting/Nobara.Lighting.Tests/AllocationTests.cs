// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Lighting.Tests;

public sealed class AllocationTests : IDisposable
{
    private readonly LightingHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void APropagationPassAllocatesNothingOnceWarm()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (31, 16, 16, probe.State("torch")), (40, 16, 16, 0), (10, 10, 10, probe.State("lamp")));
        _harness.Tick(engine);

        // Points of interest and a dynamic light, which every tick reads.
        using LightFocus focus = probe.Lighting.AddFocus(probe.Context, DimensionId.Default, WorldPosition.FromBlocks(0, 16, 0));
        probe.Tickets.Add(probe.Loading.AddAreaTicket(probe.Context, grid, WorldPosition.FromBlocks(40, 16, 0), new ChunkArea(0, 0), TicketLevel.Active));
        Assert.True(probe.Entities.TryGet(DimensionId.Default, out EntityWorld? world));
        EntityId light = world.Create(world.GetArchetype(probe.Sources.DynamicLight, probe.Entities.WorldTransform));
        world.Set(light, probe.Sources.DynamicLight, new DynamicLight { Color = new LightColor(10, 10, 10), Radius = 6 });
        var torch = new BlockCoordinate(31, 16, 16);
        for (int i = 0; i < 3; i++)
        {
            probe.Internal.Reseed(grid, torch);
            probe.Internal.RunPass();
        }

        // The loop thread runs some of the jobs while it waits for them; the analyzers check the code of the jobs on every thread.
        Assert.Equal(0, Allocated(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                probe.Internal.Reseed(grid, torch);
                probe.Internal.RunPass();
            }
        }));
        Assert.Equal(14, probe.Block(grid, 31, 16, 16));

        _harness.Tick(engine, 30);
        Assert.Equal(0, Allocated(() => _harness.Tick(engine, 60)));
    }

    // The runtime may allocate now and then on this thread, such as while it compiles methods again: a round without allocation must come.
    private static long Allocated(Action work)
    {
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            work();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return allocated;
    }
}
