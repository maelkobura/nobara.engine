// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Silk.NET.OpenGL;

namespace Nobara.Graphics.OpenGL;

/// <summary>The OpenGL format of a texel format: internal format, then the format and type of transfers.</summary>
[HotPath]
internal readonly record struct GLFormat(GLEnum Internal, GLEnum Format, GLEnum Type);

/// <summary>Converts neutral values to OpenGL values.</summary>
[HotPath]
internal static class GLConvert
{
    public static GLFormat Format(PixelFormat format) => format switch
    {
        PixelFormat.R8Unorm => new(GLEnum.R8, GLEnum.Red, GLEnum.UnsignedByte),
        PixelFormat.Rg8Unorm => new(GLEnum.RG8, GLEnum.RG, GLEnum.UnsignedByte),
        PixelFormat.Rgba8Unorm => new(GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte),
        PixelFormat.Rgba8UnormSrgb => new(GLEnum.Srgb8Alpha8, GLEnum.Rgba, GLEnum.UnsignedByte),
        PixelFormat.R16Float => new(GLEnum.R16f, GLEnum.Red, GLEnum.HalfFloat),
        PixelFormat.Rg16Float => new(GLEnum.RG16f, GLEnum.RG, GLEnum.HalfFloat),
        PixelFormat.Rgba16Float => new(GLEnum.Rgba16f, GLEnum.Rgba, GLEnum.HalfFloat),
        PixelFormat.R32Float => new(GLEnum.R32f, GLEnum.Red, GLEnum.Float),
        PixelFormat.Rg32Float => new(GLEnum.RG32f, GLEnum.RG, GLEnum.Float),
        PixelFormat.Rgba32Float => new(GLEnum.Rgba32f, GLEnum.Rgba, GLEnum.Float),
        PixelFormat.R32Uint => new(GLEnum.R32ui, GLEnum.RedInteger, GLEnum.UnsignedInt),
        PixelFormat.Depth24Stencil8 => new(GLEnum.Depth24Stencil8, GLEnum.DepthStencil, GLEnum.UnsignedInt248),
        PixelFormat.Depth32Float => new(GLEnum.DepthComponent32f, GLEnum.DepthComponent, GLEnum.Float),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The format has no OpenGL equivalent."),
    };

    public static GLEnum Topology(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.TriangleStrip => GLEnum.TriangleStrip,
        PrimitiveTopology.LineList => GLEnum.Lines,
        PrimitiveTopology.LineStrip => GLEnum.LineStrip,
        PrimitiveTopology.PointList => GLEnum.Points,
        _ => GLEnum.Triangles,
    };

    public static GLEnum Compare(CompareFunction compare) => compare switch
    {
        CompareFunction.Never => GLEnum.Never,
        CompareFunction.Less => GLEnum.Less,
        CompareFunction.Equal => GLEnum.Equal,
        CompareFunction.LessEqual => GLEnum.Lequal,
        CompareFunction.Greater => GLEnum.Greater,
        CompareFunction.NotEqual => GLEnum.Notequal,
        CompareFunction.GreaterEqual => GLEnum.Gequal,
        _ => GLEnum.Always,
    };

    public static GLEnum Blend(BlendFactor factor) => factor switch
    {
        BlendFactor.Zero => GLEnum.Zero,
        BlendFactor.One => GLEnum.One,
        BlendFactor.SourceColor => GLEnum.SrcColor,
        BlendFactor.OneMinusSourceColor => GLEnum.OneMinusSrcColor,
        BlendFactor.SourceAlpha => GLEnum.SrcAlpha,
        BlendFactor.OneMinusSourceAlpha => GLEnum.OneMinusSrcAlpha,
        BlendFactor.DestinationColor => GLEnum.DstColor,
        BlendFactor.OneMinusDestinationColor => GLEnum.OneMinusDstColor,
        BlendFactor.DestinationAlpha => GLEnum.DstAlpha,
        _ => GLEnum.OneMinusDstAlpha,
    };

