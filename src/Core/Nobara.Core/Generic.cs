// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using F = Nobara.Formats;

namespace Nobara.Core;

/// <summary>A position in a source file of the game, for error messages.</summary>
/// <param name="File">The path of the file, relative to the folder of its module or game, with forward slashes; empty when unknown.</param>
/// <param name="Line">The 1-based line, 0 when unknown.</param>
/// <param name="Column">The 1-based column, 0 when unknown.</param>
public readonly record struct SourcePosition(string File, int Line, int Column)
{
    /// <summary>Gets the path of the file, or an empty string when it is unknown.</summary>
    public string File { get; init; } = File ?? string.Empty;

    /// <summary>Returns the position as compilers write it.</summary>
    /// <returns>For example <c>Resources/blocks.nof(12,5)</c>, or the file alone when the line is unknown.</returns>
    public override string ToString() => Line > 0 ? $"{File}({Line},{Column})" : File ?? string.Empty;

    internal static SourcePosition From(F.SourcePosition? position) =>
        position is null ? default : new SourcePosition(position.File, (int)position.Line, (int)position.Column);
}

/// <summary>The type that the compiler recognized in a value written as text.</summary>
public enum GenericValueKind
{
    /// <summary>Text, or a value of no recognized type.</summary>
    Text,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number with a fraction or an exponent.</summary>
    Number,

    /// <summary>An object identifier such as <c>mesh:example.mesh.duck</c>.</summary>
    ObjectId,

    /// <summary>A resource identifier such as <c>texture://Example/Textures/Dirt</c>.</summary>
    ResourceId,

    /// <summary>A tag identifier such as <c>#example.wood</c> or <c>#block:example.wood</c>.</summary>
    TagId,
}

/// <summary>
/// A value of the generic representation: the text as written, and the type
/// the compiler recognized in it. The text stays authoritative.
/// </summary>
public sealed class GenericValue
{
    private readonly F.Value _value;

    private GenericValue(F.Value value)
    {
        _value = value;
        Kind = value.TypedCase switch
        {
            F.Value.TypedOneofCase.Boolean => GenericValueKind.Boolean,
            F.Value.TypedOneofCase.Integer => GenericValueKind.Integer,
            F.Value.TypedOneofCase.Number => GenericValueKind.Number,
            F.Value.TypedOneofCase.ObjectId => GenericValueKind.ObjectId,
            F.Value.TypedOneofCase.ResourceId => GenericValueKind.ResourceId,
            F.Value.TypedOneofCase.TagId => GenericValueKind.TagId,
            _ => GenericValueKind.Text,
        };
    }

    /// <summary>Gets the text as written, trimmed.</summary>
    public string Text => _value.Text;

    /// <summary>Gets the type the compiler recognized.</summary>
    public GenericValueKind Kind { get; }

    /// <summary>Reads the value as a boolean.</summary>
    /// <param name="value">The value, when it is <c>true</c> or <c>false</c>.</param>
    /// <returns><see langword="true"/> when the value is a boolean.</returns>
    public bool TryGetBoolean(out bool value)
    {
        value = Kind == GenericValueKind.Boolean && _value.Boolean;
        return Kind == GenericValueKind.Boolean;
    }

    /// <summary>Reads the value as a whole number.</summary>
    /// <param name="value">The value, when it is a whole number.</param>
    /// <returns><see langword="true"/> when the value is a whole number.</returns>
    public bool TryGetInteger(out long value)
    {
        value = Kind == GenericValueKind.Integer ? _value.Integer : 0;
        return Kind == GenericValueKind.Integer;
    }

    /// <summary>Reads the value as a number; a whole number is a number too.</summary>
    /// <param name="value">The value, when it is a number.</param>
    /// <returns><see langword="true"/> when the value is a number.</returns>
    public bool TryGetNumber(out double value)
    {
        switch (Kind)
        {
            case GenericValueKind.Number:
                value = _value.Number;
                return true;
            case GenericValueKind.Integer:
                value = _value.Integer;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Returns the text as written.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Text;

    internal static GenericValue From(F.Value? value) => new(value ?? new F.Value());
}

/// <summary>An attribute of an element of the generic representation.</summary>
public sealed class GenericAttribute
{
    internal GenericAttribute(F.GenericAttribute attribute)
    {
        Name = attribute.Name;
        Value = GenericValue.From(attribute.Value);
        Source = SourcePosition.From(attribute.Source);
    }

    /// <summary>Gets the name of the attribute.</summary>
    public string Name { get; }

    /// <summary>Gets the value of the attribute.</summary>
    public GenericValue Value { get; }

    /// <summary>Gets where the attribute is written.</summary>
    public SourcePosition Source { get; }
}

/// <summary>
/// An XML element in the generic representation: the form of content whose
/// schema the compiler does not know, such as free properties, component
/// values, and the configuration of a module.
/// </summary>
/// <remarks>Instances are immutable and shared by every context of the process.</remarks>
public sealed class GenericElement
{
    private GenericElement(F.GenericNode node)
    {
        Name = node.Name;
        Attributes = node.Attributes.Select(a => new GenericAttribute(a)).ToArray();
        Children = node.Children.Select(c => new GenericElement(c)).ToArray();
        Value = node.Children.Count == 0 ? GenericValue.From(node.Value) : null;
        Vector = node.Vector.ToArray();
        Source = SourcePosition.From(node.Source);
    }

    /// <summary>Gets the name of the element.</summary>
    public string Name { get; }

    /// <summary>Gets the attributes, in the order written.</summary>
    public IReadOnlyList<GenericAttribute> Attributes { get; }

    /// <summary>Gets the child elements, in the order written.</summary>
    public IReadOnlyList<GenericElement> Children { get; }

    /// <summary>Gets the text of an element without child elements, or <see langword="null"/> when it has children.</summary>
    public GenericValue? Value { get; }

    /// <summary>
    /// Gets the components of a vector, for an element whose children are only
    /// <c>X</c>, <c>Y</c>, and optionally <c>Z</c> and <c>W</c>, all numbers;
    /// empty otherwise.
    /// </summary>
    public IReadOnlyList<double> Vector { get; }

    /// <summary>Gets where the element is written.</summary>
    public SourcePosition Source { get; }

    /// <summary>Gets an attribute by name.</summary>
    /// <param name="name">The name of the attribute.</param>
    /// <returns>The attribute, or <see langword="null"/> when the element has none of that name.</returns>
    public GenericAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);

    /// <summary>Gets the first child element of a name.</summary>
    /// <param name="name">The name of the child.</param>
    /// <returns>The child, or <see langword="null"/> when the element has none of that name.</returns>
    public GenericElement? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    /// <summary>Returns the name of the element and where it is written.</summary>
    /// <returns>For example <c>&lt;Stage&gt; at engine.nobara(12,5)</c>.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"<{Name}> at {Source}");

    internal static GenericElement From(F.GenericNode node) => new(node);
}
