// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Nobara.ResourceCompiler.Plugins;

[assembly: ResourcePlugin(typeof(Nobara.Items.Build.ItemPlugin))]

namespace Nobara.Items.Build;

/// <summary>
/// The resource plugin of the type <c>Item</c>: the rules that the schema of
/// the type cannot declare.
/// </summary>
/// <remarks>
/// The schema already checks the native fields: <c>&lt;MaxStack&gt;</c> of at
/// least 1, <c>&lt;DisplayName&gt;</c> as a localization key, a
/// <c>&lt;Model&gt;</c> that names a <c>mesh:</c> object and a
/// <c>&lt;Sprite&gt;</c> that names a <c>texture://</c> resource, never both,
/// and the form of each <c>&lt;Component&gt;</c>. The values of the
/// components are checked when the game loads them.
/// </remarks>
public sealed class ItemPlugin : ResourcePlugin
{
    /// <summary>The diagnostic of a <c>&lt;Visual&gt;</c> without model or sprite.</summary>
    public const string EmptyVisual = "NITM001";

    /// <summary>The diagnostic of a component declared twice in one item.</summary>
    public const string DuplicateComponent = "NITM002";

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> ResourceTypes { get; } = [DerivedItem.Element];

    /// <inheritdoc/>
    public override void Validate(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        XElement? visual = context.Content.Element("Visual");
        if (visual is not null && !visual.HasElements)
        {
            context.Error(EmptyVisual, "<Visual> declares a <Model> or a <Sprite>; remove it when the item has no look of its own", visual);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement component in context.Content.Elements("Component"))
        {
            string? name = component.Attribute("ref")?.Value.Trim();
            if (name is not null && !seen.Add(name))
            {
                context.Error(DuplicateComponent, $"the component '{name}' is declared twice in '{context.ObjectId}'", component);
            }
        }
    }
}
