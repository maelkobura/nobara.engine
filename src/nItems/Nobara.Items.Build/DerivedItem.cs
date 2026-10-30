// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml.Linq;
using Nobara.ResourceCompiler.Plugins;

namespace Nobara.Items.Build;

/// <summary>
/// Builds the items that the plugin of another resource type derives from its
/// resources, such as the item of a block. The derived item takes the
/// namespace and the name of its source resource:
/// <c>block:example.blocks.dirt</c> gives <c>item:example.blocks.dirt</c>.
/// </summary>
/// <remarks>
/// A plugin that derives items references this assembly and returns the
/// result of <see cref="Create"/> from <see cref="ResourcePlugin.Derive"/>.
/// The module whose resources are compiled must depend on
/// <c>nobara.items</c>, so that the type <c>Item</c> is known. nItems knows
/// nothing of the source type: at run time, the item gives the identifier of
/// its source in <c>ItemType.DerivedFrom</c>.
/// </remarks>
/// <example>
/// <code>
/// public override IEnumerable&lt;DerivedResource&gt; Derive(ResourceContext context)
/// {
///     yield return DerivedItem.Create(
///         maxStack: 16,
///         components: [DerivedItem.Component("example.blocks.placeable", new XElement("Block", context.ObjectId))]);
/// }
/// </code>
/// </example>
public static class DerivedItem
{
    /// <summary>The XML element of the type: <c>Item</c>.</summary>
    public const string Element = "Item";

    /// <summary>Creates a derived item.</summary>
    /// <param name="maxStack">The number of items in one stack, or <see langword="null"/> for the default of the distribution.</param>
    /// <param name="displayName">The localization key of the name, or <see langword="null"/> to derive it from the identifier.</param>
    /// <param name="components">The <c>&lt;Component&gt;</c> elements of the item, such as those built by <see cref="Component"/>.</param>
    /// <param name="properties">A <c>&lt;Properties&gt;</c> element of free properties, or <see langword="null"/>.</param>
    /// <returns>The derived resource, compiled against the schema of the type like a written item.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxStack"/> is less than 1.</exception>
    public static DerivedResource Create(int? maxStack = null, string? displayName = null, IEnumerable<XElement>? components = null, XElement? properties = null)
    {
        var content = new XElement("Content");
        if (maxStack is { } stack)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(stack, 1, nameof(maxStack));
            content.Add(new XElement("MaxStack", stack.ToString(CultureInfo.InvariantCulture)));
        }

        if (displayName is not null)
        {
            content.Add(new XElement("DisplayName", displayName));
        }

        foreach (XElement component in components ?? [])
        {
            content.Add(new XElement(component));
        }

        return new DerivedResource(Element, content) { Properties = properties is null ? null : new XElement(properties) };
    }

    /// <summary>Builds a <c>&lt;Component&gt;</c> element.</summary>
    /// <param name="name">The qualified name of the component: <c>example.blocks.placeable</c>.</param>
    /// <param name="values">Its values, as child elements.</param>
    /// <returns>The element.</returns>
    public static XElement Component(string name, params XElement[] values)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new XElement("Component", new XAttribute("ref", name), values);
    }
}
