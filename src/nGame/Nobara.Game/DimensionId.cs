// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Nobara.Core;

namespace Nobara.Game;

/// <summary>
/// Identifies a dimension of the world for game time: each dimension has its
/// own clock. nGame does not know what a dimension contains; the module that
/// owns the dimensions gives them their identifiers.
/// </summary>
/// <remarks>
/// An identifier is a number. Identifiers are ordered by that number, which
/// gives a deterministic order to code that goes through dimensions. The
/// clock of <see cref="Default"/> exists from the start of the context.
/// </remarks>
public readonly struct DimensionId : IEquatable<DimensionId>, IComparable<DimensionId>
{
    /// <summary>Initializes an identifier.</summary>
    /// <param name="value">The number of the dimension. 0 is <see cref="Default"/>.</param>
    [HotPath]
    public DimensionId(uint value)
    {
        Value = value;
    }

    /// <summary>Gets the dimension whose clock exists from the start: number 0. A game without several dimensions uses it.</summary>
    public static DimensionId Default
    {
        [HotPath]
        get => default;
    }

    /// <summary>Gets the number of the dimension, to save it or to send it.</summary>
    public uint Value { get; }

    /// <summary>Tells whether two identifiers are equal.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when they name the same dimension.</returns>
    [HotPath]
    public static bool operator ==(DimensionId left, DimensionId right) => left.Value == right.Value;

    /// <summary>Tells whether two identifiers differ.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when they name different dimensions.</returns>
    [HotPath]
    public static bool operator !=(DimensionId left, DimensionId right) => left.Value != right.Value;

    /// <summary>Tells whether an identifier comes before another.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> has the smaller number.</returns>
    [HotPath]
    public static bool operator <(DimensionId left, DimensionId right) => left.Value < right.Value;

    /// <summary>Tells whether an identifier comes after another.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> has the larger number.</returns>
    [HotPath]
    public static bool operator >(DimensionId left, DimensionId right) => left.Value > right.Value;

    /// <summary>Tells whether an identifier comes before another or is equal to it.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not have the larger number.</returns>
    [HotPath]
    public static bool operator <=(DimensionId left, DimensionId right) => left.Value <= right.Value;

    /// <summary>Tells whether an identifier comes after another or is equal to it.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not have the smaller number.</returns>
    [HotPath]
    public static bool operator >=(DimensionId left, DimensionId right) => left.Value >= right.Value;

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(DimensionId other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DimensionId other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc/>
    [HotPath]
    public int CompareTo(DimensionId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the identifier as logs show it.</summary>
    /// <returns>For example <c>dimension 2</c>.</returns>
    public override string ToString() => "dimension " + Value.ToString(CultureInfo.InvariantCulture);
}
