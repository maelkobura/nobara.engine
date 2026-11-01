// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>
/// Declares the render graph of a view: its resources and its passes, in the
/// order they run. A pipeline receives it in
/// <see cref="IRenderPipeline.Build"/>, and modules in
/// <see cref="IRenderInsertion.Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each pass declares what it reads and writes. When the graph is built,
/// nRenderer removes the passes whose result nothing uses (a pass that writes
/// the target of the view, an imported resource, or that calls
/// <see cref="RenderPassBuilder.KeepAlive"/> always runs), creates the
/// transient resources, and shares one texture or buffer between transient
/// resources of the same description that no pass uses at the same time. It
/// inserts a barrier before a pass that uses what an earlier pass wrote as
/// storage.
/// </para>
/// <para>
/// The target of a view on the screen is the back buffer of the window: a pass
/// that draws to <see cref="Target"/> draws to it alone, with
/// <see cref="TargetDepth"/> or no depth, and no pass reads it.
/// </para>
/// </remarks>
public sealed class RenderGraphBuilder
{
    private readonly Func<RenderGraphBuilder, RenderInsertionPoint, int> _insert;

    internal RenderGraphBuilder(RenderView view, GraphicsDevice device, ModuleContext owner, Func<RenderGraphBuilder, RenderInsertionPoint, int> insert)
    {
        View = view;
        Capabilities = device.Capabilities;
        Owner = owner;
        _insert = insert;
        if (view.TargetTexture is { } color)
        {
            Target = Add(new GraphResource("target", isTexture: true) { External = true, Imported = color, Format = color.Format, IsViewTarget = true });
            if (view.TargetDepthTexture is { } depth)
            {
                TargetDepth = Add(new GraphResource("target depth", isTexture: true) { External = true, Imported = depth, Format = depth.Format, IsViewTarget = true });
            }
        }
        else
        {
            Swapchain swapchain = device.Swapchain;
            Target = Add(new GraphResource("screen", isTexture: true) { External = true, Screen = true, Format = swapchain.ColorFormat, IsViewTarget = true });
            TargetDepth = Add(new GraphResource("screen depth", isTexture: true) { External = true, Screen = true, Format = swapchain.DepthFormat, IsViewTarget = true });
        }
    }

    /// <summary>Gets the view whose graph is built.</summary>
    public RenderView View { get; }

    /// <summary>Gets what the device supports.</summary>
    public GraphicsCapabilities Capabilities { get; }

    /// <summary>Gets the color target of the view: the screen, or the texture of <see cref="RenderView.SetTarget"/>.</summary>
    public RenderGraphTexture Target { get; }

    /// <summary>Gets the depth of the target: the depth of the screen, the depth texture of the view, or the default value when the texture target has none.</summary>
    public RenderGraphTexture TargetDepth { get; }

    /// <summary>Gets whether the view draws to the screen.</summary>
    public bool TargetIsScreen => View.TargetTexture is null;

    /// <summary>Gets the insertion points declared so far, in order.</summary>
    public IReadOnlyList<string> InsertionPoints => DeclaredPoints;

    internal List<GraphResource> Resources { get; } = [];

    internal List<GraphPass> Passes { get; } = [];

    internal List<string> DeclaredPoints { get; } = [];

    internal ModuleContext Owner { get; set; }

    /// <summary>Gets the format of a texture of the graph.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>The format.</returns>
    /// <exception cref="ArgumentException">The texture is not of this graph.</exception>
    public PixelFormat GetFormat(RenderGraphTexture texture) => Texture(texture, nameof(texture)).Format;

    /// <summary>Declares a transient texture, created by the graph.</summary>
    /// <param name="name">The name, for messages and capture tools.</param>
    /// <param name="description">The format and size.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentException">The format is missing, the scale or the fixed size is not positive, or the mip levels are fewer than 1.</exception>
    public RenderGraphTexture CreateTexture(string name, in RenderGraphTextureDescription description)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        bool sized = description.Size == RenderTextureSize.Fixed ? description.Width > 0 && description.Height > 0 : description.Scale > 0;
        if (description.Format == PixelFormat.None || !sized || description.MipLevels < 1)
        {
            throw new ArgumentException($"Transient texture '{name}' needs a format, a positive scale or fixed size, and at least one mip level.", nameof(description));
        }

