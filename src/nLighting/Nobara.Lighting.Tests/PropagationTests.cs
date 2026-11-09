// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Lighting.Tests;

public sealed class PropagationTests(ITestOutputHelper output) : IDisposable
{
    private readonly LightingHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AnIsolatedSourceLosesOneLevelPerBlock()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (16, 16, 16, probe.State("torch")));
        _harness.Tick(engine);

        Assert.Equal(14, probe.Block(grid, 16, 16, 16));
        for (int distance = 1; distance <= 14; distance++)
        {
            Assert.Equal(14 - distance, probe.Block(grid, 16 + distance, 16, 16));
            Assert.Equal(14 - distance, probe.Block(grid, 16 - distance, 16, 16));
            Assert.Equal(Math.Max(0, 14 - distance), probe.Block(grid, 16, 16 - distance, 16));
        }

        Assert.Equal(9, probe.Block(grid, 18, 19, 16));
        Assert.Equal(0, probe.Block(grid, 16, 16, 31));
        Assert.Equal(15, probe.Light(grid, 16, 16, 16).Sky);
    }

    [Fact]
    public void ATorchInAClosedCaveLightsItAndAHoleLetsTheLightOut()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        uint stone = probe.State("stone");
        Grid grid = probe.CreateGrid();
        probe.Set(grid, [.. Box(9, 15, stone)]);
        probe.Set(grid, (12, 12, 12, probe.State("torch")));
        _harness.Tick(engine);

        Assert.Equal(14, probe.Block(grid, 12, 12, 12));
        Assert.Equal(12, probe.Block(grid, 14, 12, 12));
        Assert.Equal(8, probe.Block(grid, 14, 14, 14));
        Assert.Equal(0, probe.Block(grid, 15, 12, 12));
        Assert.Equal(0, probe.Block(grid, 16, 12, 12));
        SortedDictionary<(int, int, int), ushort[]> closed = probe.Dump(grid);

        probe.Set(grid, (12, 12, 12, 0));
        _harness.Tick(engine);
        for (int x = 10; x <= 14; x++)
        {
            for (int y = 10; y <= 14; y++)
            {
                for (int z = 10; z <= 14; z++) Assert.Equal(0, probe.Block(grid, x, y, z));
            }
        }

        probe.Set(grid, (12, 12, 12, probe.State("torch")));
        _harness.Tick(engine);
        AssertSame(closed, probe.Dump(grid));

        probe.Set(grid, (15, 12, 12, 0));
        _harness.Tick(engine);
        Assert.Equal(11, probe.Block(grid, 15, 12, 12));
        Assert.Equal(10, probe.Block(grid, 16, 12, 12));
        Assert.Equal(8, probe.Block(grid, 17, 13, 12));

        probe.Set(grid, (15, 12, 12, stone));
        _harness.Tick(engine);
        Assert.Equal(0, probe.Block(grid, 16, 12, 12));
        AssertSame(closed, probe.Dump(grid));
    }

    [Fact]
    public void ABlockClosedOnSixFacesLetsItsOwnLightOutButNoLightIn()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (16, 16, 16, probe.State("lamp")), (12, 16, 16, probe.State("stone")), (10, 16, 16, probe.State("torch")));
        _harness.Tick(engine);

        Assert.Equal(15, probe.Block(grid, 16, 16, 16));
        Assert.Equal(14, probe.Block(grid, 17, 16, 16));
        Assert.Equal(0, probe.Block(grid, 12, 16, 16));
        Assert.Equal(12, probe.Block(grid, 13, 16, 16));
    }

    [Fact]
    public void AFilterTakesItsLevelsFromTheLightThatCrossesIt()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, [.. Wall(15, probe.State("stone"), probe.State("mist"))]);
        probe.Set(grid, (13, 16, 16, probe.State("torch")));
        _harness.Tick(engine);

        Assert.Equal(13, probe.Block(grid, 14, 16, 16));
        Assert.Equal(10, probe.Block(grid, 15, 16, 16));
        Assert.Equal(9, probe.Block(grid, 16, 16, 16));
    }

    [Fact]
    public void RemovingASourceAndAWallBringsTheExactValuesBack()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (10, 16, 16, probe.State("torch")));
        _harness.Tick(engine);
        SortedDictionary<(int, int, int), ushort[]> open = probe.Dump(grid);

        probe.Set(grid, (12, 16, 16, probe.State("stone")), (12, 17, 16, probe.State("stone")), (12, 16, 17, probe.State("stone")));
        _harness.Tick(engine);
        Assert.Equal(0, probe.Block(grid, 12, 16, 16));
        Assert.Equal(9, probe.Block(grid, 13, 16, 16));

        probe.Set(grid, (12, 16, 16, 0), (12, 17, 16, 0), (12, 16, 17, 0));
        _harness.Tick(engine);
        AssertSame(open, probe.Dump(grid));

        probe.Set(grid, (10, 16, 16, 0));
        _harness.Tick(engine);
        Assert.True(probe.Internal.TryGetChunk(grid, new ChunkCoordinate(0, 0, 0), out LightChunk? chunk));
        Assert.True(chunk!.IsUniform);
        Assert.Equal(LightChunk.DefaultValue, chunk.UniformValue);
    }

    [Fact]
    public void RemovingOneOfTwoSourcesLeavesTheLightOfTheOther()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        uint torch = probe.State("torch");
        Grid both = probe.CreateGrid();
        Grid alone = probe.CreateGrid();
        probe.Set(both, (8, 16, 16, torch), (20, 16, 16, torch), (14, 18, 16, torch));
        probe.Set(alone, (20, 16, 16, torch), (14, 18, 16, torch));
        _harness.Tick(engine);
        Assert.Equal(12, probe.Block(both, 14, 16, 16));

        probe.Set(both, (8, 16, 16, 0));
        _harness.Tick(engine);

        AssertSame(probe.Dump(alone), probe.Dump(both));
    }

    [Fact]
    public void AFrontWaitsForTheChunkBesideAndGoesOnOnceItLoads()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        uint torch = probe.State("torch");
        Grid terrain = probe.Terrain;
        ChunkTicket first = probe.Loading.AddTicket(probe.Context, terrain, new ChunkCoordinate(0, 0, 0), TicketLevel.Loaded);
        _harness.RunUntil(engine, () => probe.Loading.GetStatus(terrain, new ChunkCoordinate(0, 0, 0)).IsLoaded);
        probe.Set(terrain, (30, 16, 16, torch));
        _harness.Tick(engine);
        Assert.Equal(12, probe.Block(terrain, 31, 17, 16));

        ChunkTicket second = probe.Loading.AddTicket(probe.Context, terrain, new ChunkCoordinate(1, 0, 0), TicketLevel.Loaded);
        probe.Tickets.Add(second);
        _harness.RunUntil(engine, () => probe.Loading.GetStatus(terrain, new ChunkCoordinate(1, 0, 0)).IsLoaded);
        _harness.Tick(engine);
        Assert.Equal(11, probe.Block(terrain, 33, 16, 16));

        // The same blocks in a grid whose two chunks exist before the torch.
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (40, 16, 16, 0));
        _harness.Tick(engine);
        probe.Set(grid, (30, 16, 16, torch));
        _harness.Tick(engine);
        AssertSame(probe.Dump(grid), probe.Dump(terrain));

        // The light of a chunk that leaves memory leaves its neighbors: the chunk may come back without its torch.
        first.Dispose();
        _harness.RunUntil(engine, () => !probe.Loading.GetStatus(terrain, new ChunkCoordinate(0, 0, 0)).IsLoaded);
        _harness.Tick(engine);
        Assert.True(probe.Internal.TryGetChunk(terrain, new ChunkCoordinate(1, 0, 0), out LightChunk? beside));
        Assert.True(beside!.IsUniform);
        Assert.Equal(LightChunk.DefaultValue, beside.UniformValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABatchOfTenThousandBlocksGivesOnePassAndOneChangePerChunk(bool developmentMode)
    {
        var changes = new List<LightChunkChange>();
        using var harness = new LightingHarness(developmentMode: developmentMode);
        harness.ConfigureOwner = probe => probe.Lighting.SubscribeChanges(probe.Context, changes.Add);
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        uint torch = probe.State("torch");
        Grid grid = probe.CreateGrid();
        var blocks = new List<(int, int, int, uint)>(10_000);
        for (int x = 0; x < 100; x++)
        {
            for (int z = 0; z < 100; z++) blocks.Add((x, 16, z, torch));
        }

        probe.Set(grid, [.. blocks]);
        harness.Tick(engine);

        Assert.Equal(changes.Count, changes.Select(c => c.Chunk).Distinct().Count());
        Assert.Equal(16, changes.Count);
        Assert.Equal(14, probe.Block(grid, 50, 16, 50));
        Assert.Equal(13, probe.Block(grid, 50, 17, 50));
        Assert.Equal(1, probe.Block(grid, 50, 29, 50));
        TimeSpan placing = probe.Lighting.Statistics.LastPassDuration;
        changes.Clear();
        harness.Tick(engine, 3);
        Assert.Empty(changes);

        probe.Set(grid, [.. blocks.Select(b => (b.Item1, b.Item2, b.Item3, 0u))]);
        harness.Tick(engine);
        Assert.Equal(16, changes.Count);
        Assert.Equal(0, probe.Lighting.Statistics.ArrayChunks);
        LightingStatistics removing = probe.Lighting.Statistics;
        output.WriteLine($"Development mode {developmentMode}, 10,000 torches: placed in {placing.TotalMilliseconds:F2} ms, removed in {removing.LastPassDuration.TotalMilliseconds:F2} ms ({removing.LastPassRounds} rounds).");
    }

    [Fact]
    public void TwoContextsThatApplyTheSameBatchesHoldTheSameLight()
    {
        SortedDictionary<(int, int, int), ushort[]> first = Scripted(oneBatch: false);
        SortedDictionary<(int, int, int), ushort[]> second = Scripted(oneBatch: false);
        SortedDictionary<(int, int, int), ushort[]> together = Scripted(oneBatch: true);

        Assert.NotEmpty(first);
        AssertSame(first, second);
        AssertSame(first, together);
    }

    [Fact]
    public void AChunkHoldsOneValueUntilItsLightVariesAndComesBackToIt()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (1, 1, 1, probe.State("stone")));
        _harness.Tick(engine);
        Assert.True(probe.Internal.TryGetChunk(grid, new ChunkCoordinate(0, 0, 0), out LightChunk? chunk));
        Assert.True(chunk!.IsUniform);
        Assert.Equal(0, chunk.StorageBytes);
        Assert.Equal(new LightingStatistics(1, 0, 0, 0, 0, TimeSpan.Zero).Chunks, probe.Lighting.Statistics.Chunks);

        probe.Set(grid, (16, 16, 16, probe.State("torch")));
        _harness.Tick(engine);
        Assert.False(chunk.IsUniform);
        Assert.Equal(32_768, chunk.StorageBytes);
        Assert.Equal(1, probe.Lighting.Statistics.ArrayChunks);

        probe.Set(grid, (16, 16, 16, 0));
        _harness.Tick(engine);
        Assert.True(chunk.IsUniform);
        Assert.Equal(0, probe.Lighting.Statistics.StorageBytes);
    }

    [Fact]
    public void AFrozenCopyHoldsTheChunkAndOneBlockAroundIt()
    {
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (31, 16, 16, probe.State("torch")), (40, 16, 16, 0));
        _harness.Tick(engine);

        var copy = new LightSnapshot();
        Assert.True(probe.Lighting.CopyFrozen(grid, new ChunkCoordinate(0, 0, 0), copy));
        Assert.Equal(14, copy.Get(31, 16, 16).Block);
        Assert.Equal(13, copy.Get(32, 16, 16).Block);
        Assert.Equal(13, copy.Get(32, 16, 16).Red);
        Assert.Equal(15, copy.Get(-1, -1, -1).Sky);
        Assert.Equal(0, copy.Get(-1, 16, 16).Block);
        Assert.True(copy.HasNeighbor(0, 0, 0));
        Assert.True(copy.HasNeighbor(1, 0, 0));
        Assert.False(copy.HasNeighbor(-1, 0, 0));
        Assert.False(probe.Lighting.CopyFrozen(grid, new ChunkCoordinate(5, 0, 0), copy));
    }

    internal static void AssertSame(SortedDictionary<(int, int, int), ushort[]> expected, SortedDictionary<(int, int, int), ushort[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (((int, int, int) chunk, ushort[] values) in expected)
        {
            int index = values.AsSpan().CommonPrefixLength(actual[chunk]);
            Assert.True(index == values.Length, $"Chunk {chunk} differs at local index {index}: {(index < values.Length ? values[index] : 0):X4} expected, {(index < values.Length ? actual[chunk][index] : 0):X4} found.");
        }
    }

    // A wall of stone across the chunk at x, with one block of another state in its middle.
    internal static IEnumerable<(int, int, int, uint)> Wall(int x, uint stone, uint window)
    {
        for (int y = 0; y < 32; y++)
        {
            for (int z = 0; z < 32; z++) yield return (x, y, z, y == 16 && z == 16 ? window : stone);
        }
    }

    // The faces of a hollow box between two corners.
    private static IEnumerable<(int, int, int, uint)> Box(int from, int to, uint state)
    {
        for (int x = from; x <= to; x++)
        {
            for (int y = from; y <= to; y++)
            {
                for (int z = from; z <= to; z++)
                {
                    if (x == from || x == to || y == from || y == to || z == from || z == to) yield return (x, y, z, state);
                }
            }
        }
    }

    // Places and breaks sources, walls, and filters in a fixed order, then reads the light of the grid.
    private static SortedDictionary<(int, int, int), ushort[]> Scripted(bool oneBatch)
    {
        using var harness = new LightingHarness(colored: true);
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        Grid grid = probe.CreateGrid();
        string[] names = ["torch", "stone", "lamp", "redlamp", "blueglass", "redglass", "mist"];
        var random = new Random(1234);
        var steps = new List<(int, int, int, uint)[]>();
        for (int step = 0; step < 12; step++)
        {
            var blocks = new (int, int, int, uint)[40];
            for (int i = 0; i < blocks.Length; i++)
            {
                int pick = random.Next(names.Length + 2);
                uint state = pick < names.Length ? probe.State(names[pick]) : 0;
                blocks[i] = (random.Next(-20, 52), random.Next(0, 32), random.Next(-8, 40), state);
            }

            steps.Add(blocks);
        }

        if (oneBatch)
        {
            // The final state of each cell, in one batch.
            var last = new Dictionary<(int, int, int), uint>();
            foreach ((int x, int y, int z, uint state) in steps.SelectMany(s => s)) last[(x, y, z)] = state;
            probe.Set(grid, [.. last.Select(pair => (pair.Key.Item1, pair.Key.Item2, pair.Key.Item3, pair.Value))]);
            harness.Tick(engine);
        }
        else
        {
            foreach ((int, int, int, uint)[] blocks in steps)
            {
                probe.Set(grid, blocks);
                harness.Tick(engine);
            }
        }

        return probe.Dump(grid);
    }
}
