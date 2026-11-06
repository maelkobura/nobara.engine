// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Graphics;

namespace Nobara.Renderer.Tests.Support;

/// <summary>
/// An engine whose pipeline culls and draws chunk geometry: on the fake device
/// with the features chunk geometry needs, or on OpenGL for the tests of the
/// category GPU, which draw to a texture.
/// </summary>
internal sealed class ChunkScene : IDisposable
{
    private readonly TestExtractor _extractor = new();

    public ChunkScene(bool gpu = false, GraphicsFeatures features = ChunkGeometry.RequiredFeatures, int size = 64)
    {
        Harness = new RendererHarness(gpu);
        Harness.Backend.Features = features;
        Engine = Harness.WithGame(RendererHarness.Compiled(Harness.CopyResources())).Start();
        Harness.Render.AddExtractor(Harness.Context, _extractor);
        Draw = new ChunkDrawPass(Harness.Device, gpu) { Geometry = Harness.Render.Chunks };
        Pipeline = new DelegatePipeline("chunks", Build);
        if (gpu)
        {
            Color = Harness.Device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, size, size, TextureUsage.RenderTarget | TextureUsage.Sampled));
            Depth = Harness.Device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Depth32Float, size, size, TextureUsage.DepthStencil | TextureUsage.Sampled));
            Harness.Render.MainView.SetTarget(Color, Depth);
        }

        DepthPyramid = gpu;
        Harness.Render.RegisterPipeline(Harness.Context, Pipeline);
    }

    public RendererHarness Harness { get; }

    public Engine Engine { get; }

    public DelegatePipeline Pipeline { get; }

    public ChunkDrawPass Draw { get; }

    public Texture? Color { get; }

    public Texture? Depth { get; }

    public ChunkGeometry Chunks => Harness.Render.Chunks;

    /// <summary>Gets or sets whether the pipeline builds the depth pyramid after drawing.</summary>
    public bool DepthPyramid { get; set; }

    /// <summary>Gets or sets whether the pipeline culls the sections; without it, nothing is drawn.</summary>
    public bool CullingPass { get; set; } = true;

    /// <summary>Runs a frame whose extraction runs a function first.</summary>
    public void Extract(Action<RenderExtraction> extract)
    {
        _extractor.Next = extract;
        Frame();
    }

    public void Frame()
    {
        // The loop measures time with the clock of the harness, which moves only when told.
        Harness.Clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
        Engine.MainContext.RunFrame();
    }

    /// <summary>Places the camera at a position, looking along -Z.</summary>
    public void Look(WorldPosition position, Quaternion? rotation = null) =>
        Extract(e => e.MainView.TeleportCamera(new Camera { Position = position, Rotation = rotation ?? Quaternion.Identity }));

    public void Dispose()
    {
        Draw.Dispose();
        Color?.Dispose();
        Depth?.Dispose();
        Engine.Dispose();
        Harness.Dispose();
    }

    private void Build(RenderGraphBuilder graph)
    {
        if (CullingPass)
        {
            Chunks.AddCullingPass(graph);
        }
        graph.AddRasterPass("chunks", Draw).Color(graph.Target, LoadAction.Clear, TestPipeline.Background).Depth(graph.TargetDepth, LoadAction.Clear).Require(ChunkGeometry.RequiredFeatures);
        if (DepthPyramid)
        {
            Chunks.AddDepthPyramidPass(graph, graph.TargetDepth);
        }
    }

    /// <summary>A box of a section: its 12 triangles, from <paramref name="low"/> to <paramref name="high"/>, in blocks of the chunk, one color per layer of texture.</summary>
    public static (ChunkVertex[] Vertices, uint[] Indices) Box(Vector3 low, Vector3 high, int layer = 0)
    {
        var vertices = new List<ChunkVertex>();
        var indices = new List<uint>();
        void Face(ChunkNormal normal, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            uint first = (uint)vertices.Count;
            foreach (Vector3 p in new[] { a, b, c, d })
            {
                vertices.Add(new ChunkVertex(p, default, new TextureLocation(0, layer), normal, ChunkLight.Full));
            }

            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        (float x0, float y0, float z0) = (low.X, low.Y, low.Z);
        (float x1, float y1, float z1) = (high.X, high.Y, high.Z);
        Face(ChunkNormal.PositiveZ, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
        Face(ChunkNormal.NegativeZ, new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0));
        Face(ChunkNormal.PositiveX, new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1));
        Face(ChunkNormal.NegativeX, new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0));
        Face(ChunkNormal.PositiveY, new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0));
        Face(ChunkNormal.NegativeY, new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1));
        return ([.. vertices], [.. indices]);
    }
}

