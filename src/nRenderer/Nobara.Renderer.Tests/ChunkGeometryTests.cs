// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Core.Mathematics;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;

namespace Nobara.Renderer.Tests;

public sealed class ChunkGeometryTests : IDisposable
{
    private readonly ChunkScene _scene = new();

    public void Dispose() => _scene.Dispose();

    [Fact]
    public void ThePaletteOfTintsIsWhiteUntilSetAndReachesTheGpuWithTheNextFrame()
    {
        Assert.Equal(ChunkGeometry.TintCount, _scene.Chunks.Tints.Length);
        Assert.All(_scene.Chunks.Tints.ToArray(), t => Assert.Equal(Vector3.One, t));
        _scene.Frame();
        Assert.False(_scene.Chunks.TintsPending);

        _scene.Chunks.SetTints([Vector3.One, new Vector3(0.2f, 0.5f, 0.1f)]);

        Assert.Equal(new Vector3(0.2f, 0.5f, 0.1f), _scene.Chunks.Tints[1]);
        Assert.Equal(Vector3.One, _scene.Chunks.Tints[2]);
        Assert.True(_scene.Chunks.TintsPending);
        _scene.Frame();
        Assert.False(_scene.Chunks.TintsPending);
        Assert.Throws<ArgumentException>(() => _scene.Chunks.SetTints(new Vector3[ChunkGeometry.TintCount + 1]));
    }

