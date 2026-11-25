// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Google.Protobuf;
using Nobara.Blocks;
using Nobara.Blocks.Build;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Items.Build;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests.Support;

/// <summary>
/// A scene whose terrain is a flat ground of stone, every block below y = 0,
/// made by a provider of chunks, with the blocks and shapes of the tests:
/// a model, a slab, a half block that turns, a multi-block of two parts, a
/// block whose shape depends on its instance, and a block without collision.
/// </summary>
internal sealed class VoxelScene : IDisposable
{
    public const string Namespace = "block:example.test.";

    private static readonly string Blocks = """
        <Block><Name>Stone</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>
        <Block><Name>Model</Name><Content><Shape>shape:nobara.model</Shape></Content></Block>
        <Block><Name>Slab</Name><Content><Shape>shape:example.slab</Shape></Content></Block>
        <Block><Name>Half</Name><Content><Shape>shape:example.half</Shape><Orientations>horizontal</Orientations></Content></Block>
        <Block><Name>Bench</Name><Content><Shape>shape:example.bench</Shape><Footprint><Cell x="1"/></Footprint></Content></Block>
        <Block><Name>Carved</Name><Content><Shape>shape:example.carved</Shape></Content></Block>
        <Block><Name>Grass</Name><Content><Shape>shape:nobara.cube</Shape><Collision>none</Collision></Content></Block>
        """;

    private static byte[]? _bundle;

    public VoxelScene(ChunkArea area, WorldPosition? center = null, Action<BepuHarness>? configure = null)
    {
        Terrain = new FlatTerrain();
        Carved = new CarvedShape();
        Scene = new BepuScene(harness =>
        {
            harness.ActiveArea = area;
            harness.ActiveCenter = center ?? WorldPosition.FromBlocks(16, 16, 16);
            harness.Resources = Bundle();
            harness.Configure = probe =>
            {
                probe.Shapes.Register(probe.Context, new BoxesShape("shape:example.slab", _ => [new CollisionBox(Vector3.Zero, new Vector3(1, 0.5f, 1))]));
                probe.Shapes.Register(probe.Context, new BoxesShape("shape:example.half", _ => [new CollisionBox(Vector3.Zero, new Vector3(1, 1, 0.5f))]));
                probe.Shapes.Register(probe.Context, new BoxesShape("shape:example.bench", part => part == 0
                    ? [new CollisionBox(Vector3.Zero, Vector3.One)]
                    : [new CollisionBox(Vector3.Zero, new Vector3(1, 0.5f, 1))]));
                probe.Shapes.Register(probe.Context, Carved);
                probe.Loading.AddProvider(probe.Context, Terrain);
            };
            configure?.Invoke(harness);
        });
        Terrain.Stone = State("stone");
        Center = center ?? WorldPosition.FromBlocks(16, 16, 16);
        WaitForTerrain();
    }

    public BepuScene Scene { get; }

    public FlatTerrain Terrain { get; }

    public CarvedShape Carved { get; }

    public WorldPosition Center { get; }

    public BepuProbe Probe => Scene.Probe;

    public IPhysicsSystem Physics => Scene.Physics;

    public Grid Ground => Probe.Voxels.TryGetTerrain(DimensionId.Default, out Grid? terrain) ? terrain! : throw new InvalidOperationException("No terrain.");

    /// <summary>Gets the default state of a block of the tests.</summary>
    public uint State(string name) => Probe.Blocks.Get(Namespace + name).DefaultState;

    /// <summary>Gets a state of the half block, turned toward a horizontal direction.</summary>
    public uint Half(string direction)
    {
        BlockType type = Probe.Blocks.Get(Namespace + "half");
        StateProperty orientation = type.GetProperty("orientation");
        return Probe.Blocks.With(type.DefaultState, orientation, orientation.IndexOf(direction));
    }

    /// <summary>Gets the position of a block of the terrain, from the center of the scene, in whole blocks.</summary>
    public BlockCoordinate Block(int x, int y, int z)
    {
        BlockCoordinate center = VoxelCoordinates.ToBlock(Center);
        return new BlockCoordinate(center.X - 16 + x, y, center.Z - 16 + z);
    }

    /// <summary>Gets a position in the world, from the corner of the chunk at the center of the scene, at a height above y = 0.</summary>
    public WorldPosition At(double x, double y, double z) =>
        new WorldPosition(Center.ChunkX, 0, Center.ChunkZ, Vector3.Zero).Offset(new Vector3((float)x, (float)y, (float)z));

    /// <summary>Places blocks in the terrain, applied at the next tick.</summary>
    public void Place(params (BlockCoordinate Position, uint State)[] blocks) => Place(Ground, blocks);

