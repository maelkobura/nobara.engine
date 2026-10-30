// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Core;

namespace Nobara.Items;

/// <summary>
/// The item registry: the item types defined by the <c>Item</c> resources of
/// every module, their tags, and the types of item components. Resolve it
/// with <c>context.Resolve&lt;IItemRegistry&gt;()</c>; there is one per
/// context.
/// </summary>
/// <remarks>
/// <para>
/// Modules register their component types while they initialize. When nItems
/// starts, once every module has initialized, the registry converts the
/// components of every item, resolves the tags, and freezes: from then on no
/// item, component type, or interaction kind is added, and the items can be
/// read. A reload of the resources in development mode is the only change
/// after that: it updates the items in place.
/// </para>
/// <para>Call the registry on the loop thread of the context.</para>
/// </remarks>
public interface IItemRegistry
{
    /// <summary>Gets whether the registry is frozen, which happens when nItems starts.</summary>
    bool IsFrozen { get; }

    /// <summary>Gets the default number of items in one stack: the configuration key <c>items.defaultMaxStack</c>, 64 unless the distribution sets it.</summary>
    int DefaultMaxStack { get; }

    /// <summary>Gets the items, by index.</summary>
    /// <exception cref="InvalidOperationException">The registry is not frozen yet.</exception>
    IReadOnlyList<ItemType> Items { get; }

    /// <summary>Gets the item tags, sorted by identifier.</summary>
    /// <exception cref="InvalidOperationException">The registry is not frozen yet.</exception>
    IReadOnlyList<ItemTag> Tags { get; }

    /// <summary>Gets the component types, in order of registration.</summary>
    IReadOnlyList<ItemComponentType> ComponentTypes { get; }

    /// <summary>Gets an item by its object identifier.</summary>
    /// <param name="objectId">The identifier: <c>item:example.food.steak</c>.</param>
    /// <returns>The item.</returns>
    /// <exception cref="KeyNotFoundException">No item has that identifier.</exception>
    /// <exception cref="InvalidOperationException">The registry is not frozen yet.</exception>
    ItemType Get(string objectId);

    /// <summary>Finds an item by its object identifier.</summary>
    /// <param name="objectId">The identifier: <c>item:example.food.steak</c>.</param>
    /// <param name="item">The item, or <see langword="null"/> when none has that identifier.</param>
    /// <returns><see langword="true"/> when the item exists.</returns>
    /// <exception cref="InvalidOperationException">The registry is not frozen yet.</exception>
    bool TryGet(string objectId, [NotNullWhen(true)] out ItemType? item);

    /// <summary>Gets an item by its index.</summary>
    /// <param name="index">The index, from 0 to the number of items minus 1.</param>
    /// <returns>The item.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No item has that index.</exception>
    /// <exception cref="InvalidOperationException">The registry is not frozen yet.</exception>
    [HotPath]
    ItemType GetByIndex(int index);

    /// <summary>Finds an item tag.</summary>
    /// <param name="id">The identifier, short (<c>#example.meat</c>) or in full form (<c>#item:example.meat</c>).</param>
    /// <param name="tag">The tag, or <see langword="null"/> when no module contributes to it.</param>
    /// <returns><see langword="true"/> when the tag exists.</returns>
    /// <exception cref="ArgumentException">The identifier is not a tag identifier, or names another registry.</exception>
    /// <exception cref="InvalidOperationException">The registry is not frozen yet.</exception>
    bool TryGetTag(string id, [NotNullWhen(true)] out ItemTag? tag);

    /// <summary>
    /// Registers a type of item component. Items declare its default values
    /// with <c>&lt;Component ref="…"&gt;</c>, which the reader converts once,
    /// when nItems starts.
    /// </summary>
    /// <typeparam name="T">The type of the values, immutable.</typeparam>
    /// <param name="module">The module that registers the type.</param>
    /// <param name="name">The qualified name: the identifier of the module, a dot, and a lowercase name, such as <c>example.food.edible</c>.</param>
    /// <param name="read">Converts the values that an item writes; see <see cref="ComponentValues"/>.</param>
    /// <param name="equality">The equality of the values, which decides whether two stacks merge.</param>
    /// <returns>The component type.</returns>
    /// <exception cref="ArgumentException">The name does not start with the identifier of the module, or is already registered.</exception>
    /// <exception cref="InvalidOperationException">The registry is frozen: component types are registered while modules initialize.</exception>
    ItemComponentType<T> RegisterComponent<T>(ModuleContext module, string name, Func<ComponentValues, T> read, IEqualityComparer<T> equality)
        where T : notnull;

    /// <summary>Finds a component type by its qualified name.</summary>
    /// <param name="name">The qualified name.</param>
    /// <param name="type">The type, or <see langword="null"/> when none has that name.</param>
    /// <returns><see langword="true"/> when the type exists.</returns>
    bool TryGetComponentType(string name, [NotNullWhen(true)] out ItemComponentType? type);
}
