// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>A texture of a render graph. The default value names no texture.</summary>
/// <param name="Index">The index of the texture in its graph, plus one.</param>
public readonly record struct RenderGraphTexture(int Index)
{
    /// <summary>Gets whether the value names a texture.</summary>
    public bool IsValid => Index > 0;
}

/// <summary>A buffer of a render graph. The default value names no buffer.</summary>
/// <param name="Index">The index of the buffer in its graph, plus one.</param>
public readonly record struct RenderGraphBuffer(int Index)
{
    /// <summary>Gets whether the value names a buffer.</summary>
    public bool IsValid => Index > 0;
}

/// <summary>What the size of a transient texture follows.</summary>
public enum RenderTextureSize
{
    /// <summary>The render resolution of the view, times <see cref="RenderGraphTextureDescription.Scale"/>.</summary>
    RenderResolution,

    /// <summary>The viewport of the view in its target, in pixels, times <see cref="RenderGraphTextureDescription.Scale"/>.</summary>
    TargetResolution,

    /// <summary><see cref="RenderGraphTextureDescription.Width"/> by <see cref="RenderGraphTextureDescription.Height"/> pixels.</summary>
    Fixed,
}

/// <summary>Describes a transient texture: a 2D texture that the graph creates, and shares between passes that do not use it at the same time.</summary>
/// <param name="Format">The format.</param>
public readonly record struct RenderGraphTextureDescription(PixelFormat Format)
{
    /// <summary>Gets what the size follows; the render resolution by default.</summary>
    public RenderTextureSize Size { get; init; } = RenderTextureSize.RenderResolution;

    /// <summary>Gets the factor applied to a resolution; 1 by default.</summary>
    public float Scale { get; init; } = 1;

    /// <summary>Gets the width of a <see cref="RenderTextureSize.Fixed"/> texture.</summary>
    public int Width { get; init; }

    /// <summary>Gets the height of a <see cref="RenderTextureSize.Fixed"/> texture.</summary>
    public int Height { get; init; }

    /// <summary>Gets the number of mip levels; 1 by default.</summary>
    public int MipLevels { get; init; } = 1;
}

/// <summary>How a pass uses a buffer.</summary>
public enum RenderBufferAccess
{
    /// <summary>Read as vertex buffer.</summary>
    Vertex,

    /// <summary>Read as index buffer.</summary>
    Index,

    /// <summary>Read as uniform buffer.</summary>
    Uniform,

    /// <summary>Read as storage buffer.</summary>
    Storage,

    /// <summary>Read as indirect arguments.</summary>
    Indirect,

    /// <summary>Read by a copy.</summary>
    Copy,
}

/// <summary>
/// The code of a pass of the render graph, called every frame where the pass
/// runs. A raster pass draws inside a render pass that the graph begins on the
/// attachments it declares; a compute pass runs outside render passes.
/// </summary>
public interface IRenderPass
{
    /// <summary>
    /// Records commands before the render pass begins, such as writes to
    /// buffers with <see cref="CommandBuffer.UpdateBuffer"/>. By default,
    /// nothing.
    /// </summary>
    /// <param name="context">The frame, the commands, and the resources of the graph.</param>
    [LoopCallback]
    void Prepare(RenderPassContext context)
    {
    }

    /// <summary>Records the commands of the pass: draws inside the render pass of a raster pass, dispatches for a compute pass.</summary>
    /// <param name="context">The frame, the commands, and the resources of the graph.</param>
    [LoopCallback]
    void Execute(RenderPassContext context);
}

/// <summary>What a pass receives when it runs: the frame, the commands it records, and the GPU objects behind the resources of the graph.</summary>
/// <remarks>The object is reused for every pass of every frame: do not keep it.</remarks>
public sealed class RenderPassContext
{
    private CompiledGraph? _graph;

    internal RenderPassContext()
    {
    }

    /// <summary>Gets the device.</summary>
    public GraphicsDevice Device { get; internal set; } = null!;

    /// <summary>Gets the commands of the frame.</summary>
    public CommandBuffer Commands { get; internal set; } = null!;

    /// <summary>Gets the view drawn: its camera, matrices, items, and sizes.</summary>
    public RenderView View { get; internal set; } = null!;

    /// <summary>Gets the name of the pass.</summary>
    public string PassName { get; internal set; } = string.Empty;

    /// <summary>Gets the framebuffer of a raster pass, whose formats a graphics pipeline must match; <see langword="null"/> for a compute pass.</summary>
    public Framebuffer? Framebuffer { get; internal set; }

    /// <summary>Gets the number of the frame.</summary>
    public ulong Frame { get; internal set; }

    /// <summary>Gets the slot of the frame in flight, for resources kept once per slot.</summary>
    public int FrameIndex => Device.FrameIndex;

    /// <summary>Gets the texture behind a texture of the graph.</summary>
    /// <param name="texture">A texture that the pass declared.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentException">The texture is the screen, or not of this graph.</exception>
    [HotPath]
    public Texture GetTexture(RenderGraphTexture texture) => _graph!.GetTexture(texture);