    public static GLEnum Blend(BlendOperation operation) => operation switch
    {
        BlendOperation.Subtract => GLEnum.FuncSubtract,
        BlendOperation.ReverseSubtract => GLEnum.FuncReverseSubtract,
        BlendOperation.Min => GLEnum.Min,
        BlendOperation.Max => GLEnum.Max,
        _ => GLEnum.FuncAdd,
    };

    public static GLEnum Address(AddressMode mode) => mode switch
    {
        AddressMode.MirroredRepeat => GLEnum.MirroredRepeat,
        AddressMode.ClampToEdge => GLEnum.ClampToEdge,
        AddressMode.ClampToBorder => GLEnum.ClampToBorder,
        _ => GLEnum.Repeat,
    };

    public static GLEnum MinFilter(FilterMode min, FilterMode mip) => (min, mip) switch
    {
        (FilterMode.Nearest, FilterMode.Nearest) => GLEnum.NearestMipmapNearest,
        (FilterMode.Nearest, FilterMode.Linear) => GLEnum.NearestMipmapLinear,
        (FilterMode.Linear, FilterMode.Nearest) => GLEnum.LinearMipmapNearest,
        _ => GLEnum.LinearMipmapLinear,
    };

    public static GLEnum MagFilter(FilterMode mag) => mag == FilterMode.Nearest ? GLEnum.Nearest : GLEnum.Linear;

    public static uint Barrier(BarrierScope scope)
    {
        if (scope == BarrierScope.All)
        {
            return uint.MaxValue;
        }

        uint bits = 0;
        if ((scope & BarrierScope.VertexInput) != 0)
        {
            bits |= (uint)(MemoryBarrierMask.VertexAttribArrayBarrierBit | MemoryBarrierMask.ElementArrayBarrierBit);
        }

        if ((scope & BarrierScope.Uniform) != 0)
        {
            bits |= (uint)MemoryBarrierMask.UniformBarrierBit;
        }

        if ((scope & BarrierScope.StorageBuffer) != 0)
        {
            bits |= (uint)MemoryBarrierMask.ShaderStorageBarrierBit;
        }

        if ((scope & BarrierScope.Texture) != 0)
        {
            bits |= (uint)MemoryBarrierMask.TextureFetchBarrierBit;
        }

        if ((scope & BarrierScope.StorageTexture) != 0)
        {
            bits |= (uint)MemoryBarrierMask.ShaderImageAccessBarrierBit;
        }

        if ((scope & BarrierScope.Indirect) != 0)
        {
            bits |= (uint)MemoryBarrierMask.CommandBarrierBit;
        }

        if ((scope & BarrierScope.Transfer) != 0)
        {
            bits |= (uint)(MemoryBarrierMask.BufferUpdateBarrierBit | MemoryBarrierMask.TextureUpdateBarrierBit | MemoryBarrierMask.PixelBufferBarrierBit);
        }

        if ((scope & BarrierScope.RenderTarget) != 0)
        {
            bits |= (uint)MemoryBarrierMask.FramebufferBarrierBit;
        }

        return bits;
    }

    /// <summary>Gets the component count, type, and kind of a vertex attribute: <c>Integer</c> for attributes read as integers.</summary>
    public static (int Size, GLEnum Type, bool Normalized, bool Integer) Vertex(VertexFormat format) => format switch
    {
        VertexFormat.Float1 => (1, GLEnum.Float, false, false),
        VertexFormat.Float2 => (2, GLEnum.Float, false, false),
        VertexFormat.Float3 => (3, GLEnum.Float, false, false),
        VertexFormat.Float4 => (4, GLEnum.Float, false, false),
        VertexFormat.Byte4Norm => (4, GLEnum.UnsignedByte, true, false),
        VertexFormat.Half2 => (2, GLEnum.HalfFloat, false, false),
        VertexFormat.Half4 => (4, GLEnum.HalfFloat, false, false),
        VertexFormat.UInt1 => (1, GLEnum.UnsignedInt, false, true),
        VertexFormat.UInt2 => (2, GLEnum.UnsignedInt, false, true),
        VertexFormat.UInt4 => (4, GLEnum.UnsignedInt, false, true),
        _ => (1, GLEnum.Int, false, true),
    };
}
