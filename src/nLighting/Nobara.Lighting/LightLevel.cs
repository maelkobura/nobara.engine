// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Lighting;

/// <summary>
/// The light of one block: its sky channel and its block channel, each level
/// from 0 (dark) to 15 (full).
/// </summary>
/// <remarks>
/// Without color (<see cref="LightingSettings.Color"/> off), the block channel
/// has one level, which <see cref="Red"/>, <see cref="Green"/>, and
/// <see cref="Blue"/> all give.
/// </remarks>
public readonly struct LightLevel : IEquatable<LightLevel>
{
    private readonly ushort _value;

    /// <summary>Creates a light level.</summary>
    /// <param name="sky">The sky channel, from 0 to 15.</param>
    /// <param name="red">The red component of the block channel, from 0 to 15.</param>
    /// <param name="green">The green component of the block channel, from 0 to 15.</param>
    /// <param name="blue">The blue component of the block channel, from 0 to 15.</param>
    /// <exception cref="ArgumentOutOfRangeException">A level is outside 0 to 15.</exception>
    public LightLevel(int sky, int red, int green, int blue)
    {
        Check(sky, nameof(sky));
        Check(red, nameof(red));
        Check(green, nameof(green));
        Check(blue, nameof(blue));
        _value = (ushort)((sky << 12) | (red << 8) | (green << 4) | blue);
    }

    [HotPath]
    internal LightLevel(ushort value) => _value = value;

    /// <summary>Gets the sky channel: the exposure to the sky, from 0 to 15.</summary>
    public int Sky
    {
        [HotPath]
        get => _value >> 12;
    }

    /// <summary>Gets the red component of the block channel, from 0 to 15.</summary>
    public int Red
    {
        [HotPath]
        get => (_value >> 8) & 15;
    }

    /// <summary>Gets the green component of the block channel, from 0 to 15.</summary>
    public int Green
    {
        [HotPath]
        get => (_value >> 4) & 15;
    }

    /// <summary>Gets the blue component of the block channel, from 0 to 15.</summary>
    public int Blue
    {
        [HotPath]
        get => _value & 15;
    }

    /// <summary>Gets the brightest component of the block channel, from 0 to 15: the level of the block channel without color.</summary>
    public int Block
    {
        [HotPath]
        get => Math.Max(Red, Math.Max(Green, Blue));
    }

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(LightLevel other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LightLevel other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => _value;

    /// <summary>Returns the levels, such as <c>sky 15, block 14 10 4</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"sky {Sky}, block {Red} {Green} {Blue}";

    /// <summary>Compares two light levels.</summary>
    /// <param name="left">A level.</param>
    /// <param name="right">Another level.</param>
    /// <returns><see langword="true"/> when every channel is equal.</returns>
    public static bool operator ==(LightLevel left, LightLevel right) => left.Equals(right);

    /// <summary>Compares two light levels.</summary>
    /// <param name="left">A level.</param>
    /// <param name="right">Another level.</param>
    /// <returns><see langword="true"/> when a channel differs.</returns>
    public static bool operator !=(LightLevel left, LightLevel right) => !left.Equals(right);

    private static void Check(int level, string name)
    {
        if (level is < 0 or > 15) throw new ArgumentOutOfRangeException(name, level, "A light level goes from 0 to 15.");
    }
}

/// <summary>
/// A color of light, each component from 0 to 15: the light that a block
/// emits, or the levels that it takes from the light that crosses it.
/// </summary>
public readonly struct LightColor : IEquatable<LightColor>
{
    /// <summary>Creates a color.</summary>
    /// <param name="red">The red component, from 0 to 15.</param>
    /// <param name="green">The green component, from 0 to 15.</param>
    /// <param name="blue">The blue component, from 0 to 15.</param>
    /// <exception cref="ArgumentOutOfRangeException">A component is outside 0 to 15.</exception>
    public LightColor(int red, int green, int blue)
    {
        if (red is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(red), red, "A light component goes from 0 to 15.");
        if (green is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(green), green, "A light component goes from 0 to 15.");
        if (blue is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(blue), blue, "A light component goes from 0 to 15.");
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>Gets a color whose three components are 0.</summary>
    public static LightColor None => default;

    /// <summary>Gets the red component, from 0 to 15.</summary>
    public int Red { get; }

    /// <summary>Gets the green component, from 0 to 15.</summary>
    public int Green { get; }

    /// <summary>Gets the blue component, from 0 to 15.</summary>
    public int Blue { get; }

    /// <summary>Gets whether every component is 0.</summary>
    public bool IsNone => (Red | Green | Blue) == 0;

    /// <inheritdoc/>
    public bool Equals(LightColor other) => Red == other.Red && Green == other.Green && Blue == other.Blue;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LightColor other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Red << 8) | (Green << 4) | Blue;

    /// <summary>Returns the components, such as <c>14 10 4</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"{Red} {Green} {Blue}";

    /// <summary>Compares two colors.</summary>
    /// <param name="left">A color.</param>
    /// <param name="right">Another color.</param>
    /// <returns><see langword="true"/> when every component is equal.</returns>
    public static bool operator ==(LightColor left, LightColor right) => left.Equals(right);

    /// <summary>Compares two colors.</summary>
    /// <param name="left">A color.</param>
    /// <param name="right">Another color.</param>
    /// <returns><see langword="true"/> when a component differs.</returns>
    public static bool operator !=(LightColor left, LightColor right) => !left.Equals(right);
}

/// <summary>The configuration keys of nLighting.</summary>
public static class LightingSettings
{
    /// <summary>
    /// Whether the block channel has a red, a green, and a blue component; a
    /// <c>distribution</c> key, read when the context starts. Without it, the
    /// block channel has one level and the light takes half the memory.
    /// </summary>
    public const string Color = "lighting.color";
}
