// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;
using StbImageSharp;

namespace Nobara.Renderer;

/// <summary>A decoded image in RGBA, 8 bits per channel, with its mip levels.</summary>
internal sealed class TextureImage
{
    private static readonly ImmutableArray<float> SrgbToLinear = [.. Enumerable.Range(0, 256).Select(i =>
    {
        float c = i / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    })];

    private TextureImage(int width, int height, byte[][] levels)
    {
        Width = width;
        Height = height;
        Levels = levels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Gets the texels of each mip level, rows from the top of the image.</summary>
    public byte[][] Levels { get; }

    /// <summary>Gets the length of the full mip chain of a size.</summary>
    public static int FullChain(int width, int height) => 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));

    /// <summary>Reads the size of a PNG file, or explains why it is not one that the renderer reads.</summary>
    public static string? CheckPng(ReadOnlySpan<byte> file, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (file.Length < PngSignature.Length || !file[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return "the file is not a PNG image";
        }

        using var stream = new MemoryStream(file.ToArray(), writable: false);
        ImageInfo? info = ImageInfo.FromStream(stream);
        if (info is null)
        {
            return "the PNG image cannot be read";
        }

        width = info.Value.Width;
        height = info.Value.Height;
        if (width < 1 || height < 1 || width > RenderRules.MaxTextureSize || height > RenderRules.MaxTextureSize)
        {
            return $"the image is {width}x{height}; a texture is from 1 to {RenderRules.MaxTextureSize} texels on each side";
        }

        return null;
    }

    /// <summary>Decodes a PNG file and computes its mip levels.</summary>
    /// <param name="file">The file.</param>
    /// <param name="mipLevels">The number of levels; 0 for the full chain.</param>
    /// <param name="srgb">Whether the colors are in sRGB, so levels are averaged in linear values.</param>
    /// <exception cref="InvalidDataException">The file is not a PNG image the renderer reads.</exception>
    public static TextureImage Decode(byte[] file, int mipLevels, bool srgb)
    {
        if (CheckPng(file, out _, out _) is string problem)
        {
            throw new InvalidDataException(problem);
        }

        ImageResult image;
        using (var stream = new MemoryStream(file, writable: false))
        {
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        }

        int levels = mipLevels == 0 ? FullChain(image.Width, image.Height) : Math.Min(mipLevels, FullChain(image.Width, image.Height));
        var data = new byte[levels][];
        data[0] = image.Data;
        int width = image.Width;
        int height = image.Height;
        for (int level = 1; level < levels; level++)
        {
            int nextWidth = Math.Max(1, width / 2);
            int nextHeight = Math.Max(1, height / 2);
            data[level] = Downsample(data[level - 1], width, height, nextWidth, nextHeight, srgb);
            width = nextWidth;
            height = nextHeight;
        }

        return new TextureImage(image.Width, image.Height, data);
    }

    /// <summary>Gets an image of one color, with its full mip chain.</summary>
    public static TextureImage Solid(int size, byte r, byte g, byte b, byte a)
    {
        int levels = FullChain(size, size);
        var data = new byte[levels][];
        for (int level = 0; level < levels; level++)
        {
            int side = Math.Max(1, size >> level);
            data[level] = new byte[side * side * 4];
            for (int i = 0; i < data[level].Length; i += 4)
            {
                data[level][i] = r;
                data[level][i + 1] = g;
                data[level][i + 2] = b;
                data[level][i + 3] = a;
            }
        }

        return new TextureImage(size, size, data);
    }

    // A box filter over the texels that each texel of the next level covers.
    private static byte[] Downsample(byte[] source, int width, int height, int nextWidth, int nextHeight, bool srgb)
    {
        var result = new byte[nextWidth * nextHeight * 4];
        int stepX = width / nextWidth;
        int stepY = height / nextHeight;
        Span<float> sum = stackalloc float[4];
        for (int y = 0; y < nextHeight; y++)
        {
            for (int x = 0; x < nextWidth; x++)
            {
                sum.Clear();
                int count = 0;
                for (int dy = 0; dy < stepY; dy++)
                {
                    for (int dx = 0; dx < stepX; dx++)
                    {
                        int at = ((((y * stepY) + dy) * width) + (x * stepX) + dx) * 4;
                        for (int c = 0; c < 3; c++)
                        {
                            sum[c] += srgb ? SrgbToLinear[source[at + c]] : source[at + c] / 255f;
                        }

                        sum[3] += source[at + 3] / 255f;
                        count++;
                    }
                }

                int to = ((y * nextWidth) + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float value = sum[c] / count;
                    result[to + c] = srgb ? LinearToSrgb(value) : ToByte(value);
                }

                result[to + 3] = ToByte(sum[3] / count);
            }
        }

        return result;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte LinearToSrgb(float value) =>
        ToByte(value <= 0.0031308f ? value * 12.92f : (1.055f * MathF.Pow(value, 1 / 2.4f)) - 0.055f);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
