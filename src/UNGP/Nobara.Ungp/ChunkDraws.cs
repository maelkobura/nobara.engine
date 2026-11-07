// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Ungp;

/// <summary>
/// Draws the chunk geometry of nRenderer with the chunk shader of UNGP: the
/// pipeline states of each pass and category, and the resource set of the
/// view and of the texture arrays.
/// </summary>
/// <remarks>
/// The chunk shader reads the view block at its binding and the texture arrays
/// as <c>sampler2DArray BlockTextures[16]</c> at bindings 0 to 15, in set 0;
/// <see cref="ChunkGeometry.DrawLayout"/> is set 1.
/// </remarks>
internal sealed class ChunkDraws : IDisposable
{
    /// <summary>The number of texture arrays that the chunk shader samples, from index 0.</summary>
    public const int TextureSlots = 16;

    private const int ChunkSet = 1;
    private readonly ModuleContext _context;
    private readonly GraphicsDevice _device;
    private readonly ChunkGeometry _chunks;
    private readonly IRenderResources _resources;
    private readonly RenderShader _shader;
    private readonly SceneDraws _scene;
    private readonly Texture _white;
    private readonly Sampler _whiteSampler;
    private readonly List<PipelineEntry> _pipelines = [];
    private readonly ResourceSet?[] _sets;
    private ResourceLayout? _layout;
    private int _shaderVersion = -1;
    private bool _warnedArrays;

    public ChunkDraws(ModuleContext context, GraphicsDevice device, ChunkGeometry chunks, IRenderResources resources, SceneDraws scene)
    {
        _context = context;
        _device = device;
        _chunks = chunks;
        _resources = resources;
        _scene = scene;
        _shader = resources.GetShader(UngpModule.ChunkShader);
        _sets = new ResourceSet?[device.FramesInFlight];
        _white = device.CreateTexture(new TextureDescription(TextureDimension.Texture2DArray, PixelFormat.Rgba8Unorm, 1, 1, 1, 1, TextureUsage.Sampled) { Name = "nobara.ungp.chunk-white" });
        device.UpdateTexture(_white, TextureRegion.Whole(_white), [255, 255, 255, 255]);
        _whiteSampler = device.CreateSampler(SamplerDescription.Point with { Name = "nobara.ungp.chunk-white" });
    }

    /// <summary>Gets the number of graphics pipelines created, for tests.</summary>
    internal int PipelineCount => _pipelines.Count;

    /// <summary>Draws one category of the visible sections, in a pass whose view <see cref="SceneDraws.Prepare"/> wrote.</summary>
    public void Draw(RenderPassContext context, SceneDrawKind kind, ChunkCategory category)
    {
        if (_chunks.SectionCount == 0)
        {
            return;
        }

        if (_shaderVersion != _shader.Version)
        {
            CreateLayout();
        }

        int slot = context.FrameIndex;
        _sets[slot] ??= CreateSet(slot);
        CommandBuffer commands = context.Commands;
        commands.SetPipeline(PipelineFor(kind, category, context.Framebuffer!));
        commands.SetResourceSet(0, _sets[slot]!);
        _chunks.Draw(context, category, ChunkSet);
    }

    public void Dispose()
    {
        DisposeLayout();
        _whiteSampler.Dispose();
        _white.Dispose();
    }

    // The layout follows the binding of the view block, which a hot reload of the shader may move.
    private void CreateLayout()
    {
        DisposeLayout();
        int binding = -1;
        foreach (ShaderBufferBinding uniform in _shader.Uniforms)
        {
            if (uniform.Name == SceneDraws.ViewUniform)
            {
                binding = uniform.Binding;
            }
        }

        if (binding < 0 || _shader.Programs[0].Vertex is null)
        {
            throw new InvalidOperationException($"Shader '{_shader.ObjectId}' cannot draw chunks: it needs a vertex stage and the uniform block '{SceneDraws.ViewUniform}'.");
        }

        var entries = new ResourceLayoutEntry[TextureSlots + 1];
        entries[0] = new ResourceLayoutEntry(binding, ResourceKind.UniformBuffer, ShaderStages.Vertex);
        for (int i = 0; i < TextureSlots; i++)
        {
            entries[i + 1] = new ResourceLayoutEntry(i, ResourceKind.SampledTexture, ShaderStages.Fragment);
        }

        _layout = _device.CreateResourceLayout(new ResourceLayoutDescription(entries) { Name = "nobara.ungp.chunks" });
        _shaderVersion = _shader.Version;
    }

