// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>The format of the texels of a texture or of a render target.</summary>
public enum PixelFormat
{
    /// <summary>No format: an absent attachment.</summary>
    None,

    /// <summary>One 8-bit channel, read as a value from 0 to 1.</summary>
    R8Unorm,

    /// <summary>Two 8-bit channels, read as values from 0 to 1.</summary>
    Rg8Unorm,

    /// <summary>Four 8-bit channels, red first, read as values from 0 to 1.</summary>
    Rgba8Unorm,

    /// <summary>Four 8-bit channels, red first; color channels are sRGB encoded and read as linear values.</summary>
    Rgba8UnormSrgb,

    /// <summary>One 16-bit floating-point channel.</summary>
    R16Float,

    /// <summary>Two 16-bit floating-point channels.</summary>
    Rg16Float,

    /// <summary>Four 16-bit floating-point channels.</summary>
    Rgba16Float,

    /// <summary>One 32-bit floating-point channel.</summary>
    R32Float,

    /// <summary>Two 32-bit floating-point channels.</summary>
    Rg32Float,

    /// <summary>Four 32-bit floating-point channels.</summary>
    Rgba32Float,

    /// <summary>One 32-bit unsigned integer channel.</summary>
    R32Uint,

    /// <summary>A 24-bit depth and an 8-bit stencil.</summary>
    Depth24Stencil8,

    /// <summary>A 32-bit floating-point depth.</summary>
    Depth32Float,
}

/// <summary>Facts about the pixel formats.</summary>
[HotPath]
public static class PixelFormats
{
    /// <summary>Gets the size of one texel, in bytes.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size; 0 for <see cref="PixelFormat.None"/>.</returns>
    public static int SizeOf(PixelFormat format) => format switch
    {
        PixelFormat.R8Unorm => 1,
        PixelFormat.Rg8Unorm or PixelFormat.R16Float => 2,
        PixelFormat.Rgba8Unorm or PixelFormat.Rgba8UnormSrgb or PixelFormat.Rg16Float or PixelFormat.R32Float or PixelFormat.R32Uint
            or PixelFormat.Depth24Stencil8 or PixelFormat.Depth32Float => 4,
        PixelFormat.Rgba16Float or PixelFormat.Rg32Float => 8,
        PixelFormat.Rgba32Float => 16,
        _ => 0,
    };

    /// <summary>Gets whether the format holds a depth, and maybe a stencil.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for a depth format.</returns>
    public static bool IsDepth(PixelFormat format) => format is PixelFormat.Depth24Stencil8 or PixelFormat.Depth32Float;

    /// <summary>Gets whether the format holds a stencil.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for a format with a stencil.</returns>
    public static bool HasStencil(PixelFormat format) => format == PixelFormat.Depth24Stencil8;
}

/// <summary>The type of the indices of an index buffer.</summary>
public enum IndexFormat
{
    /// <summary>16-bit unsigned indices.</summary>
    UInt16,

    /// <summary>32-bit unsigned indices.</summary>
    UInt32,
}

/// <summary>The format of a vertex attribute in a vertex buffer.</summary>
public enum VertexFormat
{
    /// <summary>One 32-bit float.</summary>
    Float1,

    /// <summary>Two 32-bit floats.</summary>
    Float2,

    /// <summary>Three 32-bit floats.</summary>
    Float3,

    /// <summary>Four 32-bit floats.</summary>
    Float4,

    /// <summary>Four 8-bit unsigned values, read as floats from 0 to 1.</summary>
    Byte4Norm,

    /// <summary>Two 16-bit floats.</summary>
    Half2,

    /// <summary>Four 16-bit floats.</summary>
    Half4,

    /// <summary>One 32-bit unsigned integer, read as an integer.</summary>
    UInt1,

    /// <summary>Two 32-bit unsigned integers, read as integers.</summary>
    UInt2,

    /// <summary>Four 32-bit unsigned integers, read as integers.</summary>
    UInt4,

    /// <summary>One 32-bit signed integer, read as an integer.</summary>
    Int1,
}

/// <summary>Facts about the vertex formats.</summary>
public static class VertexFormats
{
    /// <summary>Gets the size of an attribute, in bytes.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size.</returns>
    public static int SizeOf(VertexFormat format) => format switch
    {
        VertexFormat.Float1 or VertexFormat.Byte4Norm or VertexFormat.Half2 or VertexFormat.UInt1 or VertexFormat.Int1 => 4,
        VertexFormat.Float2 or VertexFormat.Half4 or VertexFormat.UInt2 => 8,
        VertexFormat.Float3 => 12,
        _ => 16,
    };
}
