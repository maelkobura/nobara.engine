// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
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
        var torch = new BlockCoordinate(31, 16, 16);
        for (int i = 0; i < 3; i++)
        {
            probe.Internal.Reseed(grid, torch);
            probe.Internal.RunPass();
        }

        // The loop thread runs some of the jobs while it waits for them; the analyzers check the code of the jobs on every thread.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            probe.Internal.Reseed(grid, torch);
            probe.Internal.RunPass();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(14, probe.Block(grid, 31, 16, 16));

        _harness.Tick(engine, 30);
        before = GC.GetAllocatedBytesForCurrentThread();
        _harness.Tick(engine, 60);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
