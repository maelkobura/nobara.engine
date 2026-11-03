// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Buffers;
using Google.Protobuf;
using Nobara.Blocks;
using Nobara.Blocks.Build;
using Nobara.Core;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using Nobara.Items.Build;
using F = Nobara.Formats;

namespace Nobara.Voxel.Tests;

public sealed class ShapeTests : IDisposable
{
    private const string ModuleId = VoxelHarness.OwnerId;
    private readonly VoxelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void NativeShapesAreCachedAndModelUsesAFullBlockFallback()
    {
        _harness.OwnerResources = Bundle("""
            <Block><Name>Cube</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>
            <Block><Name>Model</Name><Content><Shape>shape:nobara.model</Shape></Content></Block>
            """);
        using Engine engine = _harness.Start();
        IBlockRegistry blocks = _harness.Owner.Blocks;
        IShapeRegistry shapes = _harness.Owner.Shapes;
        uint state = blocks.Get("block:example.shapes.model").DefaultState;

        ShapeEvaluation first = shapes.Get(state, BlockDataView.Empty);
        ShapeEvaluation second = shapes.Get(state, BlockDataView.Empty);

        Assert.Same(first.Collision, second.Collision);
        Assert.Equal(CollisionShape.FullBlock[0], first.Collision[0]);
        Assert.Equal(FaceMask.All, first.Opacity);
        Assert.Equal(ContactMasks.Full, first.Contact);
    }

