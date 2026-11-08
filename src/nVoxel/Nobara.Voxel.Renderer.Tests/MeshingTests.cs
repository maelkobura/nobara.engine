// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Renderer;
using Nobara.Voxel.Renderer.Tests.Support;

namespace Nobara.Voxel.Renderer.Tests;

/// <summary>nVoxel.Renderer in an engine: chunks of nVoxel meshed into the chunk geometry of nRenderer.</summary>
public sealed class MeshingTests : IDisposable
{
    private readonly MeshingHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AFlatWorldAtTheDefaultViewRadiusIsMeshedAndDrawn()
    {
        _harness.Settings.Remove(VoxelSettings.ViewRadius);
        _harness.Settings.Remove(VoxelSettings.ViewHeight);
        _harness.Settings.Remove(VoxelSettings.SimulationRadius);
        _harness.Settings.Remove(VoxelSettings.SimulationHeight);
        var tops = new Dictionary<ChunkCoordinate, int>();
        _harness.Start();
        _harness.Remesher.Applied = (_, chunk, output) => tops[chunk] = output.Indices(ChunkCategory.Opaque).Length / 6;

        _harness.Settle();
        _harness.Frame();

        // The chunks of stone just below Y = 0 have one merged quad on top; deeper ones none,
        // as the chunks around them are stone or not loaded. Air has no section.
        Grid terrain = _harness.Terrain;
        int stoneChunks = 0;
        foreach (ChunkCoordinate chunk in tops.Keys)
        {
            Assert.True(chunk.Y < 0);
            Assert.True(_harness.Meshing.IsMeshed(terrain, chunk));
            Assert.Equal(chunk.Y == -1 ? 1 : 0, tops[chunk]);
            stoneChunks++;
        }

        Assert.Equal(_harness.Probe.Loading.ViewArea.Count * 4 / 9, stoneChunks);
        Assert.Equal(stoneChunks, _harness.Meshing.Statistics.Sections);
        Assert.Equal(stoneChunks, _harness.Chunks.SectionCount);
        Assert.True(_harness.Probe.Pipeline.Draws > 0);
        Assert.True(_harness.Chunks.Statistics.Candidates > 0);
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void PlacingABlockRemeshesTheTouchedChunkAtTheNextFrameAndItsNeighborsWhenOnABorder()
    {
        var applied = new List<ChunkCoordinate>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, _) => applied.Add(chunk);
        uint stone = _harness.State("block:example.world.stone");
        Grid terrain = _harness.Terrain;

        // The tick of a frame applies the batch and the remesh starts; the next frame shows it.
        _harness.Place(terrain, (new BlockCoordinate(5, 5, 5), stone));
        _harness.Frame();
        Assert.False(_harness.Meshing.IsMeshed(terrain, new ChunkCoordinate(0, 0, 0)));
        _harness.Frame();

        Assert.Equal([new ChunkCoordinate(0, 0, 0)], applied);
        Assert.True(_harness.Meshing.IsMeshed(terrain, new ChunkCoordinate(0, 0, 0)));

        // On the border with the chunk to the east, and on the stone below: those chunks change too.
        // The chunk to the east is air, which has no geometry to apply.
        applied.Clear();
        _harness.Place(terrain, (new BlockCoordinate(31, 0, 5), stone));
        _harness.Frame(2);

        Assert.Equal([new ChunkCoordinate(0, -1, 0), new ChunkCoordinate(0, 0, 0), new ChunkCoordinate(1, -1, 0)], applied.Order().ToArray());
        Assert.True(_harness.Meshing.IsMeshed(terrain, new ChunkCoordinate(1, 0, 0)));
    }

    [Fact]
    public void AHundredChangesOfAChunkInOneTickRemeshItOnce()
    {
        _harness.Start();
        _harness.Settle();
        uint stone = _harness.State("block:example.world.stone");
        Grid terrain = _harness.Terrain;
        long started = _harness.Meshing.Statistics.Started;

        for (int i = 0; i < 100; i++)
        {
            _harness.Place(terrain, (new BlockCoordinate(1 + (i % 10), 1 + (i / 10), 7), stone));
        }

        _harness.Frame(2);

        Assert.Equal(started + 1, _harness.Meshing.Statistics.Started);
        Assert.True(_harness.Meshing.IsMeshed(terrain, new ChunkCoordinate(0, 0, 0)));
    }

