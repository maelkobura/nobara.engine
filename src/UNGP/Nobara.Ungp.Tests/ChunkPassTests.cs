// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Graphics;
using Nobara.Renderer;
using Nobara.Ungp.Tests.Support;

namespace Nobara.Ungp.Tests;

/// <summary>The culling pass, the chunk geometry in the passes of UNGP, and the transparent pass, on the fake device.</summary>
public sealed class ChunkPassTests : IDisposable
{
    private readonly UngpHarness _harness = new();
    private Engine? _engine;

    public void Dispose()
    {
        _engine?.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void TheModuleProvidesTheChunkShader()
    {
        Start();

        RenderShader chunk = _harness.Resources.GetShader(UngpModule.ChunkShader);
        Assert.True(chunk.TryGetProgram(UngpModule.CutoutVariant, out _));
        Assert.Contains(chunk.Uniforms, u => u.Name == "View");
        Assert.NotNull(chunk.Programs[0].Vertex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChunksAreDrawnByIndirectDrawsInTheGeometryPassesAndTheTransparentPass(bool prepass)
    {
        Start();
        _harness.Ungp.DepthPrepass = prepass;
        AddSection(new ChunkFaces()
            .Box(ChunkCategory.Opaque, Red, new Vector3(0, 0, 0), new Vector3(4, 4, 4))
            .Box(ChunkCategory.Cutout, Red, new Vector3(6, 0, 0), new Vector3(8, 2, 2))
            .Box(ChunkCategory.Transparent, Red, new Vector3(10, 0, 0), new Vector3(12, 2, 2)));
        _harness.Fake.Executed.Clear();

        _engine!.MainContext.RunFrame();

        // Opaque, then cut-out faces in each geometry pass; the transparent faces once.
        Assert.Equal(prepass ? 4 : 2, _harness.Fake.Executed.Count(k => k == CommandKind.DrawIndexedIndirectCount));
        Assert.Equal(1, _harness.Fake.Executed.Count(k => k == CommandKind.DrawIndexedIndirect));

        // The culling pass dispatches once, then the depth pyramid pass once per level.
        Assert.True(_harness.Fake.Executed.Count(k => k == CommandKind.Dispatch) > 1);
        Assert.Empty(_harness.Errors);
        Assert.DoesNotContain(_harness.Warnings, w => w.Contains("culling pass", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningOcclusionCullingOffRemovesTheDepthPyramidAndKeepsTheCulling()
    {
        Start();
        ChunkGeometry chunks = _harness.Render.Chunks;
        _engine!.MainContext.RunFrame();
        Assert.True(chunks.OcclusionCulling && chunks.VisibilityGraph);

        _harness.Ungp.OcclusionCulling = false;
        _engine.MainContext.RunFrame();

        Assert.Equal(
            [UngpPasses.Culling, UngpPasses.DepthPrepass, UngpPasses.Opaque, UngpPasses.Sky, UngpPasses.Transparent, UngpPasses.Tonemap],
            _harness.Render.LastGraph!.Passes);
        Assert.False(chunks.OcclusionCulling);
        Assert.False(chunks.VisibilityGraph);

        _harness.Ungp.OcclusionCulling = true;
        _engine.MainContext.RunFrame();

        Assert.Contains(UngpPasses.DepthPyramid, _harness.Render.LastGraph!.Passes);
        Assert.True(chunks.OcclusionCulling && chunks.VisibilityGraph);
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void ADeviceWithoutTheFeaturesOfUngpIsRefusedWithTheMissingOnes()
    {
        _harness.Backend.Features = GraphicsFeatures.Compute | GraphicsFeatures.StorageBuffers | GraphicsFeatures.DrawIndirect;

        Exception error = Assert.ThrowsAny<Exception>(() => _engine = _harness.Start());

        string message = Flatten(error);
        Assert.Contains("'ungp'", message, StringComparison.Ordinal);
        Assert.Contains(nameof(GraphicsFeatures.IndirectCount), message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResortKeysSetTheThresholdsOfChunkGeometry()
    {
        _harness.Settings[UngpSettings.TransparentResortAngle] = "10";
        _harness.Settings[UngpSettings.TransparentResortDistance] = "2";
        Start();

        ChunkGeometry chunks = _harness.Render.Chunks;
        Assert.Equal(10f, chunks.TransparentResortAngle);
        Assert.Equal(2f, chunks.TransparentResortDistance);
    }

    [Fact]
    public void TheResortThresholdsDefaultToThoseOfChunkGeometry()
    {
        Start();

        Assert.Equal(5f, _harness.Render.Chunks.TransparentResortAngle);
        Assert.Equal(0.5f, _harness.Render.Chunks.TransparentResortDistance);
    }

    [Fact]
    public void TheChunkPipelinesAreCreatedOnce()
    {
        Start();
        AddSection(new ChunkFaces()
            .Box(ChunkCategory.Opaque, Red, new Vector3(0, 0, 0), new Vector3(4, 4, 4))
            .Box(ChunkCategory.Cutout, Red, new Vector3(6, 0, 0), new Vector3(8, 2, 2))
            .Box(ChunkCategory.Transparent, Red, new Vector3(10, 0, 0), new Vector3(12, 2, 2)));
        int before = _harness.Fake.NativeObjects;

        for (int i = 0; i < 5; i++)
        {
            _engine!.MainContext.RunFrame();
        }

        Assert.Equal(before, _harness.Fake.NativeObjects);
    }

    private TextureLocation Red => _harness.Resources.GetTexture("texture:example.scene.red").Location;

    private static string Flatten(Exception error)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            text.AppendLine(e.Message);
        }

        return text.ToString();
    }

    // The section sits in front of the camera, which stands at the origin and looks along -Z; frames run until it reaches the GPU.
    private void AddSection(ChunkFaces faces)
    {
        ChunkSectionId section = default;
        _harness.Render.AddExtractor(_harness.Context, new TestExtractor
        {
            Next = e =>
            {
                ChunkGroupId group = e.Chunks.CreateGroup(WorldTransform.Identity);
                section = e.Chunks.AddSection(group, new SectionOrigin(-1, -1, -1), faces.Geometry);
            },
        });
        for (int i = 0; i < 3; i++)
        {
            _engine!.MainContext.RunFrame();
        }

        Assert.True(_harness.Render.Chunks.IsUploaded(section));
    }

    private void Start() => _engine = _harness.Start();
}