    [Fact]
    public void CachedShapeQueriesDoNotAllocate()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Cube</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>");
        using Engine engine = _harness.Start();
        uint state = _harness.Owner.Blocks.Get("block:example.shapes.cube").DefaultState;
        _ = _harness.Owner.Shapes.Get(state, BlockDataView.Empty);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10_000; i++) _ = _harness.Owner.Shapes.Get(state, BlockDataView.Empty);

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void InstanceDependentShapesReceiveBlockData()
    {
        var data = new BlockDataType<float>("example.shapes.value", static (value, writer) => Write(writer, value), static bytes => BitConverter.ToSingle(bytes));
        CountingShape shape = new(data);
        _harness.OwnerResources = Bundle("<Block><Name>Instance</Name><Content><Shape>shape:example.instance</Shape></Content></Block>");
        _harness.ConfigureOwner = probe => probe.Shapes.Register(probe.Context, shape);
        using Engine engine = _harness.Start();
        BlockType type = _harness.Owner.Blocks.Get("block:example.shapes.instance");
        using var chunk = new Chunk(new ChunkCoordinate(0, 0, 0));
        chunk.SetBlockData(data, 0, 0.75f);

        ShapeEvaluation result = _harness.Owner.Shapes.Get(type.DefaultState, new BlockDataView(chunk, 0));

        Assert.Equal(0.75f, result.Collision[0].Maximum.X);
        Assert.Equal(1, shape.Calls);
    }

    [Fact]
    public void FullOrientationsRotateAnAsymmetricCollision()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Asymmetric</Name><Content><Shape>shape:example.asymmetric</Shape><Orientations>full</Orientations></Content></Block>");
        _harness.ConfigureOwner = probe => probe.Shapes.Register(probe.Context, new AsymmetricShape());
        using Engine engine = _harness.Start();
        BlockType type = _harness.Owner.Blocks.Get("block:example.shapes.asymmetric");
        StateProperty orientation = type.GetProperty("orientation");
        var boxes = new HashSet<string>(StringComparer.Ordinal);

        for (int value = 0; value < orientation.Count; value++)
        {
            uint state = _harness.Owner.Blocks.With(type.DefaultState, orientation, value);
            ShapeEvaluation result = _harness.Owner.Shapes.Get(state, BlockDataView.Empty);
            CollisionBox box = result.Collision[0];
            boxes.Add($"{box.Minimum.X:R},{box.Minimum.Y:R},{box.Minimum.Z:R}:{box.Maximum.X:R},{box.Maximum.Y:R},{box.Maximum.Z:R}");
            Assert.Equal(ContactMasks.FromCollision(result.Collision), result.Contact);
            Assert.Equal(1, BitOperations.PopCount(result.Opacity.Bits));
        }

        Assert.Equal(24, boxes.Count);
    }

    [Fact]
    public void AnUnknownShapeStopsTheContextWithTheBlockInTheProblem()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Missing</Name><Content><Shape>shape:example.missing</Shape></Content></Block>");

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Start());
        BlockLoadException error = Assert.IsType<BlockLoadException>(stop.InnerException);

        Assert.Contains("block:example.shapes.missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("blocks.nof", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARotatedMultiBlockCalculatesTheSameOriginForEveryPart()
    {
        _harness.OwnerResources = Bundle("<Block><Name>Door</Name><Content><Shape>shape:nobara.cube</Shape><Orientations>horizontal</Orientations><Footprint><Cell x=\"1\"/><Cell y=\"1\"/></Footprint></Content></Block>");
        using Engine engine = _harness.Start();
        BlockType type = _harness.Owner.Blocks.Get("block:example.shapes.door");
        StateProperty orientation = type.GetProperty("orientation");
        StateProperty part = type.GetProperty("part");
        uint state = _harness.Owner.Blocks.With(type.DefaultState, orientation, orientation.IndexOf("east"));
        var origin = new BlockCoordinate(20, 30, 40);

        for (int value = 0; value < part.Count; value++)
        {
            uint partState = _harness.Owner.Blocks.With(state, part, value);
            BlockCell offset = _harness.Owner.MultiBlocks.GetOffset(partState);
            BlockCoordinate cell = new(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
            Assert.Equal(origin, _harness.Owner.MultiBlocks.GetOrigin(partState, cell));
        }
    }

    [Fact]
    public void FourTurnsRotateStateAndBlockDataBackToTheOrigin()
    {
        var data = new BlockDataType<Vector2>("example.shapes.vector", static (value, writer) => Write(writer, value.X), static bytes => new Vector2(BitConverter.ToSingle(bytes)), value => new Vector2(-value.Y, value.X));
        _harness.OwnerResources = Bundle("<Block><Name>Turnable</Name><Content><Shape>shape:nobara.cube</Shape><Orientations>horizontal</Orientations></Content></Block>");
        using Engine engine = _harness.Start();
        BlockType type = _harness.Owner.Blocks.Get("block:example.shapes.turnable");
        uint original = type.DefaultState;
        uint turned = BlockRotation.RotateState(_harness.Owner.Blocks, original, 4);
        Vector2 value = new(1, 0);
        object rotated = BlockRotation.RotateData(data, value, 4);

        Assert.Equal(original, turned);
        Assert.Equal(value, rotated);
    }

    [Fact]
    public void ARotatedTwoByTwoByTwoMultiBlockCalculatesEveryOrigin()
    {
        _harness.OwnerResources = Bundle("""
            <Block><Name>CubeMachine</Name><Content><Shape>shape:nobara.cube</Shape><Orientations>horizontal</Orientations><Footprint>
                <Cell x="1"/><Cell y="1"/><Cell x="1" y="1"/><Cell z="1"/><Cell x="1" z="1"/><Cell y="1" z="1"/><Cell x="1" y="1" z="1"/>
            </Footprint></Content></Block>
            """);
        using Engine engine = _harness.Start();
        BlockType type = _harness.Owner.Blocks.Get("block:example.shapes.cubemachine");
        StateProperty orientation = type.GetProperty("orientation");
        StateProperty part = type.GetProperty("part");
        uint state = _harness.Owner.Blocks.With(type.DefaultState, orientation, orientation.IndexOf("west"));
        var origin = new BlockCoordinate(-12, 4, 27);

        for (int value = 0; value < part.Count; value++)
        {
            uint partState = _harness.Owner.Blocks.With(state, part, value);
            BlockCell offset = _harness.Owner.MultiBlocks.GetOffset(partState);
            BlockCoordinate cell = new(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
            Assert.Equal(origin, _harness.Owner.MultiBlocks.GetOrigin(partState, cell));
        }
    }

    private static byte[] Bundle(string body)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-shape-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "blocks.nof"), $"<Nobara version=\"1.0.0\" namespace=\"Example/Shapes\">{body}</Nobara>");
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

    private static void Write(IBufferWriter<byte> writer, float value)
    {
        BitConverter.TryWriteBytes(writer.GetSpan(sizeof(float)), value);
        writer.Advance(sizeof(float));
    }

    private sealed class AsymmetricShape : IBlockShape
    {
        public string Id => "shape:example.asymmetric";

        public bool IsInstanceDependent => false;

        public ShapeEvaluation Evaluate(in BlockShapeRequest request)
        {
            CollisionShape collision = new([new CollisionBox(new Vector3(0, 0, 0), new Vector3(0.25f, 0.5f, 1))]);
            return new ShapeEvaluation(collision, new FaceMask(1 << (int)BlockFace.North), ContactMasks.FromCollision(collision));
        }
    }

    private sealed class CountingShape(BlockDataType<float> data) : IBlockShape
    {
        public string Id => "shape:example.instance";

        public bool IsInstanceDependent => true;

        public int Calls { get; private set; }

        public ShapeEvaluation Evaluate(in BlockShapeRequest request)
        {
            Calls++;
            float width = request.Data.Get(data);
            return new ShapeEvaluation(new CollisionShape([new CollisionBox(Vector3.Zero, new Vector3(width, 1, 1))]), FaceMask.All, ContactMasks.Full);
        }
    }
}
