// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>
/// An object of a <see cref="GraphicsDevice"/> that lives on the GPU.
/// Disposing it retires it: the device destroys it once no frame in flight
/// can still use it.
/// </summary>
/// <remarks>Create, use, and dispose resources on the loop thread of the client context.</remarks>
public abstract class GraphicsResource : IDisposable
{
    /// <summary>Initializes the resource.</summary>
    /// <param name="device">The device that owns it.</param>
    /// <param name="name">The debug name shown by capture tools, or <see langword="null"/>.</param>
    protected GraphicsResource(GraphicsDevice device, string? name)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        Name = name;
    }

    /// <summary>Gets the device that owns the resource.</summary>
    public GraphicsDevice Device { get; }

    /// <summary>Gets the debug name, or <see langword="null"/>.</summary>
    public string? Name { get; }

    /// <summary>Gets whether the resource was disposed: it can no longer be used in new commands.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Gets whether the device destroyed the GPU object behind the resource.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>Retires the resource. The device destroys it after the frames in flight that may use it.</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Device.Retire(this);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public override string ToString() => Name is null ? GetType().Name : $"{GetType().Name} '{Name}'";

    /// <summary>Destroys the GPU object now; the device calls it when no frame in flight uses the resource.</summary>
    [HotPath]
    internal void Destroy()
    {
        if (!IsDestroyed)
        {
            IsDestroyed = true;
            OnDestroy();
        }
    }

    /// <summary>Frees the GPU object behind the resource. Called once, on the loop thread.</summary>
    [HotPath]
    protected abstract void OnDestroy();
}

/// <summary>
/// A large block of GPU memory that the device sub-allocates buffers from.
/// A backend implements it with one buffer object of its API.
/// </summary>
public abstract class GpuMemoryBlock : GraphicsResource
{
    /// <summary>Initializes the block.</summary>
    /// <param name="device">The device.</param>
    /// <param name="size">The size, in bytes.</param>
    /// <param name="name">The debug name.</param>
    protected GpuMemoryBlock(GraphicsDevice device, long size, string? name)
        : base(device, name)
    {
        Size = size;
    }

    /// <summary>Gets the size, in bytes.</summary>
    public long Size { get; }
}

/// <summary>
/// A buffer: a range of a <see cref="GpuMemoryBlock"/>. Small buffers share
/// blocks; a large one has a block of its own.
/// </summary>
public sealed class GpuBuffer : GraphicsResource
{
    private readonly GpuMemoryAllocator _allocator;

    internal GpuBuffer(GraphicsDevice device, in BufferDescription description, GpuMemoryAllocator allocator, GpuAllocation allocation)
        : base(device, description.Name)
    {
        _allocator = allocator;
        Allocation = allocation;
        Size = description.Size;
        Usage = description.Usage;
    }

    /// <summary>Gets the size, in bytes.</summary>
    public long Size { get; }

    /// <summary>Gets how the buffer is used.</summary>
    public BufferUsage Usage { get; }

    /// <summary>Gets the block that holds the buffer, for the backend.</summary>
    public GpuMemoryBlock Block
    {
        [HotPath]
        get => Allocation.Block;
    }

    /// <summary>Gets the offset of the buffer in <see cref="Block"/>, in bytes, for the backend.</summary>
    public long Offset
    {
        [HotPath]
        get => Allocation.Offset;
    }

    internal GpuAllocation Allocation { get; }

    /// <inheritdoc/>
    [HotPath]
    protected override void OnDestroy() => _allocator.Free(Allocation);
}

/// <summary>A texture: 2D, 2D array, or 3D. A backend derives it.</summary>
public abstract class Texture : GraphicsResource
{
    /// <summary>Initializes the texture from its description.</summary>
    /// <param name="device">The device.</param>
    /// <param name="description">The validated description.</param>
    protected Texture(GraphicsDevice device, in TextureDescription description)
        : base(device, description.Name)
    {
        Dimension = description.Dimension;
        Format = description.Format;
        Width = description.Width;
        Height = description.Height;
        DepthOrLayers = description.DepthOrLayers;
        MipLevels = description.MipLevels;
        Usage = description.Usage;
    }

