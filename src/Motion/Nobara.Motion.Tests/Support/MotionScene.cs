// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Google.Protobuf;
using Nobara.Blocks;
using Nobara.Blocks.Build;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Items.Build;
using Nobara.Physics;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using Nobara.Voxel;

namespace Nobara.Motion.Tests.Support;

/// <summary>
/// A running engine with Motion, whose terrain is a flat ground of stone, every
/// block below y = 0, and whose blocks weigh: stone 2,500 kilograms, wood
/// nothing given (the default), lead 10 kilograms for everyone but 5,000 for
/// Motion, a bench of two cells of 300 kilograms, a carved block whose shape
/// and mass depend on its instance, and grass without collision.
/// </summary>
internal sealed class MotionScene : IDisposable
{
    public const string Namespace = "block:example.test.";

    private static readonly string Blocks = """
        <Block><Name>Stone</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><Weight>2500</Weight></Properties></Block>
        <Block><Name>Wood</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>
        <Block><Name>Lead</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><Weight>10</Weight><Motion.Weight>5000</Motion.Weight></Properties></Block>
        <Block><Name>Bench</Name><Content><Shape>shape:nobara.cube</Shape><Footprint><Cell x="1"/></Footprint></Content><Properties><Weight>300</Weight></Properties></Block>
        <Block><Name>Carved</Name><Content><Shape>shape:example.carved</Shape></Content><Properties><Weight>1</Weight></Properties></Block>
        <Block><Name>Grass</Name><Content><Shape>shape:nobara.cube</Shape><Collision>none</Collision></Content><Properties><Weight>50</Weight></Properties></Block>
        """;

    private static byte[]? _bundle;

    public MotionScene(ChunkArea? area = null, WorldPosition? center = null, int smallGridBlocks = 8, Action<MotionProbe>? configure = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        Carved = new CarvedShape();
        Center = center ?? WorldPosition.FromBlocks(16, 16, 16);
        Harness.ActiveArea = area ?? new ChunkArea(1, 1);
        Harness.ActiveCenter = Center;
        Harness.Resources = Bundle();
        Harness.Settings[MotionSettings.SmallGridBlocks] = smallGridBlocks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach ((string key, string value) in settings ?? new Dictionary<string, string>())
        {
            Harness.Settings[key] = value;
        }

        Harness.Configure = probe =>
        {
            probe.Shapes.Register(probe.Context, Carved);
            probe.Loading.AddProvider(probe.Context, new FlatTerrain(() => State("stone")));
            configure?.Invoke(probe);
        };
        Engine = Harness.Start();
        WaitForTerrain();
    }

    public MotionHarness Harness { get; } = new();

    public Engine Engine { get; }

    public CarvedShape Carved { get; }

    public WorldPosition Center { get; }

    public MotionProbe Probe => Harness.Probe;

    public Grid Terrain => Probe.Voxels.TryGetTerrain(DimensionId.Default, out Grid? terrain) ? terrain! : throw new InvalidOperationException("No terrain.");

    public IPhysicsSystem Physics => Probe.Physics;

    public IMotionSystem Motion => Probe.Motion;

    /// <summary>Gets the default state of a block of the tests.</summary>
    public uint State(string name) => Probe.Blocks.Get(Namespace + name).DefaultState;

    /// <summary>Gets a position in the world, from the corner of the chunk at the center of the scene, at a height above y = 0.</summary>
    public WorldPosition At(double x, double y, double z) =>
        new WorldPosition(Center.ChunkX, 0, Center.ChunkZ, Vector3.Zero).Offset(new Vector3((float)x, (float)y, (float)z));

    /// <summary>Creates a grid at a place, with blocks written straight into its chunks.</summary>
    public Grid Grid(in WorldPosition origin, uint state, params BlockCoordinate[] blocks) => Grid(new WorldTransform(origin, Quaternion.Identity, Vector3.One), state, blocks);

    /// <summary>Creates a grid with a transform, with blocks written straight into its chunks.</summary>
    public Grid Grid(in WorldTransform transform, uint state, params BlockCoordinate[] blocks)
    {
        Grid grid = Probe.Voxels.CreateGrid(Probe.Context, DimensionId.Default, new GridOptions { Transform = transform });
        foreach (BlockCoordinate block in blocks)
        {
            grid.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(block)).Palette.Set(VoxelCoordinates.ToIndex(block), state);
        }