    private ResourceSet CreateSet(int slot)
    {
        IReadOnlyList<TextureArray> arrays = _resources.TextureArrays;
        if (arrays.Count > TextureSlots && !_warnedArrays)
        {
            _warnedArrays = true;
            _context.Logger.Warning($"The textures fill {arrays.Count} texture arrays, and the chunk shader samples the first {TextureSlots}: chunk faces whose texture is in a later array are drawn white. Fewer sizes or settings of textures make fewer arrays.");
        }

        var bound = new BoundResource[TextureSlots + 1];
        bound[0] = BoundResource.Of(_scene.ViewBuffer(slot));
        for (int i = 0; i < TextureSlots; i++)
        {
            bound[i + 1] = i < arrays.Count ? BoundResource.Of(arrays[i].Texture, arrays[i].Sampler) : BoundResource.Of(_white, _whiteSampler);
        }

        return _device.CreateResourceSet(new ResourceSetDescription(_layout!, bound) { Name = $"nobara.ungp.chunks.{slot}" });
    }

    private GraphicsPipeline PipelineFor(SceneDrawKind kind, ChunkCategory category, Framebuffer framebuffer)
    {
        foreach (PipelineEntry entry in _pipelines)
        {
            if (entry.Kind == kind && entry.Category == category)
            {
                return entry.Pipeline;
            }
        }

        ShaderProgram program = category == ChunkCategory.Cutout && _shader.TryGetProgram(UngpModule.CutoutVariant, out ShaderProgram? cutout) ? cutout : _shader.Programs[0];
        bool transparent = category == ChunkCategory.Transparent;
        GraphicsPipeline pipeline = _device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            Name = $"nobara.ungp.chunks.{kind}.{category}",
            VertexShader = program.Vertex!,

            // The depth of opaque faces needs no fragment stage; cut-out faces discard their texels in it.
            FragmentShader = kind == SceneDrawKind.DepthOnly && category == ChunkCategory.Opaque ? null : program.Fragment,
            VertexBuffers = _chunks.VertexLayouts,
            ResourceLayouts = [_layout!, _chunks.DrawLayout],

            // Cut-out faces, such as plants, are seen from both sides, as cut-out meshes are.
            Rasterizer = new RasterizerState { CullMode = category == ChunkCategory.Cutout ? CullMode.None : CullMode.Back },
            Depth = new DepthState { TestEnabled = true, WriteEnabled = (kind is SceneDrawKind.DepthOnly or SceneDrawKind.Full) && !transparent, Compare = Camera.DepthCompare },
            Blend = transparent ? BlendState.AlphaBlend : BlendState.Opaque,
            Outputs = new OutputDescription([.. framebuffer.ColorFormats], framebuffer.DepthFormat),
        });
        _pipelines.Add(new PipelineEntry(kind, category, pipeline));
        return pipeline;
    }

    private void DisposeLayout()
    {
        foreach (PipelineEntry entry in _pipelines)
        {
            entry.Pipeline.Dispose();
        }

        _pipelines.Clear();
        for (int slot = 0; slot < _sets.Length; slot++)
        {
            _sets[slot]?.Dispose();
            _sets[slot] = null;
        }

        _layout?.Dispose();
        _layout = null;
    }

    /// <summary>The pipeline state of one kind of pass and one category of faces.</summary>
    private sealed record PipelineEntry(SceneDrawKind Kind, ChunkCategory Category, GraphicsPipeline Pipeline);
}
