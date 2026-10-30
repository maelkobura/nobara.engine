// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Resources;

namespace Nobara.Items;

/// <summary>
/// A type of item in the registry, such as <c>item:example.food.steak</c>:
/// its native fields, its look, and its components with their default values.
/// It describes the type, never an instance: stacks of items belong to the
/// module that stores them.
/// </summary>
/// <remarks>
/// <para>
/// There is one instance per item and per context, created when nItems
/// starts. Keep references to it: a reload of the resources in development
/// mode changes its values in place, and the object stays the same.
/// </para>
/// <para>Read it on the loop thread of the context.</para>
/// </remarks>
public sealed class ItemType
{
    internal ItemType(string objectId, int index, ResourceData data)
    {
        ObjectId = objectId;
        Index = index;
        Data = data;
    }

    /// <summary>Gets the object identifier: <c>item:example.food.steak</c>. Disk and network name the item by it.</summary>
    public string ObjectId { get; }

    /// <summary>
    /// Gets the index of the item in the registry, from 0: the position of its
    /// identifier among the identifiers of every item, sorted by ordinal
    /// comparison. The same items give the same indices on every side.
    /// </summary>
    /// <remarks>Use it in hot paths and tables; never save it.</remarks>
    public int Index { get; }

    /// <summary>Gets how many items one stack holds: <c>&lt;MaxStack&gt;</c>, or the default of the distribution.</summary>
    public int MaxStack { get; internal set; }

    /// <summary>Gets the localization key of the name: <c>&lt;DisplayName&gt;</c>, or <c>item.</c> followed by the identifier without its type, such as <c>item.example.food.steak</c>.</summary>
    public string DisplayName { get; internal set; } = string.Empty;

    /// <summary>Gets the declared look of the item: a model, a sprite, or none.</summary>
    public ItemVisual Visual { get; internal set; } = ItemVisual.None;

    /// <summary>
    /// Gets the object identifier of the resource that generated this item,
    /// such as the block of an item derived from a block; an empty string for
    /// an item written as such.
    /// </summary>
    public string DerivedFrom => Data.DerivedFrom;

    /// <summary>Gets where the item is written.</summary>
    public SourcePosition Source => Data.Source;

    /// <summary>Gets the resource of the item, with its free properties.</summary>
    public ResourceData Data { get; internal set; }

    /// <summary>Gets the components of the item with their default values. A stack starts with these.</summary>
    public ItemComponents Components { get; internal set; } = ItemComponents.Empty;

    /// <summary>Gets the handlers of each interaction kind, by the index of the kind, for the default components.</summary>
    internal HandlerEntry[][] Handlers { get; set; } = [];

    /// <summary>Tells whether the item is a member of a tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns><see langword="true"/> when the item is a member.</returns>
    [HotPath]
    public bool HasTag(ItemTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.Contains(this);
    }

    /// <summary>Returns the object identifier.</summary>
    /// <returns>The identifier.</returns>
    public override string ToString() => ObjectId;
}

/// <summary>What an item declares to show it.</summary>
public enum ItemVisualKind
{
    /// <summary>No look of its own, such as an item derived from a block, shown as its block.</summary>
    None,

    /// <summary>A 3D model: <c>&lt;Model&gt;</c>, a <c>mesh:</c> object.</summary>
    Model,

    /// <summary>A sprite, shown with thickness: <c>&lt;Sprite&gt;</c>, a <c>texture://</c> resource.</summary>
    Sprite,
}

/// <summary>
/// The look that an item declares in <c>&lt;Visual&gt;</c>. nItems checks and
/// keeps it, and draws nothing.
/// </summary>
/// <param name="Kind">What the item declares.</param>
/// <param name="Reference">The <c>mesh:</c> object of a model or the <c>texture://</c> resource of a sprite; empty for <see cref="ItemVisualKind.None"/>.</param>
public sealed record ItemVisual(ItemVisualKind Kind, string Reference)
{
    /// <summary>Gets the look of an item that declares none.</summary>
    public static ItemVisual None { get; } = new(ItemVisualKind.None, string.Empty);
}