        return grid;
    }

    /// <summary>Gets the cells of a box, from one corner to the other, both included.</summary>
    public static BlockCoordinate[] Box(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var cells = new List<BlockCoordinate>();
        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    cells.Add(new BlockCoordinate(x, y, z));
                }
            }
        }

        return [.. cells];
    }

    /// <summary>Places blocks in a grid, applied at the next tick.</summary>
    public void Place(Grid grid, uint state, params BlockCoordinate[] blocks)
    {
        var placements = new BlockPlacement[blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            placements[i] = new BlockPlacement(blocks[i], state);
        }

        Probe.Batches.Place(grid, BlockActor.OfCommand("test"), BlockCause.Hand, placements);
    }

    /// <summary>Breaks blocks of a grid, at the next tick.</summary>
    public void Break(Grid grid, params BlockCoordinate[] blocks) => Probe.Batches.Break(grid, BlockActor.OfCommand("test"), BlockCause.Damage, blocks);

    /// <summary>Creates a static box body, centered on a position.</summary>
    public BodyHandle StaticBox(in WorldPosition center, Vector3 size, PhysicsMaterial? material = null)
    {
        EntityId entity = Probe.CreateEntity(WorldTransform.At(center));
        return Physics.AddBody(DimensionId.Default, entity, new BodyDescription { Kind = BodyKind.Static, Shape = Physics.CreateBox(size), Material = material ?? PhysicsMaterial.Default });
    }

    /// <summary>Gets the height of the origin of a grid above y = 0, in blocks.</summary>
    public double Height(Grid grid) => grid.Transform.Position.RelativeTo(At(0, 0, 0)).Y;

    public void Tick(int count = 1) => Harness.Tick(Engine, count);

    public void Dispose()
    {
        Engine.MainContext.Stop();
        Engine.Dispose();
        Harness.Dispose();
    }

    /// <summary>Compiles the blocks of the tests once.</summary>
    private static byte[] Bundle()
    {
        if (_bundle is { } bundle)
        {
            return bundle;
        }

        string root = Path.Combine(Path.GetTempPath(), "nobara-motion-blocks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "blocks.nof"), $"<Nobara version=\"1.0.0\" namespace=\"Example/Test\">{Blocks}</Nobara>");
            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = MotionHarness.GameId,
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

    /// <summary>Ticks until the chunks of the active area are loaded.</summary>
    private void WaitForTerrain()
    {
        if (!Probe.Voxels.TryGetTerrain(DimensionId.Default, out Grid? terrain))
        {
            throw new InvalidOperationException("No terrain.");
        }

        ChunkArea area = Harness.ActiveArea!.Value;
        // Chunks load in jobs: wait on the clock, not on a number of ticks, and let the workers run.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int tick = 0; watch.Elapsed < TimeSpan.FromSeconds(30); tick++)
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
                            loaded = Probe.Loading.GetStatus(terrain!, new ChunkCoordinate(Center.ChunkX + x, Center.ChunkY + y, Center.ChunkZ + z)).IsLoaded;
                        }
                    }
                }
            }

            if (loaded)
            {
                return;
            }

            Tick();
            if (tick % 16 == 15)
            {
                Thread.Sleep(1);
            }
        }

        throw new InvalidOperationException("The terrain did not load.");
    }
}

/// <summary>A flat ground: every block below y = 0 is stone.</summary>
internal sealed class FlatTerrain(Func<uint> stone) : IChunkProvider
{
    public bool Accepts(in ChunkRequest request) => request.IsTerrain;

    public void Produce(in ChunkRequest request, Span<uint> states)
    {
        if (request.Chunk.Y < 0)
        {
            states.Fill(stone());
        }
    }
}

/// <summary>
/// A shape that depends on the instance: a box from the bottom of the cell to
/// <see cref="Height"/>, of 800 kilograms per block of height, read when a
/// cell is evaluated, so that two cells placed at two heights keep their own.
/// </summary>
internal sealed class CarvedShape : IBlockShape
{
    public string Id => "shape:example.carved";

    public bool IsInstanceDependent => true;

    public float Height { get; set; } = 0.5f;

    public ShapeEvaluation Evaluate(in BlockShapeRequest request)
    {
        var collision = new CollisionShape([new CollisionBox(Vector3.Zero, new Vector3(1, Height, 1))]);
        return new ShapeEvaluation(collision, FaceMask.Empty, ContactMasks.FromCollision(collision), mass: 800 * Height);
    }
}
