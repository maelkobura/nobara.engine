// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Voxel;

namespace Nobara.Lighting.Tests;

public sealed class GridLightTests : IDisposable
{
    private readonly LightingHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ATurnedBoundedGridLightsItselfWithItsOwnSkyAndSources()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        uint stone = probe.State("stone");
        var transform = new WorldTransform(WorldPosition.FromBlocks(500, 80, -300), Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0), Vector3.One);
        Grid ship = probe.CreateBoundedGrid(new GridBounds(new BlockCoordinate(0, 0, 0), new BlockCoordinate(15, 15, 15)), transform);
        var hull = new List<(int, int, int, uint)>();
        for (int x = 0; x <= 15; x++)
        {
            for (int z = 0; z <= 15; z++)
            {
                hull.Add((x, 0, z, stone));
                hull.Add((x, 8, z, stone));
            }
        }

        probe.Set(ship, [.. hull]);
        _harness.Tick(engine);

        // Its sky is in its own frame: above its deck, and from its open sides under it.
        Assert.Equal(15, probe.Light(ship, 8, 10, 8).Sky);
        Assert.Equal(0, probe.Light(ship, 8, 8, 8).Sky);
        Assert.Equal(7, probe.Light(ship, 8, 4, 8).Sky);
        Assert.Equal(14, probe.Light(ship, 15, 4, 8).Sky);

        probe.Set(ship, (8, 4, 8, probe.State("torch")));
        _harness.Tick(engine);
        Assert.Equal(14, probe.Block(ship, 8, 4, 8));
        Assert.Equal(10, probe.Block(ship, 12, 4, 8));
    }

    [Fact]
    public void TheTerrainSampleOfAGridFallsWhenItEntersUnderARoof()
    {
        _harness.ConfigureOwner = probe => probe.Loading.AddProvider(probe.Context, new TestTerrain(() => probe.State("stone")) { Roof = 20 });
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        probe.Hold(0, 0, 0);
        _harness.RunUntil(engine, () => probe.Loading.GetStatus(probe.Terrain, new ChunkCoordinate(0, 0, 0)).IsLoaded);
        Grid ship = probe.CreateBoundedGrid(new GridBounds(new BlockCoordinate(0, 0, 0), new BlockCoordinate(3, 3, 3)), WorldTransform.At(WorldPosition.FromBlocks(10, 24, 10)));
        probe.Set(ship, (1, 1, 1, probe.State("stone")));
        _harness.Tick(engine, 2);

        Assert.True(probe.Lighting.TryGetTerrainSample(ship, out TerrainLightSample open));
        Assert.Equal(15, open.Sky);
        Assert.Equal(9, open.Points);
        Assert.False(probe.Lighting.TryGetTerrainSample(probe.Terrain, out _));

        // Under the layer of stone, read at nine points: no sky reaches there, as the chunks beside are not loaded.
        ship.UpdateTransform(WorldTransform.At(WorldPosition.FromBlocks(10, 8, 10)));
        _harness.Tick(engine);
        Assert.True(probe.Lighting.TryGetTerrainSample(ship, out TerrainLightSample covered));
        Assert.Equal(0, covered.Sky);
        Assert.True(covered.Version > open.Version);

        // A torch of the terrain beside it brightens it; the grid gives the terrain nothing.
        probe.Set(probe.Terrain, (12, 10, 12, probe.State("torch")));
        _harness.Tick(engine);
        Assert.True(probe.Lighting.TryGetTerrainSample(ship, out TerrainLightSample lit));
        Assert.True(lit.Red > 0);
        Assert.Equal(lit.Red, lit.Blue);
        Assert.Equal(0, probe.Block(probe.Terrain, 30, 10, 30));
    }

    [Fact]
    public void ASplitGridTakesItsLightWithItAndTheSourceGridLosesIt()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        uint torch = probe.State("torch");
        Grid source = probe.CreateGrid();
        probe.Set(source, (5, 5, 5, torch), (5, 4, 5, probe.State("stone")), (20, 5, 5, probe.State("stone")));
        _harness.Tick(engine);
        Assert.Equal(14, probe.Block(source, 5, 5, 5));
        Assert.Equal(11, probe.Block(source, 8, 5, 5));

        Grid part = probe.Worlds.Split(probe.Context, source, [new BlockCoordinate(5, 5, 5), new BlockCoordinate(5, 4, 5)]);
        probe.Grids.Add(part);
        _harness.Tick(engine, 2);

        Assert.Equal(14, probe.Block(part, 5, 5, 5));
        Assert.Equal(11, probe.Block(part, 8, 5, 5));
        Assert.Equal(14, probe.Light(part, 5, 3, 5).Sky);
        Assert.Equal(0, probe.Block(source, 5, 5, 5));
        Assert.Equal(0, probe.Block(source, 8, 5, 5));
        Assert.Equal(15, probe.Light(source, 5, 3, 5).Sky);
    }
}
