// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Core.Mathematics;
using Nobara.Graphics;
using Nobara.Renderer.Tests.Support;
using Xunit.Abstractions;

namespace Nobara.Renderer.Tests;

[Trait("Category", "GPU")]
[Collection(nameof(GpuCollection))]
public sealed class OpenGLChunkTests(ITestOutputHelper output)
{
    private const int Size = 128;

    [Fact]
    public void TheShaderLibraryDecodesVerticesAsTheCpuDoes()
    {
        const string Source = """
            #version 460
            #include <nobara/renderer/chunk.glsl>

            struct Decoded
            {
                vec4 position;
                vec4 normal;
                vec4 uv;
                vec4 light;
                uvec4 texture;
            };

            layout(std430, binding = 0) writeonly buffer Out { Decoded decoded[]; };

            void main()
            {
                NobaraChunkVertex v = nobara_chunkVertex();
                decoded[gl_VertexID] = Decoded(vec4(v.position, 0.0), vec4(v.normal, 0.0), vec4(v.uv, 0.0, 0.0), vec4(v.skyLight, v.blockLight), uvec4(v.textureArray, v.textureLayer, v.face, v.occlusion));
                gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
                gl_PointSize = 1.0;
            }
            """;
        const string Fragment = "#version 460\nlayout(location = 0) out vec4 color;\nvoid main() { color = vec4(1.0); }\n";

        using var scene = new ChunkScene(gpu: true);
        GraphicsDevice device = scene.Harness.Device;
        var random = new Random(3);
        var vertices = new ChunkVertex[64];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new ChunkVertex(
                new Vector3(random.Next(-16, 48), random.Next(0, 32), random.Next(0, 32)) + new Vector3(random.Next(1024), random.Next(1024), random.Next(1024)) / 1024,
                new Vector2(random.Next(0, 64 * 1024), random.Next(0, 64 * 1024)) / 1024,
                new TextureLocation(random.Next(32), random.Next(2048)),
                (ChunkNormal)random.Next(7),
                new ChunkLight(random.Next(64) / 4f, random.Next(64) / 4f, random.Next(64) / 4f, random.Next(64) / 4f),
                random.Next(4));
        }

