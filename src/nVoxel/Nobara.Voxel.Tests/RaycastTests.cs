// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Xunit.Abstractions;

namespace Nobara.Voxel.Tests;

public sealed class RaycastTests : IDisposable
{
    private const string Blocks = """
        <Block><Name>Stone</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>
        <Block><Name>Grass</Name><Content><Shape>shape:nobara.cube</Shape><Collision>none</Collision></Content></Block>
        """;

    private readonly VoxelHarness _harness = new();
    private readonly ITestOutputHelper _output;

    public RaycastTests(ITestOutputHelper output)
    {
        _output = output;
        _harness.OwnerResources = LoadingTests.Bundle(Blocks);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void GridFrameMatchesTheTransformOfTheGrid()
    {
        var transform = new WorldTransform(WorldPosition.FromBlocks(1000, -20, 3), Quaternion.CreateFromYawPitchRoll(0.3f, 0.7f, -1.1f), new Vector3(2, 0.5f, 1));
        var frame = new GridFrame(transform);
        var local = new Vector3(3.5f, -2, 7.25f);
        Double3 back = frame.ToLocal(transform.TransformPoint(local));

        Assert.Equal(local.X, back.X, 3);
        Assert.Equal(local.Y, back.Y, 3);
        Assert.Equal(local.Z, back.Z, 3);
    }

    [Fact]
    public void ARayHitsTheSameBlockOfARotatedGridAtTheOriginAndABillionBlocksAway()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6);
        VoxelRayHit near = CastAt(engine, probe, WorldPosition.FromBlocks(0.25, 0.25, 0.25), rotation);
        VoxelRayHit far = CastAt(engine, probe, WorldPosition.FromBlocks(1_000_000_000.25, -1_000_000_000.75, 1_000_000_000.25), rotation);

        foreach (VoxelRayHit hit in new[] { near, far })
        {
            Assert.Equal(VoxelHitKind.Block, hit.Kind);
            Assert.Equal(new BlockCoordinate(3, 0, 0), hit.Position);
            Assert.Equal(BlockFace.North, hit.Face);
            Assert.Equal(5, hit.Distance, 3);
        }

