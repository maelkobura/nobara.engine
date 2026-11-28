// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Lighting.Tests;

public sealed class BrightnessTests
{
    [Fact]
    public void TheQueryGivesTheSkyTheBlocksAndTheNearDynamicLightsApart()
    {
        using var harness = new LightingHarness(colored: true);
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (16, 25, 16, probe.State("stone")), (4, 16, 16, probe.State("redlamp")));
        harness.Tick(engine);

        Assert.True(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(16, 24, 16), out Brightness shaded));
        Assert.Equal(new Brightness(14, LightColor.None, LightColor.None), shaded);
        Assert.True(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(6, 16, 16), out Brightness lamp));
        Assert.Equal(new Brightness(15, new LightColor(13, 0, 0), LightColor.None), lamp);
        Assert.False(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(200, 16, 16), out _));

        // A torch in a hand: a dynamic light of radius 8 at the center of block (16, 16, 16).
        EntityId holder = CreateLight(probe, WorldPosition.FromBlocks(16.5, 16.5, 16.5), new LightColor(12, 8, 4), 8);
        harness.Tick(engine);
        Assert.Equal(1, probe.Sources.GetDynamicSources(DimensionId.Default).Length);

        Assert.True(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(16, 16, 16), out Brightness at));
        Assert.Equal(new LightColor(12, 8, 4), at.Dynamic);
        Assert.Equal(14, at.Sky);
        Assert.True(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(20, 16, 16), out Brightness near));
        Assert.Equal(new LightColor(6, 4, 2), near.Dynamic);
        Assert.True(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(24, 16, 16), out Brightness far));
        Assert.Equal(LightColor.None, far.Dynamic);

        // Moving the light changes the query at the next tick, and nothing in the stored light.
        SortedDictionary<(int, int, int), ushort[]> before = probe.Dump(grid);
        Assert.True(probe.Entities.TryGet(DimensionId.Default, out EntityWorld? world));
        world.Set(holder, probe.Entities.WorldTransform, WorldTransform.At(WorldPosition.FromBlocks(24.5, 16.5, 16.5)));
        harness.Tick(engine);
        Assert.True(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(24, 16, 16), out far));
        Assert.Equal(new LightColor(12, 8, 4), far.Dynamic);
        PropagationTests.AssertSame(before, probe.Dump(grid));

        world.Destroy(holder);
        harness.Tick(engine);
        Assert.Equal(0, probe.Sources.GetDynamicSources(DimensionId.Default).Length);
    }

    [Fact]
    public void WithoutColorADynamicLightGivesItsBrightestComponent()
    {
        using var harness = new LightingHarness();
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (1, 1, 1, probe.State("redglass")));
        harness.Tick(engine);
        CreateLight(probe, WorldPosition.FromBlocks(16.5, 16.5, 16.5), new LightColor(3, 10, 6), 5);
        harness.Tick(engine);

        Assert.True(probe.Lighting.TryGetBrightness(grid, new BlockCoordinate(16, 16, 16), out Brightness brightness));
        Assert.Equal(new LightColor(10, 10, 10), brightness.Dynamic);
    }

    [Fact]
    public void TheQueryAllocatesNothing()
    {
        using var harness = new LightingHarness();
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (16, 16, 16, probe.State("torch")));
        CreateLight(probe, WorldPosition.FromBlocks(10, 16, 16), new LightColor(15, 15, 15), 10);
        CreateLight(probe, WorldPosition.FromBlocks(20, 16, 16), new LightColor(15, 15, 15), 10);
        harness.Tick(engine, 2);
        var block = new BlockCoordinate(15, 16, 16);
        probe.Lighting.TryGetBrightness(grid, block, out _);

        int sum = 0;

        // The runtime may allocate now and then on this thread, such as while it compiles methods again: a round without allocation must come.
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                probe.Lighting.TryGetBrightness(grid, block, out Brightness brightness);
                sum += brightness.Dynamic.Red;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.True(sum > 0);
    }

    [Fact]
    public void WithoutLightingTheServiceDoesNotExist()
    {
        using var harness = new LightingHarness(lighting: false);
        using Engine engine = harness.Start();

        Assert.False(harness.Owner.Context.TryResolve(out ILighting? _));
        Assert.False(harness.Owner.Context.TryResolve(out ILightSources? _));
    }

    [Theory]
    [InlineData(0f, 8f, 12)]
    [InlineData(4f, 8f, 6)]
    [InlineData(7.9f, 8f, 0)]
    [InlineData(8f, 8f, 0)]
    [InlineData(1f, 0f, 0)]
    public void TheLightOfADynamicSourceFallsWithItsDistance(float distance, float radius, int expected)
    {
        Assert.Equal(expected, DynamicLight.LevelAt(12, distance, radius));
    }

    private static EntityId CreateLight(Probe probe, WorldPosition position, LightColor color, float radius)
    {
        Assert.True(probe.Entities.TryGet(DimensionId.Default, out EntityWorld? world));
        EntityArchetype archetype = world.GetArchetype(probe.Sources.DynamicLight, probe.Entities.WorldTransform);
        EntityId entity = world.Create(archetype);
        world.Set(entity, probe.Sources.DynamicLight, new DynamicLight { Color = color, Radius = radius });
        world.Set(entity, probe.Entities.WorldTransform, WorldTransform.At(position));
        return entity;
    }
}