        using Shader vertex = device.CreateShader(new ShaderDescription(ShaderStages.Vertex, ChunkDrawPass.Compile(Source, "vertex")));
        using Shader fragment = device.CreateShader(new ShaderDescription(ShaderStages.Fragment, ChunkDrawPass.Compile(Fragment, "fragment")));
        using ResourceLayout layout = device.CreateResourceLayout(new ResourceLayoutDescription([new ResourceLayoutEntry(0, ResourceKind.ReadWriteStorageBuffer, ShaderStages.Vertex)]));
        using GpuBuffer input = device.CreateBuffer(new BufferDescription(vertices.Length * ChunkVertex.Size, BufferUsage.Vertex));
        using GpuBuffer result = device.CreateBuffer(new BufferDescription(vertices.Length * 80, BufferUsage.Storage));
        using ResourceSet set = device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(result)]));
        using Texture target = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, 1, 1, TextureUsage.RenderTarget));
        using Framebuffer framebuffer = device.CreateFramebuffer(new FramebufferDescription([new FramebufferAttachment(target)]));
        using GraphicsPipeline pipeline = device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            VertexShader = vertex,
            FragmentShader = fragment,
            VertexBuffers = [ChunkVertex.Layout()],
            ResourceLayouts = [layout],
            Topology = PrimitiveTopology.PointList,
            Outputs = new OutputDescription([PixelFormat.Rgba8Unorm]),
        });
        device.UpdateBuffer(input, 0, MemoryMarshal.AsBytes(vertices.AsSpan()));
        CommandBuffer commands = device.CreateCommandBuffer();
        Assert.True(device.BeginFrame());
        commands.Begin();
        commands.BeginRenderPass(framebuffer, new RenderPassDescription());
        commands.SetPipeline(pipeline);
        commands.SetResourceSet(0, set);
        commands.SetVertexBuffer(0, input);
        commands.Draw(vertices.Length);
        commands.EndRenderPass();
        commands.End();
        device.Submit(commands);
        device.EndFrame();
        var read = new float[vertices.Length * 20];
        device.ReadBuffer(result, 0, MemoryMarshal.AsBytes(read.AsSpan()));

        for (int i = 0; i < vertices.Length; i++)
        {
            ChunkVertex v = vertices[i];
            int o = i * 20;
            Assert.Equal(v.Position, new Vector3(read[o], read[o + 1], read[o + 2]));
            Vector3 normal = v.Normal == ChunkNormal.None ? Vector3.UnitY : NormalOf(v.Normal);
            Assert.Equal(normal, new Vector3(read[o + 4], read[o + 5], read[o + 6]));
            Assert.Equal(v.TextureCoordinates, new Vector2(read[o + 8], read[o + 9]));
            Assert.Equal(v.Light, new ChunkLight(read[o + 12], read[o + 13], read[o + 14], read[o + 15]));
            uint[] words = [.. read.AsSpan(o + 16, 4).ToArray().Select(BitConverter.SingleToUInt32Bits)];
            Assert.Equal(((uint)v.Texture.Array, (uint)v.Texture.Layer, (uint)v.Normal, (uint)v.AmbientOcclusion), (words[0], words[1], words[2], words[3]));
        }
    }

    [Fact]
    public void TwoThousandSectionsAreDrawnByAFewIndirectDrawsAndAWallHidesThem()
    {
        using var scene = new ChunkScene(gpu: true, size: Size);
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(new Vector3(8), new Vector3(24), layer: 1);
        ChunkGroupId wall = default;
        scene.Look(WorldPosition.FromBlocks(0, 0, 0));
        scene.Extract(e =>
        {
            ChunkGroupId field = e.Chunks.CreateGroup(WorldTransform.Identity);
            foreach (SectionOrigin origin in Field())
            {
                e.Chunks.AddSection(field, origin, new ChunkSectionGeometry(vertices, indices));
            }
        });

        for (int i = 0; i < 4; i++)
        {
            scene.Frame();
        }

        ChunkGeometryStatistics open = scene.Chunks.Statistics;
        Assert.Equal(2000, open.Candidates);
        Assert.Equal(2000, open.Drawn + open.FrustumCulled + open.OcclusionCulled);
        Assert.True(open.Drawn > 200, $"Only {open.Drawn} sections are drawn.");
        Assert.True(open.FrustumCulled > 0, "No section is outside the view.");
        Assert.True(Covered(scene) > 0.05, "The sections cover nothing.");

        // A wall of one section, scaled up, stands between the camera and the field.
        scene.Extract(e =>
        {
            wall = e.Chunks.CreateGroup(new WorldTransform(WorldPosition.FromBlocks(-1024, -1024, -20), Quaternion.Identity, new Vector3(64)));
            ChunkVertex[] quad =
            [
                new(new Vector3(0, 0, 0), default, new TextureLocation(0, 2), ChunkNormal.PositiveZ, ChunkLight.Full),
                new(new Vector3(32, 0, 0), default, new TextureLocation(0, 2), ChunkNormal.PositiveZ, ChunkLight.Full),
                new(new Vector3(32, 32, 0), default, new TextureLocation(0, 2), ChunkNormal.PositiveZ, ChunkLight.Full),
                new(new Vector3(0, 32, 0), default, new TextureLocation(0, 2), ChunkNormal.PositiveZ, ChunkLight.Full),
            ];
            e.Chunks.AddSection(wall, default, new ChunkSectionGeometry(quad, [0, 1, 2, 0, 2, 3]));
        });

        for (int i = 0; i < 4; i++)
        {
            scene.Frame();
        }

        ChunkGeometryStatistics walled = scene.Chunks.Statistics;
        output.WriteLine($"Without the wall: {open.Drawn} drawn, {open.FrustumCulled} outside the view, {open.OcclusionCulled} hidden.");
        output.WriteLine($"With the wall: {walled.Drawn} drawn, {walled.FrustumCulled} outside the view, {walled.OcclusionCulled} hidden.");
        Assert.True(walled.Drawn < open.Drawn / 10, $"With the wall, {walled.Drawn} sections are drawn; {open.Drawn} without it.");
        Assert.True(walled.OcclusionCulled > open.Drawn / 2);

        // Without occlusion culling, the wall hides nothing from the GPU.
        scene.Chunks.OcclusionCulling = false;
        for (int i = 0; i < 4; i++)
        {
            scene.Frame();
        }

        Assert.Equal(0, scene.Chunks.Statistics.OcclusionCulled);
    }

    [Theory]
    [InlineData(1_000_000_000)]
    [InlineData(-1_000_000_000)]
    public void ASceneFarFromTheOriginLooksTheSameAsAtTheOrigin(double offset)
    {
        byte[] near = Render(0);
        byte[] far = Render(offset);
        int different = 0;
        for (int i = 0; i < near.Length; i++)
        {
            different += near[i] != far[i] ? 1 : 0;
        }

        Assert.True(near.Distinct().Count() > 2, "The scene draws nothing.");
        Assert.Equal(0, different);
    }

    [Fact]
    public void TheCostOfTheCullingIsMeasured()
    {
        using var scene = new ChunkScene(gpu: true, size: 1280 / 2);
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(new Vector3(8), new Vector3(24), layer: 1);
        scene.Look(WorldPosition.FromBlocks(0, 0, 0));
        scene.Extract(e =>
        {
            ChunkGroupId field = e.Chunks.CreateGroup(WorldTransform.Identity);
            foreach (SectionOrigin origin in Field())
            {
                e.Chunks.AddSection(field, origin, new ChunkSectionGeometry(vertices, indices));
            }
        });

        double full = Measure(scene, cull: true, draw: true, out double cpu);
        double culled = Measure(scene, cull: true, draw: false, out _);
        double empty = Measure(scene, cull: false, draw: false, out double emptyCpu);
        output.WriteLine($"2000 sections on {scene.Harness.Device.Info.Renderer}, {scene.Chunks.Statistics.Drawn} drawn:");
        output.WriteLine($"  frame with culling and drawing: {full:F3} ms; CPU time to record it: {cpu:F3} ms");
        output.WriteLine($"  frame with culling only: {culled:F3} ms; frame with neither: {empty:F3} ms (CPU {emptyCpu:F3} ms)");
        output.WriteLine($"  culling: about {Math.Max(0, culled - empty) * 1000:F0} µs per frame");
    }

    [Fact]
    public void TheVisibilityGraphAndTheDepthPyramidAreComparedUnderground()
    {
        using var scene = new ChunkScene(gpu: true, size: 640);
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(Vector3.Zero, new Vector3(32), layer: 1);

        // Dense ground of 20 x 8 x 20 solid sections, with a straight tunnel along -Z and a side cave; the camera is in the tunnel.
        scene.Look(WorldPosition.FromBlocks(16, 16, -48));
        scene.Extract(e =>
        {
            ChunkGroupId ground = e.Chunks.CreateGroup(WorldTransform.Identity);
            var solid = new ChunkSectionGeometry(vertices, indices) { Visibility = SectionConnectivity.None };
            for (int x = -10; x < 10; x++)
            {
                for (int y = -6; y < 2; y++)
                {
                    for (int z = -20; z < 0; z++)
                    {
                        bool tunnel = x == 0 && y == 0;
                        bool cave = z == -10 && y == 0 && x is > 0 and < 5;
                        if (!tunnel && !cave)
                        {
                            e.Chunks.AddSection(ground, new SectionOrigin(x, y, z), solid);
                        }
                    }
                }
            }
        });

        int drawnWithoutCulling = 0;
        output.WriteLine($"{scene.Chunks.SectionCount} sections, camera in a tunnel, on {scene.Harness.Device.Info.Renderer}:");
        foreach ((bool graph, bool occlusion) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            scene.Chunks.VisibilityGraph = graph;
            scene.Chunks.OcclusionCulling = occlusion;
            double frame = Measure(scene, cull: true, draw: true, out double cpu);
            ChunkGeometryStatistics s = scene.Chunks.Statistics;
            output.WriteLine($"  graph {(graph ? "on " : "off")}, depth pyramid {(occlusion ? "on " : "off")}: {s.Candidates} candidates, {s.Drawn} drawn, {s.FrustumCulled} outside the view, {s.OcclusionCulled} hidden; frame {frame:F3} ms, CPU {cpu:F3} ms");
            if (graph)
            {
                Assert.True(s.Candidates < scene.Chunks.SectionCount / 10, $"The graph keeps {s.Candidates} sections.");
            }

            if (occlusion)
            {
                Assert.True(s.Drawn < drawnWithoutCulling / 4, $"{s.Drawn} sections are drawn underground; {drawnWithoutCulling} without culling.");
            }
            else if (!graph)
            {
                drawnWithoutCulling = s.Drawn;
            }
        }
    }

    // A field of 40 x 5 x 10 sections in front of the camera, which looks along -Z from the origin.
    private static IEnumerable<SectionOrigin> Field()
    {
        for (int x = -20; x < 20; x++)
        {
            for (int y = -3; y < 2; y++)
            {
                for (int z = -12; z < -2; z++)
                {
                    yield return new SectionOrigin(x, y, z);
                }
            }
        }
    }

    // Draws frames until the GPU is idle, and gives the mean time of a frame, culled or not (the draws are then skipped).
    private static double Measure(ChunkScene scene, bool cull, bool draw, out double cpu)
    {
        scene.Draw.Skip = !draw;
        scene.CullingPass = cull;
        scene.Harness.Render.RebuildGraph();
        for (int i = 0; i < 20; i++)
        {
            scene.Frame();
        }


        scene.Harness.Device.WaitIdle();
        const int Frames = 200;
        var clock = Stopwatch.StartNew();
        double recording = 0;
        for (int i = 0; i < Frames; i++)
        {
            long start = Stopwatch.GetTimestamp();
            scene.Frame();
            recording += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        scene.Harness.Device.WaitIdle();
        cpu = recording / Frames;
        return clock.Elapsed.TotalMilliseconds / Frames;
    }

    private static Vector3 NormalOf(ChunkNormal normal) => normal switch
    {
        ChunkNormal.NegativeX => -Vector3.UnitX,
        ChunkNormal.PositiveX => Vector3.UnitX,
        ChunkNormal.NegativeY => -Vector3.UnitY,
        ChunkNormal.PositiveY => Vector3.UnitY,
        ChunkNormal.NegativeZ => -Vector3.UnitZ,
        _ => Vector3.UnitZ,
    };

    private static double Covered(ChunkScene scene)
    {
        byte[] pixels = Pixels(scene);
        int covered = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            covered += pixels[i + 1] > 100 ? 1 : 0;
        }

        return (double)covered / (pixels.Length / 4);
    }

    private static byte[] Pixels(ChunkScene scene)
    {
        Texture color = scene.Color!;
        byte[] pixels = new byte[color.Width * color.Height * 4];
        scene.Harness.Device.ReadTexture(color, TextureRegion.Whole(color), pixels);
        return pixels;
    }

    // A few boxes in a group turned about Y, seen from 40 blocks away, all moved by an offset.
    private static byte[] Render(double offset)
    {
        using var scene = new ChunkScene(gpu: true, size: Size);
        (ChunkVertex[] vertices, uint[] indices) = ChunkScene.Box(new Vector3(3.25f), new Vector3(29.5f), layer: 0);
        (ChunkVertex[] small, uint[] smallIndices) = ChunkScene.Box(new Vector3(0.125f), new Vector3(1.875f), layer: 3);
        scene.Look(WorldPosition.FromBlocks(offset + 16.3, offset + 20.7, offset + 70.1), Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0));
        scene.Extract(e =>
        {
            var transform = new WorldTransform(WorldPosition.FromBlocks(offset - 3.5, offset, offset), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.25f), Vector3.One);
            ChunkGroupId group = e.Chunks.CreateGroup(transform);
            e.Chunks.AddSection(group, new SectionOrigin(0, 0, 0), new ChunkSectionGeometry(vertices, indices));
            e.Chunks.AddSection(group, new SectionOrigin(1, 0, 0), new ChunkSectionGeometry(small, smallIndices));
            e.Chunks.AddSection(group, new SectionOrigin(0, 1, -1), new ChunkSectionGeometry(vertices, indices));
        });
        scene.Frame();
        return Pixels(scene);
    }
}
