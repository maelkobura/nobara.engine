// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Items;

namespace Nobara.Blocks;

/// <summary>
/// The block registry: the types of blocks defined by the <c>Block</c>
/// resources of every module, their states, and their tags. Resolve it with
/// <c>context.Resolve&lt;IBlockRegistry&gt;()</c>; there is one per context.
/// </summary>
/// <remarks>
/// <para>
/// nBlocks builds the registry while it initializes, before the modules that
/// depend on it, and freezes it: no type or state is added afterwards. A
/// reload of the resources in development mode is the only change; it updates
/// the types in place when their states stay the same.
/// </para>
/// <para>
/// A state is a <see cref="uint"/> index: 0 is <c>block:nobara.air</c>, then
/// the states of each type, types sorted by identifier, states in the order of
/// the properties. The same modules with the same overrides give the same
/// indices in every context. Never save an index nor send it as such: convert
/// it with <see cref="Describe"/>.
/// </para>
/// <para>Call the registry on the loop thread of the context.</para>
/// </remarks>
public interface IBlockRegistry
{
    /// <summary>Gets whether the registry is built, which happens while nBlocks initializes.</summary>
    bool IsFrozen { get; }

    /// <summary>Gets the most states that one type may have: the configuration key <c>blocks.maxStatesPerType</c>, 1024 unless the distribution sets it.</summary>
    int MaxStatesPerType { get; }

    /// <summary>Gets the types, by index; <c>block:nobara.air</c> is the first.</summary>
    /// <exception cref="InvalidOperationException">The registry is not built yet.</exception>
    IReadOnlyList<BlockType> Types { get; }

    /// <summary>Gets <c>block:nobara.air</c>, whose only state is 0.</summary>
    /// <exception cref="InvalidOperationException">The registry is not built yet.</exception>
    BlockType Air { get; }

    /// <summary>Gets the number of states of every type.</summary>
    int StateCount { get; }

    /// <summary>Gets the block tags, sorted by identifier.</summary>
    /// <exception cref="InvalidOperationException">The registry is not built yet.</exception>
    IReadOnlyList<BlockTag> Tags { get; }

    /// <summary>Gets the item component that places a block, <c>nobara.blocks.placeable</c>, which <c>&lt;CreateItem&gt;</c> adds to the item of a block.</summary>
    /// <remarks>The module that puts blocks in the world attaches its handler for <c>UseOnBlock</c>.</remarks>
    ItemComponentType<PlaceableBlock> Placeable { get; }

    /// <summary>Gets a type by its object identifier.</summary>
    /// <param name="objectId">The identifier: <c>block:example.blocks.lamp</c>.</param>
    /// <returns>The type.</returns>
    /// <exception cref="KeyNotFoundException">No type has that identifier.</exception>
    /// <exception cref="InvalidOperationException">The registry is not built yet.</exception>
    BlockType Get(string objectId);

    /// <summary>Finds a type by its object identifier.</summary>
    /// <param name="objectId">The identifier.</param>
    /// <param name="type">The type, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the type exists.</returns>
    /// <exception cref="InvalidOperationException">The registry is not built yet.</exception>
    bool TryGet(string objectId, [NotNullWhen(true)] out BlockType? type);

    /// <summary>Finds a block tag.</summary>
    /// <param name="id">The identifier, short (<c>#example.wood</c>) or in full form (<c>#block:example.wood</c>).</param>
    /// <param name="tag">The tag, or <see langword="null"/> when no module contributes to it.</param>
    /// <returns><see langword="true"/> when the tag exists.</returns>
    /// <exception cref="ArgumentException">The identifier is not a tag identifier, or names another registry.</exception>
    /// <exception cref="InvalidOperationException">The registry is not built yet.</exception>
    bool TryGetTag(string id, [NotNullWhen(true)] out BlockTag? tag);

    /// <summary>Gets the type of a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No state has that index.</exception>
    [HotPath]
    BlockType TypeOf(uint state);

    /// <summary>Gets the opacity of a state, variants applied.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The opacity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No state has that index.</exception>
    [HotPath]
    BlockOpacity GetOpacity(uint state);

    /// <summary>Gets the collision of a state, variants applied.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The collision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No state has that index.</exception>
    [HotPath]
    BlockCollision GetCollision(uint state);

    /// <summary>Gets the hardness of a state, variants applied; <see cref="float.PositiveInfinity"/> when it is unbreakable.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The hardness.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No state has that index.</exception>
    [HotPath]
    float GetHardness(uint state);

    /// <summary>Gets the number of the value that a state gives a property.</summary>
    /// <param name="state">A state of the type of the property.</param>
    /// <param name="property">The property.</param>
    /// <returns>The number of the value, from 0 to <see cref="StateProperty.Count"/> minus 1.</returns>
    /// <exception cref="ArgumentException">The state is not of the type of the property.</exception>
    [HotPath]
    int GetValue(uint state, StateProperty property);

    /// <summary>Gets the state whose properties are those of a state, except one: <c>With(state, lit, 1)</c>.</summary>
    /// <param name="state">A state of the type of the property.</param>
    /// <param name="property">The property to change.</param>
    /// <param name="value">The number of the new value.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentException">The state is not of the type of the property.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The property has no such value.</exception>
    [HotPath]
    uint With(uint state, StateProperty property, int value);

    /// <summary>
    /// Gets the state after a quarter turn around the vertical axis, clockwise
    /// seen from above: the orientation turns, and so do the properties that
    /// declare <c>rotatesTo</c> or <c>rotation</c>.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The turned state; the state itself for a type that does not turn.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No state has that index.</exception>
    [HotPath]
    uint Rotate(uint state);

    /// <summary>Tells whether the type of a state is a member of a tag.</summary>
    /// <param name="state">The state.</param>
    /// <param name="tag">The tag.</param>
    /// <returns><see langword="true"/> when its type is a member.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No state has that index.</exception>
    [HotPath]
    bool HasTag(uint state, BlockTag tag);

    /// <summary>
    /// Gets the stable identifier of a state, which disk and network use:
    /// the identifier of its type, and the value of each property in index
    /// order, such as <c>block:example.blocks.lamp[lit=true]</c>.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The identifier; the identifier of the type alone when it has no property.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No state has that index.</exception>
    string Describe(uint state);

    /// <summary>Finds the state that a stable identifier names; the properties it does not give take their default value.</summary>
    /// <param name="id">The identifier: <c>block:example.blocks.lamp[lit=true]</c> or <c>block:example.blocks.lamp</c>.</param>
    /// <param name="state">The state.</param>
    /// <returns><see langword="true"/> when the identifier names a state.</returns>
    bool TryParseState(string id, out uint state);

    /// <summary>
    /// Gets the value of a free property for every state, variants applied:
    /// the value of the last variant that matches the state, or the value of
    /// the resource. At each level, the form qualified by the short name of
    /// the reader (<c>Motion.Weight</c>) wins over the plain form.
    /// </summary>
    /// <param name="reader">The module that reads the property, which declared it.</param>
    /// <param name="property">The property, as <see cref="ModuleResources.DeclareProperty"/> returned it.</param>
    /// <returns>A new array, by state: the value, or <see langword="null"/> where the state does not have the property.</returns>
    /// <remarks>Call it while the module initializes or starts, to build a table of its own: it allocates.</remarks>
    /// <exception cref="ArgumentException"><paramref name="property"/> is not declared by <paramref name="reader"/>.</exception>
    /// <exception cref="BlockLoadException">A variant gives the property a value of another type than the declared one.</exception>
    GenericElement?[] ResolveProperty(ModuleContext reader, FreeProperty property);
}
