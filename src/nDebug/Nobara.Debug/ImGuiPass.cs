// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Debug;

/// <summary>
/// Draws the frame of Dear ImGui at the insertion point of the user interface,
/// with the shader of nDebug: it asks <see cref="DebugUi"/> to build the frame
/// before the render pass begins, uploads the draw lists, then draws each
/// command with its clip rectangle.
/// </summary>
internal sealed class ImGuiPass : IRenderInsertion, IRenderPass, IDisposable
{
    /// <summary>The name of the pass.</summary>
    public const string PassName = "nobara.debug.ui";

    /// <summary>The identifier of the shader of the pass.</summary>
    public const string ShaderId = "shader:nobara.debug.imgui";

    /// <summary>The identifier that Dear ImGui gives to the font atlas in its draw commands.</summary>
    public const nint AtlasId = 1;

    private const int VertexSize = 20;
    private const int UniformSize = 80;
    private const int MinimumBufferSize = 64 * 1024;

    private readonly DebugUi _ui;
    private readonly List<DrawCommand> _commands = [];
    private readonly List<(PixelFormat[] Colors, GraphicsPipeline Pipeline)> _pipelines = [];
    private GraphicsDevice? _device;
    private RenderShader? _shader;
    private Sampler? _sampler;
    private ResourceLayout? _layout;
    private PerFrame<GpuBuffer>? _uniforms;
    private ResourceSet?[] _sets = [];
    private GpuBuffer?[] _vertices = [];
    private GpuBuffer?[] _indices = [];
    private Texture? _atlas;
    private int _shaderVersion = -1;
    private bool _draws;
    private Vector2 _displayPos;
    private Vector2 _scale = Vector2.One;

    public ImGuiPass(DebugUi ui)
    {
        _ui = ui;
    }

    /// <summary>Gets the number of draw commands of the last frame, for tests.</summary>
    internal int CommandCount => _draws ? _commands.Count : 0;

    /// <summary>Gets the font atlas on the GPU, for tests.</summary>
    internal Texture? Atlas => _atlas;

    /// <summary>Creates the GPU objects that do not depend on the target.</summary>
    public void Start(GraphicsDevice device, IRenderResources resources)
    {
        _device = device;
        _shader = resources.GetShader(ShaderId);
        _sampler = device.CreateSampler(new SamplerDescription
        {
            MipFilter = FilterMode.Nearest,
            AddressU = AddressMode.ClampToEdge,
            AddressV = AddressMode.ClampToEdge,
            AddressW = AddressMode.ClampToEdge,
            Name = "nobara.debug.atlas",
        });
        _uniforms = new PerFrame<GpuBuffer>(device, slot => device.CreateBuffer(new BufferDescription(UniformSize, BufferUsage.Uniform) { Name = $"nobara.debug.frame.{slot}" }));
        _sets = new ResourceSet?[device.FramesInFlight];
        _vertices = new GpuBuffer?[device.FramesInFlight];
        _indices = new GpuBuffer?[device.FramesInFlight];
    }

    /// <summary>Uploads the font atlas, in RGBA 8 bits, and replaces the previous one.</summary>
    public void UploadAtlas(ReadOnlySpan<byte> pixels, int width, int height)
    {
        GraphicsDevice device = _device!;
        _atlas?.Dispose();
        _atlas = device.CreateTexture(TextureDescription.Texture2D(PixelFormat.Rgba8Unorm, width, height, TextureUsage.Sampled) with { Name = "nobara.debug.atlas" });
        device.UpdateTexture(_atlas, TextureRegion.Whole(_atlas), pixels);
        ForgetSets();
    }

    public void Build(RenderGraphBuilder graph, in RenderInsertionPoint point) =>
        graph.AddRasterPass(PassName, this).Color(point.Color);

