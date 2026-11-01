// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Graphics;
using Nobara.Renderer;

namespace Nobara.Ungp;

/// <summary>
/// A pass that draws one triangle over the viewport with a shader of UNGP,
/// which reads a uniform block that the pass writes at every frame. The GPU
/// objects follow the shader when a hot reload changes it.
/// </summary>
internal abstract class FullscreenPass : IRenderPass, IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly RenderShader _shader;
    private readonly string _uniform;
    private readonly int _uniformSize;
    private readonly List<(ShaderProgram Program, PixelFormat Color, PixelFormat Depth, GraphicsPipeline Pipeline)> _pipelines = [];
    private PerFrame<GpuBuffer>? _uniforms;
    private ResourceLayout? _layout;
    private ResourceSet?[] _sets;
    private int _shaderVersion = -1;

    protected FullscreenPass(GraphicsDevice device, RenderShader shader, string uniform, int uniformSize)
    {
        _device = device;
        _shader = shader;
        _uniform = uniform;
        _uniformSize = uniformSize;
        _sets = new ResourceSet?[device.FramesInFlight];
    }

    /// <summary>Gets the device.</summary>
    protected GraphicsDevice Device => _device;

    /// <summary>Gets the depth state of the pipeline.</summary>
    protected virtual DepthState Depth => DepthState.Disabled;

    public void Prepare(RenderPassContext context)
    {
        if (_shaderVersion != _shader.Version)
        {
            CreateLayout();
        }

        int slot = context.FrameIndex;
        PrepareResources(context, slot);
        if (_sets[slot] is null)
        {
            _sets[slot] = _device.CreateResourceSet(new ResourceSetDescription(_layout!, Bound(slot, _uniforms![slot])) { Name = $"{_layout!.Name}.{slot}" });
        }

        WriteUniforms(context, _uniforms!.Current);
    }

    public void Execute(RenderPassContext context)
    {
        CommandBuffer commands = context.Commands;
        commands.SetPipeline(PipelineFor(context.Framebuffer!));
        commands.SetResourceSet(0, _sets[context.FrameIndex]!);
        commands.Draw(3);
    }

    public void Dispose()
    {
        DisposeLayout();
        _uniforms?.Dispose();
        DisposeResources();
    }

    /// <summary>Disposes the GPU objects of the derived pass: none by default.</summary>
    protected virtual void DisposeResources()
    {
    }

    /// <summary>Chooses the program of the shader to draw with.</summary>
    protected abstract ShaderProgram Program(RenderShader shader);

    /// <summary>Lists the entries of the layout after the uniform block: none by default.</summary>
    protected virtual ResourceLayoutEntry[] ExtraEntries() => [];

    /// <summary>Lists the resources of the set of a slot: the uniform buffer, then the resources of <see cref="ExtraEntries"/>.</summary>
    protected virtual BoundResource[] Bound(int slot, GpuBuffer uniforms) => [BoundResource.Of(uniforms)];

    /// <summary>Checks the resources of the set of a slot before the frame; <see cref="ForgetSet"/> makes the set be created again.</summary>
    protected virtual void PrepareResources(RenderPassContext context, int slot)
    {
    }

    /// <summary>Writes the uniform block of the frame.</summary>
    protected abstract void WriteUniforms(RenderPassContext context, GpuBuffer uniforms);

    /// <summary>Drops the resource set of a slot, so that it is created again with new resources.</summary>
    protected void ForgetSet(int slot)
    {
        _sets[slot]?.Dispose();
        _sets[slot] = null;
    }

    private void CreateLayout()
    {
        DisposeLayout();
        int binding = -1;
        for (int i = 0; i < _shader.Uniforms.Count; i++)
        {
            if (_shader.Uniforms[i].Name == _uniform)
            {
                binding = _shader.Uniforms[i].Binding;
            }
        }

        if (binding < 0)
        {
            throw new InvalidOperationException($"Shader '{_shader.ObjectId}' declares no uniform block '{_uniform}'.");
        }

        ResourceLayoutEntry[] extra = ExtraEntries();
        var entries = new ResourceLayoutEntry[extra.Length + 1];
        entries[0] = new ResourceLayoutEntry(binding, ResourceKind.UniformBuffer, ShaderStages.Fragment);
        extra.CopyTo(entries, 1);
        _layout = _device.CreateResourceLayout(new ResourceLayoutDescription(entries) { Name = $"nobara.ungp.{_uniform.ToLowerInvariant()}" });
        _uniforms ??= new PerFrame<GpuBuffer>(_device, slot => _device.CreateBuffer(new BufferDescription(_uniformSize, BufferUsage.Uniform) { Name = $"nobara.ungp.{_uniform.ToLowerInvariant()}.{slot}" }));
        _shaderVersion = _shader.Version;
    }

    private void DisposeLayout()
    {
        foreach ((_, _, _, GraphicsPipeline pipeline) in _pipelines)
        {
            pipeline.Dispose();
        }

        _pipelines.Clear();
        for (int slot = 0; slot < _sets.Length; slot++)
        {
            ForgetSet(slot);
        }

        _layout?.Dispose();
        _layout = null;
    }

    // One pipeline per program and target format, created the first time the pass draws with them.
    private GraphicsPipeline PipelineFor(Framebuffer framebuffer)
    {
        ShaderProgram program = Program(_shader);
        PixelFormat color = framebuffer.ColorFormats[0];
        foreach ((ShaderProgram Program, PixelFormat Color, PixelFormat Depth, GraphicsPipeline Pipeline) entry in _pipelines)
        {
            if (ReferenceEquals(entry.Program, program) && entry.Color == color && entry.Depth == framebuffer.DepthFormat)
            {
                return entry.Pipeline;
            }
        }

        GraphicsPipeline pipeline = _device.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            Name = $"nobara.ungp.{_uniform.ToLowerInvariant()}.{program.Variant}",
            VertexShader = program.Vertex!,
            FragmentShader = program.Fragment,
            ResourceLayouts = [_layout!],
            Rasterizer = new RasterizerState { CullMode = CullMode.None },
            Depth = Depth,
            Outputs = new OutputDescription([.. framebuffer.ColorFormats], framebuffer.DepthFormat),
        });
        _pipelines.Add((program, color, framebuffer.DepthFormat, pipeline));
        return pipeline;
    }
}

