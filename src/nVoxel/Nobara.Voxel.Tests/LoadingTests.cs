// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Blocks;
using Nobara.Blocks.Build;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Items.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;

namespace Nobara.Voxel.Tests;

public sealed class LoadingTests : IDisposable
{
    private const string Blocks = """
        <Block><Name>Stone</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>
        <Block><Name>Leaves</Name><Content><Shape>shape:nobara.cube</Shape><Opacity>cutout</Opacity></Content></Block>
        <Block><Name>Door</Name><Content><Shape>shape:nobara.cube</Shape><Footprint><Cell x="1"/></Footprint></Content></Block>
        """;

    private readonly VoxelHarness _harness = new();

    public LoadingTests() => _harness.OwnerResources = Bundle(Blocks);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TheDefaultViewAreaHoldsAboutEighteenHundredChunks()
    {
        Assert.Equal(1773, new ChunkArea(8, 4).Count);
        Assert.Equal(245, new ChunkArea(4, 2).Count);
        Assert.True(new ChunkArea(8, 4).Contains(8, 4, 0));
        Assert.False(new ChunkArea(8, 4).Contains(6, 0, 6));
    }

    [Fact]
    public void AFlatProviderFillsTheViewAreaAndFollowsTheTicket()
    {
        _harness.VoxelDefaults[VoxelSettings.UnloadDelay] = "0";
        ViewerTicket? viewer = null;
        int loaded = 0, unloading = 0;
        WorldSession? session = null;
        _harness.ConfigureOwner = probe =>
        {
            probe.Loading.AddProvider(probe.Context, new FlatProvider(() => probe.Blocks.Get("block:example.loading.stone").DefaultState));
            probe.Loading.SubscribeResidency(probe.Context, change =>
            {
                if (change.Kind == ChunkResidencyKind.Loaded) loaded++;
                else unloading++;
            });
            probe.Sessions.SubscribeStarting(probe.Context, started =>
            {
                session = started;
                Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
                viewer = probe.Loading.AddViewer(probe.Context, terrain!, WorldPosition.FromBlocks(0.5, 0.5, 0.5));
            });
            probe.Sessions.SubscribeEnding(probe.Context, _ => viewer?.Dispose());
        };

        using Engine engine = _harness.Start();
        Probe probe = _harness.Owner;
        RunUntil(engine, () => session?.State == SessionState.Active);
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        for (int x = -4; x <= 4; x++)
        {
            for (int z = -4; z <= 4; z++)
            {
                for (int y = -2; y <= 2; y++)
                {
                    if (new ChunkArea(4, 2).Contains(x, y, z)) Assert.True(probe.Loading.GetStatus(terrain!, new ChunkCoordinate(x, y, z)).IsActive);
                }
            }
        }

        RunUntil(engine, () => probe.Loading.PendingChunks == 0 && probe.Loading.LoadedChunks == 1773);
        Assert.Equal(1773, loaded);
        Assert.Equal(new ChunkStatus(ChunkLoadState.Loaded, TicketLevel.Loaded), probe.Loading.GetStatus(terrain!, new ChunkCoordinate(8, 0, 0)));
        Assert.Equal(ChunkLoadState.NotLoaded, probe.Loading.GetStatus(terrain!, new ChunkCoordinate(9, 0, 0)).State);
        uint stone = probe.Blocks.Get("block:example.loading.stone").DefaultState;
        Assert.Equal(stone, terrain!.GetBlock(new BlockCoordinate(5, -1, 5)));
        Assert.Equal(0u, terrain.GetBlock(new BlockCoordinate(5, 0, 5)));

        viewer!.Move(WorldPosition.FromBlocks((3 * 32) + 0.5, 0.5, 0.5));
        RunUntil(engine, () => probe.Loading.PendingChunks == 0 && probe.Loading.LoadedChunks == 1773 && unloading > 0);
        Assert.True(probe.Loading.GetStatus(terrain, new ChunkCoordinate(11, 0, 0)).IsLoaded);
        Assert.Equal(ChunkLoadState.NotLoaded, probe.Loading.GetStatus(terrain, new ChunkCoordinate(-8, 0, 0)).State);
        Assert.Equal(1773 + unloading, loaded);
        Assert.Equal(new ChunkArea(8, 4).Count - CountOverlap(new ChunkArea(8, 4), 3), unloading);
    }