    [Fact]
    public void AResultOfAChunkThatChangedWhileItWasMeshedIsDiscarded()
    {
        var quads = new List<int>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) =>
        {
            if (chunk == new ChunkCoordinate(0, 0, 0))
            {
                quads.Add(output.Indices(ChunkCategory.Opaque).Length / 6);
            }
        };
        uint stone = _harness.State("block:example.world.stone");
        Grid terrain = _harness.Terrain;
        VoxelMeshingStatistics before = _harness.Meshing.Statistics;

        // The remesh of the first block runs when the second block arrives: it is thrown away.
        _harness.Place(terrain, (new BlockCoordinate(5, 5, 5), stone));
        _harness.Frame();
        _harness.Place(terrain, (new BlockCoordinate(9, 5, 9), stone));
        _harness.Frame();
        _harness.Frame();

        VoxelMeshingStatistics after = _harness.Meshing.Statistics;
        Assert.Equal(before.Discarded + 1, after.Discarded);
        Assert.Equal(before.Started + 2, after.Started);
        Assert.Equal([12], quads);
    }

    [Fact]
    public void TheLoadingTaskEndsWhenTheSimulationAreaIsMeshed()
    {
        _harness.Start();
        WorldSession? session = null;
        _harness.RunUntil(() => (session = _harness.Probe.Sessions.Current) is not null);
        LoadingTask task = Assert.Single(session!.Loading.Tasks, t => t.Module == VoxelRendererModule.ModuleId);
        Assert.Equal("meshed chunks of the simulation area", task.Name);

        // When the session becomes active, the simulation area is meshed; the rest of the view area may still load.
        var unmeshed = new List<ChunkCoordinate>();
        int checkedChunks = 0;
        _harness.Probe.Sessions.SubscribeStarted(_harness.Probe.Context, _ =>
        {
            Grid terrain = _harness.Terrain;
            ChunkArea area = _harness.Probe.Loading.SimulationArea;
            for (int x = -area.Radius; x <= area.Radius; x++)
            {
                for (int y = -area.Height; y <= area.Height; y++)
                {
                    for (int z = -area.Radius; z <= area.Radius; z++)
                    {
                        if (area.Contains(x, y, z))
                        {
                            checkedChunks++;
                            if (!_harness.Meshing.IsMeshed(terrain, new ChunkCoordinate(x, y, z)))
                            {
                                unmeshed.Add(new ChunkCoordinate(x, y, z));
                            }
                        }
                    }
                }
            }
        });
        _harness.RunUntil(() => _harness.Probe.Sessions.State == SessionState.Active);

        Assert.Equal(LoadingTaskState.Completed, task.State);
        Assert.Equal(_harness.Probe.Loading.SimulationArea.Count, checkedChunks);
        Assert.Empty(unmeshed);
    }

    [Fact]
    public void AMultiBlockIsMeshedFromItsOriginEvenAcrossTwoChunks()
    {
        var models = new Dictionary<ChunkCoordinate, ChunkVertex[]>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) => models[chunk] = [.. output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.PositiveZ && v.Position.Z == 4.5f)];
        Grid terrain = _harness.Terrain;

        // The bench covers (31, 0, 4) and (32, 0, 4): its model, two blocks wide, goes out of the chunk of its origin.
        _harness.Place(terrain, (new BlockCoordinate(31, 0, 4), _harness.State("block:example.world.bench")));
        _harness.Frame(2);

        Assert.Equal(4, models[new ChunkCoordinate(0, 0, 0)].Length);
        Assert.Contains(models[new ChunkCoordinate(0, 0, 0)], v => v.Position == new Vector3(33, 0.5f, 4.5f));
        Assert.False(models.TryGetValue(new ChunkCoordinate(1, 0, 0), out ChunkVertex[]? other) && other.Length > 0);
    }

    [Fact]
    public void AModelIsTurnedWithTheOrientationOfItsState()
    {
        var models = new List<ChunkVertex>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) =>
        {
            if (chunk == new ChunkCoordinate(0, 0, 0))
            {
                models.AddRange(output.Vertices.ToArray().Where(v => v.Position.Y > 0));
            }
        };
        Grid terrain = _harness.Terrain;
        IBlockRegistry blocks = _harness.Probe.Blocks;
        BlockType sign = blocks.Get("block:example.world.sign");
        uint east = blocks.With(sign.DefaultState, sign.GetProperty("orientation"), sign.GetProperty("orientation").IndexOf("east"));

        _harness.Place(terrain, (new BlockCoordinate(4, 0, 4), sign.DefaultState), (new BlockCoordinate(8, 0, 4), east));
        _harness.Frame(2);

        // The square faces south (+Z) in the reference orientation; a quarter turn clockwise seen from above makes it face west.
        ChunkVertex[] north = [.. models.Where(v => v.Position.X < 6)];
        ChunkVertex[] turned = [.. models.Where(v => v.Position.X > 6)];
        Assert.All(north, v => Assert.Equal(ChunkNormal.PositiveZ, v.Normal));
        Assert.All(north, v => Assert.Equal(4.5f, v.Position.Z));
        Assert.All(turned, v => Assert.Equal(ChunkNormal.NegativeX, v.Normal));
        Assert.All(turned, v => Assert.Equal(8.5f, v.Position.X));
        Assert.Equal(ChunkCategory.Cutout, _harness.Meshing.GetVisual(east).Category);
    }

    [Fact]
    public void RemeshesStartWithChangedChunksThenTheNearestTheCamera()
    {
        var grid = new GridEntry(null!);
        ChunkEntry Entry(int x, double distance, bool urgent) => new(grid, new ChunkCoordinate(x, 0, 0)) { Distance = distance, Urgent = urgent };
        List<ChunkEntry> entries = [Entry(0, 9e6, false), Entry(1, 1e6, false), Entry(2, 4e6, true), Entry(3, 2e6, false), Entry(4, 1e2, true)];

        entries.Sort(new ChunkRemesher.DistanceOrder());

        Assert.Equal([4, 2, 1, 3, 0], entries.Select(e => e.Coordinate.X));
    }

    [Fact]
    public void TheWorldLoadsFromTheViewerOutward()
    {
        var first = new List<ChunkCoordinate>();
        _harness.Settings[VoxelSettings.ViewRadius] = "4";
        _harness.Start();
        _harness.Remesher.MaxRunning = 1;
        _harness.Remesher.Applied = (_, chunk, _) => first.Add(chunk);
        _harness.Settle();

        // With one remesh at a time, the stone just under the viewer is meshed before the far edge of the view.
        int near = first.FindIndex(c => c == new ChunkCoordinate(0, -1, 0));
        int far = first.FindIndex(c => c == new ChunkCoordinate(4, -1, 0));
        Assert.True(near >= 0 && far >= 0);
        Assert.True(near < far, $"(0, -1, 0) meshed at {near}, (4, -1, 0) at {far}");
    }

    [Fact]
    public void TheVisualOfAStateComesFromItsRenderProperties()
    {
        _harness.Start();
        IVoxelMeshing meshing = _harness.Meshing;
        IRenderResources resources = _harness.Probe.Context.Resolve<IRenderResources>();
        BlockVisual grass = meshing.GetVisual(_harness.State("block:example.world.grass"));

        Assert.True(grass.TryGetTexture(BlockFace.Up, out TextureLocation top));
        Assert.True(grass.TryGetTexture(BlockFace.North, out TextureLocation side));
        Assert.Equal(resources.GetTexture("texture://Example/World/Grass").Location, top);
        Assert.Equal(resources.GetTexture("texture://Example/World/Dirt").Location, side);
        Assert.Equal(1, grass.Tint);
        Vector3 tint = _harness.Chunks.Tints[1];
        Assert.Equal(0.2016f, tint.X, 3);
        Assert.Equal(ChunkCategory.Transparent, meshing.GetVisual(_harness.State("block:example.world.glass")).Category);
        Assert.Equal(ChunkCategory.Cutout, meshing.GetVisual(_harness.State("block:example.world.leaves")).Category);
    }

    [Fact]
    public void AnUnknownTextureStopsTheStart()
    {
        _harness.Blocks = "<Block><Name>Broken</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><RenderTexture>texture://Example/World/Missing</RenderTexture></Properties></Block>";

        Exception error = Assert.ThrowsAny<Exception>(() => _harness.Start());

        BlockVisualException visual = Assert.IsType<BlockVisualException>(Unwrap(error));
        Assert.Contains("texture://Example/World/Missing", Assert.Single(visual.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("north", "", 0)]
    [InlineData("east", "", 1)]
    [InlineData("north", " turn=\"90\"", 1)]
    [InlineData("east", " turn=\"270\"", 0)]
    public void TheTextureOfAFaceTurnsWithTheBlockAndItsAttribute(string orientation, string attribute, int quarterTurns)
    {
        // A quarter turn clockwise seen from above turns the top face too: its texture follows, then the attribute adds to it.
        _harness.Blocks = MeshingHarness.StandardBlocks + $"<Block><Name>Log</Name><Content><Shape>shape:nobara.cube</Shape><Orientations>facing</Orientations></Content><Properties><RenderTexture>texture://Example/World/Stone</RenderTexture><RenderTextureTop{attribute}>texture://Example/World/Dirt</RenderTextureTop></Properties></Block>";
        var top = new List<ChunkVertex>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) =>
        {
            if (chunk == new ChunkCoordinate(0, 0, 0))
            {
                top.AddRange(output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.PositiveY && v.Position.Y > 0.5f));
            }
        };
        BlockType log = _harness.Probe.Blocks.Get("block:example.world.log");
        StateProperty property = log.GetProperty("orientation");
        _harness.Place(_harness.Terrain, (new BlockCoordinate(4, 0, 4), _harness.Probe.Blocks.With(log.DefaultState, property, property.IndexOf(orientation))));
        _harness.Frame(2);

        // Without a turn, U follows X and V follows Z; each quarter turn, clockwise seen from above, turns them.
        Assert.Equal(4, top.Count);
        Assert.All(top, v =>
        {
            float u = v.Position.X - 4;
            float w = v.Position.Z - 4;
            Vector2 expected = quarterTurns switch
            {
                0 => new Vector2(u, w),
                1 => new Vector2(w, 1 - u),
                2 => new Vector2(1 - u, 1 - w),
                _ => new Vector2(1 - w, u),
            };
            Assert.Equal(expected, v.TextureCoordinates);
        });
    }

    [Fact]
    public void AStandingLogShowsItsSideTexturesUprightWithTurnsOnItsEastAndWestFaces()
    {
        // The reference log lies along Z, its bark turned on its east and west faces; standing up, both show it upright.
        _harness.Blocks = MeshingHarness.StandardBlocks + "<Block><Name>Log</Name><Content><Shape>shape:nobara.cube</Shape><Orientations>facing</Orientations></Content><Properties><RenderTexture>texture://Example/World/Stone</RenderTexture><RenderTextureEast turn=\"90\">texture://Example/World/Stone</RenderTextureEast><RenderTextureWest turn=\"270\">texture://Example/World/Stone</RenderTextureWest></Properties></Block>";
        var east = new List<ChunkVertex>();
        var west = new List<ChunkVertex>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) =>
        {
            if (chunk == new ChunkCoordinate(0, 0, 0))
            {
                east.AddRange(output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.PositiveX && v.Position.Y > -0.5f));
                west.AddRange(output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.NegativeX && v.Position.Y > -0.5f));
            }
        };
        BlockType log = _harness.Probe.Blocks.Get("block:example.world.log");
        StateProperty property = log.GetProperty("orientation");
        _harness.Place(_harness.Terrain, (new BlockCoordinate(4, 0, 4), _harness.Probe.Blocks.With(log.DefaultState, property, property.IndexOf("up"))));
        _harness.Frame(2);

        // As on a block that is not turned: U toward -Z on the east face, toward +Z on the west face, V down.
        Assert.Equal(4, east.Count);
        Assert.All(east, v => Assert.Equal(new Vector2(5 - v.Position.Z, 1 - v.Position.Y), v.TextureCoordinates));
        Assert.Equal(4, west.Count);
        Assert.All(west, v => Assert.Equal(new Vector2(v.Position.Z - 4, 1 - v.Position.Y), v.TextureCoordinates));
    }

    [Fact]
    public void AnInvalidTurnOfATextureStopsTheStart()
    {
        _harness.Blocks = "<Block><Name>Odd</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><RenderTexture turn=\"45\">texture://Example/World/Stone</RenderTexture></Properties></Block>";

        Exception error = Assert.ThrowsAny<Exception>(() => _harness.Start());

        BlockVisualException visual = Assert.IsType<BlockVisualException>(Unwrap(error));
        Assert.Contains("is '45'; it is 0, 90, 180, or 270", Assert.Single(visual.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AShapeWithoutAMesherIsReported()
    {
        _harness.Blocks = MeshingHarness.StandardBlocks + "<Block><Name>Odd</Name><Content><Shape>shape:example.odd</Shape></Content></Block>";
        _harness.Configure = probe => probe.Shapes.Register(probe.Context, new FakeShape("shape:example.odd", instanceDependent: false));

        _harness.Start();

        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("shape:example.odd", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMesherOfAnInstanceDependentShapeReceivesTheBlockData()
    {
        var data = new BlockDataType<int>("example.world.height", static (value, writer) =>
        {
            BitConverter.TryWriteBytes(writer.GetSpan(sizeof(int)), value);
            writer.Advance(sizeof(int));
        }, static bytes => BitConverter.ToInt32(bytes));
        var mesher = new SculptedMesher(data);
        _harness.Blocks = MeshingHarness.StandardBlocks + "<Block><Name>Sculpted</Name><Content><Shape>shape:example.sculpted</Shape></Content><Properties><RenderTexture>texture://Example/World/Stone</RenderTexture></Properties></Block>";
        _harness.Configure = probe =>
        {
            probe.Shapes.Register(probe.Context, new FakeShape("shape:example.sculpted", instanceDependent: true));
            probe.Context.Resolve<IVoxelMeshing>().RegisterMesher(probe.Context, "shape:example.sculpted", mesher);
        };
        _harness.Start();
        _harness.Settle();
        Grid terrain = _harness.Terrain;
        var position = new BlockCoordinate(3, 0, 3);
        _harness.Place(terrain, (position, _harness.State("block:example.world.sculpted")));
        _harness.Frame(2);
        Assert.Equal(0, mesher.Seen);

        // The test writes the data, then changes another block of the chunk so that it is remeshed.
        Assert.True(terrain.Storage.TryGetChunk(new ChunkCoordinate(0, 0, 0), out Chunk? chunk));
        chunk!.SetBlockData(data, VoxelCoordinates.ToIndex(position), 7);
        _harness.Place(terrain, (new BlockCoordinate(10, 0, 10), _harness.State("block:example.world.stone")));
        _harness.Frame(2);

        Assert.Equal(7, mesher.Seen);
        Assert.True(mesher.SawCopy);
        Assert.Equal(new LocalBlockCoordinate(3, 0, 3), mesher.Position);
    }

    [Fact]
    public void TheEndOfTheSessionFreesTheSections()
    {
        _harness.Start();
        _harness.Settle();
        Assert.True(_harness.Chunks.SectionCount > 0);

        _harness.Probe.Sessions.End();
        _harness.Frame(3);

        Assert.Equal(SessionState.None, _harness.Probe.Sessions.State);
        Assert.Equal(0, _harness.Chunks.SectionCount);
        Assert.Equal(0, _harness.Chunks.GroupCount);
        Assert.Equal(0, _harness.Remesher.WorldState);
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void MeshersAreRegisteredOnlyBeforeTheStart()
    {
        _harness.Configure = probe => Assert.Throws<ArgumentException>(() => probe.Context.Resolve<IVoxelMeshing>().RegisterMesher(probe.Context, "shape:nobara.cube", new SculptedMesher(null!)));
        _harness.Start();

        Assert.Throws<InvalidOperationException>(() => _harness.Meshing.RegisterMesher(_harness.Probe.Context, "shape:example.late", new SculptedMesher(null!)));
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is not BlockVisualException && error.InnerException is { } inner)
        {
            error = inner;
        }

        return error;
    }

    /// <summary>A full cube, instance-dependent or not.</summary>
    private sealed class FakeShape(string id, bool instanceDependent) : IBlockShape
    {
        public string Id { get; } = id;

        public bool IsInstanceDependent { get; } = instanceDependent;

        public ShapeEvaluation Evaluate(in BlockShapeRequest request) => new(CollisionShape.FullBlock, FaceMask.All, ContactMasks.Full);
    }

    /// <summary>A mesher that reads the block data of its block and draws one triangle.</summary>
    private sealed class SculptedMesher(BlockDataType<int> data) : IBlockMesher
    {
        public int Seen { get; private set; }

        public bool SawCopy { get; private set; }

        public LocalBlockCoordinate Position { get; private set; }

        public void Mesh(in BlockMeshContext block, BlockMeshWriter writer)
        {
            Seen = block.Data.Get(data);
            SawCopy = block.Data.IsCopy;
            Position = block.Position;
            Assert.True(block.Visual.TryGetTexture(BlockFace.Up, out TextureLocation texture));
            int a = writer.AddVertex(new Vector3(0, 1, 0), default, texture, ChunkNormal.PositiveY);
            int b = writer.AddVertex(new Vector3(0, 1, 1), default, texture, ChunkNormal.PositiveY);
            int c = writer.AddVertex(new Vector3(1, 1, 0), default, texture, ChunkNormal.PositiveY);
            writer.AddTriangle(ChunkCategory.Opaque, a, b, c);
        }
    }
}
