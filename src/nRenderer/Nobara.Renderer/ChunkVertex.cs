// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using Nobara.Core;
using Nobara.Graphics;

namespace Nobara.Renderer;

/// <summary>The normal of a vertex of chunk geometry: one of the six directions of the axes, or none.</summary>
public enum ChunkNormal
{
    /// <summary>Toward -X.</summary>
    NegativeX,

    /// <summary>Toward +X.</summary>
    PositiveX,

    /// <summary>Toward -Y.</summary>
    NegativeY,

    /// <summary>Toward +Y.</summary>
    PositiveY,

    /// <summary>Toward -Z.</summary>
    NegativeZ,

    /// <summary>Toward +Z.</summary>
    PositiveZ,

    /// <summary>No direction, for faces that a pipeline does not shade by direction, such as crossed plants. Shaders read +Y.</summary>
    None,
}

/// <summary>
/// The light of a vertex of chunk geometry, from 0 (dark) to
/// <see cref="ChunkVertex.MaxLight"/>, in steps of a half: the light of
/// the sky, and the light of blocks in red, green, and blue.
/// </summary>
/// <param name="Sky">The light of the sky.</param>
/// <param name="Red">The red channel of the light of blocks.</param>
/// <param name="Green">The green channel of the light of blocks.</param>
/// <param name="Blue">The blue channel of the light of blocks.</param>
/// <remarks>
/// The level of light 15 is the light of the open sky or of the brightest
/// block. Light without color has the same value in its three channels. The
/// day and night multiplier applies in the shader, not here.
/// </remarks>
[HotPath]
public readonly record struct ChunkLight(float Sky, float Red, float Green, float Blue)
{
    /// <summary>Gets the light at its full value: 15 in every channel, as chunks are lit before the computation of light exists.</summary>
    public static ChunkLight Full => new(15, 15, 15, 15);

    /// <summary>Gets light without color.</summary>
    /// <param name="sky">The light of the sky.</param>
    /// <param name="block">The light of blocks.</param>
    /// <returns>The light.</returns>
    public static ChunkLight White(float sky, float block) => new(sky, block, block, block);
}

