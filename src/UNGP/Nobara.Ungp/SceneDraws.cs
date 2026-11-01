// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Ungp;

/// <summary>How a pass draws the geometry of the scene.</summary>
internal enum SceneDrawKind
{
    /// <summary>Depth only: the depth prepass.</summary>
    DepthOnly,

    /// <summary>Color, tested against the depth of the prepass without writing it.</summary>
    AfterPrepass,

    /// <summary>Color and depth, without a prepass.</summary>
    Full,
}

/// <summary>A pass that draws the opaque and cut-out items of the view: the depth prepass, or the opaque pass.</summary>
internal sealed class SceneGeometryPass(SceneDraws scene, SceneDrawKind kind) : IRenderPass
{
    /// <summary>Gets or sets how the pass draws; the pipeline sets it when it builds the graph.</summary>
    public SceneDrawKind Kind { get; set; } = kind;

    public void Prepare(RenderPassContext context) => scene.Prepare(context);

    public void Execute(RenderPassContext context) => scene.Draw(context, Kind);
}

/// <summary>
/// What the geometry passes share: the view of the frame in a uniform buffer,
/// the transform of each item as instance data, and the pipeline states of the
/// materials drawn.
/// </summary>
/// <remarks>
/// A shader drawn by UNGP reads the mesh vertices at locations 0 to 3
/// (<see cref="RenderMesh.VertexLayout"/>), the transform of the object seen
/// from the camera as a <c>mat4</c> at locations 4 to 7, one per instance, and
/// declares the uniform block <c>View</c>, which holds <c>mat4
/// ViewProjection</c>.
/// </remarks>
internal sealed class SceneDraws : IDisposable
{
    /// <summary>The name of the uniform block of the view, as shaders declare it.</summary>
    public const string ViewUniform = "View";

    /// <summary>The first vertex location of the transform of an item.</summary>
    public const int ModelLocation = 4;

    private const int ModelSize = 64;
    private const int ViewSize = 64;
    private readonly ModuleContext _context;
    private readonly GraphicsDevice _device;
    private readonly VertexBufferLayout _modelLayout;
    private readonly PerFrame<GpuBuffer> _viewBuffers;
    private readonly GpuBuffer?[] _models;
    private readonly List<ViewBinding> _viewBindings = [];
    private readonly List<PipelineEntry> _pipelines = [];
    private readonly HashSet<RenderMaterial> _skipped = [];
    private Matrix4x4[] _staging = new Matrix4x4[256];
    private ulong _preparedFrame = ulong.MaxValue;

    public SceneDraws(ModuleContext context, GraphicsDevice device)
    {
        _context = context;
        _device = device;
        _modelLayout = new VertexBufferLayout(ModelSize,
        [
            new VertexAttribute(ModelLocation, VertexFormat.Float4, 0),
            new VertexAttribute(ModelLocation + 1, VertexFormat.Float4, 16),
            new VertexAttribute(ModelLocation + 2, VertexFormat.Float4, 32),
            new VertexAttribute(ModelLocation + 3, VertexFormat.Float4, 48),
        ], VertexStepMode.Instance);
        _viewBuffers = new PerFrame<GpuBuffer>(device, slot => device.CreateBuffer(new BufferDescription(ViewSize, BufferUsage.Uniform) { Name = $"nobara.ungp.view.{slot}" }));
        _models = new GpuBuffer?[device.FramesInFlight];
    }

    /// <summary>Gets the number of graphics pipelines created for the materials drawn, for tests.</summary>
    internal int PipelineCount => _pipelines.Count;

    /// <summary>Writes the view and the transforms of the items, once per frame, before the first geometry pass.</summary>
    public void Prepare(RenderPassContext context)
    {
        if (_preparedFrame == context.Frame)
        {
            return;
        }

        _preparedFrame = context.Frame;
        CommandBuffer commands = context.Commands;
        Matrix4x4 viewProjection = context.View.ViewProjectionMatrix;
        commands.UpdateBuffer(_viewBuffers.Current, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(ref viewProjection)));

        ReadOnlySpan<RenderItem> items = context.View.Items;
        if (items.Length == 0)
        {
            return;
        }

