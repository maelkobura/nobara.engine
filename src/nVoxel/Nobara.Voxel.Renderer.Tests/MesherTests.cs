// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using Nobara.Core;
using Nobara.Renderer;
using Xunit.Abstractions;

namespace Nobara.Voxel.Renderer.Tests;

/// <summary>The mesher alone, on copies of chunks that the tests fill.</summary>
public sealed class MesherTests(ITestOutputHelper output)
{
    private const uint Stone = 1;
    private const uint Dirt = 2;
    private const uint Glass = 3;
    private const uint Leaves = 4;
    private const uint Statue = 5;
    private const uint Custom = 6;

    private readonly ChunkMesher _mesher = new();
    private readonly MeshOutput _output = new();

    [Fact]
    public void ASingleCubeHasSixFaces()
    {
        MeshInput input = Air();
        Set(input, 4, 5, 6, Stone);

        Mesh(input);

        Assert.Equal(6, Quads(ChunkCategory.Opaque));
        Assert.Equal(24, _output.VertexCount);
        Assert.All(_output.Vertices.ToArray(), v => Assert.Equal(new TextureLocation(0, 1), v.Texture));
        Assert.Contains(_output.Vertices.ToArray(), v => v.Position == new Vector3(5, 6, 7));
    }

    [Fact]
    public void AFlatLayerMergesIntoSixQuads()
    {
        MeshInput input = Air();
        Fill(input, 0, 0, 0, 32, 1, 32, Stone);

        Mesh(input);

        Assert.Equal(6, Quads(ChunkCategory.Opaque));
        ChunkVertex[] top = [.. _output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.PositiveY)];
        Assert.Equal(4, top.Length);
        Assert.All(top, v => Assert.Equal(1, v.Position.Y));
        Assert.Contains(top, v => v.TextureCoordinates == new Vector2(32, 32));
    }

    [Fact]
    public void FacesWhoseTextureIsTurnedDifferentlyDoNotMerge()
    {
        MeshInput input = Air();
        Fill(input, 0, 0, 0, 2, 1, 1, Stone);
        BlockVisuals visuals = Visuals();
        visuals.FaceTurns[(Dirt * 6) + 3] = 1;
        for (int face = 0; face < 6; face++)
        {
            visuals.FaceTextures[(Dirt * 6) + face] = visuals.FaceTextures[(Stone * 6) + face];
            visuals.OccludeMasks[Dirt] = visuals.OccludeMasks[Stone];
            visuals.TypeIndices[Dirt] = visuals.TypeIndices[Stone];
        }

        _mesher.Mesh(input, visuals, _output);
        int merged = _output.Vertices.ToArray().Count(v => v.Normal == ChunkNormal.PositiveY);
        Set(input, 1, 0, 0, Dirt);
        _mesher.Mesh(input, visuals, _output);
        int split = _output.Vertices.ToArray().Count(v => v.Normal == ChunkNormal.PositiveY);

        Assert.Equal(4, merged);
        Assert.Equal(8, split);
    }

    [Fact]
    public void FacesOfDifferentTexturesDoNotMerge()
    {
        MeshInput input = Air();
        Fill(input, 0, 0, 0, 32, 1, 16, Stone);
        Fill(input, 0, 0, 16, 32, 1, 32, Dirt);

        Mesh(input);

        // Top, bottom, and the two X sides split in two; each Z side has one block type.
        Assert.Equal(10, Quads(ChunkCategory.Opaque));
    }

    [Fact]
    public void ACheckerboardDoesNotMerge()
    {
        MeshInput input = Air();
        for (int x = 0; x < 4; x++)
        {
            for (int z = 0; z < 4; z++)
            {
                if (((x + z) & 1) == 0)
                {
                    Set(input, x, 0, z, Stone);
                }
            }
        }

        Mesh(input);

        Assert.Equal(8 * 6, Quads(ChunkCategory.Opaque));
    }

    [Fact]
    public void FacesTowardANeighboringChunkAreHiddenByItsBlocksAndWhenItIsNotLoaded()
    {
        MeshInput input = Air();
        Set(input, 31, 3, 3, Stone);
        Mesh(input);
        Assert.Equal(6, Quads(ChunkCategory.Opaque));

        Set(input, 32, 3, 3, Stone);
        Mesh(input);
        Assert.Equal(5, Quads(ChunkCategory.Opaque));
        Assert.DoesNotContain(_output.Vertices.ToArray(), v => v.Normal == ChunkNormal.PositiveX);

        Set(input, 32, 3, 3, MeshInput.NotLoaded);
        Mesh(input);
        Assert.Equal(5, Quads(ChunkCategory.Opaque));
    }

    [Fact]
    public void TransparentBlocksOfOneTypeHideTheFacesBetweenThemAndCutOutBlocksKeepThem()
    {
        MeshInput input = Air();
        Set(input, 1, 1, 1, Glass);
        Set(input, 2, 1, 1, Glass);
        Set(input, 1, 5, 1, Leaves);
        Set(input, 2, 5, 1, Leaves);

        Mesh(input);

        Assert.Equal(6, Quads(ChunkCategory.Transparent));
        Assert.Equal(8, Quads(ChunkCategory.Cutout));
        Assert.Equal(0, Quads(ChunkCategory.Opaque));
    }

    [Fact]
    public void AnOpaqueBlockHidesTheFaceOfATransparentOneButNotTheReverse()
    {
        MeshInput input = Air();
        Set(input, 1, 1, 1, Stone);
        Set(input, 2, 1, 1, Glass);

        Mesh(input);

        Assert.Equal(6, Quads(ChunkCategory.Opaque));
        Assert.Equal(5, Quads(ChunkCategory.Transparent));
    }

    [Fact]
    public void AmbientOcclusionDarkensTheCornersNextToBlocks()
    {
        MeshInput input = Air();
        Set(input, 5, 0, 5, Stone);
        Set(input, 6, 1, 5, Stone);
        Set(input, 5, 1, 6, Stone);

        Mesh(input);

        // The top of the block at (5, 0, 5): its corner (6, 1, 6) has a block on each side, (5, 1, 5) none.
        ChunkVertex[] top = [.. _output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.PositiveY && v.Position.Y == 1 && v.Position.X <= 6 && v.Position.Z <= 6 && v.Position.X >= 5 && v.Position.Z >= 5)];
        Assert.Equal(4, top.Length);
        Assert.Equal(3, top.Single(v => v.Position == new Vector3(6, 1, 6)).AmbientOcclusion);
        Assert.Equal(1, top.Single(v => v.Position == new Vector3(6, 1, 5)).AmbientOcclusion);
        Assert.Equal(1, top.Single(v => v.Position == new Vector3(5, 1, 6)).AmbientOcclusion);
        Assert.Equal(0, top.Single(v => v.Position == new Vector3(5, 1, 5)).AmbientOcclusion);
    }

    [Fact]
    public void FacesInTheShadowOfABlockAreNotMergedWithTheOthers()
    {
        MeshInput input = Air();
        Fill(input, 0, 0, 0, 4, 1, 1, Stone);
        Set(input, 1, 1, 0, Stone);

        Mesh(input);

        // The top of the row: (0) and (2) touch the block above at one side, (3) is clear and merges with nothing else.
        ChunkVertex[] top = [.. _output.Vertices.ToArray().Where(v => v.Normal == ChunkNormal.PositiveY && v.Position.Y == 1)];
        Assert.Equal(3 * 4, top.Length);
    }

    [Fact]
    public void AModelIsPlacedInItsCellWithTheTintAndCategoryOfItsBlock()
    {
        MeshInput input = Air();
        Set(input, 3, 4, 5, Statue);

        Mesh(input);

        Assert.Equal(1, _output.TriangleCount);
        Assert.Equal(3, _output.Indices(ChunkCategory.Cutout).Length);
        ChunkVertex[] vertices = _output.Vertices.ToArray();
        Assert.Contains(vertices, v => v.Position == new Vector3(3, 4, 5));
        Assert.Contains(vertices, v => v.Position == new Vector3(4, 4, 5));
        Assert.All(vertices, v => Assert.Equal(2, v.Tint));
    }

    [Fact]
    public void AMesherOfAnotherShapeWritesInItsCell()
    {
        MeshInput input = Air();
        Set(input, 7, 8, 9, Custom);
        Set(input, 7, 9, 9, Stone);

        Mesh(input);

        Assert.Equal(new LocalBlockCoordinate(7, 8, 9), TestMesher.Last.Position);
        Assert.Equal(Stone, TestMesher.Last.Above);
        Assert.Contains(_output.Vertices.ToArray(), v => v.Position == new Vector3(7.5f, 8.5f, 9.5f));
    }

    [Fact]
    public void TheConnectionsOfTheFacesFollowTheOpenCells()
    {
        MeshInput input = Air();
        Mesh(input);
        Assert.Equal(SectionConnectivity.All, _output.Connectivity);

        Fill(input, 0, 0, 0, 32, 32, 32, Stone);
        Mesh(input);
        Assert.Equal(SectionConnectivity.None, _output.Connectivity);

        // A wall at X = 16 parts the chunk in two halves.
        input = Air();
        Fill(input, 16, 0, 0, 17, 32, 32, Stone);
        Mesh(input);
        Assert.False(_output.Connectivity.IsConnected(SectionFace.NegativeX, SectionFace.PositiveX));
        Assert.True(_output.Connectivity.IsConnected(SectionFace.NegativeX, SectionFace.PositiveY));
        Assert.True(_output.Connectivity.IsConnected(SectionFace.NegativeY, SectionFace.PositiveY));
    }

    [Fact]
    public void TheMesherAllocatesNothingOnceItMeshedAChunk()
    {
        MeshInput input = Surface();
        Mesh(input);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Mesh(input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TheTimeOfMeshingATypicalChunkIsMeasured()
    {
        MeshInput input = Surface();
        Mesh(input);
        const int Runs = 50;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Runs; i++)
        {
            Mesh(input);
        }

        watch.Stop();
        output.WriteLine($"A surface chunk: {watch.Elapsed.TotalMilliseconds / Runs:F3} ms per remesh, {_output.TriangleCount} triangles, {_output.VertexCount} vertices.");
        Assert.True(_output.TriangleCount > 0);
    }

    internal static BlockVisuals Visuals()
    {
        var visuals = new BlockVisuals(7);
        void Cube(uint state, int layer, byte occludes, ChunkCategory category, int type)
        {
            visuals.Kinds[state] = BlockVisuals.Cube;
            visuals.OccludeMasks[state] = occludes;
            visuals.Categories[state] = (byte)category;
            visuals.TypeIndices[state] = type;
            visuals.ModelIndices[state] = -1;
            for (int face = 0; face < 6; face++)
            {
                visuals.FaceTextures[(state * 6) + face] = BlockVisuals.Pack(new TextureLocation(0, layer));
            }
        }

        visuals.ModelIndices[0] = -1;
        Cube(Stone, 1, BlockVisuals.AllFaces, ChunkCategory.Opaque, 1);
        Cube(Dirt, 2, BlockVisuals.AllFaces, ChunkCategory.Opaque, 2);
        Cube(Glass, 3, 0, ChunkCategory.Transparent, 3);
        Cube(Leaves, 4, 0, ChunkCategory.Cutout, 4);
        visuals.Kinds[Statue] = BlockVisuals.Model;
        visuals.Categories[Statue] = (byte)ChunkCategory.Cutout;
        visuals.TintIndices[Statue] = 2;
        visuals.ModelIndices[Statue] = 0;
        visuals.Models =
        [
            new ModelGeometry(
                [
                    new ModelVertex(new Vector3(0, 0, 0), new Vector2(0, 1), ChunkNormal.PositiveZ, new TextureLocation(0, 5)),
                    new ModelVertex(new Vector3(1, 0, 0), new Vector2(1, 1), ChunkNormal.PositiveZ, new TextureLocation(0, 5)),
                    new ModelVertex(new Vector3(0, 1, 0), new Vector2(0, 0), ChunkNormal.PositiveZ, new TextureLocation(0, 5)),
                ],
                [0, 1, 2]),
        ];
        visuals.Kinds[Custom] = BlockVisuals.Custom;
        visuals.ModelIndices[Custom] = -1;
        visuals.MesherIndices[Custom] = 0;
        visuals.Meshers = [new TestMesher()];
        return visuals;
    }

    internal static MeshInput Air()
    {
        var input = new MeshInput();
        Array.Clear(input.States);
        return input;
    }

    internal static void Set(MeshInput input, int x, int y, int z, uint state) => input.States[MeshInput.Index(x, y, z)] = state;

    internal static void Fill(MeshInput input, int x0, int y0, int z0, int x1, int y1, int z1, uint state)
    {
        for (int y = y0; y < y1; y++)
        {
            for (int z = z0; z < z1; z++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Set(input, x, y, z, state);
                }
            }
        }
    }

    // A chunk of the surface of a world: hills of dirt over stone, with some glass and leaves.
    internal static MeshInput Surface()
    {
        MeshInput input = Air();
        for (int x = -1; x <= 32; x++)
        {
            for (int z = -1; z <= 32; z++)
            {
                int height = 12 + (int)(6 * MathF.Sin(x * 0.3f) * MathF.Cos(z * 0.25f));
                for (int y = -1; y <= 32; y++)
                {
                    uint state = y < height - 3 ? Stone : y < height ? Dirt : 0;
                    if (y == height && ((x * 7) + (z * 13)) % 17 == 0)
                    {
                        state = (x & 1) == 0 ? Leaves : Glass;
                    }

                    Set(input, x, y, z, state);
                }
            }
        }

        return input;
    }

    private void Mesh(MeshInput input) => _mesher.Mesh(input, s_visuals, _output);

    private int Quads(ChunkCategory category) => _output.Indices(category).Length / 6;

    private static readonly BlockVisuals s_visuals = Visuals();

    /// <summary>A mesher that writes a triangle at the center of its cell, and remembers what it saw.</summary>
    private sealed class TestMesher : IBlockMesher
    {
        public static (LocalBlockCoordinate Position, uint Above) Last { get; private set; }

        public void Mesh(in BlockMeshContext block, BlockMeshWriter writer)
        {
            Last = (block.Position, block.GetNeighbor(0, 1, 0));
            int a = writer.AddVertex(new Vector3(0.5f), default, default, ChunkNormal.None);
            int b = writer.AddVertex(new Vector3(0.5f, 1, 0.5f), default, default, ChunkNormal.None);
            int c = writer.AddVertex(new Vector3(1, 0.5f, 0.5f), default, default, ChunkNormal.None);
            writer.AddTriangle(ChunkCategory.Opaque, a, b, c);
        }
    }
}
