// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>How a buffer is used. A buffer can be used in several ways at once.</summary>
[Flags]
public enum BufferUsage
{
    /// <summary>No use declared; invalid.</summary>
    None = 0,

    /// <summary>Vertices read by a vertex layout.</summary>
    Vertex = 1,

    /// <summary>Indices of indexed draws.</summary>
    Index = 2,

    /// <summary>A uniform buffer, read by shaders.</summary>
    Uniform = 4,

    /// <summary>A storage buffer, read and written by shaders. Requires <see cref="GraphicsFeatures.StorageBuffers"/>.</summary>
    Storage = 8,

    /// <summary>The arguments of indirect draws and dispatches. Requires <see cref="GraphicsFeatures.DrawIndirect"/>.</summary>
    Indirect = 16,
}

/// <summary>Describes a buffer.</summary>
/// <param name="Size">The size, in bytes; more than 0.</param>
/// <param name="Usage">How the buffer is used.</param>
public readonly record struct BufferDescription(long Size, BufferUsage Usage)
{
    /// <summary>Gets the debug name shown by capture tools, or <see langword="null"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>The shape of a texture.</summary>
public enum TextureDimension
{
    /// <summary>A two-dimensional texture.</summary>
    Texture2D,

    /// <summary>An array of two-dimensional textures of the same size, chosen by layer in the shader.</summary>
    Texture2DArray,

    /// <summary>A three-dimensional texture.</summary>
    Texture3D,
}

/// <summary>How a texture is used. A texture can be used in several ways at once.</summary>
[Flags]
public enum TextureUsage
{
    /// <summary>No use declared; invalid.</summary>
    None = 0,

    /// <summary>Sampled by shaders.</summary>
    Sampled = 1,

    /// <summary>Read and written by shaders as a storage image. Requires <see cref="GraphicsFeatures.Compute"/>.</summary>
    Storage = 2,

    /// <summary>A color attachment of a framebuffer.</summary>
    RenderTarget = 4,

    /// <summary>The depth attachment of a framebuffer; requires a depth format.</summary>
    DepthStencil = 8,
}

/// <summary>Describes a texture.</summary>
/// <param name="Dimension">The shape.</param>
/// <param name="Format">The texel format.</param>
/// <param name="Width">The width, in texels; more than 0.</param>
/// <param name="Height">The height, in texels; more than 0.</param>
/// <param name="DepthOrLayers">The depth of a 3D texture or the layer count of an array; 1 for a 2D texture.</param>
/// <param name="MipLevels">The number of mip levels, from 1 to the full chain.</param>
/// <param name="Usage">How the texture is used.</param>
public readonly record struct TextureDescription(
    TextureDimension Dimension,
    PixelFormat Format,
    int Width,
    int Height,
    int DepthOrLayers,
    int MipLevels,
    TextureUsage Usage)
{
    /// <summary>Gets the debug name shown by capture tools, or <see langword="null"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Describes a 2D texture with one mip level.</summary>
    /// <param name="format">The texel format.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="usage">How the texture is used.</param>
    /// <returns>The description.</returns>
    public static TextureDescription Texture2D(PixelFormat format, int width, int height, TextureUsage usage) =>
        new(TextureDimension.Texture2D, format, width, height, 1, 1, usage);
}

/// <summary>How a sampler reads between texels.</summary>
public enum FilterMode
{
    /// <summary>The nearest texel.</summary>
    Nearest,

    /// <summary>A linear blend of the nearest texels.</summary>
    Linear,
}

/// <summary>What a sampler reads outside the texture.</summary>
public enum AddressMode
{
    /// <summary>The texture repeats.</summary>
    Repeat,

    /// <summary>The texture repeats, mirrored every other time.</summary>
    MirroredRepeat,

    /// <summary>The texel at the edge.</summary>
    ClampToEdge,

    /// <summary>Transparent black.</summary>
    ClampToBorder,
}

/// <summary>A comparison between two values, for depth tests and shadow samplers.</summary>
public enum CompareFunction
{
    /// <summary>Never passes.</summary>
    Never,

    /// <summary>Passes when the new value is less.</summary>
    Less,

    /// <summary>Passes when the values are equal.</summary>
    Equal,

    /// <summary>Passes when the new value is less or equal.</summary>
    LessEqual,

    /// <summary>Passes when the new value is greater.</summary>
    Greater,

    /// <summary>Passes when the values differ.</summary>
    NotEqual,

    /// <summary>Passes when the new value is greater or equal.</summary>
    GreaterEqual,

    /// <summary>Always passes.</summary>
    Always,
}

/// <summary>Describes a sampler: how shaders read a texture.</summary>
public readonly record struct SamplerDescription()
{
    /// <summary>Gets the filter when the texture is magnified.</summary>
    public FilterMode MagFilter { get; init; } = FilterMode.Linear;

    /// <summary>Gets the filter when the texture is minified.</summary>
    public FilterMode MinFilter { get; init; } = FilterMode.Linear;

    /// <summary>Gets the filter between mip levels.</summary>
    public FilterMode MipFilter { get; init; } = FilterMode.Linear;

    /// <summary>Gets the address mode along the width.</summary>
    public AddressMode AddressU { get; init; } = AddressMode.Repeat;

    /// <summary>Gets the address mode along the height.</summary>
    public AddressMode AddressV { get; init; } = AddressMode.Repeat;

    /// <summary>Gets the address mode along the depth.</summary>
    public AddressMode AddressW { get; init; } = AddressMode.Repeat;

    /// <summary>Gets the highest anisotropy, from 1 (none) to <see cref="GraphicsCapabilities.MaxAnisotropy"/>.</summary>
    public float MaxAnisotropy { get; init; } = 1;

    /// <summary>Gets the comparison of a shadow sampler, or <see langword="null"/> for a plain sampler.</summary>
    public CompareFunction? Compare { get; init; }

    /// <summary>Gets the lowest mip level read.</summary>
    public float MinLod { get; init; }

    /// <summary>Gets the highest mip level read.</summary>
    public float MaxLod { get; init; } = 1000;

    /// <summary>Gets the debug name shown by capture tools, or <see langword="null"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Gets a sampler that reads the nearest texel and clamps to the edge.</summary>
    public static SamplerDescription Point => new()
    {
        MagFilter = FilterMode.Nearest,
        MinFilter = FilterMode.Nearest,
        MipFilter = FilterMode.Nearest,
        AddressU = AddressMode.ClampToEdge,
        AddressV = AddressMode.ClampToEdge,
        AddressW = AddressMode.ClampToEdge,
    };
}

/// <summary>A programmable stage of the GPU.</summary>
[Flags]
public enum ShaderStages
{
    /// <summary>No stage.</summary>
    None = 0,

    /// <summary>The vertex stage.</summary>
    Vertex = 1,

    /// <summary>The fragment stage.</summary>
    Fragment = 2,

    /// <summary>The compute stage. Requires <see cref="GraphicsFeatures.Compute"/>.</summary>
    Compute = 4,
}

/// <summary>Describes a shader: one stage, as SPIR-V.</summary>
/// <param name="Stage">The stage; exactly one.</param>
/// <param name="SpirV">The SPIR-V module, compiled ahead of time; its length is a multiple of 4.</param>
/// <param name="EntryPoint">The name of the entry point.</param>
/// <remarks>
/// Shaders use explicit locations and bindings: OpenGL reads no names from
/// SPIR-V. Bindings are numbered per kind of resource, as
/// <see cref="ResourceLayoutEntry.Binding"/> says.
/// </remarks>
public readonly record struct ShaderDescription(ShaderStages Stage, ReadOnlyMemory<byte> SpirV, string EntryPoint = "main")
{
    /// <summary>Gets the debug name shown by capture tools, or <see langword="null"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>The kind of a resource bound to shaders.</summary>
public enum ResourceKind
{
    /// <summary>A uniform buffer (<see cref="BufferUsage.Uniform"/>).</summary>
    UniformBuffer,

    /// <summary>A storage buffer that shaders only read (<see cref="BufferUsage.Storage"/>).</summary>
    ReadOnlyStorageBuffer,

    /// <summary>A storage buffer that shaders read and write (<see cref="BufferUsage.Storage"/>).</summary>
    ReadWriteStorageBuffer,

    /// <summary>A texture and the sampler that reads it, as one GLSL sampler.</summary>
    SampledTexture,

    /// <summary>A texture that shaders read and write as an image (<see cref="TextureUsage.Storage"/>), level 0.</summary>
    StorageTexture,
}

/// <summary>One resource of a resource layout.</summary>
/// <param name="Binding">The binding number in the shaders.</param>
/// <param name="Kind">The kind of resource.</param>
/// <param name="Stages">The stages that read it.</param>
/// <remarks>
/// Binding numbers are counted per class of resource, as in OpenGL: uniform
/// buffers, storage buffers, sampled textures, and storage textures each have
/// their own numbers. Two entries of the same class in the layouts of one
/// pipeline cannot share a number.
/// </remarks>
public readonly record struct ResourceLayoutEntry(int Binding, ResourceKind Kind, ShaderStages Stages);

/// <summary>Describes a resource layout: the resources that a resource set binds at once.</summary>
/// <param name="Entries">The resources.</param>
public readonly record struct ResourceLayoutDescription(IReadOnlyList<ResourceLayoutEntry> Entries)
{
    /// <summary>Gets the debug name, or <see langword="null"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>A resource of a resource set: a buffer range, or a texture and its sampler.</summary>
public readonly record struct BoundResource
{
    /// <summary>Gets the buffer, for a buffer entry.</summary>
    public GpuBuffer? Buffer { get; init; }

    /// <summary>Gets the first byte of the range, from the start of <see cref="Buffer"/>.</summary>
    public long Offset { get; init; }

    /// <summary>Gets the length of the range in bytes; 0 for the rest of the buffer.</summary>
    public long Size { get; init; }

    /// <summary>Gets the texture, for a texture entry.</summary>
    public Texture? Texture { get; init; }

    /// <summary>Gets the sampler, for a <see cref="ResourceKind.SampledTexture"/> entry.</summary>
    public Sampler? Sampler { get; init; }

    /// <summary>Binds a whole buffer, or a range of it.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The first byte of the range.</param>
    /// <param name="size">The length of the range; 0 for the rest of the buffer.</param>
    /// <returns>The resource.</returns>
    public static BoundResource Of(GpuBuffer buffer, long offset = 0, long size = 0) => new() { Buffer = buffer, Offset = offset, Size = size };

    /// <summary>Binds a texture with its sampler.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="sampler">The sampler.</param>
    /// <returns>The resource.</returns>
    public static BoundResource Of(Texture texture, Sampler sampler) => new() { Texture = texture, Sampler = sampler };

    /// <summary>Binds a storage texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>The resource.</returns>
    public static BoundResource Of(Texture texture) => new() { Texture = texture };
}

/// <summary>Describes a resource set: resources bound together, one per entry of its layout.</summary>
/// <param name="Layout">The layout.</param>
/// <param name="Resources">The resources, in the order of the entries of the layout.</param>
public readonly record struct ResourceSetDescription(ResourceLayout Layout, IReadOnlyList<BoundResource> Resources)
{
    /// <summary>Gets the debug name, or <see langword="null"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>Whether a vertex buffer advances per vertex or per instance.</summary>
public enum VertexStepMode
{
    /// <summary>One element per vertex.</summary>
    Vertex,

    /// <summary>One element per instance.</summary>
    Instance,
}

/// <summary>An attribute of a vertex buffer.</summary>
/// <param name="Location">The input location in the vertex shader.</param>
/// <param name="Format">The format.</param>
/// <param name="Offset">The offset in the element, in bytes.</param>
public readonly record struct VertexAttribute(int Location, VertexFormat Format, int Offset);

/// <summary>The layout of one vertex buffer.</summary>
/// <param name="Stride">The size of one element, in bytes.</param>
/// <param name="Attributes">The attributes.</param>
/// <param name="StepMode">Whether it advances per vertex or per instance.</param>
public readonly record struct VertexBufferLayout(int Stride, IReadOnlyList<VertexAttribute> Attributes, VertexStepMode StepMode = VertexStepMode.Vertex);

/// <summary>How vertices form primitives.</summary>
public enum PrimitiveTopology
{
    /// <summary>Every three vertices form a triangle.</summary>
    TriangleList,

    /// <summary>Each vertex after the second forms a triangle with the two before it.</summary>
    TriangleStrip,

    /// <summary>Every two vertices form a line.</summary>
    LineList,

    /// <summary>Each vertex forms a line with the one before it.</summary>
    LineStrip,

    /// <summary>Each vertex is a point.</summary>
    PointList,
}

/// <summary>Which faces are not drawn.</summary>
public enum CullMode
{
    /// <summary>Both faces are drawn.</summary>
    None,

    /// <summary>Front faces are not drawn.</summary>
    Front,

    /// <summary>Back faces are not drawn.</summary>
    Back,
}

/// <summary>Which winding is the front face.</summary>
public enum FrontFace
{
    /// <summary>Counter-clockwise triangles face the viewer.</summary>
    CounterClockwise,

    /// <summary>Clockwise triangles face the viewer.</summary>
    Clockwise,
}

/// <summary>How triangles are filled.</summary>
public enum FillMode
{
    /// <summary>Filled triangles.</summary>
    Solid,

    /// <summary>Edges only.</summary>
    Wireframe,
}

/// <summary>The rasterizer state of a graphics pipeline.</summary>
public readonly record struct RasterizerState()
{
    /// <summary>Gets which faces are not drawn.</summary>
    public CullMode CullMode { get; init; } = CullMode.Back;

    /// <summary>Gets which winding is the front face.</summary>
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;

    /// <summary>Gets how triangles are filled.</summary>
    public FillMode FillMode { get; init; } = FillMode.Solid;

    /// <summary>Gets whether the scissor rectangle clips the pixels.</summary>
    public bool ScissorTest { get; init; }

    /// <summary>Gets whether depths outside the range are clamped instead of clipped.</summary>
    public bool DepthClamp { get; init; }
}

/// <summary>The depth state of a graphics pipeline.</summary>
public readonly record struct DepthState()
{
    /// <summary>Gets whether fragments are tested against the depth buffer.</summary>
    public bool TestEnabled { get; init; }

    /// <summary>Gets whether fragments that pass write their depth.</summary>
    public bool WriteEnabled { get; init; }

    /// <summary>Gets the comparison of the test.</summary>
    public CompareFunction Compare { get; init; } = CompareFunction.LessEqual;

    /// <summary>Gets no depth test and no depth write.</summary>
    public static DepthState Disabled => new();

    /// <summary>Gets the usual test: nearer fragments pass and write their depth.</summary>
    public static DepthState LessEqual => new() { TestEnabled = true, WriteEnabled = true, Compare = CompareFunction.LessEqual };
}

/// <summary>A factor of the blend equation.</summary>
public enum BlendFactor
{
    /// <summary>0.</summary>
    Zero,

    /// <summary>1.</summary>
    One,

    /// <summary>The color of the fragment.</summary>
    SourceColor,

    /// <summary>1 minus the color of the fragment.</summary>
    OneMinusSourceColor,

    /// <summary>The alpha of the fragment.</summary>
    SourceAlpha,

    /// <summary>1 minus the alpha of the fragment.</summary>
    OneMinusSourceAlpha,

    /// <summary>The color in the target.</summary>
    DestinationColor,

    /// <summary>1 minus the color in the target.</summary>
    OneMinusDestinationColor,

    /// <summary>The alpha in the target.</summary>
    DestinationAlpha,

    /// <summary>1 minus the alpha in the target.</summary>
    OneMinusDestinationAlpha,
}

/// <summary>How the two terms of the blend equation combine.</summary>
public enum BlendOperation
{
    /// <summary>Source plus destination.</summary>
    Add,

    /// <summary>Source minus destination.</summary>
    Subtract,

    /// <summary>Destination minus source.</summary>
    ReverseSubtract,

    /// <summary>The smaller of the two.</summary>
    Min,

    /// <summary>The larger of the two.</summary>
    Max,
}

/// <summary>The channels written to a color attachment.</summary>
[Flags]
public enum ColorWriteMask
{
    /// <summary>No channel.</summary>
    None = 0,

    /// <summary>Red.</summary>
    Red = 1,

    /// <summary>Green.</summary>
    Green = 2,

    /// <summary>Blue.</summary>
    Blue = 4,

    /// <summary>Alpha.</summary>
    Alpha = 8,

    /// <summary>All channels.</summary>
    All = Red | Green | Blue | Alpha,
}

/// <summary>The blend state of one color attachment.</summary>
public readonly record struct BlendState()
{
    /// <summary>Gets whether blending is on; off, the fragment replaces the target.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the factor of the fragment color.</summary>
    public BlendFactor SourceColor { get; init; } = BlendFactor.One;

    /// <summary>Gets the factor of the target color.</summary>
    public BlendFactor DestinationColor { get; init; } = BlendFactor.Zero;

    /// <summary>Gets the operation on colors.</summary>
    public BlendOperation ColorOperation { get; init; } = BlendOperation.Add;

    /// <summary>Gets the factor of the fragment alpha.</summary>
    public BlendFactor SourceAlpha { get; init; } = BlendFactor.One;

    /// <summary>Gets the factor of the target alpha.</summary>
    public BlendFactor DestinationAlpha { get; init; } = BlendFactor.Zero;

    /// <summary>Gets the operation on alpha.</summary>
    public BlendOperation AlphaOperation { get; init; } = BlendOperation.Add;

    /// <summary>Gets the channels written.</summary>
    public ColorWriteMask WriteMask { get; init; } = ColorWriteMask.All;

    /// <summary>Gets the state that replaces the target.</summary>
    public static BlendState Opaque => new();

    /// <summary>Gets the usual blending by the alpha of the fragment.</summary>
    public static BlendState AlphaBlend => new()
    {
        Enabled = true,
        SourceColor = BlendFactor.SourceAlpha,
        DestinationColor = BlendFactor.OneMinusSourceAlpha,
        SourceAlpha = BlendFactor.One,
        DestinationAlpha = BlendFactor.OneMinusSourceAlpha,
    };
}

/// <summary>The formats of the attachments a graphics pipeline draws to.</summary>
/// <param name="Colors">The formats of the color attachments, in order.</param>
/// <param name="Depth">The format of the depth attachment, or <see cref="PixelFormat.None"/>.</param>
public readonly record struct OutputDescription(IReadOnlyList<PixelFormat> Colors, PixelFormat Depth = PixelFormat.None);

/// <summary>Describes an immutable graphics pipeline: shaders and every fixed state of a draw.</summary>
public readonly record struct GraphicsPipelineDescription()
{
    /// <summary>Gets the vertex shader.</summary>
    public required Shader VertexShader { get; init; }

    /// <summary>Gets the fragment shader, or <see langword="null"/> for a depth-only pipeline.</summary>
    public Shader? FragmentShader { get; init; }

    /// <summary>Gets the layouts of the vertex buffers, by slot; empty when the shader reads no vertex buffer.</summary>
    public IReadOnlyList<VertexBufferLayout> VertexBuffers { get; init; } = [];

    /// <summary>Gets the resource layouts, by slot of <see cref="CommandBuffer.SetResourceSet"/>.</summary>
    public IReadOnlyList<ResourceLayout> ResourceLayouts { get; init; } = [];

    /// <summary>Gets how vertices form primitives.</summary>
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;

    /// <summary>Gets the rasterizer state.</summary>
    public RasterizerState Rasterizer { get; init; } = new();

    /// <summary>Gets the depth state.</summary>
    public DepthState Depth { get; init; } = new();

    /// <summary>Gets the blend state, the same for every color attachment.</summary>
    public BlendState Blend { get; init; } = new();

    /// <summary>Gets the formats of the attachments the pipeline draws to.</summary>
    public required OutputDescription Outputs { get; init; }

    /// <summary>Gets the debug name, or <see langword="null"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>Describes an immutable compute pipeline. Requires <see cref="GraphicsFeatures.Compute"/>.</summary>
/// <param name="Shader">The compute shader.</param>
/// <param name="ResourceLayouts">The resource layouts, by slot.</param>
public readonly record struct ComputePipelineDescription(Shader Shader, IReadOnlyList<ResourceLayout> ResourceLayouts)
{
    /// <summary>Gets the debug name, or <see langword="null"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>An attachment of a framebuffer: one mip level and one layer of a texture.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="MipLevel">The mip level.</param>
/// <param name="Layer">The layer of an array, or the slice of a 3D texture.</param>
public readonly record struct FramebufferAttachment(Texture Texture, int MipLevel = 0, int Layer = 0);

/// <summary>Describes a framebuffer: the textures a render pass draws to.</summary>
/// <param name="Colors">The color attachments, in order; textures with <see cref="TextureUsage.RenderTarget"/>.</param>
/// <param name="Depth">The depth attachment, a texture with <see cref="TextureUsage.DepthStencil"/>, or <see langword="null"/>.</param>
public readonly record struct FramebufferDescription(IReadOnlyList<FramebufferAttachment> Colors, FramebufferAttachment? Depth = null)
{
    /// <summary>Gets the debug name, or <see langword="null"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>What a render pass does with an attachment when it begins.</summary>
public enum LoadAction
{
    /// <summary>Keeps the content.</summary>
    Load,

    /// <summary>Clears it to the clear value.</summary>
    Clear,

    /// <summary>The content does not matter: the pass draws over all of it.</summary>
    DontCare,
}

/// <summary>A color with floating-point channels.</summary>
/// <param name="R">The red channel.</param>
/// <param name="G">The green channel.</param>
/// <param name="B">The blue channel.</param>
/// <param name="A">The alpha channel.</param>
public readonly record struct ColorF(float R, float G, float B, float A)
{
    /// <summary>Gets opaque black.</summary>
    public static ColorF Black => new(0, 0, 0, 1);

    /// <summary>Gets transparent black.</summary>
    public static ColorF Transparent => new(0, 0, 0, 0);
}

/// <summary>How a render pass begins.</summary>
public readonly record struct RenderPassDescription()
{
    /// <summary>Gets what happens to the color attachments.</summary>
    public LoadAction ColorLoad { get; init; } = LoadAction.Clear;

    /// <summary>Gets the clear color of every color attachment.</summary>
    public ColorF ClearColor { get; init; } = ColorF.Black;

    /// <summary>Gets what happens to the depth attachment.</summary>
    public LoadAction DepthLoad { get; init; } = LoadAction.Clear;

    /// <summary>Gets the clear depth.</summary>
    public float ClearDepth { get; init; } = 1;

    /// <summary>Gets the clear stencil.</summary>
    public byte ClearStencil { get; init; }
}

/// <summary>A rectangle of the viewport, in pixels from the bottom-left corner of the target, with a depth range.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The bottom edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="MinDepth">The depth of the near plane, from 0 to 1.</param>
/// <param name="MaxDepth">The depth of the far plane, from 0 to 1.</param>
[HotPath]
public readonly record struct Viewport(float X, float Y, float Width, float Height, float MinDepth = 0, float MaxDepth = 1);

/// <summary>A rectangle in whole pixels, from the bottom-left corner of the target.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The bottom edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
[HotPath]
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>What a barrier makes visible: writes by shaders that later commands read.</summary>
[Flags]
public enum BarrierScope
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Later reads as vertex or index buffers.</summary>
    VertexInput = 1,

    /// <summary>Later reads as uniform buffers.</summary>
    Uniform = 2,

    /// <summary>Later reads and writes of storage buffers.</summary>
    StorageBuffer = 4,

    /// <summary>Later samples of textures.</summary>
    Texture = 8,

    /// <summary>Later reads and writes of storage textures.</summary>
    StorageTexture = 16,

    /// <summary>Later reads as indirect arguments.</summary>
    Indirect = 32,

    /// <summary>Later copies and reads back to the CPU.</summary>
    Transfer = 64,

    /// <summary>Later uses as render targets.</summary>
    RenderTarget = 128,

    /// <summary>Everything.</summary>
    All = VertexInput | Uniform | StorageBuffer | Texture | StorageTexture | Indirect | Transfer | RenderTarget,
}

/// <summary>A region of a texture: one mip level, and a box of texels across layers or slices.</summary>
/// <param name="MipLevel">The mip level.</param>
/// <param name="X">The first column.</param>
/// <param name="Y">The first row.</param>
/// <param name="Z">The first layer of an array, or the first slice of a 3D texture.</param>
/// <param name="Width">The number of columns.</param>
/// <param name="Height">The number of rows.</param>
/// <param name="Depth">The number of layers or slices.</param>
public readonly record struct TextureRegion(int MipLevel, int X, int Y, int Z, int Width, int Height, int Depth)
{
    /// <summary>Gets the whole of one mip level of one layer.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="mipLevel">The mip level.</param>
    /// <param name="layer">The layer or slice.</param>
    /// <returns>The region.</returns>
    public static TextureRegion Whole(Texture texture, int mipLevel = 0, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return new(mipLevel, 0, 0, layer, Math.Max(1, texture.Width >> mipLevel), Math.Max(1, texture.Height >> mipLevel), 1);
    }

    /// <summary>Gets the number of bytes of the region in a format, rows packed without padding.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size.</returns>
    public long SizeIn(PixelFormat format) => (long)Width * Height * Depth * PixelFormats.SizeOf(format);
}