        return Add(new GraphResource(name, isTexture: true) { Description = description, Format = description.Format });
    }

    /// <summary>Declares a transient buffer, created by the graph; its usage follows the accesses of the passes.</summary>
    /// <param name="name">The name, for messages and capture tools.</param>
    /// <param name="size">The size, in bytes.</param>
    /// <returns>The buffer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
    public RenderGraphBuffer CreateBuffer(string name, long size)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        Resources.Add(new GraphResource(name, isTexture: false) { BufferSize = size });
        return new RenderGraphBuffer(Resources.Count);
    }

    /// <summary>Declares a texture that lives outside the graph, such as one that a module keeps from frame to frame. A pass that writes it always runs.</summary>
    /// <param name="name">The name, for messages.</param>
    /// <param name="texture">The texture.</param>
    /// <returns>The texture of the graph.</returns>
    public RenderGraphTexture ImportTexture(string name, Texture texture)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(texture);
        return Add(new GraphResource(name, isTexture: true) { External = true, Imported = texture, Format = texture.Format });
    }

    /// <summary>Declares a buffer that lives outside the graph. A pass that writes it always runs.</summary>
    /// <param name="name">The name, for messages.</param>
    /// <param name="buffer">The buffer.</param>
    /// <returns>The buffer of the graph.</returns>
    public RenderGraphBuffer ImportBuffer(string name, GpuBuffer buffer)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(buffer);
        Resources.Add(new GraphResource(name, isTexture: false) { External = true, ImportedBuffer = buffer, BufferSize = buffer.Size });
        return new RenderGraphBuffer(Resources.Count);
    }

    /// <summary>Adds a raster pass, which draws inside a render pass on the attachments it declares.</summary>
    /// <param name="name">The name, unique in the graph, for messages and capture tools.</param>
    /// <param name="pass">The code of the pass.</param>
    /// <returns>The declarations of the pass.</returns>
    public RenderPassBuilder AddRasterPass(string name, IRenderPass pass) => AddPass(name, pass, compute: false);

    /// <summary>Adds a compute pass, which runs outside render passes. Requires <see cref="GraphicsFeatures.Compute"/>.</summary>
    /// <param name="name">The name, unique in the graph, for messages and capture tools.</param>
    /// <param name="pass">The code of the pass.</param>
    /// <returns>The declarations of the pass.</returns>
    public RenderPassBuilder AddComputePass(string name, IRenderPass pass) => AddPass(name, pass, compute: true);

    /// <summary>
    /// Declares an insertion point: the passes that modules insert at that
    /// name are added here, in their order. A pipeline declares each point
    /// once; the names of <see cref="RenderInsertionPoints"/> are the usual ones.
    /// </summary>
    /// <param name="name">The name of the point.</param>
    /// <param name="color">The color texture at that point.</param>
    /// <param name="depth">The depth texture at that point, or the default value.</param>
    /// <returns>The number of insertions whose passes were added.</returns>
    /// <exception cref="ArgumentException">The point is declared already, or a texture is not of this graph.</exception>
    public int InsertionPoint(string name, RenderGraphTexture color, RenderGraphTexture depth = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Texture(color, nameof(color));
        if (depth.IsValid)
        {
            Texture(depth, nameof(depth));
        }

        if (DeclaredPoints.Contains(name))
        {
            throw new ArgumentException($"Insertion point '{name}' is declared twice in the graph of {View}.", nameof(name));
        }

        DeclaredPoints.Add(name);
        return _insert(this, new RenderInsertionPoint(name, color, depth));
    }

    internal GraphResource Texture(RenderGraphTexture texture, string parameter)
    {
        if ((uint)(texture.Index - 1) >= (uint)Resources.Count || !Resources[texture.Index - 1].IsTexture)
        {
            throw new ArgumentException($"{texture} is not a texture of the graph of {View}.", parameter);
        }

        return Resources[texture.Index - 1];
    }

    internal GraphResource Buffer(RenderGraphBuffer buffer, string parameter)
    {
        if ((uint)(buffer.Index - 1) >= (uint)Resources.Count || Resources[buffer.Index - 1].IsTexture)
        {
            throw new ArgumentException($"{buffer} is not a buffer of the graph of {View}.", parameter);
        }

        return Resources[buffer.Index - 1];
    }

    /// <summary>Removes what was declared after a point, when the code that declared it failed.</summary>
    internal void Truncate(int passes, int resources, int points)
    {
        Passes.RemoveRange(passes, Passes.Count - passes);
        Resources.RemoveRange(resources, Resources.Count - resources);
        DeclaredPoints.RemoveRange(points, DeclaredPoints.Count - points);
    }

    private RenderGraphTexture Add(GraphResource resource)
    {
        Resources.Add(resource);
        return new RenderGraphTexture(Resources.Count);
    }

    private RenderPassBuilder AddPass(string name, IRenderPass pass, bool compute)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(pass);
        foreach (GraphPass other in Passes)
        {
            if (other.Name == name)
            {
                throw new ArgumentException($"The graph of {View} has two passes named '{name}'.", nameof(name));
            }
        }

        var declared = new GraphPass(name, pass, compute, Owner);
        if (compute)
        {
            declared.Required |= GraphicsFeatures.Compute;
        }

        Passes.Add(declared);
        return new RenderPassBuilder(this, declared);
    }
}