        Assert.Equal(near.Distance, far.Distance, 6);
    }

    [Fact]
    public void ARayReportsAChunkThatIsNotLoaded()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;

        Assert.True(probe.Queries.Raycast(DimensionId.Default, WorldPosition.FromBlocks(500.5, 0.5, 0.5), Vector3.UnitX, 10, out VoxelRayHit hit));
        Assert.Equal(VoxelHitKind.NotLoaded, hit.Kind);
        Assert.True(hit.Grid.IsTerrain);
        Assert.Equal(0, hit.Distance, 6);
    }

    [Fact]
    public void ATickAllocatesNothingOnceTheWorldIsLoaded()
    {
        _harness.VoxelDefaults[VoxelSettings.ViewRadius] = "2";
        _harness.VoxelDefaults[VoxelSettings.ViewHeight] = "1";
        _harness.VoxelDefaults[VoxelSettings.SimulationRadius] = "1";
        _harness.VoxelDefaults[VoxelSettings.SimulationHeight] = "1";
        _harness.VoxelDefaults[VoxelSettings.RandomTicksPerChunk] = "64";
        var counter = new Counter();
        ViewerTicket? viewer = null;
        _harness.ConfigureOwner = probe =>
        {
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.RandomTick, probe.Blocks.Get("block:example.loading.stone"), counter);
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.ScheduledTick, probe.Blocks.Get("block:example.loading.stone"), counter);
            probe.Loading.AddProvider(probe.Context, new LoadingTests.FlatProvider(() => probe.Blocks.Get("block:example.loading.stone").DefaultState));
            probe.Sessions.SubscribeStarting(probe.Context, _ =>
            {
                Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
                viewer = probe.Loading.AddViewer(probe.Context, terrain!, WorldPosition.FromBlocks(0.5, 0.5, 0.5));
            });
            probe.Sessions.SubscribeEnding(probe.Context, _ => viewer?.Dispose());
        };
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        LoadingTests.RunUntil(_harness, engine, () => probe.Sessions.State == Nobara.Context.SessionState.Active && probe.Loading.PendingChunks == 0);
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        Assert.True(probe.Ticks.Schedule(terrain!, new BlockCoordinate(0, -1, 0), 60));
        _harness.Tick(engine, 30);

        int before = counter.Calls;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        _harness.Tick(engine, 60);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.True(counter.Calls > before);
        Assert.Equal(0, probe.Ticks.CountScheduled(terrain!, new ChunkCoordinate(0, -1, 0)));
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RandomTicksOfTheSimulationAreaAreMeasured()
    {
        var counter = new Counter();
        ViewerTicket? viewer = null;
        _harness.VoxelDefaults[VoxelSettings.ViewRadius] = "4";
        _harness.VoxelDefaults[VoxelSettings.ViewHeight] = "2";
        _harness.ConfigureOwner = probe =>
        {
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.RandomTick, probe.Blocks.Get("block:example.loading.stone"), counter);
            probe.Loading.AddProvider(probe.Context, new LoadingTests.FlatProvider(() => probe.Blocks.Get("block:example.loading.stone").DefaultState));
            probe.Sessions.SubscribeStarting(probe.Context, _ =>
            {
                Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
                viewer = probe.Loading.AddViewer(probe.Context, terrain!, WorldPosition.FromBlocks(0.5, 0.5, 0.5));
            });
            probe.Sessions.SubscribeEnding(probe.Context, _ => viewer?.Dispose());
        };
        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        LoadingTests.RunUntil(_harness, engine, () => probe.Sessions.State == Nobara.Context.SessionState.Active && probe.Loading.PendingChunks == 0);
        var ticks = (BlockTicks)probe.Ticks;
        const int Passes = 2000;
        for (int i = 0; i < 100; i++) ticks.RunRandom(4);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Passes; i++) ticks.RunRandom(4);
        watch.Stop();

        int active = probe.Loading.SimulationArea.Count;
        _output.WriteLine($"Random ticks: {active} active chunks, 3 draws each, {watch.Elapsed.TotalMicroseconds / Passes:F1} µs per tick, {counter.Calls} calls.");
        Assert.Equal(245, active);
        Assert.True(counter.Calls > 0);
    }

    private VoxelRayHit CastAt(Engine engine, Probe probe, WorldPosition position, Quaternion rotation)
    {
        Grid grid = probe.Worlds.CreateGrid(probe.Context, DimensionId.Default, new GridOptions
        {
            Bounds = new GridBounds(new BlockCoordinate(-8, -8, -8), new BlockCoordinate(8, 8, 8)),
            Transform = new WorldTransform(position, rotation, Vector3.One),
        });
        probe.CreatedGrids.Add(grid);
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        AreaTicket air = probe.Loading.AddAreaTicket(probe.Context, terrain!, position, new ChunkArea(1, 1), TicketLevel.Loaded);
        uint stone = probe.Blocks.Get("block:example.loading.stone").DefaultState;
        uint grass = probe.Blocks.Get("block:example.loading.grass").DefaultState;
        probe.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockPlacement(new BlockCoordinate(3, 0, 0), stone), new BlockPlacement(new BlockCoordinate(3, 0, -2), grass)]);
        _harness.Tick(engine, 2);

        WorldPosition origin = grid.Transform.TransformPoint(new Vector3(3.5f, 0.5f, -5));
        Vector3 direction = Vector3.Transform(Vector3.UnitZ, rotation);
        Assert.True(probe.Queries.Raycast(DimensionId.Default, origin, direction, 20, out VoxelRayHit hit));
        air.Dispose();
        _output.WriteLine($"{hit.Kind} {hit.Position} {hit.Face} {hit.Distance:R}");
        return hit;
    }

    private sealed class Counter : IBlockHandler<BlockTick>
    {
        public int Calls { get; private set; }

        public bool Handle(in BlockTick args)
        {
            Calls++;
            return false;
        }
    }
}
