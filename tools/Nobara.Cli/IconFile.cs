// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;

namespace Nobara.Cli;

/// <summary>Turns the PNG icon of a game into an <c>.ico</c> file, which holds the PNG as it is.</summary>
internal static class IconFile
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Wraps a PNG image in an icon file.</summary>
    /// <param name="png">The bytes of the PNG file.</param>
    /// <param name="error">Why the image cannot be used, when it cannot.</param>
    /// <returns>The icon file, or <see langword="null"/>.</returns>
    public static byte[]? FromPng(byte[] png, out string? error)
    {
        // Signature, then the IHDR chunk: length, type, width, height.
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(Signature) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            error = "it is not a PNG image";
            return null;
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width is < 1 or > 256 || height is < 1 or > 256)
        {
            error = $"it is {width}x{height} pixels; an icon is at most 256x256";
            return null;
        }

        // ICONDIR (6 bytes), one ICONDIRENTRY (16 bytes), then the image.
        var icon = new byte[22 + png.Length];
        Span<byte> span = icon;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        span[6] = (byte)(width == 256 ? 0 : width);
        span[7] = (byte)(height == 256 ? 0 : height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], 22);
        png.CopyTo(span[22..]);
        error = null;
        return icon;
    }
}
