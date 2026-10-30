// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Core;

namespace Nobara.Items;

/// <summary>
/// An immutable set of item components and their values: the default
/// components of an item, or those of a stack that changed some of them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="With{T}"/> and <see cref="Without"/> return a new set and leave
/// this one unchanged. Two sets are equal when they have the same types and
/// values that are equal by the equality of each type.
/// </para>
/// <para>Reading a value allocates nothing.</para>
/// </remarks>
public sealed class ItemComponents : IEquatable<ItemComponents>
{
    // Sorted by the number of the type.
    private readonly ItemComponentType[] _types;
    private readonly object[] _values;

    private ItemComponents(ItemComponentType[] types, object[] values)
    {
        _types = types;
        _values = values;
        Types = Array.AsReadOnly(types);
    }

    /// <summary>Gets the set without components.</summary>
    public static ItemComponents Empty { get; } = new([], []);

    /// <summary>Gets the number of components.</summary>
    public int Count => _types.Length;

    /// <summary>Gets the types of the components, in order of registration of the types.</summary>
    public IReadOnlyList<ItemComponentType> Types { get; }

    /// <summary>Tells whether the set has a component.</summary>
    /// <param name="type">The type of the component.</param>
    /// <returns><see langword="true"/> when the set has it.</returns>
    [HotPath]
    public bool Contains(ItemComponentType type) => IndexOf(type) >= 0;

    /// <summary>Reads the value of a component.</summary>
    /// <typeparam name="T">The type of the values.</typeparam>
    /// <param name="type">The type of the component.</param>
    /// <param name="value">The value, or the default of <typeparamref name="T"/> when the set does not have the component.</param>
    /// <returns><see langword="true"/> when the set has the component.</returns>
    [HotPath]
    public bool TryGet<T>(ItemComponentType<T> type, [MaybeNullWhen(false)] out T value)
        where T : notnull
    {
        int index = IndexOf(type);
        if (index < 0)
        {
            value = default;
            return false;
        }

        value = (T)_values[index];
        return true;
    }

    /// <summary>Reads the value of a component.</summary>
    /// <typeparam name="T">The type of the values.</typeparam>
    /// <param name="type">The type of the component.</param>
    /// <returns>The value.</returns>
    /// <exception cref="KeyNotFoundException">The set does not have the component.</exception>
    [HotPath]
    public T Get<T>(ItemComponentType<T> type)
        where T : notnull =>
        TryGet(type, out T? value) ? value : throw new KeyNotFoundException($"The components have no '{type.Name}'.");

    /// <summary>Reads the value of a component without knowing its type.</summary>
    /// <param name="type">The type of the component.</param>
    /// <returns>The value, or <see langword="null"/> when the set does not have the component.</returns>
    public object? GetValue(ItemComponentType type)
    {
        int index = IndexOf(type);
        return index < 0 ? null : _values[index];
    }

    /// <summary>Returns a set where a component has a value, added or replaced.</summary>
    /// <typeparam name="T">The type of the values.</typeparam>
    /// <param name="type">The type of the component.</param>
    /// <param name="value">The value.</param>
    /// <returns>The new set; this one when the component already has an equal value.</returns>
    public ItemComponents With<T>(ItemComponentType<T> type, T value)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        int index = IndexOf(type);
        if (index >= 0)
        {
            if (type.Equality.Equals((T)_values[index], value))
            {
                return this;
            }

            object[] values = (object[])_values.Clone();
            values[index] = value;
            return new ItemComponents(_types, values);
        }

        var entries = new List<(ItemComponentType, object)>(_types.Length + 1);
        for (int i = 0; i < _types.Length; i++)
        {
            entries.Add((_types[i], _values[i]));
        }

        entries.Add((type, value));
        return Create(entries);
    }

    /// <summary>Returns a set without a component.</summary>
    /// <param name="type">The type of the component.</param>
    /// <returns>The new set; this one when it does not have the component.</returns>
    public ItemComponents Without(ItemComponentType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        int index = IndexOf(type);
        if (index < 0)
        {
            return this;
        }

        var entries = new List<(ItemComponentType, object)>(_types.Length - 1);
        for (int i = 0; i < _types.Length; i++)
        {
            if (i != index)
            {
                entries.Add((_types[i], _values[i]));
            }
        }

        return Create(entries);
    }

    /// <inheritdoc/>
    public bool Equals(ItemComponents? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other._types.Length != _types.Length)
        {
            return false;
        }

        for (int i = 0; i < _types.Length; i++)
        {
            if (!ReferenceEquals(_types[i], other._types[i]) || !_types[i].ValuesEqual(_values[i], other._values[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ItemComponents);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        for (int i = 0; i < _types.Length; i++)
        {
            hash.Add(_types[i].Id);
            hash.Add(_types[i].HashValue(_values[i]));
        }

        return hash.ToHashCode();
    }

    /// <summary>Returns the names of the components.</summary>
    /// <returns>For example <c>{example.food.edible, example.tools.durability}</c>.</returns>
    public override string ToString() => "{" + string.Join(", ", _types.Select(t => t.Name)) + "}";

    /// <summary>Creates a set from components of distinct types.</summary>
    internal static ItemComponents Create(List<(ItemComponentType Type, object Value)> entries)
    {
        if (entries.Count == 0)
        {
            return Empty;
        }

        entries.Sort((a, b) => a.Type.Id.CompareTo(b.Type.Id));
        var types = new ItemComponentType[entries.Count];
        var values = new object[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            types[i] = entries[i].Type;
            values[i] = entries[i].Value;
        }

        return new ItemComponents(types, values);
    }

    [HotPath]
    private int IndexOf(ItemComponentType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        ItemComponentType[] types = _types;
        for (int i = 0; i < types.Length; i++)
        {
            if (ReferenceEquals(types[i], type))
            {
                return i;
            }
        }

        return -1;
    }
}
