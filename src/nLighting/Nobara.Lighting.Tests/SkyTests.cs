// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Lighting.Tests;

public sealed class SkyTests : IDisposable
{
    private readonly LightingHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AFlatWorldIsFullSkyAboveItsGroundAndDarkInside()
    {
        Engine engine = StartFlat(new TestTerrainOptions());
        using (engine)
        {
            Probe probe = _harness.Owner;
            Grid terrain = probe.Terrain;

            Assert.Equal(15, probe.Light(terrain, 5, 0, 5).Sky);
            Assert.Equal(15, probe.Light(terrain, 20, 31, 9).Sky);
            Assert.Equal(0, probe.Light(terrain, 5, -1, 5).Sky);
            Assert.Equal(0, probe.Light(terrain, 5, -20, 5).Sky);
            Assert.True(probe.Internal.TryGetChunk(terrain, new ChunkCoordinate(0, 0, 0), out LightChunk? above));
            Assert.True(above!.IsUniform);
            Assert.Equal(LightChunk.DefaultValue, above.UniformValue);
            Assert.True(probe.Internal.TryGetChunk(terrain, new ChunkCoordinate(0, -1, 0), out LightChunk? ground));
            Assert.True(ground!.IsUniform);
            Assert.Equal(0, ground.UniformValue);
        }
    }

    [Fact]
    public void ACaveLightsUpFromItsOpeningAndGrowsDarkWhenItCloses()
    {
        Engine engine = StartFlat(new TestTerrainOptions());
        using (engine)
        {
            Probe probe = _harness.Owner;
            Grid terrain = probe.Terrain;
            uint stone = probe.State("stone");

            // A shaft from the surface, then a tunnel under the ground.
            var cave = new List<(int, int, int, uint)>();
            for (int y = -5; y <= -1; y++) cave.Add((5, y, 5, 0));
            for (int x = 6; x <= 12; x++) cave.Add((x, -5, 5, 0));
            probe.Set(terrain, [.. cave]);
            _harness.Tick(engine);

            Assert.Equal(15, probe.Light(terrain, 5, -5, 5).Sky);
            Assert.Equal(14, probe.Light(terrain, 6, -5, 5).Sky);
            Assert.Equal(8, probe.Light(terrain, 12, -5, 5).Sky);
            Assert.Equal(0, probe.Light(terrain, 13, -5, 5).Sky);
            SortedDictionary<(int, int, int), ushort[]> open = probe.Dump(terrain);

            probe.Set(terrain, (5, -1, 5, stone));
            _harness.Tick(engine);
            for (int y = -5; y <= -2; y++) Assert.Equal(0, probe.Light(terrain, 5, y, 5).Sky);
            for (int x = 6; x <= 12; x++) Assert.Equal(0, probe.Light(terrain, x, -5, 5).Sky);

            probe.Set(terrain, (5, -1, 5, 0));
            _harness.Tick(engine);
            PropagationTests.AssertSame(open, probe.Dump(terrain));
        }
    }

    [Fact]
    public void ARoofShadesTheColumnUnderItUntilItBreaks()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (16, 25, 16, probe.State("stone")));
        _harness.Tick(engine);

        Assert.Equal(0, probe.Light(grid, 16, 25, 16).Sky);
        Assert.Equal(14, probe.Light(grid, 16, 24, 16).Sky);
        Assert.Equal(14, probe.Light(grid, 16, 0, 16).Sky);
        Assert.Equal(15, probe.Light(grid, 17, 24, 16).Sky);
        Assert.Equal(15, probe.Light(grid, 16, 26, 16).Sky);

        // A wider roof: the middle of the shade is further from the sky beside it.
        var roof = new List<(int, int, int, uint)>();
        for (int x = 10; x <= 22; x++)
        {
            for (int z = 10; z <= 22; z++) roof.Add((x, 25, z, probe.State("stone")));
        }

        probe.Set(grid, [.. roof]);
        _harness.Tick(engine);
        Assert.Equal(8, probe.Light(grid, 16, 20, 16).Sky);
        Assert.Equal(14, probe.Light(grid, 10, 20, 16).Sky);

        probe.Set(grid, [.. roof.Select(b => (b.Item1, b.Item2, b.Item3, 0u))]);
        _harness.Tick(engine);
        Assert.True(probe.Internal.TryGetChunk(grid, new ChunkCoordinate(0, 0, 0), out LightChunk? chunk));
        Assert.True(chunk!.IsUniform);
        Assert.Equal(LightChunk.DefaultValue, chunk.UniformValue);
    }

    [Fact]
    public void AChunkLoadedAboveShadesTheChunksBelowIt()
    {
        Engine engine = StartFlat(new TestTerrainOptions { Roof = 40 }, holdGround: false);
        using (engine)
        {
            Probe probe = _harness.Owner;
            Grid terrain = probe.Terrain;

            // Nothing is known above the chunk: its space is sky.
            Assert.Equal(15, probe.Light(terrain, 5, 5, 5).Sky);

            probe.Hold(0, 1, 0);
            _harness.RunUntil(engine, () => probe.Loading.GetStatus(terrain, new ChunkCoordinate(0, 1, 0)).IsLoaded);
            _harness.Tick(engine);
            Assert.Equal(0, probe.Light(terrain, 5, 5, 5).Sky);
            Assert.Equal(0, probe.Light(terrain, 5, 39, 5).Sky);
            Assert.Equal(15, probe.Light(terrain, 5, 41, 5).Sky);
        }
    }

    [Fact]
    public void FiltersTakeTheirSmallestComponentFromTheSky()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();

        // Mist under a roof: the sky that crosses it loses its filter on top of one level.
        probe.Set(grid, (16, 25, 16, probe.State("stone")), (16, 24, 16, probe.State("mist")), (16, 23, 16, probe.State("redglass")));
        _harness.Tick(engine);

        Assert.Equal(12, probe.Light(grid, 16, 24, 16).Sky);
        Assert.Equal(14, probe.Light(grid, 16, 23, 16).Sky);
    }

    private Engine StartFlat(TestTerrainOptions options, bool holdGround = true)
    {
        _harness.ConfigureOwner = probe => probe.Loading.AddProvider(probe.Context, new TestTerrain(() => probe.State("stone")) { GroundTop = options.GroundTop, Roof = options.Roof });
        Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        probe.Hold(0, 0, 0);
        if (holdGround) probe.Hold(0, -1, 0);
        _harness.RunUntil(engine, () => probe.Loading.GetStatus(probe.Terrain, new ChunkCoordinate(0, 0, 0)).IsLoaded
            && (!holdGround || probe.Loading.GetStatus(probe.Terrain, new ChunkCoordinate(0, -1, 0)).IsLoaded));
        _harness.Tick(engine);
        return engine;
    }

    private sealed class TestTerrainOptions
    {
        public int GroundTop { get; init; } = -1;

        public int Roof { get; init; } = int.MinValue;
    }
}