    public unsafe void Prepare(RenderPassContext context)
    {
        Framebuffer framebuffer = context.Framebuffer!;
        _draws = _device is not null && _ui.BuildFrame(framebuffer.Width, framebuffer.Height);
        _commands.Clear();
        if (!_draws)
        {
            return;
        }

        ImDrawDataPtr data = ImGui.GetDrawData();
        if (data.TotalVtxCount == 0)
        {
            _draws = false;
            return;
        }

        int slot = context.FrameIndex;
        GpuBuffer vertices = Ensure(_vertices, slot, (long)data.TotalVtxCount * VertexSize, BufferUsage.Vertex, "nobara.debug.vertices");
        GpuBuffer indices = Ensure(_indices, slot, (long)data.TotalIdxCount * sizeof(ushort), BufferUsage.Index, "nobara.debug.indices");
        int vertexOffset = 0;
        int indexOffset = 0;
        for (int i = 0; i < data.CmdListsCount; i++)
        {
            ImDrawListPtr list = data.CmdLists[i];
            _device!.UpdateBuffer(vertices, (long)vertexOffset * VertexSize, new ReadOnlySpan<byte>((void*)list.VtxBuffer.Data, list.VtxBuffer.Size * VertexSize));
            _device.UpdateBuffer(indices, (long)indexOffset * sizeof(ushort), new ReadOnlySpan<byte>((void*)list.IdxBuffer.Data, list.IdxBuffer.Size * sizeof(ushort)));
            for (int c = 0; c < list.CmdBuffer.Size; c++)
            {
                ImDrawCmdPtr command = list.CmdBuffer[c];
                if (command.UserCallback == IntPtr.Zero && command.ElemCount > 0)
                {
                    _commands.Add(new DrawCommand(command.ClipRect, (int)command.ElemCount, indexOffset + (int)command.IdxOffset, vertexOffset + (int)command.VtxOffset));
                }
            }

            vertexOffset += list.VtxBuffer.Size;
            indexOffset += list.IdxBuffer.Size;
        }

        if (_shaderVersion != _shader!.Version)
        {
            CreateLayout();
        }

        _sets[slot] ??= _device!.CreateResourceSet(new ResourceSetDescription(_layout!, [BoundResource.Of(_uniforms![slot]), BoundResource.Of(_atlas!, _sampler!)]) { Name = $"nobara.debug.{slot}" });
        var uniforms = new FrameUniforms(Projection(data.DisplayPos, data.DisplaySize), DecodesSrgb(framebuffer.ColorFormats[0]) ? 1 : 0);
        _device!.UpdateBuffer(_uniforms!.Current, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<FrameUniforms>(ref uniforms)));
        _displayPos = data.DisplayPos;
        _scale = data.FramebufferScale;
    }

    public void Execute(RenderPassContext context)
    {
        if (!_draws || _commands.Count == 0)
        {
            return;
        }

        Framebuffer framebuffer = context.Framebuffer!;
        int slot = context.FrameIndex;
        CommandBuffer commands = context.Commands;
        commands.SetViewport(new Viewport(0, 0, framebuffer.Width, framebuffer.Height));
        commands.SetPipeline(PipelineFor(framebuffer));
        commands.SetResourceSet(0, _sets[slot]!);
        commands.SetVertexBuffer(0, _vertices[slot]!);
        commands.SetIndexBuffer(_indices[slot]!, IndexFormat.UInt16);
        foreach (DrawCommand draw in _commands)
        {
            // From the top-left corner of the display, in logical units, to pixels from the bottom-left corner of the target.
            float left = Math.Max(0, (draw.Clip.X - _displayPos.X) * _scale.X);
            float top = Math.Max(0, (draw.Clip.Y - _displayPos.Y) * _scale.Y);
            float right = Math.Min(framebuffer.Width, (draw.Clip.Z - _displayPos.X) * _scale.X);
            float bottom = Math.Min(framebuffer.Height, (draw.Clip.W - _displayPos.Y) * _scale.Y);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            commands.SetScissor(new PixelRect((int)left, framebuffer.Height - (int)bottom, (int)(right - left), (int)(bottom - top)));
            commands.DrawIndexed(draw.Count, 1, draw.FirstIndex, draw.BaseVertex);
        }
    }

    public void Dispose()
    {
        DisposePipelines();
        ForgetSets();
        foreach (GpuBuffer? buffer in _vertices)
        {
            buffer?.Dispose();
        }

        foreach (GpuBuffer? buffer in _indices)
        {
            buffer?.Dispose();
        }

        _layout?.Dispose();
        _uniforms?.Dispose();
        _atlas?.Dispose();
        _sampler?.Dispose();
    }

    /// <summary>Whether the shader decodes the sRGB colors of Dear ImGui: the target encodes linear colors itself, or keeps linear values.</summary>
    internal static bool DecodesSrgb(PixelFormat format) => format is not (PixelFormat.R8Unorm or PixelFormat.Rg8Unorm or PixelFormat.Rgba8Unorm);

    /// <summary>The orthographic projection of the display: its top-left corner at the top-left of the target.</summary>
    internal static Matrix4x4 Projection(Vector2 position, Vector2 size)
    {
        float left = position.X;
        float right = position.X + size.X;
        float top = position.Y;
        float bottom = position.Y + size.Y;
        return new Matrix4x4(
            2 / (right - left), 0, 0, 0,
            0, 2 / (top - bottom), 0, 0,
            0, 0, -1, 0,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0, 1);
    }

    private GpuBuffer Ensure(GpuBuffer?[] buffers, int slot, long size, BufferUsage usage, string name)
    {
        if (buffers[slot] is { } buffer && buffer.Size >= size)
        {
            return buffer;
        }

        long capacity = Math.Max(MinimumBufferSize, buffers[slot] is { } old ? Math.Max(size, old.Size * 2) : size);
        buffers[slot]?.Dispose();
        buffers[slot] = _device!.CreateBuffer(new BufferDescription(capacity, usage) { Name = $"{name}.{slot}" });
        return buffers[slot]!;
    }

    // The layout and the pipelines follow the shader when a hot reload changes it.
    private void CreateLayout()
    {
        DisposePipelines();
        ForgetSets();
        _layout?.Dispose();
        _layout = _device!.CreateResourceLayout(new ResourceLayoutDescription(
        [
            new ResourceLayoutEntry(0, ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutEntry(0, ResourceKind.SampledTexture, ShaderStages.Fragment),
        ])
        { Name = "nobara.debug.ui" });
        _shaderVersion = _shader!.Version;
    }

    private void ForgetSets()
    {
        for (int slot = 0; slot < _sets.Length; slot++)
        {
            _sets[slot]?.Dispose();
            _sets[slot] = null;
        }
    }

    private void DisposePipelines()
    {
        foreach ((_, GraphicsPipeline pipeline) in _pipelines)
        {
            pipeline.Dispose();
        }

        _pipelines.Clear();
    }

    // One pipeline per set of target formats, created the first time the pass draws to them.
    private GraphicsPipeline PipelineFor(Framebuffer framebuffer)
    {
        IReadOnlyList<PixelFormat> colors = framebuffer.ColorFormats;
        foreach ((PixelFormat[] Colors, GraphicsPipeline Pipeline) entry in _pipelines)
        {
            if (Same(entry.Colors, colors))
            {
                return entry.Pipeline;
            }
        }

        ShaderProgram program = _shader!.Programs[0];
        PixelFormat[] formats = [.. colors];
        GraphicsPipeline pipeline = _device!.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            Name = "nobara.debug.ui",
            VertexShader = program.Vertex!,
            FragmentShader = program.Fragment,
            VertexBuffers =
            [
                new VertexBufferLayout(VertexSize,
                [
                    new VertexAttribute(0, VertexFormat.Float2, 0),
                    new VertexAttribute(1, VertexFormat.Float2, 8),
                    new VertexAttribute(2, VertexFormat.Byte4Norm, 16),
                ]),
            ],
            ResourceLayouts = [_layout!],
            Rasterizer = new RasterizerState { CullMode = CullMode.None, ScissorTest = true },
            Depth = DepthState.Disabled,
            Blend = BlendState.AlphaBlend,
            Outputs = new OutputDescription(formats, framebuffer.DepthFormat),
        });
        _pipelines.Add((formats, pipeline));
        return pipeline;
    }

    private static bool Same(PixelFormat[] formats, IReadOnlyList<PixelFormat> colors)
    {
        if (formats.Length != colors.Count)
        {
            return false;
        }

        for (int i = 0; i < formats.Length; i++)
        {
            if (formats[i] != colors[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A command of a draw list: its clip rectangle, in logical units, and its indices in the buffers of the frame.</summary>
    private readonly record struct DrawCommand(Vector4 Clip, int Count, int FirstIndex, int BaseVertex);

    [StructLayout(LayoutKind.Sequential, Size = UniformSize)]
    private readonly record struct FrameUniforms(Matrix4x4 Projection, int DecodeSrgb);
}
