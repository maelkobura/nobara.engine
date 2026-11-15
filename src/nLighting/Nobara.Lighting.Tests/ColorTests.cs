// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Lighting.Tests;

public sealed class ColorTests : IDisposable
{
    private readonly LightingHarness _harness = new(colored: true);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ARedSourceDoesNotCrossBlueGlass()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, [.. PropagationTests.Wall(15, probe.State("stone"), probe.State("blueglass"))]);
        probe.Set(grid, (13, 16, 16, probe.State("redlamp")));
        _harness.Tick(engine);

        Assert.Equal(new LightLevel(15, 14, 0, 0), probe.Light(grid, 14, 16, 16));
        Assert.Equal(new LightLevel(15, 0, 0, 0), probe.Light(grid, 15, 16, 16));
        Assert.Equal(new LightLevel(15, 0, 0, 0), probe.Light(grid, 16, 16, 16));
    }

    [Fact]
    public void OnlyTheRedOfAWhiteSourceCrossesRedGlass()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, [.. PropagationTests.Wall(15, probe.State("stone"), probe.State("redglass"))]);
        probe.Set(grid, (13, 16, 16, probe.State("torch")));
        _harness.Tick(engine);

        Assert.Equal(new LightLevel(15, 13, 13, 13), probe.Light(grid, 14, 16, 16));
        Assert.Equal(new LightLevel(15, 12, 0, 0), probe.Light(grid, 15, 16, 16));
        Assert.Equal(new LightLevel(15, 11, 0, 0), probe.Light(grid, 16, 16, 16));

        probe.Set(grid, (13, 16, 16, probe.State("redlamp")));
        _harness.Tick(engine);
        Assert.Equal(new LightLevel(15, 13, 0, 0), probe.Light(grid, 15, 16, 16));
        Assert.Equal(new LightLevel(15, 12, 0, 0), probe.Light(grid, 16, 16, 16));
    }

    [Fact]
    public void ComponentsSpreadAndLeaveOnTheirOwn()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (10, 16, 16, probe.State("redlamp")), (16, 16, 16, probe.State("torch")));
        _harness.Tick(engine);
        Assert.Equal(new LightLevel(15, 12, 11, 11), probe.Light(grid, 13, 16, 16));

        probe.Set(grid, (16, 16, 16, 0));
        _harness.Tick(engine);
        Assert.Equal(new LightLevel(15, 12, 0, 0), probe.Light(grid, 13, 16, 16));

        probe.Set(grid, (10, 16, 16, 0));
        _harness.Tick(engine);
        Assert.Equal(new LightLevel(15, 0, 0, 0), probe.Light(grid, 13, 16, 16));
    }

    [Fact]
    public void AColoredChunkTakesTwoBytesPerBlock()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (16, 16, 16, probe.State("redlamp")));
        _harness.Tick(engine);

        Assert.True(probe.Lighting.IsColored);
        Assert.True(probe.Internal.TryGetChunk(grid, new ChunkCoordinate(0, 0, 0), out LightChunk? chunk));
        Assert.Equal(65_536, chunk!.StorageBytes);
        Assert.Equal(new LightColor(15, 0, 0), probe.Sources.GetEmission(probe.State("redlamp")));
        Assert.Equal(new LightColor(15, 15, 0), probe.Sources.GetFilter(probe.State("blueglass")));
    }
}
