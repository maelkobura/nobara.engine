// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Graphics;
using Nobara.Renderer;
using Nobara.Voxel.Renderer.Tests.Support;

namespace Nobara.Voxel.Renderer.Tests;

/// <summary>nVoxel.Renderer with nLighting, its optional dependency, and without it.</summary>
public sealed class LightingTests : IDisposable
{
    private static readonly ChunkCoordinate Ground = new(0, -1, 0);

    private readonly MeshingHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void WithoutLightingChunksAreMeshedAtFullLight()
    {
        var vertices = new List<ChunkVertex>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) => vertices.AddRange(output.Vertices.ToArray());

        _harness.Place(_harness.Terrain, (new BlockCoordinate(5, 0, 5), _harness.State("block:example.world.lantern")));
        _harness.Frame(2);

        Assert.Null(_harness.Remesher.Lighting);
        Assert.NotEmpty(vertices);
        Assert.All(vertices, v => Assert.Equal(ChunkLight.Full, v.Light));
    }

    [Fact]
    public void ALanternLightsTheGroundAroundItAtTheNextFrameAndTakingItAwayDarkensItAgain()
    {
        _harness.Lighting = true;
        var tops = new Dictionary<Vector3, ChunkLight>();
        _harness.Start();
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) =>
        {
            if (chunk == Ground)
            {
                tops.Clear();
                foreach (ChunkVertex vertex in output.Vertices)
                {
                    if (vertex.Normal == ChunkNormal.PositiveY)
                    {
                        tops[vertex.Position] = vertex.Light;
                    }
                }
            }
        };
        Assert.NotNull(_harness.Remesher.Lighting);

        // The ground is under the open sky: its tops are at Y = 32 in the chunk below Y = 0.
        uint lantern = _harness.State("block:example.world.lantern");
        _harness.Place(_harness.Terrain, (new BlockCoordinate(5, 0, 5), lantern));
        _harness.Frame(2);

        Assert.NotEmpty(tops);
        Assert.Equal(15, tops[new Vector3(4, 32, 5)].Sky);
        float near = tops[new Vector3(4, 32, 5)].Red;
        float middle = tops[new Vector3(2, 32, 5)].Red;
        float far = tops[new Vector3(0, 32, 5)].Red;
        Assert.True(near > middle && middle > far && far > 0, $"Light of blocks on the ground: {near}, {middle}, {far}.");

        _harness.Probe.Batches.Break(_harness.Terrain, BlockActor.OfCommand("test"), BlockCause.Hand, [new BlockCoordinate(5, 0, 5)]);
        _harness.Frame(2);

        // The ground has one light again: its tops merge.
        Assert.NotEmpty(tops);
        Assert.All(tops.Values, light => Assert.Equal(new ChunkLight(15, 0, 0, 0), light));
        Assert.False(tops.ContainsKey(new Vector3(4, 32, 5)));
    }

    [Fact]
    public void AChangeOfLightAloneRemeshesAChunk()
    {
        _harness.Lighting = true;
        var applied = new List<float>();
        _harness.Start();
        _harness.Settle();
        Grid terrain = _harness.Terrain;
        _harness.Place(terrain, (new BlockCoordinate(36, 5, 16), _harness.State("block:example.world.stone")));
        _harness.Settle();
        _harness.Remesher.Applied = (_, chunk, output) =>
        {
            if (chunk == new ChunkCoordinate(1, 0, 0))
            {
                applied.Add(output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.NegativeX).Max(v => v.Light.Red));
            }
        };

        // The lantern stands three blocks from the border: no block of the next chunk changes, only its light.
        _harness.Place(terrain, (new BlockCoordinate(28, 5, 16), _harness.State("block:example.world.lantern")));
        _harness.RunUntil(() => applied.Count > 0 && _harness.Meshing.Statistics is { Dirty: 0, Running: 0 });

        Assert.True(applied[^1] > 0, $"The face of the block of the next chunk toward the lantern has light {applied[^1]}.");
    }

    [Fact]
    public void AResultOfAChunkWhoseLightChangedWhileItWasMeshedIsDiscarded()
    {
        // The change of light is told by hand, so that nothing else marks the chunk meanwhile.
        _harness.Start();
        _harness.Settle();
        Grid terrain = _harness.Terrain;
        VoxelMeshingStatistics before = _harness.Meshing.Statistics;

        _harness.Place(terrain, (new BlockCoordinate(9, 5, 9), _harness.State("block:example.world.stone")));
        _harness.Frame();
        Assert.Equal(before.Running + 1, _harness.Meshing.Statistics.Running);
        _harness.Remesher.OnLightChanged(terrain, new ChunkCoordinate(0, 0, 0), touchesBorder: false);
        _harness.Frame();

        Assert.Equal(before.Discarded + 1, _harness.Meshing.Statistics.Discarded);
        _harness.Settle();
        Assert.True(_harness.Meshing.IsMeshed(terrain, new ChunkCoordinate(0, 0, 0)));
    }

    [Fact]
    public void AGridThatEntersUnderARoofGetsTheDarkerLightOfTheTerrain()
    {
        _harness.Lighting = true;
        _harness.FlatTerrain = false;
        _harness.Configure = probe => probe.Loading.AddProvider(probe.Context, new RoofProvider(() => probe.Blocks.Get("block:example.world.stone").DefaultState));
        _harness.Start();
        _harness.Settle();
        WorldProbe probe = _harness.Probe;
        Grid ship = probe.Worlds.CreateGrid(probe.Context, DimensionId.Default, new GridOptions
        {
            Bounds = new GridBounds(new BlockCoordinate(0, 0, 0), new BlockCoordinate(3, 3, 3)),
            Transform = WorldTransform.At(WorldPosition.FromBlocks(10, 40, 10)),
        });
        _harness.Place(ship, (new BlockCoordinate(1, 1, 1), _harness.State("block:example.world.stone")));
        ChunkGroupId group = default;
        _harness.RunUntil(() => _harness.Remesher.TryGetGroup(ship, out group) && _harness.Chunks.GetGroupLight(group).Sky == 15);
        Assert.False(_harness.Remesher.TryGetGroup(_harness.Terrain, out ChunkGroupId terrain) && _harness.Chunks.GetGroupLight(terrain) != ChunkLight.OpenSky);

        ship.UpdateTransform(WorldTransform.At(WorldPosition.FromBlocks(10, 8, 10)));
        _harness.RunUntil(() => _harness.Chunks.GetGroupLight(group).Sky == 0, () => $"The light around the grid is {_harness.Chunks.GetGroupLight(group)}.");

        probe.Worlds.Destroy(probe.Context, ship);
        _harness.Frame(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramesAllocateNothingOnceTheWorldIsLitAndMeshed(bool lighting)
    {
        _harness.Lighting = lighting;
        _harness.Start();
        _harness.Settle();
        _harness.Place(_harness.Terrain, (new BlockCoordinate(5, 0, 5), _harness.State("block:example.world.lantern")));
        _harness.Settle();
        _harness.Frame(30);

        // The fake device records its calls; a few rounds let the runtime finish compiling.
        ((FakeDevice)_harness.Probe.Context.Resolve<IGraphicsSystem>().Device).Recording = false;
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            _harness.Frame(30);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    /// <summary>Stone below Y = 0, and a roof of stone from Y = 20 to 21 over every chunk.</summary>
    private sealed class RoofProvider(Func<uint> stone) : IChunkProvider
    {
        private uint _stone = uint.MaxValue;

        public bool Accepts(in ChunkRequest request)
        {
            if (_stone == uint.MaxValue)
            {
                _stone = stone();
            }

            return request.IsTerrain;
        }

        [HotPath]
        public void Produce(in ChunkRequest request, Span<uint> states)
        {
            if (request.Chunk.Y < 0)
            {
                states.Fill(_stone);
            }
            else if (request.Chunk.Y == 0)
            {
                states.Slice(20 * 1024, 2 * 1024).Fill(_stone);
            }
        }
    }
}