    /// <summary>Gets the shape.</summary>
    public TextureDimension Dimension { get; }

    /// <summary>Gets the texel format.</summary>
    public PixelFormat Format { get; }

    /// <summary>Gets the width of level 0, in texels.</summary>
    public int Width { get; }

    /// <summary>Gets the height of level 0, in texels.</summary>
    public int Height { get; }

    /// <summary>Gets the depth of a 3D texture, or the layer count of an array; 1 for a 2D texture.</summary>
    public int DepthOrLayers { get; }

    /// <summary>Gets the number of mip levels.</summary>
    public int MipLevels { get; }

    /// <summary>Gets how the texture is used.</summary>
    public TextureUsage Usage { get; }
}

/// <summary>A sampler. A backend derives it.</summary>
public abstract class Sampler : GraphicsResource
{
    /// <summary>Initializes the sampler.</summary>
    /// <param name="device">The device.</param>
    /// <param name="description">The validated description.</param>
    protected Sampler(GraphicsDevice device, in SamplerDescription description)
        : base(device, description.Name)
    {
        Description = description;
    }

    /// <summary>Gets the description.</summary>
    public SamplerDescription Description { get; }
}

/// <summary>A shader: one stage, loaded from SPIR-V. A backend derives it.</summary>
public abstract class Shader : GraphicsResource
{
    /// <summary>Initializes the shader.</summary>
    /// <param name="device">The device.</param>
    /// <param name="description">The validated description.</param>
    protected Shader(GraphicsDevice device, in ShaderDescription description)
        : base(device, description.Name)
    {
        Stage = description.Stage;
        EntryPoint = description.EntryPoint;
    }

    /// <summary>Gets the stage.</summary>
    public ShaderStages Stage { get; }

    /// <summary>Gets the name of the entry point.</summary>
    public string EntryPoint { get; }
}

/// <summary>The layout of a resource set: which resources, of which kinds, at which bindings.</summary>
/// <remarks>A backend may derive it to keep an object of its API; the OpenGL backend does not.</remarks>
public class ResourceLayout : GraphicsResource
{
    /// <summary>Initializes the layout.</summary>
    /// <param name="device">The device.</param>
    /// <param name="description">The validated description.</param>
    protected internal ResourceLayout(GraphicsDevice device, in ResourceLayoutDescription description)
        : base(device, description.Name)
    {
        EntryArray = [.. description.Entries];
    }

    /// <summary>Gets the resources of the layout.</summary>
    public IReadOnlyList<ResourceLayoutEntry> Entries => EntryArray;

    internal ResourceLayoutEntry[] EntryArray { get; }

    /// <inheritdoc/>
    [HotPath]
    protected override void OnDestroy()
    {
    }
}

/// <summary>Resources bound together, one per entry of a <see cref="ResourceLayout"/>.</summary>
/// <remarks>A backend may derive it to keep an object of its API; the OpenGL backend does not.</remarks>
public class ResourceSet : GraphicsResource
{
    /// <summary>Initializes the set.</summary>
    /// <param name="device">The device.</param>
    /// <param name="description">The validated description.</param>
    protected internal ResourceSet(GraphicsDevice device, in ResourceSetDescription description)
        : base(device, description.Name)
    {
        Layout = description.Layout;
        ResourceArray = [.. description.Resources];
    }

    /// <summary>Gets the layout.</summary>
    public ResourceLayout Layout { get; }

    /// <summary>Gets the resources, in the order of the entries of the layout.</summary>
    public IReadOnlyList<BoundResource> Resources => ResourceArray;

    /// <summary>Gets the resources as a span, for the backend: reading it allocates nothing.</summary>
    public ReadOnlySpan<BoundResource> ResourceSpan => ResourceArray;

