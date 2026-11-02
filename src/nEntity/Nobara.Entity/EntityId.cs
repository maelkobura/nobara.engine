// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Nobara.Core;

namespace Nobara.Entity;

/// <summary>
/// Identifies an entity of an <see cref="EntityWorld"/>: an index in the
/// world and a generation. The default value is <see cref="Null"/>.
/// </summary>
/// <remarks>
/// The identifier of an entity does not change while the entity lives, even
/// when it gains or loses components. Once the entity is destroyed, its index
/// is reused with a new generation, so an old identifier never names a new
/// entity. An identifier means something in its own world only.
/// </remarks>
public readonly struct EntityId : IEquatable<EntityId>
{
    [HotPath]
    internal EntityId(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    /// <summary>Gets the entity that names no entity.</summary>
    public static EntityId Null
    {
        [HotPath]
        get => default;
    }

    /// <summary>Gets the index of the entity in its world.</summary>
    public int Index { get; }

    /// <summary>Gets the generation of the index: it goes up each time the index is reused. 0 for <see cref="Null"/>.</summary>
    public int Generation { get; }

    /// <summary>Gets whether this is <see cref="Null"/>.</summary>
    public bool IsNull
    {
        [HotPath]
        get => Generation == 0;
    }

    /// <summary>Tells whether two identifiers are equal.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when they name the same entity.</returns>
    [HotPath]
    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);

    /// <summary>Tells whether two identifiers differ.</summary>
    /// <param name="left">An identifier.</param>
    /// <param name="right">Another identifier.</param>
    /// <returns><see langword="true"/> when they name different entities.</returns>
    [HotPath]
    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);

    /// <inheritdoc/>
    [HotPath]
    public bool Equals(EntityId other) => Index == other.Index && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);

    /// <inheritdoc/>
    [HotPath]
    public override int GetHashCode() => (Index * 397) ^ Generation;

    /// <summary>Returns the identifier as logs show it.</summary>
    /// <returns>For example <c>entity 12v3</c>, or <c>null entity</c>.</returns>
    public override string ToString() => IsNull
        ? "null entity"
        : string.Create(CultureInfo.InvariantCulture, $"entity {Index}v{Generation}");
}