        if (_staging.Length < items.Length)
        {
            Array.Resize(ref _staging, Math.Max(items.Length, _staging.Length * 2));
        }

        for (int i = 0; i < items.Length; i++)
        {
            _staging[i] = items[i].Model;
        }

        int slot = context.FrameIndex;
        long size = (long)items.Length * ModelSize;
        if (_models[slot] is not { } buffer || buffer.Size < size)
        {
            _models[slot]?.Dispose();
            buffer = _device.CreateBuffer(new BufferDescription(Math.Max(size, 64 * 1024), BufferUsage.Vertex) { Name = $"nobara.ungp.models.{slot}" });
            _models[slot] = buffer;
        }

        commands.UpdateBuffer(buffer, 0, MemoryMarshal.AsBytes(_staging.AsSpan(0, items.Length)));
    }

    /// <summary>Draws the opaque items, then the cut-out ones; transparent items are left out.</summary>
    public void Draw(RenderPassContext context, SceneDrawKind kind)
    {
        ReadOnlySpan<RenderItem> items = context.View.Items;
        if (items.Length == 0)
        {
            return;
        }

        GpuBuffer models = _models[context.FrameIndex]!;
        DrawMode(context, items, models, kind, MaterialMode.Opaque);
        DrawMode(context, items, models, kind, MaterialMode.Cutout);
    }

    public void Dispose()
    {
        foreach (PipelineEntry entry in _pipelines)
        {
            entry.Pipeline?.Dispose();
        }

        foreach (ViewBinding binding in _viewBindings)
        {
            foreach (ResourceSet set in binding.Sets)
            {
                set.Dispose();
            }

            binding.Layout.Dispose();
        }

        foreach (GpuBuffer? buffer in _models)
        {
            buffer?.Dispose();
        }

        _viewBuffers.Dispose();
    }

    private void DrawMode(RenderPassContext context, ReadOnlySpan<RenderItem> items, GpuBuffer models, SceneDrawKind kind, MaterialMode mode)
    {
        CommandBuffer commands = context.Commands;
        for (int i = 0; i < items.Length; i++)
        {
            RenderItem item = items[i];
            RenderMaterial material = item.Material;
            if (material.Mode != mode)
            {
                if (material.Mode == MaterialMode.Transparent && FirstSkip(material))
                {
                    _context.Logger.Warning($"Material '{material.ObjectId}' is transparent, and UNGP does not draw transparent geometry yet: objects drawn with it are left out.");
                }

                continue;
            }

            PipelineEntry entry = PipelineFor(material, kind, context.Framebuffer!);
            if (entry.Pipeline is not { } pipeline)
            {
                continue;
            }

            Submesh part = item.Mesh.Submeshes[item.Submesh];
            commands.SetPipeline(pipeline);
            commands.SetResourceSet(0, entry.View!.Sets[context.FrameIndex]);
            if (entry.HasMaterialSet)
            {
                commands.SetResourceSet(1, material.ResourceSet!);
            }

            commands.SetVertexBuffer(0, item.Mesh.VertexBuffer);
            commands.SetVertexBuffer(1, models);
            commands.SetIndexBuffer(item.Mesh.IndexBuffer, IndexFormat.UInt32);
            commands.DrawIndexed(part.IndexCount, 1, part.FirstIndex, 0, i);
        }
    }

    // Whether a material is left out for the first time, so that the warning is written once.
    private bool FirstSkip(RenderMaterial material) => _skipped.Add(material);

    private PipelineEntry PipelineFor(RenderMaterial material, SceneDrawKind kind, Framebuffer framebuffer)
    {
        ShaderProgram program = material.Program;
        RenderShader shader = material.Shader;
        for (int i = 0; i < _pipelines.Count; i++)
        {
            PipelineEntry entry = _pipelines[i];
            if (ReferenceEquals(entry.Program, program) && entry.Kind == kind && entry.Mode == material.Mode && entry.ShaderVersion == shader.Version)
            {
                return entry;
            }
        }

        return CreatePipeline(material, kind, framebuffer);
    }

    private PipelineEntry CreatePipeline(RenderMaterial material, SceneDrawKind kind, Framebuffer framebuffer)
    {
        // The pipelines of a shader that a hot reload changed are dropped.
        for (int i = _pipelines.Count - 1; i >= 0; i--)
        {
            if (_pipelines[i].ShaderVersion != _pipelines[i].Shader.Version)
            {
                _pipelines[i].Pipeline?.Dispose();
                _pipelines.RemoveAt(i);
            }
        }

        ShaderProgram program = material.Program;
        RenderShader shader = material.Shader;
        int binding = FindViewBinding(shader);
        PipelineEntry entry;
        if (program.Vertex is null || binding < 0)
        {
            if (FirstSkip(material))
            {
                _context.Logger.Warning($"Material '{material.ObjectId}' uses shader '{shader.ObjectId}', which UNGP cannot draw: a shader drawn by UNGP has a vertex stage and declares the uniform block '{ViewUniform}'. Objects drawn with it are left out.");
            }

            entry = new PipelineEntry(program, shader, shader.Version, kind, material.Mode, null, null, false);
        }
        else
        {
            ViewBinding view = ViewFor(binding);
            ResourceLayout? materialLayout = shader.MaterialLayout;
            bool cutout = material.Mode == MaterialMode.Cutout;
            GraphicsPipeline pipeline = _device.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                Name = $"nobara.ungp.{kind}.{shader.ObjectId}.{program.Variant}",
                VertexShader = program.Vertex,

                // The depth of opaque geometry needs no fragment stage; cut-out geometry discards its texels in it.
                FragmentShader = kind == SceneDrawKind.DepthOnly && !cutout ? null : program.Fragment,
                VertexBuffers = [RenderMesh.VertexLayout, _modelLayout],
                ResourceLayouts = materialLayout is null ? [view.Layout] : [view.Layout, materialLayout],

                // Cut-out geometry, such as leaves, is seen from both sides.
                Rasterizer = new RasterizerState { CullMode = cutout ? CullMode.None : CullMode.Back },
                Depth = new DepthState { TestEnabled = true, WriteEnabled = kind != SceneDrawKind.AfterPrepass, Compare = Camera.DepthCompare },
                Outputs = new OutputDescription([.. framebuffer.ColorFormats], framebuffer.DepthFormat),
            });
            entry = new PipelineEntry(program, shader, shader.Version, kind, material.Mode, pipeline, view, materialLayout is not null);
        }

        _pipelines.Add(entry);
        return entry;
    }

    private static int FindViewBinding(RenderShader shader)
    {
        IReadOnlyList<ShaderBufferBinding> uniforms = shader.Uniforms;
        for (int i = 0; i < uniforms.Count; i++)
        {
            if (uniforms[i].Name == ViewUniform)
            {
                return uniforms[i].Binding;
            }
        }

        return -1;
    }

    // One layout per binding of the view block, since each shader chooses its binding.
    private ViewBinding ViewFor(int binding)
    {
        foreach (ViewBinding existing in _viewBindings)
        {
            if (existing.Binding == binding)
            {
                return existing;
            }
        }

        ResourceLayout layout = _device.CreateResourceLayout(new ResourceLayoutDescription([new ResourceLayoutEntry(binding, ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)]) { Name = $"nobara.ungp.view.binding{binding}" });
        var sets = new ResourceSet[_viewBuffers.Count];
        for (int slot = 0; slot < sets.Length; slot++)
        {
            sets[slot] = _device.CreateResourceSet(new ResourceSetDescription(layout, [BoundResource.Of(_viewBuffers[slot])]) { Name = $"nobara.ungp.view.binding{binding}.{slot}" });
        }

        var view = new ViewBinding(binding, layout, sets);
        _viewBindings.Add(view);
        return view;
    }

    /// <summary>The layout of the view block at one binding, and its resource set per frame in flight.</summary>
    private sealed record ViewBinding(int Binding, ResourceLayout Layout, ResourceSet[] Sets);

    /// <summary>The pipeline state of a program of a shader for one kind of draw and one mode; no state when UNGP cannot draw the shader.</summary>
    private sealed record PipelineEntry(ShaderProgram Program, RenderShader Shader, int ShaderVersion, SceneDrawKind Kind, MaterialMode Mode, GraphicsPipeline? Pipeline, ViewBinding? View, bool HasMaterialSet);
}