    internal BoundResource[] ResourceArray { get; }

    /// <inheritdoc/>
    [HotPath]
    protected override void OnDestroy()
    {
    }
}

/// <summary>A pipeline: immutable state bound by a command buffer.</summary>
public abstract class Pipeline : GraphicsResource
{
    /// <summary>Initializes the pipeline.</summary>
    /// <param name="device">The device.</param>
    /// <param name="name">The debug name.</param>
    /// <param name="layouts">The resource layouts, by slot.</param>
    protected Pipeline(GraphicsDevice device, string? name, IReadOnlyList<ResourceLayout> layouts)
        : base(device, name)
    {
        LayoutArray = [.. layouts];
    }

    /// <summary>Gets the resource layouts, by slot.</summary>
    public IReadOnlyList<ResourceLayout> ResourceLayouts => LayoutArray;

    internal ResourceLayout[] LayoutArray { get; }
}

/// <summary>An immutable graphics pipeline. A backend derives it.</summary>
public abstract class GraphicsPipeline : Pipeline
{
    /// <summary>Initializes the pipeline.</summary>
    /// <param name="device">The device.</param>
    /// <param name="description">The validated description.</param>
    protected GraphicsPipeline(GraphicsDevice device, in GraphicsPipelineDescription description)
        : base(device, description.Name, description.ResourceLayouts)
    {
        Description = description;
        ColorFormats = [.. description.Outputs.Colors];
        DepthFormat = description.Outputs.Depth;
    }

    /// <summary>Gets the description.</summary>
    public GraphicsPipelineDescription Description { get; }

    internal PixelFormat[] ColorFormats { get; }

    internal PixelFormat DepthFormat { get; }
}

/// <summary>An immutable compute pipeline. A backend derives it.</summary>
public abstract class ComputePipeline : Pipeline
{
    /// <summary>Initializes the pipeline.</summary>
    /// <param name="device">The device.</param>
    /// <param name="description">The validated description.</param>
    protected ComputePipeline(GraphicsDevice device, in ComputePipelineDescription description)
        : base(device, description.Name, description.ResourceLayouts)
    {
        Shader = description.Shader;
    }

    /// <summary>Gets the compute shader.</summary>
    public Shader Shader { get; }
}

/// <summary>
/// The attachments a render pass draws to: textures, or the back buffer of
/// the <see cref="Swapchain"/>. A backend derives it.
/// </summary>
public abstract class Framebuffer : GraphicsResource
{
    /// <summary>Initializes a framebuffer.</summary>
    /// <param name="device">The device.</param>
    /// <param name="name">The debug name.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="colors">The formats of the color attachments.</param>
    /// <param name="depth">The format of the depth attachment, or <see cref="PixelFormat.None"/>.</param>
    /// <param name="isSwapchain"><see langword="true"/> for the back buffer of the swapchain.</param>
    protected Framebuffer(GraphicsDevice device, string? name, int width, int height, IReadOnlyList<PixelFormat> colors, PixelFormat depth, bool isSwapchain)
        : base(device, name)
    {
        Width = width;
        Height = height;
        ColorFormatArray = [.. colors];
        DepthFormat = depth;
        IsSwapchain = isSwapchain;
    }

    /// <summary>Gets the width, in pixels.</summary>
    public int Width { get; internal set; }

    /// <summary>Gets the height, in pixels.</summary>
    public int Height { get; internal set; }

    /// <summary>Gets the formats of the color attachments.</summary>
    public IReadOnlyList<PixelFormat> ColorFormats => ColorFormatArray;

    /// <summary>Gets the format of the depth attachment, or <see cref="PixelFormat.None"/>.</summary>
    public PixelFormat DepthFormat { get; }

    /// <summary>Gets whether this is the back buffer of the swapchain.</summary>
    public bool IsSwapchain { get; }

    internal PixelFormat[] ColorFormatArray { get; }
}
