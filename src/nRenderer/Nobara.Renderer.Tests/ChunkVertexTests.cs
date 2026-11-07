// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.CompilerServices;
using Nobara.Core.Mathematics;

namespace Nobara.Renderer.Tests;

public sealed class ChunkVertexTests
{
    [Fact]
    public void AVertexIsSixteenBytes() => Assert.Equal(ChunkVertex.Size, Unsafe.SizeOf<ChunkVertex>());

    [Fact]
    public void EveryFieldReadsBackAsItWasEncoded()
    {
        var vertex = new ChunkVertex(new Vector3(-3.5f, 12.25f, 47.5f), new Vector2(31.75f, 0.0625f), new TextureLocation(17, 1234), ChunkNormal.NegativeZ, new ChunkLight(15, 2.5f, 7.5f, 15.5f), 2, 101);

        Assert.Equal(new Vector3(-3.5f, 12.25f, 47.5f), vertex.Position);
        Assert.Equal(new Vector2(31.75f, 0.0625f), vertex.TextureCoordinates);
        Assert.Equal(new TextureLocation(17, 1234), vertex.Texture);
        Assert.Equal(ChunkNormal.NegativeZ, vertex.Normal);
        Assert.Equal(new ChunkLight(15, 2.5f, 7.5f, 15.5f), vertex.Light);
        Assert.Equal(2, vertex.AmbientOcclusion);
        Assert.Equal(101, vertex.Tint);
        Assert.Equal(vertex, ChunkVertex.FromWords(vertex.Word0, vertex.Word1, vertex.Word2, vertex.Word3));
    }

    [Fact]
    public void PositionsRoundToTheNearestStepAndValuesOutsideTheirRangeAreClamped()
    {
        var vertex = new ChunkVertex(new Vector3(1.0004f, -20, 100), new Vector2(-1, 70), default, ChunkNormal.None, new ChunkLight(-2, 20, 3.2f, 0));

        Assert.Equal(1, vertex.Position.X, 1e-6);
        Assert.Equal(ChunkVertex.MinPosition, vertex.Position.Y);
        Assert.Equal(ChunkVertex.MaxPosition - ChunkVertex.PositionStep, vertex.Position.Z, 1e-6);
        Assert.Equal(0, vertex.TextureCoordinates.X);
        Assert.Equal(ChunkVertex.MaxTextureCoordinate - ChunkVertex.PositionStep, vertex.TextureCoordinates.Y, 1e-6);
        Assert.Equal(new ChunkLight(0, ChunkVertex.MaxLight, 3, 0), vertex.Light);
        Assert.Equal(0, vertex.Tint);
    }

    [Fact]
    public void EveryPositionOfAChunkIsExactOnTheGrid()
    {
        for (int i = 0; i <= WorldPosition.ChunkSize * 16; i++)
        {
            float p = i / 16f;
            Assert.Equal(p, new ChunkVertex(new Vector3(p), default, default, ChunkNormal.PositiveY, ChunkLight.Full).Position.X);
        }
    }

    [Fact]
    public void ATextureAnOcclusionOrATintOutsideTheFormatIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkVertex(default, default, new TextureLocation(32, 0), ChunkNormal.PositiveY, ChunkLight.Full));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkVertex(default, default, new TextureLocation(0, 2048), ChunkNormal.PositiveY, ChunkLight.Full));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkVertex(default, default, default, ChunkNormal.PositiveY, ChunkLight.Full, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkVertex(default, default, default, ChunkNormal.PositiveY, ChunkLight.Full, 0, 128));
    }

    [Fact]
    public void TheLayoutReadsTheFourWordsAtOneLocation()
    {
        Graphics.VertexBufferLayout layout = ChunkVertex.Layout(3);
        Assert.Equal(16, layout.Stride);
        Assert.Equal(new Graphics.VertexAttribute(3, Graphics.VertexFormat.UInt4, 0), Assert.Single(layout.Attributes));
    }
}

public sealed class SectionConnectivityTests
{
    [Fact]
    public void FacesAreConnectedInPairs()
    {
        SectionConnectivity connectivity = SectionConnectivity.None.With(SectionFace.NegativeX, SectionFace.PositiveY);
        Assert.True(connectivity.IsConnected(SectionFace.PositiveY, SectionFace.NegativeX));
        Assert.True(connectivity.IsConnected(SectionFace.PositiveZ, SectionFace.PositiveZ));
        Assert.False(connectivity.IsConnected(SectionFace.NegativeX, SectionFace.PositiveX));
    }

    [Fact]
    public void FifteenPairsCoverEveryConnection()
    {
        SectionConnectivity connectivity = SectionConnectivity.None;
        for (int a = 0; a < 6; a++)
        {
            for (int b = a + 1; b < 6; b++)
            {
                connectivity = connectivity.With((SectionFace)a, (SectionFace)b);
            }
        }

        Assert.Equal(SectionConnectivity.All, connectivity);
    }
}

public sealed class VisibilityGraphTests
{
    private readonly VisibilityGraph _graph = new();
    private readonly SectionConnectivity[] _connectivity = new SectionConnectivity[64];
    private int _slots;

