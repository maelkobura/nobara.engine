// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using System.Numerics;
using Google.Protobuf;
using Nobara.Blocks;
using Nobara.Blocks.Build;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Items.Build;
using Nobara.Items;
using ItemBlockFace = Nobara.Items.BlockFace;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Voxel.Tests;

public sealed class BatchTests : IDisposable
{
    private const string ModuleId = VoxelHarness.OwnerId;
    private readonly VoxelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void PlaceAndBreakApplyOnTheWorldTickAndDetachData()
    {
        var data = new BlockDataType<int>("example.batches.value", static (value, writer) =>
        {
            BitConverter.TryWriteBytes(writer.GetSpan(sizeof(int)), value);
            writer.Advance(sizeof(int));
        }, static bytes => BitConverter.ToInt32(bytes));
        var hooks = new List<string>();
        var notifications = new List<VoxelBatchNotification>();
        _harness.OwnerResources = Bundle("<Block><Name>Cube</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>");
        _harness.ConfigureOwner = probe =>
        {
            BlockType type = probe.Blocks.Get("block:example.batches.cube");
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.Placing, type, new BatchRecorder(hooks, "placing"));
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.Placed, type, new BatchRecorder(hooks, "placed"));
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.Breaking, type, new BatchRecorder(hooks, "breaking"));
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.Broken, type, new BatchRecorder(hooks, "broken"));
            probe.Batches.Subscribe(probe.Context, notifications.Add);
        };

        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid grid = _harness.Owner.CreatedGrids[0];
        BlockType cube = _harness.Owner.Blocks.Get("block:example.batches.cube");
        var position = new BlockCoordinate(0, 0, 0);
        VoxelBatchHandle placement = _harness.Owner.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, [new BlockPlacement(position, cube.DefaultState)]);

        Assert.False(placement.IsCompleted);
        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(position));

        _harness.Tick(engine);

        Assert.True(placement.IsCompleted);
        Assert.Equal(cube.DefaultState, grid.GetBlock(position));
        Assert.Equal(["placing", "placed"], hooks);

        Chunk chunk = grid.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        int localIndex = VoxelCoordinates.ToIndex(position);
        chunk.SetBlockData(data, localIndex, 42);
        VoxelBatchHandle breaking = _harness.Owner.Batches.Break(grid, BlockActor.OfCommand("test"), BlockCause.Damage, [position]);
        _harness.Tick(engine);

        Assert.True(breaking.IsCompleted);
        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(position));
        Assert.Equal(0, chunk.GetBlockData(data, localIndex));
        Assert.Equal(["placing", "placed", "breaking", "broken"], hooks);
        Assert.Equal(2, notifications.Count);
        VoxelBlockChange change = Assert.Single(notifications[1].Changes);
        Assert.Equal(cube.DefaultState, change.OldState);
        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, change.NewState);
        Assert.NotNull(change.RemovedData);
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(change.RemovedData!.Values).Value));
    }

    [Fact]
    public void NeighborChangesRunOnTheFollowingTickWithoutCascading()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Cube</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>");
        CascadingNeighbor? neighbor = null;
        _harness.ConfigureOwner = probe =>
        {
            BlockType type = probe.Blocks.Get("block:example.batches.cube");
            neighbor = new CascadingNeighbor(probe.Batches, type.DefaultState);
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.NeighborChanged, type, neighbor);
        };

        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid grid = _harness.Owner.CreatedGrids[0];
        BlockType cube = _harness.Owner.Blocks.Get("block:example.batches.cube");
        neighbor!.Grid = grid;
        var first = new BlockCoordinate(0, 0, 0);
        var second = new BlockCoordinate(1, 0, 0);
        _harness.Owner.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, [new BlockPlacement(first, cube.DefaultState)]);
        _harness.Tick(engine);
        _harness.Owner.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, [new BlockPlacement(second, cube.DefaultState)]);
        _harness.Tick(engine);

        Assert.Equal(0, neighbor.Calls);

        _harness.Tick(engine);

        Assert.Equal(1, neighbor.Calls);
        Assert.Equal(cube.DefaultState, neighbor.State);
        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(new BlockCoordinate(2, 0, 0)));

        _harness.Tick(engine);

        Assert.Equal(cube.DefaultState, grid.GetBlock(new BlockCoordinate(2, 0, 0)));
    }

    [Fact]
    public void MultiBlockPlacementAndBreakAreAtomic()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Door</Name><Content><Shape>shape:nobara.cube</Shape><Footprint><Cell x=\"1\"/></Footprint></Content></Block>");
        _harness.ConfigureOwner = probe => { };
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid grid = _harness.Owner.CreatedGrids[0];
        BlockType door = _harness.Owner.Blocks.Get("block:example.batches.door");
        var origin = new BlockCoordinate(0, 0, 0);
        _harness.Owner.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, [new BlockPlacement(origin, door.DefaultState)]);
        _harness.Tick(engine);

        Assert.NotEqual(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(origin));
        Assert.NotEqual(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(new BlockCoordinate(1, 0, 0)));

        _harness.Owner.Batches.Break(grid, BlockActor.OfCommand("test"), BlockCause.Damage, [origin]);
        _harness.Tick(engine);

        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(origin));
        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(new BlockCoordinate(1, 0, 0)));
    }

    [Fact]
    public void ABeforeHookCanCancelTheWholeBatch()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Cube</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>");
        _harness.ConfigureOwner = probe =>
        {
            BlockType type = probe.Blocks.Get("block:example.batches.cube");
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.Placing, type, new CancellingBatch());
        };
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid grid = _harness.Owner.CreatedGrids[0];
        BlockType cube = _harness.Owner.Blocks.Get("block:example.batches.cube");
        var position = new BlockCoordinate(0, 0, 0);

        VoxelBatchHandle handle = _harness.Owner.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, [new BlockPlacement(position, cube.DefaultState)]);
        _harness.Tick(engine);

        Assert.True(handle.IsCompleted);
        Assert.True(handle.IsCancelled);
        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, grid.GetBlock(position));
    }

    [Fact]
    public void FiftyThousandCellsAcrossThirtyChunksProduceThirtyNotifications()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Cube</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>");
        var notifications = new List<VoxelBatchNotification>();
        _harness.ConfigureOwner = probe => probe.Batches.Subscribe(probe.Context, notifications.Add);
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid grid = _harness.Owner.Worlds.CreateGrid(_harness.Owner.Context, DimensionId.Default);
        BlockType cube = _harness.Owner.Blocks.Get("block:example.batches.cube");
        var placements = new BlockPlacement[50_000];
        for (int i = 0; i < placements.Length; i++)
        {
            int local = i / 30;
            int chunk = i % 30;
            placements[i] = new BlockPlacement(new BlockCoordinate(chunk * 32 + (local & 31), local >> 10, (local >> 5) & 31), cube.DefaultState);
        }

        VoxelBatchHandle handle = _harness.Owner.Batches.Place(grid, BlockActor.OfCommand("bulk"), BlockCause.Generation, placements);
        _harness.Tick(engine);

        Assert.True(handle.IsCompleted);
        Assert.Equal(30, notifications.Count);
        Assert.Equal(50_000, notifications.Sum(notification => notification.Changes.Count));
    }

    [Fact]
    public void SplitAndMergeKeepMetadataAndRotateItWithTheBlock()
    {
        var data = new BlockDataType<Vector2>("example.batches.vector", static (value, writer) =>
        {
            BitConverter.TryWriteBytes(writer.GetSpan(sizeof(float)), value.X);
            writer.Advance(sizeof(float));
        }, static bytes => new Vector2(BitConverter.ToSingle(bytes)), value => new Vector2(-value.Y, value.X));
        _harness.OwnerResources = Bundle("<Block><Name>Turnable</Name><Content><Shape>shape:nobara.cube</Shape><Orientations>horizontal</Orientations></Content></Block>");
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid source = _harness.Owner.Worlds.CreateGrid(_harness.Owner.Context, DimensionId.Default);
        Grid destination = _harness.Owner.Worlds.CreateGrid(_harness.Owner.Context, DimensionId.Default);
        BlockType type = _harness.Owner.Blocks.Get("block:example.batches.turnable");
        StateProperty orientation = type.GetProperty("orientation");
        uint state = _harness.Owner.Blocks.With(type.DefaultState, orientation, orientation.IndexOf("east"));
        var sourcePosition = new BlockCoordinate(1, 0, 0);
        _harness.Owner.Batches.Place(source, BlockActor.OfCommand("test"), BlockCause.Hand, [new BlockPlacement(sourcePosition, state)]);
        _harness.Tick(engine);
        Chunk sourceChunk = source.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        sourceChunk.SetBlockData(data, VoxelCoordinates.ToIndex(sourcePosition), new Vector2(1, 0));

        Grid split = _harness.Owner.Worlds.Split(_harness.Owner.Context, source, [sourcePosition]);
        _harness.Tick(engine);
        Assert.Equal(_harness.Owner.Blocks.Air.DefaultState, source.GetBlock(sourcePosition));
        Assert.Equal(state, split.GetBlock(sourcePosition));
        Chunk splitChunk = split.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        Assert.Equal(new Vector2(1, 0), splitChunk.GetBlockData(data, VoxelCoordinates.ToIndex(sourcePosition)));

        _harness.Owner.Worlds.Merge(_harness.Owner.Context, destination, split, 1, new BlockCoordinate(2, 0, 3));
        _harness.Tick(engine);
        var destinationPosition = new BlockCoordinate(2, 0, 4);
        Assert.Equal(BlockRotation.RotateState(_harness.Owner.Blocks, state, 1), destination.GetBlock(destinationPosition));
        Chunk destinationChunk = destination.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        Assert.Equal(new Vector2(0, 1), destinationChunk.GetBlockData(data, VoxelCoordinates.ToIndex(destinationPosition)));
        Assert.DoesNotContain(split, _harness.Owner.Worlds.Grids);
    }

    [Fact]
    public void SplitPreservesTheNaturalChannel()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Cube</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>");
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid source = _harness.Owner.Worlds.CreateGrid(_harness.Owner.Context, DimensionId.Default);
        BlockType type = _harness.Owner.Blocks.Get("block:example.batches.cube");
        var position = new BlockCoordinate(0, 0, 0);
        _harness.Owner.Batches.Place(source, BlockActor.OfCommand("generator"), BlockCause.Generation, [new BlockPlacement(position, type.DefaultState)]);
        _harness.Tick(engine);

        Chunk sourceChunk = source.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        int localIndex = VoxelCoordinates.ToIndex(position);
        Assert.True(sourceChunk.IsNatural(localIndex));

        Grid split = _harness.Owner.Worlds.Split(_harness.Owner.Context, source, [position]);
        _harness.Tick(engine);

        Chunk splitChunk = split.Storage.GetOrCreateChunk(new ChunkCoordinate(0, 0, 0));
        Assert.True(splitChunk.IsNatural(localIndex));
        Assert.False(sourceChunk.IsNatural(localIndex));
    }

    [Fact]
    public void PlaceableItemUsesTheTargetFaceForPositionAndOrientation()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Orientable</Name><Content><Shape>shape:nobara.cube</Shape><Orientations>horizontal</Orientations><CreateItem/></Content></Block>");
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        Grid grid = _harness.Owner.CreatedGrids[0];
        BlockType type = _harness.Owner.Blocks.Get("block:example.batches.orientable");
        ItemType item = _harness.Owner.Items.Get("item:example.batches.orientable");
        UseOutcome outcome = _harness.Owner.Usage.UseItem(
            new ItemActor(42, DimensionId.Default),
            new ItemStackView(item),
            _harness.Owner.Usage.UseOnBlock,
            UseTarget.OnBlock(grid.Identifier, 0, 0, 0, ItemBlockFace.East));

        Assert.Equal(UseOutcome.Handled, outcome);
        _harness.Tick(engine);

        StateProperty orientation = type.GetProperty("orientation");
        uint placed = grid.GetBlock(new BlockCoordinate(1, 0, 0));
        Assert.Equal(orientation.IndexOf("east"), _harness.Owner.Blocks.GetValue(placed, orientation));
    }

    private static byte[] Bundle(string body)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-batch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "blocks.nof"), $"<Nobara version=\"1.0.0\" namespace=\"Example/Batches\">{body}</Nobara>");
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = ModuleId,
                ResourcesDirectory = root,
                Dependencies = [CompiledModule.FromAssembly(typeof(Nobara.Items.ItemsModule).Assembly.Location)!, CompiledModule.FromAssembly(typeof(Nobara.Blocks.BlocksModule).Assembly.Location)!],
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

    private sealed class BatchRecorder(List<string> calls, string name) : IBlockBatchHandler
    {
        public void Handle(BlockBatchView batch) => calls.Add(name);
    }

    private sealed class CancellingBatch : IBlockBatchHandler
    {
        public void Handle(BlockBatchView batch) => batch.Cancel();
    }

    private sealed class CascadingNeighbor(IVoxelBatches batches, uint state) : IBlockHandler<NeighborChange>
    {
        private bool _queued;

        public Grid Grid { get; set; } = null!;

        public int Calls { get; private set; }

        public uint State { get; private set; }

        public bool Handle(in NeighborChange args)
        {
            Calls++;
            State = args.State;
            if (!_queued)
            {
                _queued = true;
                batches.Place(Grid, BlockActor.OfCommand("neighbor"), BlockCause.Command, [new BlockPlacement(new BlockCoordinate(2, 0, 0), state)]);
            }

            return false;
        }
    }
}
