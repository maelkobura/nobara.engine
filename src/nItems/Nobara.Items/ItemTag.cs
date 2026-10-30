// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Collections;

namespace Nobara.Items;

/// <summary>
/// A tag of the item registry, such as <c>#item:example.meat</c>: items that
/// several modules group under one name. The core resolves the tags; nItems
/// turns each one into a set of item indices.
/// </summary>
/// <remarks>
/// Item tags are distinct from the tags of other registries: <c>#example.wood</c>
/// of the items is not <c>#example.wood</c> of the blocks. Keep references to a
/// tag: a reload in development mode changes its members in place.
/// </remarks>
public sealed class ItemTag
{
    private readonly BitSet _members;

    internal ItemTag(string name, int capacity)
    {
        Name = name;
        Id = "#" + ItemsModule.ResourceType + ":" + name;
        _members = new BitSet(Math.Max(capacity, 1));
    }

    /// <summary>Gets the identifier in full form: <c>#item:example.meat</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the identifier without the registry: <c>example.meat</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the members, nested tags expanded, in the order the core resolved them.</summary>
    public IReadOnlyList<ItemType> Members { get; private set; } = [];

    /// <summary>Tells whether an item is a member of the tag.</summary>
    /// <param name="item">The item.</param>
    /// <returns><see langword="true"/> when the item is a member.</returns>
    [HotPath]
    public bool Contains(ItemType item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _members.Contains(item.Index);
    }

    /// <summary>Returns the identifier.</summary>
    /// <returns>The identifier in full form.</returns>
    public override string ToString() => Id;

    internal void SetMembers(IReadOnlyList<ItemType> members)
    {
        _members.Clear();
        foreach (ItemType item in members)
        {
            _members.Add(item.Index);
        }

        Members = members;
    }
}