/// <summary>The sky pass: the plain sky, at the far plane, where the depth of the scene is still clear.</summary>
internal sealed class SkyPass(UniversalPipeline pipeline, GraphicsDevice device, RenderShader shader)
    : FullscreenPass(device, shader, "Sky", 16)
{
    /// <summary>The identifier of the shader of the sky.</summary>
    public const string ShaderId = "shader:nobara.ungp.sky";

    // The triangle lies at depth 0, which only the pixels without geometry keep.
    protected override DepthState Depth => new() { TestEnabled = true, WriteEnabled = false, Compare = Camera.DepthCompare };

    protected override ShaderProgram Program(RenderShader shader) => shader.Programs[0];

    protected override void WriteUniforms(RenderPassContext context, GpuBuffer uniforms)
    {
        ColorF sky = pipeline.SkyColor;
        var color = new Vector4(sky.R, sky.G, sky.B, 1);
        context.Commands.UpdateBuffer(uniforms, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(ref color)));
    }
}

/// <summary>
/// The output pass: it reads the HDR scene, applies the exposure, tonemaps
/// or clamps the colors, encodes them in sRGB when the target does not, and
/// scales the image from the render resolution to the viewport.
/// </summary>
internal sealed class OutputPass : FullscreenPass
{
    /// <summary>The identifier of the shader of the output.</summary>
    public const string ShaderId = "shader:nobara.ungp.tonemap";

    /// <summary>The variant of the shader that clamps instead of tonemapping.</summary>
    public const string ClampVariant = "Clamp";

    /// <summary>The binding of the sampled texture of the scene in the shader.</summary>
    public const int SceneBinding = 0;

    private readonly UniversalPipeline _pipeline;
    private readonly Sampler _sampler;
    private readonly Texture?[] _scenes;

    public OutputPass(UniversalPipeline pipeline, GraphicsDevice device, RenderShader shader)
        : base(device, shader, "Output", 16)
    {
        _pipeline = pipeline;
        _scenes = new Texture?[device.FramesInFlight];
        _sampler = device.CreateSampler(new SamplerDescription
        {
            MipFilter = FilterMode.Nearest,
            AddressU = AddressMode.ClampToEdge,
            AddressV = AddressMode.ClampToEdge,
            AddressW = AddressMode.ClampToEdge,
            Name = "nobara.ungp.output",
        });
    }

    /// <summary>Gets or sets whether the colors are clamped instead of tonemapped; the pipeline sets it when it builds the graph.</summary>
    public bool Clamp { get; set; }

    /// <summary>Gets or sets the texture of the scene; the pipeline sets it when it builds the graph.</summary>
    public RenderGraphTexture Scene { get; set; }

    /// <summary>Whether the shader encodes the colors in sRGB: the target stores 8-bit values without doing it.</summary>
    internal static bool EncodesSrgb(PixelFormat format) => format is PixelFormat.R8Unorm or PixelFormat.Rg8Unorm or PixelFormat.Rgba8Unorm;

    protected override void DisposeResources() => _sampler.Dispose();

    protected override ShaderProgram Program(RenderShader shader) =>
        Clamp && shader.TryGetProgram(ClampVariant, out ShaderProgram? clamp) ? clamp : shader.Programs[0];

    protected override ResourceLayoutEntry[] ExtraEntries() => [new ResourceLayoutEntry(SceneBinding, ResourceKind.SampledTexture, ShaderStages.Fragment)];

    protected override BoundResource[] Bound(int slot, GpuBuffer uniforms) => [BoundResource.Of(uniforms), BoundResource.Of(_scenes[slot]!, _sampler)];

    // The texture of the scene changes when the graph is built again at another size.
    protected override void PrepareResources(RenderPassContext context, int slot)
    {
        Texture scene = context.GetTexture(Scene);
        if (!ReferenceEquals(_scenes[slot], scene))
        {
            _scenes[slot] = scene;
            ForgetSet(slot);
        }
    }

    protected override void WriteUniforms(RenderPassContext context, GpuBuffer uniforms)
    {
        var values = new OutputUniforms(_pipeline.Exposure, EncodesSrgb(context.Framebuffer!.ColorFormats[0]) ? 1 : 0);
        context.Commands.UpdateBuffer(uniforms, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<OutputUniforms>(ref values)));
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct OutputUniforms(float Exposure, int EncodeSrgb);
}