    [Fact]
    public void GeometryChangesDuringExtractionOnly()
    {
        Assert.Throws<InvalidOperationException>(() => _scene.Chunks.CreateGroup(WorldTransform.Identity));
        ChunkGroupId group = default;
        _scene.Extract(e => group = e.Chunks.CreateGroup(WorldTransform.Identity));
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, Vector3.One);
        Assert.Throws<InvalidOperationException>(() => _scene.Chunks.AddSection(group, default, new ChunkSectionGeometry(vertices, indices)));
        Assert.True(_scene.Chunks.Contains(group));
    }

    [Fact]
    public void InvalidGeometryAndTakenOriginsAreRefused()
    {
        _scene.Extract(e =>
        {
            ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
            ChunkVertex[] vertices = new ChunkVertex[3];
            Assert.Throws<ArgumentException>(() => e.Chunks.AddSection(group, default, new ChunkSectionGeometry(vertices, [0, 1])));
            Assert.Throws<ArgumentException>(() => e.Chunks.AddSection(group, default, new ChunkSectionGeometry(vertices, [0, 1, 3])));
            e.Chunks.AddSection(group, default, new ChunkSectionGeometry(vertices, [0, 1, 2]));
            Assert.Throws<InvalidOperationException>(() => e.Chunks.AddSection(group, default, new ChunkSectionGeometry(vertices, [0, 1, 2])));
        });
        Assert.Equal(1, _scene.Chunks.SectionCount);
    }

    [Fact]
    public void ASectionIsUploadedToTheSharedBuffersAndDrawnWithAFewIndirectDraws()
    {
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(new Vector3(4), new Vector3(28), layer: 2);
        ChunkSectionId section = default;
        _scene.Look(WorldPosition.FromBlocks(16, 16, 80));
        _scene.Extract(e =>
        {
            ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
            section = e.Chunks.AddSection(group, new SectionOrigin(0, 0, 0), new ChunkSectionGeometry(vertices, indices));
            Assert.False(e.Chunks.IsUploaded(section));
        });

        ChunkGeometry chunks = _scene.Chunks;
        Assert.True(chunks.IsSupported);
        Assert.True(chunks.IsUploaded(section));
        ChunkGeometryStatistics statistics = chunks.Statistics;
        Assert.Equal((1, 1, 0, 1), (statistics.Groups, statistics.Sections, statistics.PendingUploads, statistics.Candidates));
        Assert.Equal(vertices.Length * ChunkVertex.Size, statistics.VertexBytesUsed);
        Assert.Equal(indices.Length * sizeof(uint), statistics.IndexBytesUsed);
        Assert.Equal((long)ChunkGeometry.InitialVertices * ChunkVertex.Size, statistics.VertexBytes);

        ChunkGeometry.SectionSlot slot = chunks.Slot(section);
        Assert.Equal(new Vector3(4), slot.BoundsMin);
        Assert.Equal(new Vector3(28), slot.BoundsMax);
        var read = new ChunkVertex[vertices.Length];
        _scene.Harness.Device.ReadBuffer(chunks.VertexBuffer!, (long)slot.VertexStart * ChunkVertex.Size, MemoryMarshal.AsBytes(read.AsSpan()));
        Assert.Equal(vertices, read);

        var record = new ChunkGeometry.SectionRecord();
        _scene.Harness.Device.ReadBuffer(chunks.SectionBuffer!, (long)section.Index * ChunkGeometry.SectionRecordSize, MemoryMarshal.AsBytes(new Span<ChunkGeometry.SectionRecord>(ref record)));
        Assert.Equal((uint)indices.Length, record.Opaque.Count);
        Assert.Equal((uint)slot.VertexStart, record.Opaque.BaseVertex);

        // One dispatch culls; one indirect draw with a count draws the opaque faces.
        _scene.Harness.Fake.Executed.Clear();
        _scene.Frame();
        Assert.Equal(1, _scene.Harness.Fake.Executed.Count(k => k == CommandKind.Dispatch));
        Assert.Equal(1, _scene.Harness.Fake.Executed.Count(k => k == CommandKind.DrawIndexedIndirectCount));
        Assert.Equal(0, _scene.Harness.Fake.Executed.Count(k => k is CommandKind.DrawIndexed or CommandKind.Draw));
    }

    [Fact]
    public void UploadsWaitBeyondTheBudgetOfAFrame()
    {
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, Vector3.One);
        long bytes = (vertices.Length * ChunkVertex.Size) + (indices.Length * sizeof(uint));
        _scene.Chunks.UploadBudget = bytes * 3;
        _scene.Extract(e =>
        {
            ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
            for (int i = 0; i < 10; i++)
            {
                e.Chunks.AddSection(group, new SectionOrigin(i, 0, 0), new ChunkSectionGeometry(vertices, indices));
            }
        });

        Assert.Equal(7, _scene.Chunks.Statistics.PendingUploads);
        _scene.Frame();
        _scene.Frame();
        Assert.Equal(1, _scene.Chunks.Statistics.PendingUploads);
        _scene.Frame();
        Assert.Equal(0, _scene.Chunks.Statistics.PendingUploads);
    }

    [Fact]
    public void ASectionReplacedAThousandTimesKeepsTheMemoryItHad()
    {
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, new Vector3(8));
        ChunkSectionId section = default;
        _scene.Extract(e => section = e.Chunks.AddSection(e.Chunks.CreateGroup(WorldTransform.Identity), default, new ChunkSectionGeometry(vertices, indices)));
        ChunkGeometryStatistics first = _scene.Chunks.Statistics;

        for (int i = 0; i < 1000; i++)
        {
            ChunkVertex[] next = i % 2 == 0 ? vertices : ChunkScene.Box(Vector3.Zero, new Vector3(1 + (i % 7))).Vertices;
            _scene.Extract(e => e.Chunks.ReplaceSection(section, new ChunkSectionGeometry(next, indices)));
        }

        for (int i = 0; i < 4; i++)
        {
            _scene.Frame();
        }

        ChunkGeometryStatistics last = _scene.Chunks.Statistics;
        Assert.Equal(first.VertexBytes, last.VertexBytes);
        Assert.Equal(first.IndexBytes, last.IndexBytes);
        Assert.Equal(first.VertexBytesUsed, last.VertexBytesUsed);
        Assert.Equal(first.IndexBytesUsed, last.IndexBytesUsed);
    }

    [Fact]
    public void ABufferThatIsFullDoublesAndKeepsItsContent()
    {
        var big = new ChunkVertex[(ChunkGeometry.InitialVertices / 2) + 1];
        for (int i = 0; i < big.Length; i++)
        {
            big[i] = new ChunkVertex(new Vector3(i % 32, 0, 0), default, new TextureLocation(0, i % 2048), ChunkNormal.PositiveY, ChunkLight.Full);
        }

        ChunkSectionId a = default;
        ChunkSectionId b = default;
        _scene.Chunks.UploadBudget = long.MaxValue;
        _scene.Extract(e =>
        {
            ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
            a = e.Chunks.AddSection(group, new SectionOrigin(0, 0, 0), new ChunkSectionGeometry(big, [0, 1, 2]));
            b = e.Chunks.AddSection(group, new SectionOrigin(1, 0, 0), new ChunkSectionGeometry(big, [0, 1, 2]));
        });

        ChunkGeometry chunks = _scene.Chunks;
        Assert.Equal(2L * ChunkGeometry.InitialVertices * ChunkVertex.Size, chunks.Statistics.VertexBytes);
        foreach (ChunkSectionId section in new[] { a, b })
        {
            var read = new ChunkVertex[big.Length];
            _scene.Harness.Device.ReadBuffer(chunks.VertexBuffer!, (long)chunks.Slot(section).VertexStart * ChunkVertex.Size, MemoryMarshal.AsBytes(read.AsSpan()));
            Assert.Equal(big, read);
        }
    }

    [Fact]
    public void DefragmentationMovesSectionsDownWithTheirContent()
    {
        ChunkGeometry chunks = _scene.Chunks;
        var sections = new List<ChunkSectionId>();
        var meshes = new Dictionary<ChunkSectionId, ChunkVertex[]>();
        _scene.Extract(e =>
        {
            ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
            for (int i = 0; i < 64; i++)
            {
                (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, new Vector3(1 + (i % 16)), layer: i);
                ChunkSectionId id = e.Chunks.AddSection(group, new SectionOrigin(i, 0, 0), new ChunkSectionGeometry(vertices, indices));
                sections.Add(id);
                meshes[id] = vertices;
            }
        });

        // Removing every other section leaves holes, and the whole free space of the buffer after them.
        chunks.DefragmentationThreshold = 0;
        _scene.Extract(e =>
        {
            for (int i = 0; i < sections.Count; i += 2)
            {
                e.Chunks.RemoveSection(sections[i]);
                meshes.Remove(sections[i]);
            }
        });

        int[] before = [.. meshes.Keys.Select(s => chunks.Slot(s).VertexStart)];
        long moved = 0;
        for (int i = 0; i < 8; i++)
        {
            _scene.Frame();
            moved += chunks.Statistics.DefragmentedBytes;
        }

        Assert.True(moved > 0, "Nothing was moved.");
        int[] after = [.. meshes.Keys.Select(s => chunks.Slot(s).VertexStart)];
        Assert.True(after.Sum() < before.Sum(), "Sections did not move down.");
        var ranges = meshes.Keys.Select(s => (Start: chunks.Slot(s).VertexStart, Length: chunks.Slot(s).VertexCount)).OrderBy(r => r.Start).ToList();
        for (int i = 1; i < ranges.Count; i++)
        {
            Assert.True(ranges[i - 1].Start + ranges[i - 1].Length <= ranges[i].Start, "Two sections overlap.");
        }

        foreach ((ChunkSectionId id, ChunkVertex[] vertices) in meshes)
        {
            var read = new ChunkVertex[vertices.Length];
            _scene.Harness.Device.ReadBuffer(chunks.VertexBuffer!, (long)chunks.Slot(id).VertexStart * ChunkVertex.Size, MemoryMarshal.AsBytes(read.AsSpan()));
            Assert.Equal(vertices, read);
        }
    }

    [Fact]
    public void TransparentFacesAreSortedFromBackToFrontAndAgainWhenTheCameraMovesEnough()
    {
        // Three quads along Z in one section, the camera in front of them.
        var vertices = new List<ChunkVertex>();
        var transparent = new List<uint>();
        foreach (float z in new[] { 10f, 20f, 5f })
        {
            uint first = (uint)vertices.Count;
            vertices.AddRange([Vertex(0, 0, z), Vertex(4, 0, z), Vertex(4, 4, z), Vertex(0, 4, z)]);
            transparent.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        ChunkSectionId section = default;
        _scene.Look(WorldPosition.FromBlocks(2, 2, 40));
        _scene.Extract(e => section = e.Chunks.AddSection(e.Chunks.CreateGroup(WorldTransform.Identity), default, new ChunkSectionGeometry([.. vertices], [], [], [.. transparent])));
        Assert.Equal(1, _scene.Chunks.Statistics.TransparentSorts);
        Assert.Equal([5f, 5f, 10f, 10f, 20f, 20f], SortedDepths(section, vertices));

        // A small move does not sort again; a move to the other side does.
        _scene.Look(WorldPosition.FromBlocks(2.2, 2, 40));
        Assert.Equal(0, _scene.Chunks.Statistics.TransparentSorts);
        _scene.Look(WorldPosition.FromBlocks(2, 2, -30));
        Assert.Equal(1, _scene.Chunks.Statistics.TransparentSorts);
        Assert.Equal([20f, 20f, 10f, 10f, 5f, 5f], SortedDepths(section, vertices));

        // A transparent draw follows the opaque one.
        _scene.Draw.Transparent = true;
        _scene.Harness.Fake.Executed.Clear();
        _scene.Frame();
        Assert.Equal(1, _scene.Harness.Fake.Executed.Count(k => k == CommandKind.DrawIndexedIndirect));
    }

    [Fact]
    public void TransparentSectionsAreListedFromTheFarthestToTheNearest()
    {
        ChunkVertex[] quad = [Vertex(0, 0, 16), Vertex(4, 0, 16), Vertex(4, 4, 16), Vertex(0, 4, 16)];
        uint[] faces = [0, 1, 2, 0, 2, 3];
        var sections = new ChunkSectionId[4];
        _scene.Look(WorldPosition.FromBlocks(0, 0, 200));
        _scene.Extract(e =>
        {
            ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
            for (int i = 0; i < 4; i++)
            {
                sections[i] = e.Chunks.AddSection(group, new SectionOrigin(0, 0, new[] { 2, 0, 3, 1 }[i]), new ChunkSectionGeometry(quad, [], [], faces));
            }
        });

        ChunkGeometry.Candidate[] candidates = [.. _scene.Chunks.Candidates];
        int[] byRank = [.. candidates.OrderBy(c => c.Rank).Select(c => (int)c.Slot)];

        // Chunk z = 0 is the farthest from a camera at z = 200 blocks.
        Assert.Equal([sections[1].Index, sections[3].Index, sections[0].Index, sections[2].Index], byRank);
    }

    [Fact]
    public void TheVisibilityGraphKeepsSectionsBehindAWallFromTheGpu()
    {
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, new Vector3(32));
        _scene.Look(WorldPosition.FromBlocks(16, 16, 16));
        _scene.Extract(e =>
        {
            ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
            var solid = new ChunkSectionGeometry(vertices, indices) { Visibility = SectionConnectivity.None };

            // The camera is in a closed room of solid sections; ten sections lie beyond its walls.
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if ((x, y, z) != (0, 0, 0))
                        {
                            e.Chunks.AddSection(group, new SectionOrigin(x, y, z), solid);
                        }
                    }
                }
            }

            for (int i = 0; i < 10; i++)
            {
                e.Chunks.AddSection(group, new SectionOrigin(i - 5, 0, -3), new ChunkSectionGeometry(vertices, indices));
            }
        });
        _scene.Frame();

        // The six walls next to the camera are reached; the edges, corners, and what is beyond are not.
        Assert.Equal(6, _scene.Chunks.Statistics.Candidates);
        Assert.Equal(26 + 10 - 6, _scene.Chunks.Statistics.GraphCulled);

        _scene.Chunks.VisibilityGraph = false;
        _scene.Frame();
        Assert.Equal(36, _scene.Chunks.Statistics.Candidates);
    }

    [Fact]
    public void AHiddenOrDestroyedGroupSendsNothingToTheGpu()
    {
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, Vector3.One);
        ChunkGroupId group = default;
        _scene.Extract(e =>
        {
            group = e.Chunks.CreateGroup(WorldTransform.Identity);
            e.Chunks.AddSection(group, default, new ChunkSectionGeometry(vertices, indices));
            e.Chunks.AddSection(group, new SectionOrigin(1, 0, 0), new ChunkSectionGeometry(vertices, indices));
        });
        Assert.Equal(2, _scene.Chunks.Statistics.Candidates);

        _scene.Extract(e => e.Chunks.SetGroupVisible(group, false));
        Assert.Equal(0, _scene.Chunks.Statistics.Candidates);

        _scene.Extract(e => e.Chunks.DestroyGroup(group));
        Assert.Equal((0, 0), (_scene.Chunks.Statistics.Groups, _scene.Chunks.Statistics.Sections));
        Assert.False(_scene.Chunks.Contains(group));
    }

    [Fact]
    public void AMovedGroupIsInterpolatedAndATeleportedOneIsNot()
    {
        ChunkGroupId group = default;
        _scene.Look(WorldPosition.FromBlocks(0, 0, 0));
        _scene.Extract(e => group = e.Chunks.CreateGroup(WorldTransform.At(WorldPosition.FromBlocks(0, 0, 0))));

        // Moved during a tick, the group is drawn between its two positions, by the fraction of tick of the frame.
        double alpha = -1;
        while (alpha < 0)
        {
            _scene.Extract(e =>
            {
                if (e.IsNewTick)
                {
                    e.Chunks.MoveGroup(group, WorldTransform.At(WorldPosition.FromBlocks(64, 0, 0)));
                    alpha = e.Alpha;
                }
            });
        }

        Assert.True(_scene.Harness.Render.World.Interpolates);
        Assert.Equal(WorldPosition.FromBlocks(64, 0, 0), _scene.Chunks.GetGroupTransform(group).Position);
        Assert.Equal(-64 * alpha, CameraX(group), 3);

        // Teleported, it is drawn where it is, and the depth pyramid of the previous frame no longer applies to it.
        _scene.Extract(e => e.Chunks.TeleportGroup(group, WorldTransform.At(WorldPosition.FromBlocks(128, 0, 0))));
        Assert.Equal(-128, CameraX(group), 3);
        Assert.False(_scene.Chunks.Group(group).PyramidValid);
    }

    // The camera seen from a group, along X, in blocks.
    private double CameraX(ChunkGroupId group)
    {
        ChunkGeometry.GroupSlot slot = _scene.Chunks.Group(group);
        return ((double)slot.ChunkX * WorldPosition.ChunkSize) + slot.Local.X;
    }

    [Fact]
    public void ADeviceWithoutTheFeaturesKeepsTheSectionsAndDrawsNothing()
    {
        using var scene = new ChunkScene(features: GraphicsFeatures.Compute | GraphicsFeatures.StorageBuffers | GraphicsFeatures.DrawIndirect);
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, Vector3.One);
        scene.Extract(e => e.Chunks.AddSection(e.Chunks.CreateGroup(WorldTransform.Identity), default, new ChunkSectionGeometry(vertices, indices)));
        Assert.False(scene.Chunks.IsSupported);
        Assert.Equal(1, scene.Chunks.Statistics.Sections);
        Assert.Throws<NotSupportedException>(() => scene.Chunks.DrawLayout);
    }

    [Fact]
    public void FramesAllocateNothingOnceStable()
    {
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, new Vector3(16));
        ChunkVertex[] quad = [Vertex(0, 0, 16), Vertex(4, 0, 16), Vertex(4, 4, 16), Vertex(0, 4, 16)];
        ChunkGroupId group = default;
        _scene.Look(WorldPosition.FromBlocks(16, 16, 16));
        _scene.Extract(e =>
        {
            group = e.Chunks.CreateGroup(WorldTransform.Identity);
            for (int x = 0; x < 20; x++)
            {
                for (int z = 0; z < 20; z++)
                {
                    e.Chunks.AddSection(group, new SectionOrigin(x, 0, z), new ChunkSectionGeometry(vertices, indices));
                    e.Chunks.AddSection(group, new SectionOrigin(x, 1, z), new ChunkSectionGeometry(quad, [], [], [0, 1, 2, 0, 2, 3]));
                }
            }
        });

        var mover = new MovingCamera(group);
        _scene.Harness.Render.AddExtractor(_scene.Harness.Context, mover);
        for (int i = 0; i < 30; i++)
        {
            _scene.Frame();
        }

        _scene.Harness.Fake.Recording = false;
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                _scene.Frame();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(800, _scene.Chunks.Statistics.Candidates);
    }

    private static ChunkVertex Vertex(float x, float y, float z) => new(new Vector3(x, y, z), default, default, ChunkNormal.PositiveZ, ChunkLight.Full);

    private float[] SortedDepths(ChunkSectionId section, List<ChunkVertex> vertices)
    {
        ChunkGeometry.SectionSlot slot = _scene.Chunks.Slot(section);
        uint[] indices = new uint[slot.TransparentCount];
        _scene.Harness.Device.ReadBuffer(_scene.Chunks.IndexBuffer!, (long)slot.TransparentStart * sizeof(uint), MemoryMarshal.AsBytes(indices.AsSpan()));
        return [.. indices.Chunk(3).Select(t => vertices[(int)t[0]].Position.Z)];
    }

    /// <summary>Moves the camera a little at every frame and the group too, so that frames sort and interpolate.</summary>
    private sealed class MovingCamera(ChunkGroupId group) : IRenderExtractor
    {
        private int _frame;

        public void Extract(RenderExtraction extraction)
        {
            _frame++;
            float x = 16 + (_frame % 40);
            extraction.MainView.TeleportCamera(new Camera { Position = WorldPosition.FromBlocks(x, 40, 300) });
            extraction.Chunks.MoveGroup(group, WorldTransform.At(WorldPosition.FromBlocks(0, _frame % 3, 0)));
        }
    }
}
