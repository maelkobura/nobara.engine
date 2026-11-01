// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Graphics;

namespace Nobara.Renderer.Tests.Support;

/// <summary>
/// A minimal pipeline of two passes: <c>opaque</c> draws the items of the view
/// to its target, <c>unused</c> writes a texture that nothing reads. It
/// declares the three usual insertion points.
/// </summary>
internal sealed class TestPipeline(GraphicsDevice device) : IRenderPipeline, IDisposable
{
    public static readonly ColorF Background = new(0.1f, 0.2f, 0.3f, 1);

    public string Name => "test";

    public DrawPass Opaque { get; } = new(device);

    public RecordingPass Unused { get; } = new();

    public int Builds { get; private set; }

    public void Build(RenderGraphBuilder graph)
    {
        Builds++;
        graph.AddRasterPass("opaque", Opaque).Color(graph.Target, LoadAction.Clear, Background).Depth(graph.TargetDepth, LoadAction.Clear);
        graph.InsertionPoint(RenderInsertionPoints.AfterOpaque, graph.Target, graph.TargetDepth);
        RenderGraphTexture scratch = graph.CreateTexture("scratch", new RenderGraphTextureDescription(PixelFormat.Rgba8Unorm));
        graph.AddRasterPass("unused", Unused).Color(scratch, LoadAction.Clear);
        graph.InsertionPoint(RenderInsertionPoints.BeforePostProcess, graph.Target, graph.TargetDepth);
        graph.InsertionPoint(RenderInsertionPoints.Ui, graph.Target);
    }

    public void Dispose() => Opaque.Dispose();
}

/// <summary>A pipeline that runs a function to build its graph.</summary>
internal sealed class DelegatePipeline(string name, Action<RenderGraphBuilder> build) : IRenderPipeline
{
    public string Name { get; } = name;

    public GraphicsFeatures RequiredFeatures { get; init; }

    public void Build(RenderGraphBuilder graph) => build(graph);
}

/// <summary>An insertion that adds one raster pass at its point.</summary>
internal sealed class TestInsertion(string name, IRenderPass pass) : IRenderInsertion
{
    public RenderInsertionPoint? Point { get; private set; }

    public void Build(RenderGraphBuilder graph, in RenderInsertionPoint point)
    {
        Point = point;
        RenderPassBuilder builder = graph.AddRasterPass(name, pass).Color(point.Color);
        if (point.Depth.IsValid)
        {
            builder.Depth(point.Depth, write: false);
        }
    }
}

/// <summary>A pass that counts its runs and what it saw.</summary>
internal sealed class RecordingPass : IRenderPass
{
    public int Runs { get; private set; }

    public int Prepares { get; private set; }

    public Framebuffer? Framebuffer { get; private set; }

    public Action<RenderPassContext>? OnExecute { get; set; }

    public void Prepare(RenderPassContext context) => Prepares++;

    public void Execute(RenderPassContext context)
    {
        Runs++;
        Framebuffer = context.Framebuffer;
        OnExecute?.Invoke(context);
    }
}

/// <summary>An extractor that runs a function at every frame.</summary>
internal sealed class TestExtractor(Action<RenderExtraction>? extract = null) : IRenderExtractor
{
    public Action<RenderExtraction>? Next { get; set; }

    public int Runs { get; private set; }

    public void Extract(RenderExtraction extraction)
    {
        Runs++;
        extract?.Invoke(extraction);
        Action<RenderExtraction>? next = Next;
        Next = null;
        next?.Invoke(extraction);
    }
}

/// <summary>
/// Draws meshes with their materials: the items of the view, or fixed items
/// whose model matrix is the whole transform to clip space. The shaders read
/// the uniform block <c>Camera</c> at binding 0, which holds the transform.
/// </summary>
internal sealed class DrawPass : IRenderPass, IDisposable
{
    private const int Stride = 256;
    private readonly GraphicsDevice _device;
    private readonly ResourceLayout _layout;
    private readonly List<(ShaderProgram Program, Framebuffer Framebuffer, GraphicsPipeline Pipeline)> _pipelines = [];
    private readonly GpuBuffer?[] _buffers;
    private readonly List<ResourceSet>[] _sets;