/// <summary>
/// A vertex of chunk geometry, in 16 bytes: its position in the chunk, its
/// texture coordinates, its texture (array and layer), its normal, its light,
/// its ambient occlusion, and its tint. The shader library of nRenderer decodes it
/// (<c>#include &lt;nobara/renderer/chunk.glsl&gt;</c>).
/// </summary>
/// <remarks>
/// <para>The four 32-bit words hold, from the lowest bit:</para>
/// <list type="table">
/// <item><term>0</term><description>X (16 bits), Y (16 bits)</description></item>
/// <item><term>1</term><description>Z (16 bits), U (16 bits)</description></item>
/// <item><term>2</term><description>V (16 bits), layer (11 bits), array (5 bits)</description></item>
/// <item><term>3</term><description>normal (3 bits), ambient occlusion (2 bits), sky light (5 bits), red, green, and blue light of blocks (5 bits each), tint (7 bits)</description></item>
/// </list>
/// <para>
/// Positions are in blocks from the corner of the chunk, from
/// <see cref="MinPosition"/> to <see cref="MaxPosition"/> (excluded), in
/// steps of <see cref="PositionStep"/>: models may overhang the chunk.
/// Texture coordinates are from 0 to <see cref="MaxTextureCoordinate"/>
/// (excluded), in the same steps, so a merged face may repeat its texture
/// over a whole chunk. A value outside its range is clamped; the encoding
/// rounds to the nearest step. The tint is an index in the palette of
/// <see cref="ChunkGeometry.SetTints"/>: 0 is white, which leaves the texture
/// as it is.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ChunkVertex : IEquatable<ChunkVertex>
{
    /// <summary>The size of a vertex, in bytes.</summary>
    public const int Size = 16;

    /// <summary>The lowest coordinate of a position, in blocks from the corner of the chunk.</summary>
    public const float MinPosition = -16;

    /// <summary>The highest coordinate of a position, excluded.</summary>
    public const float MaxPosition = 48;

    /// <summary>The step of positions and texture coordinates: 1/1024.</summary>
    public const float PositionStep = 1f / 1024;

    /// <summary>The highest texture coordinate, excluded.</summary>
    public const float MaxTextureCoordinate = 64;

    /// <summary>The highest index of a texture array.</summary>
    public const int MaxTextureArray = 31;

    /// <summary>The highest layer of a texture array.</summary>
    public const int MaxTextureLayer = 2047;

    /// <summary>The highest level of light: 15.5.</summary>
    public const float MaxLight = 31f / 2;

    /// <summary>The highest ambient occlusion: 3, a corner surrounded by blocks.</summary>
    public const int MaxAmbientOcclusion = 3;

    /// <summary>The highest index of a tint, in the palette of <see cref="ChunkGeometry.SetTints"/>.</summary>
    public const int MaxTint = 127;

    private const float Scale = 1024;

    /// <summary>Encodes a vertex.</summary>
    /// <param name="position">The position, in blocks from the corner of the chunk.</param>
    /// <param name="textureCoordinates">The texture coordinates, in units of the texture (1 is its width).</param>
    /// <param name="texture">The texture, from <see cref="RenderTexture.Location"/>.</param>
    /// <param name="normal">The normal.</param>
    /// <param name="light">The light.</param>
    /// <param name="ambientOcclusion">The ambient occlusion, from 0 (none) to <see cref="MaxAmbientOcclusion"/>.</param>
    /// <param name="tint">The index of the tint in the palette, from 0 (white) to <see cref="MaxTint"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The texture is beyond <see cref="MaxTextureArray"/> or <see cref="MaxTextureLayer"/>, the occlusion is outside 0 to 3, or the tint outside 0 to 127.</exception>
    [HotPath]
    public ChunkVertex(Vector3 position, Vector2 textureCoordinates, TextureLocation texture, ChunkNormal normal, ChunkLight light, int ambientOcclusion = 0, int tint = 0)
    {
        if ((uint)texture.Array > MaxTextureArray || (uint)texture.Layer > MaxTextureLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(texture), "A chunk vertex holds texture arrays 0 to 31 and layers 0 to 2047.");
        }

        if ((uint)ambientOcclusion > MaxAmbientOcclusion || (uint)normal > (uint)ChunkNormal.None)
        {
            throw new ArgumentOutOfRangeException(nameof(ambientOcclusion), "A chunk vertex holds an ambient occlusion from 0 to 3 and a normal of ChunkNormal.");
        }

        if ((uint)tint > MaxTint)
        {
            throw new ArgumentOutOfRangeException(nameof(tint), "A chunk vertex holds a tint from 0 to 127.");
        }

        Word0 = EncodePosition(position.X) | (EncodePosition(position.Y) << 16);
        Word1 = EncodePosition(position.Z) | (Coordinate(textureCoordinates.X) << 16);
        Word2 = Coordinate(textureCoordinates.Y) | ((uint)texture.Layer << 16) | ((uint)texture.Array << 27);
        Word3 = (uint)normal
            | ((uint)ambientOcclusion << 3)
            | (EncodeLight(light.Sky) << 5)
            | (EncodeLight(light.Red) << 10)
            | (EncodeLight(light.Green) << 15)
            | (EncodeLight(light.Blue) << 20)
            | ((uint)tint << 25);
    }

    [HotPath]
    private ChunkVertex(uint word0, uint word1, uint word2, uint word3)
    {
        Word0 = word0;
        Word1 = word1;
        Word2 = word2;
        Word3 = word3;
    }

    /// <summary>Gets the first word: X and Y.</summary>
    public uint Word0 { get; }

    /// <summary>Gets the second word: Z and U.</summary>
    public uint Word1 { get; }

    /// <summary>Gets the third word: V, the layer, and the array.</summary>
    public uint Word2 { get; }

    /// <summary>Gets the fourth word: the normal, the occlusion, the light, and the tint.</summary>
    public uint Word3 { get; }

    /// <summary>Gets the position, in blocks from the corner of the chunk, as the shader decodes it.</summary>
    [HotPath]
    public Vector3 Position => new(Decode(Word0 & 0xFFFF) + MinPosition, Decode(Word0 >> 16) + MinPosition, Decode(Word1 & 0xFFFF) + MinPosition);

    /// <summary>Gets the texture coordinates, as the shader decodes them.</summary>
    [HotPath]
    public Vector2 TextureCoordinates => new(Decode(Word1 >> 16), Decode(Word2 & 0xFFFF));

    /// <summary>Gets the texture: its array and its layer.</summary>
    public TextureLocation Texture => new((int)(Word2 >> 27), (int)((Word2 >> 16) & 0x7FF));

    /// <summary>Gets the normal; the reserved value 7 reads as <see cref="ChunkNormal.None"/>.</summary>
    [HotPath]
    public ChunkNormal Normal => (Word3 & 7) is var normal && normal <= (uint)ChunkNormal.None ? (ChunkNormal)normal : ChunkNormal.None;

    /// <summary>Gets the ambient occlusion, from 0 to 3.</summary>
    [HotPath]
    public int AmbientOcclusion => (int)((Word3 >> 3) & 3);

    /// <summary>Gets the light.</summary>
    [HotPath]
    public ChunkLight Light => new(((Word3 >> 5) & 31) / 2f, ((Word3 >> 10) & 31) / 2f, ((Word3 >> 15) & 31) / 2f, ((Word3 >> 20) & 31) / 2f);

    /// <summary>Gets the index of the tint in the palette, from 0 (white) to 127.</summary>
    [HotPath]
    public int Tint => (int)(Word3 >> 25);

    /// <summary>Gets the layout of a vertex buffer of chunk vertices: the four words as an unsigned integer vector at a location.</summary>
    /// <param name="location">The input location in the vertex shader; 0 in the shader library.</param>
    /// <returns>The layout.</returns>
    public static VertexBufferLayout Layout(int location = 0) => new(Size, [new VertexAttribute(location, VertexFormat.UInt4, 0)]);

    /// <summary>Gets a vertex from its four words, as a shader or a file holds them.</summary>
    /// <param name="word0">The first word.</param>
    /// <param name="word1">The second word.</param>
    /// <param name="word2">The third word.</param>
    /// <param name="word3">The fourth word.</param>
    /// <returns>The vertex.</returns>
    [HotPath]
    public static ChunkVertex FromWords(uint word0, uint word1, uint word2, uint word3) => new(word0, word1, word2, word3);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(ChunkVertex other) => Word0 == other.Word0 && Word1 == other.Word1 && Word2 == other.Word2 && Word3 == other.Word3;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ChunkVertex other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Word0, Word1, Word2, Word3);

    /// <inheritdoc/>
    public override string ToString() => $"chunk vertex at {Position}, {Normal}";

    /// <summary>Compares two vertices.</summary>
    /// <param name="left">The first vertex.</param>
    /// <param name="right">The second vertex.</param>
    /// <returns><see langword="true"/> when their words are equal.</returns>
    public static bool operator ==(ChunkVertex left, ChunkVertex right) => left.Equals(right);

    /// <summary>Compares two vertices.</summary>
    /// <param name="left">The first vertex.</param>
    /// <param name="right">The second vertex.</param>
    /// <returns><see langword="true"/> when their words differ.</returns>
    public static bool operator !=(ChunkVertex left, ChunkVertex right) => !left.Equals(right);

    [HotPath]
    private static uint EncodePosition(float value) => Quantize(value - MinPosition);

    [HotPath]
    private static uint Coordinate(float value) => Quantize(value);

    [HotPath]
    private static uint Quantize(float value)
    {
        float scaled = MathF.Round(value * Scale);
        return scaled <= 0 || float.IsNaN(scaled) ? 0 : scaled >= ushort.MaxValue ? ushort.MaxValue : (uint)scaled;
    }

    [HotPath]
    private static uint EncodeLight(float level)
    {
        float scaled = MathF.Round(level * 2);
        return scaled <= 0 || float.IsNaN(scaled) ? 0 : scaled >= 31 ? 31 : (uint)scaled;
    }

    [HotPath]
    private static float Decode(uint value) => value / Scale;
}
