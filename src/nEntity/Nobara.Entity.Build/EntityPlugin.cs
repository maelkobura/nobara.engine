// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Nobara.ResourceCompiler.Plugins;

[assembly: ResourcePlugin(typeof(Nobara.Entity.Build.EntityPlugin))]

namespace Nobara.Entity.Build;

/// <summary>
/// The resource plugin of the type <c>Entity</c>: the rules that the schema of
/// the type cannot declare.
/// </summary>
/// <remarks>
/// The resource compiler already checks the form of each
/// <c>&lt;Component&gt;</c>: a qualified name in <c>ref</c>, <c>optional</c>
/// set to <c>true</c> or <c>false</c>, and the object identifiers written in
/// the values, except in an optional component. It keeps the position of each
/// value for the messages of the game. The values themselves are checked when
/// the game loads the definition, against the C# type of each component.
/// </remarks>
public sealed class EntityPlugin : ResourcePlugin
{
    /// <summary>The diagnostic of a component declared twice in one entity.</summary>
    public const string DuplicateComponent = "NENT001";

    /// <summary>The diagnostic of the parent link written in a definition.</summary>
    public const string ParentLinkInDefinition = "NENT002";

    /// <summary>The XML element of the type: <c>Entity</c>.</summary>
    public const string Element = "Entity";

    private const string ParentLink = "nobara.entity.parent";

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> ResourceTypes { get; } = [Element];

    /// <inheritdoc/>
    public override void Validate(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement component in context.Content.Elements("Component"))
        {
            string? name = component.Attribute("ref")?.Value.Trim();
            if (name is null)
            {
                continue;
            }

            if (!seen.Add(name))
            {
                context.Error(DuplicateComponent, $"the component '{name}' is declared twice in '{context.ObjectId}'", component);
            }

            if (name == ParentLink)
            {
                context.Error(ParentLinkInDefinition, $"'{ParentLink}' is not written in a definition: the game sets the parent of an entity once it exists", component);
            }
        }
    }
}
