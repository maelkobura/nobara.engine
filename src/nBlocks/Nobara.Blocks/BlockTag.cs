// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Collections;

namespace Nobara.Blocks;

/// <summary>
/// A tag of the block registry, such as <c>#block:example.wood</c>: types of
/// blocks that several modules group under one name. The core resolves the
/// tags; nBlocks turns each one into a set of type indices.
/// </summary>
/// <remarks>
/// Tags apply to types, not to states. Block tags are distinct from the tags
/// of other registries: <c>#example.wood</c> of the blocks is not
/// <c>#example.wood</c> of the items. Keep references to a tag: a reload in
/// development mode changes its members in place.
/// </remarks>
public sealed class BlockTag
{
    private readonly BitSet _members;

    internal BlockTag(string name, int capacity)
    {
        Name = name;
        Id = "#" + BlocksModule.ResourceType + ":" + name;
        _members = new BitSet(Math.Max(capacity, 1));
    }

    /// <summary>Gets the identifier in full form: <c>#block:example.wood</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the identifier without the registry: <c>example.wood</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the member types, nested tags expanded, in the order the core resolved them.</summary>
    public IReadOnlyList<BlockType> Members { get; private set; } = [];

    /// <summary>Tells whether a type is a member of the tag.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> when the type is a member.</returns>
    [HotPath]
    public bool Contains(BlockType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _members.Contains(type.Index);
    }

    /// <summary>Returns the identifier.</summary>
    /// <returns>The identifier in full form.</returns>
    public override string ToString() => Id;

    [HotPath]
    internal bool ContainsType(int index) => _members.Contains(index);

    internal void SetMembers(IReadOnlyList<BlockType> members)
    {
        _members.Clear();
        foreach (BlockType type in members)
        {
            _members.Add(type.Index);
        }

        Members = members;
    }
}