    [Fact]
    public void TheFirstProviderThatAcceptsProducesTheChunk()
    {
        _harness.ConfigureOwner = probe =>
        {
            probe.Loading.AddProvider(probe.Context, new FlatProvider(() => probe.Blocks.Get("block:example.loading.stone").DefaultState));
            probe.Loading.AddProvider(probe.Context, new EvenProvider(() => probe.Blocks.Get("block:example.loading.leaves").DefaultState), priority: 1);
        };

        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        using ChunkTicket even = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, -1, 0), TicketLevel.Loaded);
        using ChunkTicket odd = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(1, -1, 0), TicketLevel.Loaded);
        Assert.Equal(ChunkLoadState.Loading, probe.Loading.GetStatus(terrain!, new ChunkCoordinate(0, -1, 0)).State);
        Assert.Equal(BlockReadStatus.NotLoaded, probe.Queries.Read(terrain!, new BlockCoordinate(0, -1, 0)).Status);
        RunUntil(engine, () => probe.Loading.PendingChunks == 0);

        Assert.Equal(probe.Blocks.Get("block:example.loading.leaves").DefaultState, terrain!.GetBlock(new BlockCoordinate(0, -1, 0)));
        Assert.Equal(probe.Blocks.Get("block:example.loading.stone").DefaultState, terrain.GetBlock(new BlockCoordinate(32, -1, 0)));
    }

    [Fact]
    public void AChunkThatNoProviderAcceptsIsAir()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        using ChunkTicket ticket = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(4, 4, 4), TicketLevel.Loaded);
        _harness.Tick(engine);

        Assert.Equal(new BlockRead(BlockReadStatus.Loaded, 0), probe.Queries.Read(terrain!, new BlockCoordinate(130, 130, 130)));
    }

    [Fact]
    public void AMultiBlockHoldsTheOtherChunkOfItsFootprintWithoutPropagating()
    {
        _harness.VoxelDefaults[VoxelSettings.UnloadDelay] = "0";
        _harness.ConfigureOwner = probe => probe.Loading.AddProvider(probe.Context, new DoorProvider(probe));
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        var origin = new ChunkCoordinate(0, 0, 0);
        var second = new ChunkCoordinate(1, 0, 0);
        var third = new ChunkCoordinate(2, 0, 0);
        using ChunkTicket ticket = probe.Loading.AddTicket(probe.Context, terrain!, origin, TicketLevel.Active);
        RunUntil(engine, () => probe.Loading.PendingChunks == 0);

        Assert.Equal(new ChunkStatus(ChunkLoadState.Loaded, TicketLevel.Loaded), probe.Loading.GetStatus(terrain!, second));
        Assert.Equal(ChunkLoadState.NotLoaded, probe.Loading.GetStatus(terrain!, third).State);

        probe.Batches.Break(terrain!, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockCoordinate(31, 0, 0)]);
        _harness.Tick(engine, 2);

        Assert.Equal(ChunkLoadState.NotLoaded, probe.Loading.GetStatus(terrain!, second).State);
    }

    [Fact]
    public void ScheduledTicksFollowTheClockAndWaitWhileTheDimensionIsFrozen()
    {
        var ticks = new List<BlockTick>();
        _harness.ConfigureOwner = probe => probe.Hooks.Subscribe(probe.Context, probe.Hooks.ScheduledTick, probe.Blocks.Get("block:example.loading.stone"), new Recorder(ticks));
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        Assert.True(probe.Time.IsFrozen(DimensionId.Default));

        ChunkTicket active = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, 0, 0), TicketLevel.Active);
        uint stone = probe.Blocks.Get("block:example.loading.stone").DefaultState;
        probe.Batches.Place(terrain!, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockPlacement(new BlockCoordinate(1, 2, 3), stone)]);
        _harness.Tick(engine);
        Assert.False(probe.Time.IsFrozen(DimensionId.Default));
        Assert.True(probe.Ticks.Schedule(terrain!, new BlockCoordinate(1, 2, 3), 3));
        Assert.False(probe.Ticks.Schedule(terrain!, new BlockCoordinate(100, 2, 3), 3));
        _harness.Tick(engine, 2);
        Assert.Empty(ticks);
        _harness.Tick(engine);
        Assert.Equal(new BlockPosition(terrain!.Identifier, 1, 2, 3), Assert.Single(ticks).Position);

        Assert.True(probe.Ticks.Schedule(terrain, new BlockCoordinate(1, 2, 3), 2));
        using ChunkTicket loaded = probe.Loading.AddTicket(probe.Context, terrain, new ChunkCoordinate(0, 0, 0), TicketLevel.Loaded);
        active.Dispose();
        _harness.Tick(engine);
        Assert.True(probe.Time.IsFrozen(DimensionId.Default));
        ulong clock = probe.Time.GetClock(DimensionId.Default);
        _harness.Tick(engine, 20);
        Assert.Single(ticks);
        Assert.Equal(clock, probe.Time.GetClock(DimensionId.Default));

        using ChunkTicket again = probe.Loading.AddTicket(probe.Context, terrain, new ChunkCoordinate(0, 0, 0), TicketLevel.Active);
        _harness.Tick(engine, 3);
        Assert.Equal(2, ticks.Count);
    }

    [Fact]
    public void AFreezeByAnotherModuleIsKept()
    {
        var ticks = new List<BlockTick>();
        _harness.ConfigureOwner = probe => probe.Hooks.Subscribe(probe.Context, probe.Hooks.ScheduledTick, probe.Blocks.Get("block:example.loading.stone"), new Recorder(ticks));
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        using ChunkTicket active = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, 0, 0), TicketLevel.Active);
        probe.Batches.Place(terrain!, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockPlacement(new BlockCoordinate(0, 0, 0), probe.Blocks.Get("block:example.loading.stone").DefaultState)]);
        _harness.Tick(engine);
        probe.Time.SetFrozen(DimensionId.Default, true);
        Assert.True(probe.Ticks.Schedule(terrain!, new BlockCoordinate(0, 0, 0), 1));
        _harness.Tick(engine, 5);

        Assert.True(probe.Time.IsFrozen(DimensionId.Default));
        Assert.Empty(ticks);
        probe.Time.SetFrozen(DimensionId.Default, false);
        _harness.Tick(engine, 2);
        Assert.Single(ticks);
    }

    [Fact]
    public void RandomTicksReachOnlySubscribedTypes()
    {
        _harness.VoxelDefaults[VoxelSettings.RandomTicksPerChunk] = "4096";
        var ticks = new List<BlockTick>();
        _harness.ConfigureOwner = probe =>
        {
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.RandomTick, probe.Blocks.Get("block:example.loading.leaves"), new Recorder(ticks));
            probe.Loading.AddProvider(probe.Context, new HalfProvider(
                () => probe.Blocks.Get("block:example.loading.leaves").DefaultState,
                () => probe.Blocks.Get("block:example.loading.stone").DefaultState));
        };
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        using ChunkTicket mixed = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, 0, 0), TicketLevel.Active);
        using ChunkTicket stone = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(5, 0, 0), TicketLevel.Active);
        using ChunkTicket reduced = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, 5, 0), TicketLevel.ReducedActive);
        using ChunkTicket loadedOnly = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, 9, 0), TicketLevel.Loaded);
        RunUntil(engine, () => probe.Loading.PendingChunks == 0);
        ticks.Clear();
        _harness.Tick(engine, 8);

        uint leaves = probe.Blocks.Get("block:example.loading.leaves").DefaultState;
        Assert.NotEmpty(ticks);
        Assert.All(ticks, tick => Assert.Equal(leaves, tick.State));
        Assert.All(ticks, tick => Assert.True(tick.Position.Y < 16 || tick.Position.Y is >= 160 and < 176));
        int full = ticks.Count(tick => tick.Position.Y < 16);
        int slow = ticks.Count(tick => tick.Position.Y >= 160);
        Assert.InRange(slow, 1, full / 2);
    }

    [Fact]
    public void TheHeightMapFollowsPlacementsAndBreaks()
    {
        _harness.ConfigureOwner = probe => probe.Loading.AddProvider(probe.Context, new FlatProvider(() => probe.Blocks.Get("block:example.loading.stone").DefaultState));
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        using ChunkTicket below = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, -1, 0), TicketLevel.Loaded);
        using ChunkTicket above = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, 0, 0), TicketLevel.Loaded);
        RunUntil(engine, () => probe.Loading.PendingChunks == 0);

        Assert.True(probe.HeightMaps.TryGetHeight(terrain!, 5, 5, out int height));
        Assert.Equal(-1, height);
        uint stone = probe.Blocks.Get("block:example.loading.stone").DefaultState;
        uint leaves = probe.Blocks.Get("block:example.loading.leaves").DefaultState;
        probe.Batches.Place(terrain!, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockPlacement(new BlockCoordinate(5, 10, 5), stone), new BlockPlacement(new BlockCoordinate(6, 12, 6), leaves)]);
        _harness.Tick(engine);
        Assert.True(probe.HeightMaps.TryGetHeight(terrain!, 5, 5, out height));
        Assert.Equal(10, height);
        Assert.True(probe.HeightMaps.TryGetHeight(terrain!, 6, 6, out height));
        Assert.Equal(-1, height);

        probe.Batches.Break(terrain!, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockCoordinate(5, 10, 5)]);
        _harness.Tick(engine);
        Assert.True(probe.HeightMaps.TryGetHeight(terrain!, 5, 5, out height));
        Assert.Equal(-1, height);
        Assert.False(probe.HeightMaps.TryGetHeight(terrain!, 500, 500, out _));

        Grid bounded = probe.CreatedGrids[0];
        probe.Batches.Place(bounded, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockPlacement(new BlockCoordinate(1, -1, 1), stone)]);
        _harness.Tick(engine);
        Assert.True(probe.HeightMaps.TryGetHeight(bounded, 1, 1, out height));
        Assert.Equal(-1, height);
        Assert.False(probe.HeightMaps.TryGetHeight(bounded, 0, 0, out _));
    }

    [Fact]
    public void BoxAndSphereQueriesFindBlocksAndReportChunksThatAreNotLoaded()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Grid grid = probe.CreatedGrids[0];
        uint stone = probe.Blocks.Get("block:example.loading.stone").DefaultState;
        probe.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Command, [new BlockPlacement(new BlockCoordinate(0, 0, 0), stone), new BlockPlacement(new BlockCoordinate(2, 2, 2), stone)]);
        _harness.Tick(engine);

        var hits = new List<VoxelQueryHit>();
        probe.Queries.QuerySphere(DimensionId.Default, WorldPosition.FromBlocks(0.5, 0.5, 0.5), 0.6f, hits);
        Assert.Contains(hits, hit => hit.Kind == VoxelHitKind.Block && ReferenceEquals(hit.Grid, grid) && hit.Position == new BlockCoordinate(0, 0, 0));
        Assert.DoesNotContain(hits, hit => hit.Kind == VoxelHitKind.Block && hit.Position == new BlockCoordinate(2, 2, 2));
        Assert.Contains(hits, hit => hit.Kind == VoxelHitKind.NotLoaded && hit.Grid.IsTerrain);

        hits.Clear();
        probe.Queries.QueryBox(DimensionId.Default, new WorldBox(WorldPosition.FromBlocks(0, 0, 0), new System.Numerics.Vector3(-0.5f), new System.Numerics.Vector3(2.5f)), hits);
        Assert.Equal(2, hits.Count(hit => hit.Kind == VoxelHitKind.Block && ReferenceEquals(hit.Grid, grid)));
    }

    [Fact]
    public void QueriesAndTicketsRefuseOrReportWhatIsNotThere()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Grid bounded = probe.CreatedGrids[0];
        Assert.Equal(BlockReadStatus.OutOfBounds, probe.Queries.Read(bounded, new BlockCoordinate(10, 0, 0)).Status);
        Assert.Equal(new BlockRead(BlockReadStatus.Loaded, 0), probe.Queries.Read(bounded, new BlockCoordinate(1, 1, 1)));
        Span<BlockRead> neighbors = stackalloc BlockRead[6];
        probe.Queries.ReadNeighbors(bounded, new BlockCoordinate(2, 0, 0), neighbors);
        Assert.Equal(BlockReadStatus.OutOfBounds, neighbors[(int)BlockFace.East].Status);
        Assert.Equal(BlockReadStatus.Loaded, neighbors[(int)BlockFace.West].Status);
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        Assert.Equal(BlockReadStatus.NotLoaded, probe.Queries.Read(terrain!, new BlockCoordinate(0, 0, 0)).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Loading.AddAreaTicket(probe.Context, terrain!, default, new ChunkArea(-1, 0), TicketLevel.Loaded));
    }

    [Fact]
    public void ATicketLeftAtTheEndOfTheSessionIsReported()
    {
        ChunkTicket? left = null;
        _harness.ConfigureOwner = probe => probe.Sessions.SubscribeStarted(probe.Context, _ =>
        {
            Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
            left = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, 0, 0), TicketLevel.Loaded);
        });
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.NotNull(left);
        probe.Sessions.End();

        // The policy of the module is stop: the leak stops the context.
        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Tick(engine, 2));
        var leak = Assert.IsType<WorldStateLeakException>(stop.InnerException);
        Assert.Equal("chunk tickets", leak.Counter);
        Assert.Equal(1, leak.Count);
        Assert.False(left!.IsActive);
    }

    [Fact]
    public void ABatchOnAChunkBeingLoadedWaitsForTheGeneratedBlocks()
    {
        var gate = new ManualResetEventSlim(false);
        _harness.ConfigureOwner = probe => probe.Loading.AddProvider(probe.Context, new GatedProvider(() => probe.Blocks.Get("block:example.loading.stone").DefaultState, gate));
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 2);
        Probe probe = _harness.Owner;
        Assert.True(probe.Worlds.TryGetTerrain(DimensionId.Default, out Grid? terrain));
        using ChunkTicket ticket = probe.Loading.AddTicket(probe.Context, terrain!, new ChunkCoordinate(0, -1, 0), TicketLevel.Loaded);
        _harness.Tick(engine);
        Assert.Equal(ChunkLoadState.Loading, probe.Loading.GetStatus(terrain!, new ChunkCoordinate(0, -1, 0)).State);

        VoxelBatchHandle breaking = probe.Batches.SubmitTechnical(Single(terrain!, new BlockCoordinate(1, -1, 1), 0));
        _harness.Tick(engine, 3);
        Assert.False(breaking.IsCompleted);
        Assert.Equal(BlockReadStatus.NotLoaded, probe.Queries.Read(terrain!, new BlockCoordinate(1, -1, 1)).Status);

        gate.Set();
        RunUntil(engine, () => breaking.IsCompleted);
        uint stone = probe.Blocks.Get("block:example.loading.stone").DefaultState;
        Assert.Equal(0u, terrain!.GetBlock(new BlockCoordinate(1, -1, 1)));
        Assert.Equal(stone, terrain.GetBlock(new BlockCoordinate(2, -1, 1)));
    }

    private static BlockBatch Single(Grid grid, BlockCoordinate position, uint state)
    {
        var batch = new BlockBatch { Cause = BlockCause.Command };
        batch.Add(new BlockChange(new BlockPosition(grid.Identifier, position.X, position.Y, position.Z), 0, state));
        return batch;
    }

    /// <summary>Stone, produced once the test opens the gate.</summary>
    private sealed class GatedProvider(Func<uint> ground, ManualResetEventSlim gate) : IChunkProvider
    {
        private uint _ground = uint.MaxValue;

        public bool Accepts(in ChunkRequest request)
        {
            if (_ground == uint.MaxValue) _ground = ground();
            return true;
        }

        [HotPath]
        public void Produce(in ChunkRequest request, Span<uint> states)
        {
            gate.Wait();
            states.Fill(_ground);
        }
    }

    internal static void RunUntil(VoxelHarness harness, Engine engine, Func<bool> condition, int frames = 4000)
    {
        for (int i = 0; i < frames; i++)
        {
            if (condition()) return;
            harness.Tick(engine);
            if (i % 16 == 15) Thread.Sleep(1);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }

    internal static byte[] Bundle(string body)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-loading-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "blocks.nof"), $"<Nobara version=\"1.0.0\" namespace=\"Example/Loading\">{body}</Nobara>");
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = VoxelHarness.OwnerId,
                ResourcesDirectory = root,
                Dependencies = [CompiledModule.FromAssembly(typeof(Nobara.Items.ItemsModule).Assembly.Location)!, CompiledModule.FromAssembly(typeof(BlocksModule).Assembly.Location)!],
                Plugins = [new ItemPlugin(), new BlockPlugin()],
            });
            CompilationResult result = compiler.Compile(Directory.EnumerateFiles(root, "*.nof"));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return result.Bundle!.ToByteArray();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void RunUntil(Engine engine, Func<bool> condition) => RunUntil(_harness, engine, condition);

    // The chunks of an area that are still inside it after its center moved along X.
    private static int CountOverlap(ChunkArea area, int shift)
    {
        int count = 0;
        for (int x = -area.Radius; x <= area.Radius; x++)
        {
            for (int z = -area.Radius; z <= area.Radius; z++)
            {
                for (int y = -area.Height; y <= area.Height; y++)
                {
                    if (area.Contains(x, y, z) && area.Contains(x - shift, y, z)) count++;
                }
            }
        }

        return count;
    }

    /// <summary>Stone below Y = 0, air above.</summary>
    internal sealed class FlatProvider(Func<uint> ground) : IChunkProvider
    {
        private uint _ground = uint.MaxValue;

        public bool Accepts(in ChunkRequest request)
        {
            if (_ground == uint.MaxValue) _ground = ground();
            return request.IsTerrain;
        }

        [HotPath]
        public void Produce(in ChunkRequest request, Span<uint> states)
        {
            if (request.Chunk.Y < 0) states.Fill(_ground);
        }
    }

    private sealed class EvenProvider(Func<uint> state) : IChunkProvider
    {
        private uint _state = uint.MaxValue;

        public bool Accepts(in ChunkRequest request)
        {
            if (_state == uint.MaxValue) _state = state();
            return request.Chunk.X % 2 == 0;
        }

        [HotPath]
        public void Produce(in ChunkRequest request, Span<uint> states) => states.Fill(_state);
    }

    /// <summary>Leaves in the lower half of a chunk at X = 0, stone everywhere else.</summary>
    private sealed class HalfProvider(Func<uint> lower, Func<uint> rest) : IChunkProvider
    {
        private uint _lower = uint.MaxValue;
        private uint _rest = uint.MaxValue;

        public bool Accepts(in ChunkRequest request)
        {
            if (_lower == uint.MaxValue) (_lower, _rest) = (lower(), rest());
            return true;
        }

        [HotPath]
        public void Produce(in ChunkRequest request, Span<uint> states)
        {
            states.Fill(_rest);
            if (request.Chunk.X == 0) states[..(Chunk.Volume / 2)].Fill(_lower);
        }
    }

    /// <summary>Doors across the chunks along X: an origin at X = 31 of every chunk, its second cell in the next chunk.</summary>
    private sealed class DoorProvider(Probe probe) : IChunkProvider
    {
        private uint _origin = uint.MaxValue;
        private uint _part;

        public bool Accepts(in ChunkRequest request)
        {
            if (_origin == uint.MaxValue)
            {
                BlockType door = probe.Blocks.Get("block:example.loading.door");
                Assert.True(door.TryGetProperty("part", out StateProperty? part));
                _origin = door.DefaultState;
                _part = probe.Blocks.With(door.DefaultState, part!, 1);
            }

            return request.Chunk.Y == 0 && request.Chunk.Z == 0;
        }

        [HotPath]
        public void Produce(in ChunkRequest request, Span<uint> states)
        {
            states[31] = _origin;
            if (request.Chunk.X > 0) states[0] = _part;
        }
    }

    private sealed class Recorder(List<BlockTick> ticks) : IBlockHandler<BlockTick>
    {
        public bool Handle(in BlockTick args)
        {
            ticks.Add(args);
            return false;
        }
    }
}