    /// <summary>Gets the buffer behind a buffer of the graph.</summary>
    /// <param name="buffer">A buffer that the pass declared.</param>
    /// <returns>The buffer.</returns>
    /// <exception cref="ArgumentException">The buffer is not of this graph.</exception>
    [HotPath]
    public GpuBuffer GetBuffer(RenderGraphBuffer buffer) => _graph!.GetBuffer(buffer);

    [HotPath]
    internal void SetGraph(CompiledGraph graph) => _graph = graph;
}

/// <summary>
/// A render pipeline: it decides what a frame looks like, by declaring the
/// passes of the render graph of a view. A module registers it with
/// <see cref="IRenderer.RegisterPipeline"/>.
/// </summary>
public interface IRenderPipeline
{
    /// <summary>Gets the name of the pipeline, unique among the pipelines registered, which a view names in <see cref="RenderView.Pipeline"/>.</summary>
    string Name { get; }

    /// <summary>Gets the graphics features the pipeline needs; checked when it is registered.</summary>
    GraphicsFeatures RequiredFeatures => GraphicsFeatures.None;

    /// <summary>
    /// Declares the passes of a view, with their resources, in the order they
    /// run, and the insertion points where modules add theirs. Called when the
    /// graph is built again: for the first frame, and after a change of the
    /// size, target, or pipeline of the view, of the registered insertions, or
    /// a call to <see cref="IRenderer.RebuildGraph"/>.
    /// </summary>
    /// <param name="graph">The graph of the view.</param>
    void Build(RenderGraphBuilder graph);
}

/// <summary>A named place of a render graph where modules add passes, with the textures they draw to.</summary>
/// <param name="Name">The name, such as <see cref="RenderInsertionPoints.Ui"/>.</param>
/// <param name="Color">The color texture at that point.</param>
/// <param name="Depth">The depth texture at that point, or the default value when there is none.</param>
public readonly record struct RenderInsertionPoint(string Name, RenderGraphTexture Color, RenderGraphTexture Depth);

/// <summary>The insertion points that pipelines declare, in the order of a frame.</summary>
public static class RenderInsertionPoints
{
    /// <summary>After opaque and cut-out geometry, before the sky and transparent geometry: the scene color and depth, in the internal format of the pipeline.</summary>
    public const string AfterOpaque = "AfterOpaque";

    /// <summary>After all the geometry, before post-processing: the scene color and depth. Debug drawing goes here.</summary>
    public const string BeforePostProcess = "BeforePostProcess";

    /// <summary>After post-processing, on the target of the view, without depth: the user interface. Engines of user interface order themselves here.</summary>
    public const string Ui = "Ui";
}

/// <summary>
/// Passes that a module adds at an insertion point of every pipeline, such as
/// the user interface or debug drawing. A module registers it with
/// <see cref="IRenderer.Insert"/>.
/// </summary>
public interface IRenderInsertion
{
    /// <summary>Declares the passes of the module at the point, when the graph is built again.</summary>
    /// <param name="graph">The graph of the view.</param>
    /// <param name="point">The point, with the textures to draw to.</param>
    void Build(RenderGraphBuilder graph, in RenderInsertionPoint point);
}

/// <summary>What the last build of a render graph produced, for tests and tools.</summary>
/// <param name="Pipeline">The name of the pipeline.</param>
/// <param name="Passes">The passes that run, in order.</param>
/// <param name="CulledPasses">The passes removed because nothing uses their result.</param>
/// <param name="TransientTextures">The number of transient textures declared by passes that run.</param>
/// <param name="PhysicalTextures">The number of textures created for them, after sharing.</param>
/// <param name="TransientBuffers">The number of transient buffers declared by passes that run.</param>
/// <param name="PhysicalBuffers">The number of buffers created for them, after sharing.</param>
/// <param name="Barriers">The number of barriers inserted before passes.</param>
public sealed record RenderGraphStatistics(
    string Pipeline,
    IReadOnlyList<string> Passes,
    IReadOnlyList<string> CulledPasses,
    int TransientTextures,
    int PhysicalTextures,
    int TransientBuffers,
    int PhysicalBuffers,
    int Barriers);

/// <summary>
/// A render graph that cannot run: a pass reads what no earlier pass writes,
/// uses the screen with other attachments, or needs a graphics feature the
/// device lacks. nRenderer logs it as an error, with the pass and its module,
/// and draws nothing until the graph is built again.
/// </summary>
public sealed class RenderGraphException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">The message.</param>
    public RenderGraphException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception for a pass.</summary>
    /// <param name="message">The message.</param>
    /// <param name="pass">The name of the pass.</param>
    /// <param name="moduleId">The identifier of the module that declared the pass.</param>
    /// <param name="inner">The exception that caused it, or <see langword="null"/>.</param>
    public RenderGraphException(string message, string pass, string moduleId, Exception? inner = null)
        : base(message, inner)
    {
        Pass = pass;
        ModuleId = moduleId;
    }

    /// <summary>Gets the name of the pass, or <see langword="null"/>.</summary>
    public string? Pass { get; }

    /// <summary>Gets the identifier of the module that declared the pass, or <see langword="null"/>.</summary>
    public string? ModuleId { get; }
}