    public DrawPass(GraphicsDevice device)
    {
        _device = device;
        _layout = device.CreateResourceLayout(new ResourceLayoutDescription([new ResourceLayoutEntry(0, ResourceKind.UniformBuffer, ShaderStages.Vertex)]));
        _buffers = new GpuBuffer?[device.FramesInFlight];
        _sets = [.. Enumerable.Range(0, device.FramesInFlight).Select(_ => new List<ResourceSet>())];
    }

    public RenderItem[]? FixedItems { get; set; }

    public int Draws { get; private set; }

    public List<RenderItem>? Seen { get; set; }

    public void Prepare(RenderPassContext context)
    {
        ReadOnlySpan<RenderItem> items = Items(context.View);
        int slot = context.FrameIndex;
        if (items.Length == 0)
        {
            return;
        }

        if (_buffers[slot] is not { } buffer || buffer.Size < items.Length * Stride)
        {
            _buffers[slot]?.Dispose();
            foreach (ResourceSet set in _sets[slot])
            {
                set.Dispose();
            }

            _sets[slot].Clear();
            buffer = _device.CreateBuffer(new BufferDescription(Math.Max(items.Length, 16) * Stride, BufferUsage.Uniform));
            _buffers[slot] = buffer;
        }

        while (_sets[slot].Count < items.Length)
        {
            _sets[slot].Add(_device.CreateResourceSet(new ResourceSetDescription(_layout, [BoundResource.Of(buffer, _sets[slot].Count * Stride, 64)])));
        }

        for (int i = 0; i < items.Length; i++)
        {
            Matrix4x4 transform = FixedItems is null ? items[i].Model * context.View.ViewProjectionMatrix : items[i].Model;
            _device.UpdateBuffer(buffer, i * Stride, MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(ref transform)));
        }
    }

    public void Execute(RenderPassContext context)
    {
        ReadOnlySpan<RenderItem> items = Items(context.View);
        CommandBuffer commands = context.Commands;
        for (int i = 0; i < items.Length; i++)
        {
            RenderItem item = items[i];
            Seen?.Add(item);
            Submesh part = item.Mesh.Submeshes[item.Submesh];
            commands.SetPipeline(PipelineFor(item.Material, context.Framebuffer!));
            commands.SetResourceSet(0, _sets[context.FrameIndex][i]);
            commands.SetResourceSet(1, item.Material.ResourceSet!);
            commands.SetVertexBuffer(0, item.Mesh.VertexBuffer);
            commands.SetIndexBuffer(item.Mesh.IndexBuffer, IndexFormat.UInt32);
            commands.DrawIndexed(part.IndexCount, 1, part.FirstIndex);
            Draws++;
        }
    }

    public void Dispose()
    {
        foreach ((_, _, GraphicsPipeline pipeline) in _pipelines)
        {
            pipeline.Dispose();
        }

        foreach (List<ResourceSet> sets in _sets)
        {
            sets.ForEach(s => s.Dispose());
        }

        foreach (GpuBuffer? buffer in _buffers)
        {
            buffer?.Dispose();
        }

        _layout.Dispose();
    }

    private ReadOnlySpan<RenderItem> Items(RenderView view) => FixedItems is { } items ? items : view.Items;

    private GraphicsPipeline PipelineFor(RenderMaterial material, Framebuffer framebuffer)
    {
        ShaderProgram program = material.Program;
        foreach ((ShaderProgram Program, Framebuffer Framebuffer, GraphicsPipeline Pipeline) entry in _pipelines)
        {
            if (entry.Program == program && entry.Framebuffer == framebuffer)
            {
                return entry.Pipeline;
            }
        }

        GraphicsPipeline pipeline = _device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            VertexShader = program.Vertex!,
            FragmentShader = program.Fragment,
            VertexBuffers = [RenderMesh.VertexLayout],
            ResourceLayouts = [_layout, material.Shader.MaterialLayout!],
            Rasterizer = new RasterizerState { CullMode = CullMode.None },
            Depth = framebuffer.DepthFormat == PixelFormat.None ? DepthState.Disabled : new DepthState { TestEnabled = true, WriteEnabled = true, Compare = Camera.DepthCompare },
            Outputs = new OutputDescription([.. framebuffer.ColorFormats], framebuffer.DepthFormat),
        });
        _pipelines.Add((program, framebuffer, pipeline));
        return pipeline;
    }
}
