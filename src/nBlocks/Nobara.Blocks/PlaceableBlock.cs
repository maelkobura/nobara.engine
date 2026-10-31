// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Items;

namespace Nobara.Blocks;

/// <summary>
/// The value of the item component <c>nobara.blocks.placeable</c>: the block
/// that an item places and the state it places. <c>&lt;CreateItem&gt;</c> adds
/// it to the item of a block.
/// </summary>
/// <remarks>
/// nBlocks registers the component and attaches no handler: the module that
/// puts blocks in the world handles <c>UseOnBlock</c> for it. The values are
/// <c>&lt;Block&gt;</c>, the block, and <c>&lt;State&gt;</c>, optional, the
/// values that differ from the default state, such as <c>lit=true</c>.
/// </remarks>
public sealed class PlaceableBlock : IEquatable<PlaceableBlock>
{
    /// <summary>The qualified name of the component.</summary>
    public const string ComponentName = "nobara.blocks.placeable";

    private readonly uint _state;
    private readonly bool _explicit;

    internal PlaceableBlock(BlockType block, uint? state)
    {
        Block = block;
        _state = state ?? 0;
        _explicit = state.HasValue;
    }

    /// <summary>Gets the type of block placed.</summary>
    public BlockType Block { get; }

    /// <summary>Gets the state placed: the one the item names, or the default state of the block.</summary>
    public uint State
    {
        [HotPath]
        get => _explicit ? _state : Block.DefaultState;
    }

    /// <inheritdoc/>
    public bool Equals(PlaceableBlock? other) =>
        other is not null && ReferenceEquals(Block, other.Block) && _explicit == other._explicit && _state == other._state;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PlaceableBlock);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Block.Index, _explicit, _state);

    /// <summary>Returns the block and the state.</summary>
    /// <returns>For example <c>block:example.blocks.lamp, state 12</c>.</returns>
    public override string ToString() => $"{Block.ObjectId}, state {State}";

    /// <summary>Reads the values of the component.</summary>
    internal static PlaceableBlock Read(ComponentValues values, IBlockRegistry registry)
    {
        string id = values.GetObjectId("Block");
        string text = values.GetString("State", string.Empty);
        if (!registry.TryGet(id, out BlockType? block))
        {
            throw values.Error($"<Block> is '{id}', which no module defines as a block.", values.Element.Child("Block"));
        }

        if (text.Length == 0)
        {
            return new PlaceableBlock(block, null);
        }

        (int Property, int Value)[]? condition = block.Layout.ParseCondition(text, out string error);
        if (condition is null)
        {
            throw values.Error($"<State> of '{id}': {error}.", values.Element.Child("State"));
        }

        uint state = block.DefaultState;
        foreach ((int property, int value) in condition)
        {
            state = registry.With(state, block.Properties[property], value);
        }

        return new PlaceableBlock(block, state);
    }
}
