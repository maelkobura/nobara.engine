// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Lighting.Tests;

public sealed class BudgetTests(ITestOutputHelper output)
{
    private const int Budget = 100_000;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABatchOfFiftyThousandBlocksConvergesInAFewTicksNearestFirst(bool developmentMode)
    {
        SortedDictionary<(int, int, int), ushort[]> expected = Slab(int.MaxValue, developmentMode, out _, out string unlimited);
        SortedDictionary<(int, int, int), ushort[]> budgeted = Slab(Budget, developmentMode, out int ticks, out string measures);

        PropagationTests.AssertSame(expected, budgeted);
        Assert.InRange(ticks, 2, 60);
        output.WriteLine($"Development mode {developmentMode}, 50,000 torches without a budget: {unlimited}; with a budget of {Budget:N0}: {measures}.");
    }

    [Fact]
    public void ChangesMadeWhileAPassIsLateJoinItAndGiveTheSameLight()
    {
        SortedDictionary<(int, int, int), ushort[]> expected = Interleaved(int.MaxValue);
        SortedDictionary<(int, int, int), ushort[]> budgeted = Interleaved(4_096);

        PropagationTests.AssertSame(expected, budgeted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheViewAreaLoadsItsLightWithinTheBudget(bool developmentMode)
    {
        using var harness = new LightingHarness(developmentMode: developmentMode, budget: Budget);
        harness.ConfigureOwner = probe => probe.Loading.AddProvider(probe.Context, new RoughTerrain(() => probe.State("stone")));
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        var watch = Stopwatch.StartNew();
        probe.Tickets.Add(probe.Loading.AddViewer(probe.Context, probe.Terrain, WorldPosition.FromBlocks(0, 0, 0)));
        long operations = 0;
        long longest = 0;
        TimeSpan slowest = TimeSpan.Zero;
        int ticks = 0;
        for (; ticks < 3000; ticks++)
        {
            harness.Tick(engine);
            LightingBacklog backlog = probe.Lighting.Backlog;
            // The first tick with work compiles the propagation: it is left out of the slowest tick.
            if (operations > 0 && backlog.LastTickDuration > slowest) slowest = backlog.LastTickDuration;
            operations += backlog.LastTickOperations;
            longest = Math.Max(longest, backlog.LastTickOperations);
            if (probe.Loading.PendingChunks == 0 && backlog.Chunks == 0 && ticks > 10) break;
        }

        Assert.Equal(0, probe.Lighting.Backlog.Chunks);
        Assert.InRange(longest, 0, Budget + ((Environment.ProcessorCount + 1) * Propagator.MinimumQuota));
        output.WriteLine($"Development mode {developmentMode}, view area ({probe.Loading.ViewArea.Radius} chunks, {probe.Loading.ViewArea.Height} up and down): {probe.Lighting.Statistics.Chunks:N0} chunks, {probe.Lighting.Statistics.ArrayChunks:N0} arrays, {operations:N0} operations over {ticks} ticks, at most {longest:N0} operations and {slowest.TotalMilliseconds:F2} ms in a tick after the first, {watch.Elapsed.TotalSeconds:F1} s in all.");
    }

    // 50,000 torches over 56 chunks, a focus at one corner; returns the light once converged.
    private SortedDictionary<(int, int, int), ushort[]> Slab(int budget, bool developmentMode, out int ticks, out string measures)
    {
        using var harness = new LightingHarness(developmentMode: developmentMode, budget: budget);
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        uint torch = probe.State("torch");
        Grid grid = probe.CreateGrid();
        using LightFocus focus = probe.Lighting.AddFocus(probe.Context, DimensionId.Default, WorldPosition.FromBlocks(0, 16, 0));
        var blocks = new List<(int, int, int, uint)>(50_000);
        for (int x = 0; x < 250; x++)
        {
            for (int z = 0; z < 200; z++) blocks.Add((x, 16, z, torch));
        }

        probe.Set(grid, [.. blocks]);
        long operations = 0;
        long longest = 0;
        TimeSpan slowest = TimeSpan.Zero;
        bool nearFirst = false;
        ticks = 0;
        do
        {
            harness.Tick(engine);
            ticks++;
            LightingBacklog backlog = probe.Lighting.Backlog;
            operations += backlog.LastTickOperations;
            longest = Math.Max(longest, backlog.LastTickOperations);
            if (backlog.LastTickDuration > slowest) slowest = backlog.LastTickDuration;
            Assert.InRange(backlog.LastTickOperations, 0, (long)budget + ((Environment.ProcessorCount + 1) * Propagator.MinimumQuota));

            // The corner near the focus is lit before the far one.
            if (!nearFirst && probe.Block(grid, 5, 20, 5) == 10)
            {
                nearFirst = true;
                if (budget != int.MaxValue) Assert.NotEqual(10, probe.Block(grid, 240, 20, 195));
            }
        }
        while (probe.Lighting.Backlog.Chunks > 0 && ticks < 200);

        Assert.True(nearFirst);
        Assert.Equal(10, probe.Block(grid, 240, 20, 195));
        measures = $"{operations:N0} operations over {ticks} ticks, at most {longest:N0} and {slowest.TotalMilliseconds:F2} ms in a tick";
        return probe.Dump(grid);
    }

    // Places, then changes blocks while the first batch is still spreading.
    private static SortedDictionary<(int, int, int), ushort[]> Interleaved(int budget)
    {
        using var harness = new LightingHarness(colored: true, budget: budget);
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        Grid grid = probe.CreateGrid();
        string[] names = ["torch", "stone", "lamp", "redlamp", "blueglass", "mist"];
        var random = new Random(77);
        for (int step = 0; step < 10; step++)
        {
            var blocks = new (int, int, int, uint)[300];
            for (int i = 0; i < blocks.Length; i++)
            {
                int pick = random.Next(names.Length + 2);
                blocks[i] = (random.Next(-30, 62), random.Next(0, 40), random.Next(-30, 62), pick < names.Length ? probe.State(names[pick]) : 0);
            }

            probe.Set(grid, blocks);
            harness.Tick(engine);
        }

        harness.RunUntil(engine, () => probe.Lighting.Backlog.Chunks == 0);
        return probe.Dump(grid);
    }

    /// <summary>A ground whose height varies, with scattered holes under it, some of which the sky reaches.</summary>
    private sealed class RoughTerrain(Func<uint> stone) : IChunkProvider
    {
        private uint _stone;

        public bool Accepts(in ChunkRequest request)
        {
            _stone = stone();
            return request.IsTerrain;
        }

        public void Produce(in ChunkRequest request, Span<uint> states)
        {
            int baseX = request.Chunk.X * 32;
            int baseY = request.Chunk.Y * 32;
            int baseZ = request.Chunk.Z * 32;
            for (int y = 0; y < 32; y++)
            {
                for (int z = 0; z < 32; z++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        int wx = baseX + x;
                        int wy = baseY + y;
                        int wz = baseZ + z;
                        int ground = (((wx * 31) ^ (wz * 17)) & 7) - 4;
                        if (wy > ground) continue;
                        if ((uint)((wx * 73856093) ^ (wy * 19349663) ^ (wz * 83492791)) % 7 == 0) continue;
                        states[x + (32 * (z + (32 * y)))] = _stone;
                    }
                }
            }
        }
    }
}
