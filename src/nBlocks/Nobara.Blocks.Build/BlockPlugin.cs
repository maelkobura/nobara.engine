// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml.Linq;
using Nobara.Blocks.Schemas;
using Nobara.Items.Build;
using Nobara.ResourceCompiler.Plugins;

[assembly: ResourcePlugin(typeof(Nobara.Blocks.Build.BlockPlugin))]

namespace Nobara.Blocks.Build;

/// <summary>
/// The resource plugin of the type <c>Block</c>: the rules that the schema
/// cannot declare, and the item that <c>&lt;CreateItem&gt;</c> generates.
/// </summary>
/// <remarks>
/// The rules are those that nBlocks applies again when it loads the blocks,
/// overrides applied: a resource that compiles also loads, unless an override
/// breaks it or the distribution lowers <c>blocks.maxStatesPerType</c>.
/// </remarks>
public sealed class BlockPlugin : ResourcePlugin
{
    /// <summary>The diagnostic of a <c>&lt;Shape&gt;</c> that is not a <c>shape:</c> object identifier.</summary>
    public const string InvalidShape = LayoutProblem.InvalidShape;

    /// <summary>The diagnostic of a <c>&lt;Collision&gt;</c> other than <c>shape</c> or <c>none</c>.</summary>
    public const string InvalidCollision = LayoutProblem.InvalidCollision;

    /// <summary>The diagnostic of an <c>&lt;Opacity&gt;</c> other than <c>opaque</c>, <c>cutout</c>, or <c>translucent</c>.</summary>
    public const string InvalidOpacity = LayoutProblem.InvalidOpacity;

    /// <summary>The diagnostic of a <c>&lt;Hardness&gt;</c> that is neither a number of at least 0 nor <c>unbreakable</c>.</summary>
    public const string InvalidHardness = LayoutProblem.InvalidHardness;

    /// <summary>The diagnostic of an unknown set of <c>&lt;Orientations&gt;</c>.</summary>
    public const string InvalidOrientations = LayoutProblem.InvalidOrientations;

    /// <summary>The diagnostic of a state property: name, bounds, values, or default.</summary>
    public const string InvalidProperty = LayoutProblem.InvalidProperty;

    /// <summary>The diagnostic of a <c>rotatesTo</c> or <c>rotation</c> that does not describe a quarter turn.</summary>
    public const string InvalidRotation = LayoutProblem.InvalidRotation;

    /// <summary>The diagnostic of a cell of <c>&lt;Footprint&gt;</c>: the origin, twice, or too far.</summary>
    public const string InvalidFootprint = LayoutProblem.InvalidFootprint;

    /// <summary>The diagnostic of the condition of a <c>&lt;Variant&gt;</c>.</summary>
    public const string InvalidVariant = LayoutProblem.InvalidVariant;

    /// <summary>The diagnostic of the state of <c>&lt;CreateItem&gt;</c>.</summary>
    public const string InvalidItem = LayoutProblem.InvalidItem;

    /// <summary>The warning of a type with more states than the default of <c>blocks.maxStatesPerType</c>.</summary>
    public const string TooManyStates = LayoutProblem.TooManyStates;

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> ResourceTypes { get; } = ["Block"];

    /// <inheritdoc/>
    public override void Validate(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problems = new List<LayoutProblem>();
        BlockLayout? layout = BlockLayout.Read(context.Parse(Block.Parser), problems);
        foreach (LayoutProblem problem in problems)
        {
            context.Error(problem.Code, problem.Message, Find(context.Content, problem.Location));
        }

        if (layout is not null && layout.StateCount > BlockLayout.DefaultMaxStates)
        {
            string count = layout.StateCount > int.MaxValue ? "more than " + int.MaxValue.ToString(CultureInfo.InvariantCulture) : layout.StateCount.ToString(CultureInfo.InvariantCulture);
            context.Warning(
                TooManyStates,
                $"'{context.ObjectId}' has {count} states, above the default of blocks.maxStatesPerType, {BlockLayout.DefaultMaxStates}: the game refuses it to start unless the distribution raises the key in engine.nobara",
                context.Content.Element("States") ?? context.Element);
        }
    }

    /// <inheritdoc/>
    public override IEnumerable<DerivedResource> Derive(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Block content = context.Parse(Block.Parser);
        if (content.CreateItem is not { } item || BlockLayout.Read(content, []) is null)
        {
            yield break;
        }

        var values = new List<XElement> { new("Block", context.ObjectId) };
        if (item.State.Length > 0)
        {
            values.Add(new XElement("State", item.State));
        }

        yield return DerivedItem.Create(
            maxStack: item.MaxStack > 0 ? item.MaxStack : null,
            displayName: item.DisplayName.Length > 0 ? item.DisplayName : null,
            components: [DerivedItem.Component(PlaceableBlock.ComponentName, [.. values])],
            properties: context.Content.Element("CreateItem")?.Element("Properties"));
    }

    // The node a problem is about: the element of its path, then its attribute
    // or child element; the closest that exists.
    private static XObject? Find(XElement content, LayoutLocation location)
    {
        XElement current = content;
        string[] parts = location.Path.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            IEnumerable<XElement> matches = current.Elements(parts[i]);
            XElement? next = last && location.Index >= 0 ? matches.ElementAtOrDefault(location.Index) : matches.FirstOrDefault();
            if (next is null)
            {
                return current;
            }

            current = next;
        }

        if (location.Attribute is not { } name)
        {
            return current;
        }

        string element = char.ToUpperInvariant(name[0]) + name[1..];
        string attribute = char.ToLowerInvariant(name[0]) + name[1..];
        return (XObject?)current.Attribute(attribute) ?? current.Element(element) ?? current;
    }
}