/// <summary>Declares what a pass of the render graph reads and writes. Each method returns the builder, to chain declarations.</summary>
public sealed class RenderPassBuilder
{
    private readonly RenderGraphBuilder _graph;
    private readonly GraphPass _pass;

    internal RenderPassBuilder(RenderGraphBuilder graph, GraphPass pass)
    {
        _graph = graph;
        _pass = pass;
    }

    /// <summary>Draws to a color attachment, after the ones declared before. Every color attachment of a pass begins the same way.</summary>
    /// <param name="texture">A color texture.</param>
    /// <param name="load">What happens to its content when the pass begins: kept (and read), cleared, or ignored.</param>
    /// <param name="clearColor">The clear color, with <see cref="LoadAction.Clear"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The pass is a compute pass.</exception>
    /// <exception cref="ArgumentException">The texture has a depth format, or is not of the graph.</exception>
    public RenderPassBuilder Color(RenderGraphTexture texture, LoadAction load = LoadAction.Load, ColorF clearColor = default)
    {
        CheckRaster();
        GraphResource resource = _graph.Texture(texture, nameof(texture));
        if (PixelFormats.IsDepth(resource.Format))
        {
            throw new ArgumentException($"Pass '{_pass.Name}': '{resource.Name}' has the depth format {resource.Format} and cannot be a color attachment.", nameof(texture));
        }

        _pass.Colors.Add(new Attachment(texture.Index - 1, load, clearColor, 0));
        _pass.Accesses.Add(new Access(texture.Index - 1, AccessKind.ColorAttachment, Read: load == LoadAction.Load, Write: true));
        return this;
    }

    /// <summary>Uses a depth attachment.</summary>
    /// <param name="texture">A depth texture.</param>
    /// <param name="load">What happens to its content when the pass begins.</param>
    /// <param name="clearDepth">The clear depth, with <see cref="LoadAction.Clear"/>; <see cref="Camera.ClearDepth"/> by default, for reversed depth.</param>
    /// <param name="write"><see langword="true"/> when the pass writes depth; <see langword="false"/> when it only tests against it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The pass is a compute pass, or has a depth attachment already.</exception>
    /// <exception cref="ArgumentException">The texture has a color format, or is not of the graph.</exception>
    public RenderPassBuilder Depth(RenderGraphTexture texture, LoadAction load = LoadAction.Load, float clearDepth = Camera.ClearDepth, bool write = true)
    {
        CheckRaster();
        GraphResource resource = _graph.Texture(texture, nameof(texture));
        if (!PixelFormats.IsDepth(resource.Format))
        {
            throw new ArgumentException($"Pass '{_pass.Name}': '{resource.Name}' has the color format {resource.Format} and cannot be a depth attachment.", nameof(texture));
        }

        if (_pass.Depth is not null)
        {
            throw new InvalidOperationException($"Pass '{_pass.Name}' has one depth attachment at most.");
        }

        _pass.Depth = new Attachment(texture.Index - 1, load, default, clearDepth);
        _pass.Accesses.Add(new Access(texture.Index - 1, AccessKind.DepthAttachment, Read: load == LoadAction.Load || !write, Write: write));
        return this;
    }

    /// <summary>Samples a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>This builder.</returns>
    public RenderPassBuilder Read(RenderGraphTexture texture) => Use(texture, AccessKind.Sampled);

    /// <summary>Reads a texture as a storage texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>This builder.</returns>
    public RenderPassBuilder ReadStorage(RenderGraphTexture texture) => Use(texture, AccessKind.StorageTexture);

    /// <summary>Writes a texture as a storage texture, such as from a compute shader. A pass that also reads what an earlier pass wrote there declares <see cref="ReadStorage"/> too.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>This builder.</returns>
    public RenderPassBuilder WriteStorage(RenderGraphTexture texture)
    {
        _graph.Texture(texture, nameof(texture));
        _pass.Accesses.Add(new Access(texture.Index - 1, AccessKind.StorageTexture, Read: false, Write: true));
        return this;
    }