    [Fact]
    public void ASolidWallHidesWhatIsBehindIt()
    {
        // A wall of solid sections at z = -1, a section behind it at z = -3, one in front at z = 1.
        for (int x = -2; x <= 2; x++)
        {
            for (int y = -2; y <= 2; y++)
            {
                Add(new SectionOrigin(x, y, -1), SectionConnectivity.None);
            }
        }

        int hidden = Add(new SectionOrigin(0, 0, -3), SectionConnectivity.All);
        int front = Add(new SectionOrigin(0, 0, 1), SectionConnectivity.All);

        Assert.True(_graph.Search(new SectionOrigin(0, 0, 0), _connectivity));
        Assert.Contains(front, _graph.Reached.ToArray());
        Assert.DoesNotContain(hidden, _graph.Reached.ToArray());

        // The wall itself is reached, its faces toward the camera are seen.
        Assert.Equal(25 + 1, _graph.Reached.Length);
    }

    [Fact]
    public void ATunnelThroughASectionLetsTheViewPass()
    {
        int tunnel = Add(new SectionOrigin(0, 0, -1), SectionConnectivity.None.With(SectionFace.PositiveZ, SectionFace.NegativeZ));
        int behind = Add(new SectionOrigin(0, 0, -2), SectionConnectivity.All);
        int side = Add(new SectionOrigin(1, 0, -2), SectionConnectivity.All);

        // The camera is surrounded by solid sections, except the tunnel.
        Add(new SectionOrigin(1, 0, 0), SectionConnectivity.None);
        Add(new SectionOrigin(-1, 0, 0), SectionConnectivity.None);
        Add(new SectionOrigin(0, 1, 0), SectionConnectivity.None);
        Add(new SectionOrigin(0, -1, 0), SectionConnectivity.None);
        Add(new SectionOrigin(0, 0, 1), SectionConnectivity.None);
        Add(new SectionOrigin(1, 0, -1), SectionConnectivity.None);

        Assert.True(_graph.Search(new SectionOrigin(0, 0, 0), _connectivity));
        int[] reached = _graph.Reached.ToArray();
        Assert.Contains(tunnel, reached);
        Assert.Contains(behind, reached);
        Assert.Contains(side, reached);
    }

    [Fact]
    public void ACameraOutsideTheSectionsSeesThemAll()
    {
        Add(new SectionOrigin(0, 0, 0), SectionConnectivity.None);
        Assert.False(_graph.Search(new SectionOrigin(5, 0, 0), _connectivity));
        Assert.True(_graph.Search(new SectionOrigin(1, 0, 0), _connectivity));
    }

    [Fact]
    public void TheResultIsKeptUntilTheCameraOrTheSectionsChange()
    {
        int a = Add(new SectionOrigin(0, 0, -1), SectionConnectivity.None);
        Assert.True(_graph.Search(new SectionOrigin(0, 0, 0), _connectivity));
        Assert.Equal(new[] { a }, _graph.Reached.ToArray());

        int b = Add(new SectionOrigin(0, 0, -2), SectionConnectivity.All);
        Assert.True(_graph.Search(new SectionOrigin(0, 0, 0), _connectivity));
        Assert.Equal(new[] { a }, _graph.Reached.ToArray());

        _connectivity[a] = SectionConnectivity.All;
        _graph.Invalidate();
        Assert.True(_graph.Search(new SectionOrigin(0, 0, 0), _connectivity));
        Assert.Equal(new[] { a, b }, _graph.Reached.ToArray().Order().ToArray());
    }

    private int Add(SectionOrigin origin, SectionConnectivity connectivity)
    {
        int slot = _slots++;
        _connectivity[slot] = connectivity;
        _graph.Add(origin, slot);
        return slot;
    }
}

public sealed class ChunkCameraTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000_000)]
    [InlineData(-1_000_000_000)]
    public void TheCameraSeenFromAGroupIsExactFarFromTheOrigin(double offset)
    {
        WorldTransform group = WorldTransform.At(WorldPosition.FromBlocks(offset, offset, offset));
        WorldPosition camera = WorldPosition.FromBlocks(offset + 40.125, offset + 0.5, offset - 0.25);

        ChunkGeometry.CameraIn(group, camera, out int x, out int y, out int z, out Vector3 local);

        Assert.Equal((1, 0, -1), (x, y, z));
        Assert.Equal(new Vector3(8.125f, 0.5f, 31.75f), local);
    }

    [Fact]
    public void ARotatedAndScaledGroupSeesTheCameraInItsOwnFrame()
    {
        var group = new WorldTransform(WorldPosition.FromBlocks(100, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), new Vector3(2));
        WorldPosition camera = WorldPosition.FromBlocks(90, 5, -70);

        // A group turned by 90 degrees about Y has its +X along -Z of the world and its +Z along +X:
        // the camera is at (70, 5, -10) blocks of the world from its origin, (35, 2.5, -5) of its own blocks.
        ChunkGeometry.CameraIn(group, camera, out int x, out int y, out int z, out Vector3 local);

        Assert.Equal((1, 0, -1), (x, y, z));
        Assert.Equal(3, local.X, 1e-4);
        Assert.Equal(2.5f, local.Y, 1e-4);
        Assert.Equal(27, local.Z, 1e-4);
    }
}
