// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Items.Schemas;

namespace Nobara.Items;

/// <summary>
/// The content of an item resource, decoded once per process and shared by
/// every context. Each context builds its own <see cref="ItemType"/> from it,
/// with the component types registered in that context.
/// </summary>
internal sealed class ItemDefinition
{
    private ItemDefinition(ResourceData data, Item content, GenericElement[] components)
    {
        Data = data;
        Content = content;
        Components = components;
    }

    /// <summary>Gets the resource, overrides applied.</summary>
    public ResourceData Data { get; }

    /// <summary>Gets the native fields. Never change it: it is shared.</summary>
    public Item Content { get; }

    /// <summary>Gets the components and their default values, in the generic representation, in the order written.</summary>
    public IReadOnlyList<GenericElement> Components { get; }

    /// <summary>Decodes a resource of type <c>item</c>.</summary>
    /// <param name="data">The resource.</param>
    /// <returns>The definition.</returns>
    public static ItemDefinition Decode(ResourceData data)
    {
        Item content = Item.Parser.ParseFrom(data.Content.Span);
        var components = new GenericElement[content.Component.Count];
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = GenericElement.Parse(content.Component[i].Span);
        }

        return new ItemDefinition(data, content, components);
    }
}