    /// <summary>Places blocks in a grid, applied at the next tick.</summary>
    public void Place(Grid grid, params (BlockCoordinate Position, uint State)[] blocks)
    {
        var placements = new BlockPlacement[blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            placements[i] = new BlockPlacement(blocks[i].Position, blocks[i].State);
        }

        Probe.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, placements);
    }

    /// <summary>Breaks blocks of the terrain, at the next tick.</summary>
    public void Break(params BlockCoordinate[] positions) => Probe.Batches.Break(Ground, BlockActor.OfCommand("test"), BlockCause.Damage, positions);

    public BodyHandle Add(WorldPosition position, PhysicsShape shape, float mass = 1, PhysicsMaterial? material = null, CollisionFilter? filter = null, Quaternion? rotation = null) =>
        Scene.Add(position, shape, BodyKind.Dynamic, mass, material, filter, rotation: rotation);

    public void Tick(int count = 1) => Scene.Tick(count);

    public BodyState State(BodyHandle body) => Physics.GetState(body);

    /// <summary>Gets the height of a body above y = 0, in blocks.</summary>
    public double Height(BodyHandle body) => Scene.Height(body);

    /// <summary>Gets the offset of a body from the corner of the chunk at the center of the scene, in blocks.</summary>
    public Vector3 Offset(BodyHandle body) => State(body).Transform.Position.RelativeTo(new WorldPosition(Center.ChunkX, 0, Center.ChunkZ, Vector3.Zero));

    public void Dispose() => Scene.Dispose();

    /// <summary>Compiles the blocks of the tests once.</summary>
    private static byte[] Bundle()
    {
        if (_bundle is { } bundle)
        {
            return bundle;
        }

        string root = Path.Combine(Path.GetTempPath(), "nobara-physics-blocks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "blocks.nof"), $"<Nobara version=\"1.0.0\" namespace=\"Example/Test\">{Blocks}</Nobara>");
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = BepuHarness.GameId,
                ResourcesDirectory = root,
                Dependencies = [CompiledModule.FromAssembly(typeof(Nobara.Items.ItemsModule).Assembly.Location)!, CompiledModule.FromAssembly(typeof(BlocksModule).Assembly.Location)!],
                Plugins = [new ItemPlugin(), new BlockPlugin()],
            });
            CompilationResult result = compiler.Compile(Directory.EnumerateFiles(root, "*.nof"));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return _bundle = result.Bundle!.ToByteArray();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Ticks until the chunks of the active area of the harness are loaded.</summary>
    private void WaitForTerrain()
    {
        Grid terrain = Ground;
        ChunkArea area = Scene.Harness.ActiveArea!.Value;
        for (int tick = 0; tick < 200; tick++)
        {
            bool loaded = true;
            for (int y = -area.Height; y <= area.Height && loaded; y++)
            {
                for (int z = -area.Radius; z <= area.Radius && loaded; z++)
                {
                    for (int x = -area.Radius; x <= area.Radius && loaded; x++)
                    {
                        if (area.Contains(x, y, z))
                        {
                            loaded = Probe.Loading.GetStatus(terrain, new ChunkCoordinate(Center.ChunkX + x, Center.ChunkY + y, Center.ChunkZ + z)).IsLoaded;
                        }
                    }
                }
            }

            if (loaded)
            {
                return;
            }

            Scene.Tick();
        }

        throw new InvalidOperationException("The terrain did not load.");
    }
}

/// <summary>A flat ground: every block below y = 0 is stone.</summary>
internal sealed class FlatTerrain : IChunkProvider
{
    /// <summary>Gets or sets the state of stone, set before the first chunk loads.</summary>
    public uint Stone { get; set; }

    public bool Accepts(in ChunkRequest request) => request.IsTerrain;

    public void Produce(in ChunkRequest request, Span<uint> states)
    {
        if (request.Chunk.Y < 0)
        {
            states.Fill(Stone);
        }
    }
}

/// <summary>A shape whose boxes depend on the part of a multi-block.</summary>
internal sealed class BoxesShape(string id, Func<int, CollisionBox[]> boxes) : IBlockShape
{
    public string Id => id;

    public bool IsInstanceDependent => false;

    public ShapeEvaluation Evaluate(in BlockShapeRequest request)
    {
        var collision = new CollisionShape(boxes(request.Part));
        return new ShapeEvaluation(collision, FaceMask.Empty, ContactMasks.FromCollision(collision));
    }
}

/// <summary>
/// A shape that depends on the instance: a box from the bottom of the cell to
/// <see cref="Height"/>, read when nPhysics evaluates a cell, so that two cells
/// evaluated at two heights keep their own.
/// </summary>
internal sealed class CarvedShape : IBlockShape
{
    public string Id => "shape:example.carved";

    public bool IsInstanceDependent => true;

    public float Height { get; set; } = 0.25f;

    public int Calls { get; private set; }

    public ShapeEvaluation Evaluate(in BlockShapeRequest request)
    {
        Calls++;
        var collision = new CollisionShape([new CollisionBox(Vector3.Zero, new Vector3(1, Height, 1))]);
        return new ShapeEvaluation(collision, FaceMask.Empty, ContactMasks.FromCollision(collision));
    }
}