    /// <summary>Reads a buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="access">How the pass reads it.</param>
    /// <returns>This builder.</returns>
    public RenderPassBuilder Read(RenderGraphBuffer buffer, RenderBufferAccess access)
    {
        _graph.Buffer(buffer, nameof(buffer));
        AccessKind kind = access switch
        {
            RenderBufferAccess.Vertex => AccessKind.Vertex,
            RenderBufferAccess.Index => AccessKind.Index,
            RenderBufferAccess.Uniform => AccessKind.Uniform,
            RenderBufferAccess.Storage => AccessKind.StorageBuffer,
            RenderBufferAccess.Indirect => AccessKind.Indirect,
            _ => AccessKind.Copy,
        };
        _pass.Accesses.Add(new Access(buffer.Index - 1, kind, Read: true, Write: false));
        return this;
    }

    /// <summary>Writes a buffer as a storage buffer, or with <see cref="CommandBuffer.UpdateBuffer"/> in <see cref="IRenderPass.Prepare"/>. A pass that also reads what an earlier pass wrote there declares <see cref="Read(RenderGraphBuffer, RenderBufferAccess)"/> too.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <returns>This builder.</returns>
    public RenderPassBuilder Write(RenderGraphBuffer buffer)
    {
        _graph.Buffer(buffer, nameof(buffer));
        _pass.Accesses.Add(new Access(buffer.Index - 1, AccessKind.StorageBuffer, Read: false, Write: true));
        return this;
    }

    /// <summary>Declares graphics features the pass needs; the graph does not build on a device without them.</summary>
    /// <param name="features">The features.</param>
    /// <returns>This builder.</returns>
    public RenderPassBuilder Require(GraphicsFeatures features)
    {
        _pass.Required |= features;
        return this;
    }

    /// <summary>Keeps the pass even when nothing uses its result, for a pass with effects outside the graph.</summary>
    /// <returns>This builder.</returns>
    public RenderPassBuilder KeepAlive()
    {
        _pass.KeepAlive = true;
        return this;
    }

    private RenderPassBuilder Use(RenderGraphTexture texture, AccessKind kind)
    {
        _graph.Texture(texture, nameof(texture));
        _pass.Accesses.Add(new Access(texture.Index - 1, kind, Read: true, Write: false));
        return this;
    }

    private void CheckRaster()
    {
        if (_pass.Compute)
        {
            throw new InvalidOperationException($"Pass '{_pass.Name}' is a compute pass: it has no attachments.");
        }
    }
}

/// <summary>How a pass uses a resource.</summary>
internal enum AccessKind
{
    Sampled,
    StorageTexture,
    ColorAttachment,
    DepthAttachment,
    Vertex,
    Index,
    Uniform,
    StorageBuffer,
    Indirect,
    Copy,
}

/// <summary>A use of a resource by a pass.</summary>
internal readonly record struct Access(int Resource, AccessKind Kind, bool Read, bool Write);

/// <summary>An attachment of a raster pass.</summary>
internal readonly record struct Attachment(int Resource, LoadAction Load, ColorF ClearColor, float ClearDepth);

/// <summary>A resource declared in a graph.</summary>
internal sealed class GraphResource(string name, bool isTexture)
{
    public string Name { get; } = name;

    public bool IsTexture { get; } = isTexture;

    // Imported, the view target, or the screen: its content lives on, so a pass that writes it runs.
    public bool External { get; init; }

    public bool Screen { get; init; }

    public bool IsViewTarget { get; init; }

    public Texture? Imported { get; init; }

    public GpuBuffer? ImportedBuffer { get; init; }

    public PixelFormat Format { get; init; }

    public RenderGraphTextureDescription Description { get; init; }

    public long BufferSize { get; init; }
}

/// <summary>A pass declared in a graph.</summary>
internal sealed class GraphPass(string name, IRenderPass pass, bool compute, ModuleContext owner)
{
    public string Name { get; } = name;

    public IRenderPass Pass { get; } = pass;

    public bool Compute { get; } = compute;

    public ModuleContext Owner { get; } = owner;

    public List<Access> Accesses { get; } = [];

    public List<Attachment> Colors { get; } = [];

    public Attachment? Depth { get; set; }

    public GraphicsFeatures Required { get; set; }

    public bool KeepAlive { get; set; }
}