/// <summary>
/// Draws the opaque faces of chunk geometry, then the transparent ones when
/// asked. On the fake device, any shaders do; on OpenGL, a shader that uses the
/// shader library of nRenderer colors each face by its texture layer.
/// </summary>
internal sealed class ChunkDrawPass : IRenderPass, IDisposable
{
    private const string Vertex = """
        #version 460
        #include <nobara/renderer/chunk.glsl>

        layout(std140, binding = 0) uniform View
        {
            mat4 ViewProjection;
        };

        layout(location = 0) out vec3 vColor;

        void main()
        {
            NobaraChunkVertex v = nobara_chunkVertex();
            const vec3 colors[4] = vec3[4](vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(1.0, 1.0, 0.0));
            vColor = colors[v.textureLayer & 3u] * (0.6 + 0.4 * abs(v.normal.z)) * nobara_chunkLight(v, 1.0);
            gl_Position = ViewProjection * vec4(nobara_chunkPosition(nobara_ChunkSection, v.position), 1.0);
        }
        """;

    private const string Fragment = """
        #version 460
        layout(location = 0) in vec3 vColor;
        layout(location = 0) out vec4 outColor;

        void main()
        {
            outColor = vec4(vColor, 0.5);
        }
        """;

    private readonly GraphicsDevice _device;
    private readonly Shader _vertex;
    private readonly Shader _fragment;
    private readonly ResourceLayout _layout;
    private readonly GpuBuffer _view;
    private readonly ResourceSet _set;
    private GraphicsPipeline? _opaque;
    private GraphicsPipeline? _transparent;

    public ChunkDrawPass(GraphicsDevice device, bool gpu)
    {
        _device = device;
        _vertex = device.CreateShader(new ShaderDescription(ShaderStages.Vertex, gpu ? Compile(Vertex, "vertex") : EmbeddedShaders.Read("debug-draw.vert.spv")));
        _fragment = device.CreateShader(new ShaderDescription(ShaderStages.Fragment, gpu ? Compile(Fragment, "fragment") : EmbeddedShaders.Read("debug-draw.frag.spv")));
        _layout = device.CreateResourceLayout(new ResourceLayoutDescription([new ResourceLayoutEntry(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]));
        _view = device.CreateBuffer(new BufferDescription(64, BufferUsage.Uniform));
        _set = device.CreateResourceSet(new ResourceSetDescription(_layout, [BoundResource.Of(_view)]));
    }

    public bool Transparent { get; set; }

    /// <summary>Gets or sets whether the pass draws nothing, for measurements.</summary>
    public bool Skip { get; set; }

    public static byte[] Compile(string source, string stage)
    {
        using var compiler = new Build.GlslCompiler();
        Build.GlslResult result = compiler.Compile(System.Text.Encoding.UTF8.GetBytes(source), "test." + stage, stage, []);
        Assert.True(result.SpirV is not null, string.Join(Environment.NewLine, result.Messages));
        return result.SpirV;
    }

    public void Prepare(RenderPassContext context)
    {
        Matrix4x4 viewProjection = context.View.ViewProjectionMatrix;
        context.Commands.UpdateBuffer(_view, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(ref viewProjection)));
    }

    public void Execute(RenderPassContext context)
    {
        if (Skip)
        {
            return;
        }

        ChunkGeometry chunks = Geometry;
        CommandBuffer commands = context.Commands;
        _opaque ??= Create(context.Framebuffer!, chunks, transparent: false);
        commands.SetPipeline(_opaque);
        commands.SetResourceSet(0, _set);
        chunks.Draw(context, ChunkCategory.Opaque, 1);
        if (Transparent)
        {
            _transparent ??= Create(context.Framebuffer!, chunks, transparent: true);
            commands.SetPipeline(_transparent);
            commands.SetResourceSet(0, _set);
            chunks.Draw(context, ChunkCategory.Transparent, 1);
        }
    }

    public ChunkGeometry Geometry { get; set; } = null!;

    public void Dispose()
    {
        _opaque?.Dispose();
        _transparent?.Dispose();
        _set.Dispose();
        _view.Dispose();
        _layout.Dispose();
        _vertex.Dispose();
        _fragment.Dispose();
    }

    private GraphicsPipeline Create(Framebuffer framebuffer, ChunkGeometry chunks, bool transparent) => _device.CreateGraphicsPipeline(new GraphicsPipelineDescription
    {
        VertexShader = _vertex,
        FragmentShader = _fragment,
        VertexBuffers = chunks.VertexLayouts,
        ResourceLayouts = [_layout, chunks.DrawLayout],
        Rasterizer = new RasterizerState { CullMode = CullMode.Back },
        Depth = new DepthState { TestEnabled = true, WriteEnabled = !transparent, Compare = Camera.DepthCompare },
        Blend = transparent ? BlendState.AlphaBlend : BlendState.Opaque,
        Outputs = new OutputDescription([.. framebuffer.ColorFormats], framebuffer.DepthFormat),
    });
}
