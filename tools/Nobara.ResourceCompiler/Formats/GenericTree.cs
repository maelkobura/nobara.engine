// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Nobara.Formats;

namespace Nobara.ResourceCompiler.Formats;

/// <summary>Builds the generic representation of XML content.</summary>
public static class GenericTree
{
    private static readonly string[] VectorNames = ["X", "Y", "Z", "W"];

    /// <summary>Converts an element and its descendants.</summary>
    /// <param name="element">An element loaded with line information.</param>
    /// <param name="file">The path recorded in the source positions, relative to the module folder.</param>
    /// <param name="visit">Called for every value, with the node that holds it; <see langword="null"/> to skip.</param>
    /// <returns>The node.</returns>
    public static GenericNode FromElement(XElement element, string file, Action<Value, XObject>? visit = null)
    {
        var node = new GenericNode { Name = element.Name.LocalName, Source = Position(file, element) };
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            Value value = Infer(attribute.Value);
            visit?.Invoke(value, attribute);
            node.Attributes.Add(new GenericAttribute { Name = attribute.Name.LocalName, Value = value, Source = Position(file, attribute) });
        }

        if (!element.HasElements)
        {
            string text = element.Value.Trim();
            if (text.Length > 0 || element.Nodes().Any())
            {
                node.Value = Infer(text);
                visit?.Invoke(node.Value, element);
            }

            return node;
        }

        foreach (XElement child in element.Elements())
        {
            node.Children.Add(FromElement(child, file, visit));
        }

        if (IsVector(node))
        {
            node.Vector.AddRange(node.Children.Select(c => c.Value.TypedCase == Value.TypedOneofCase.Number ? c.Value.Number : c.Value.Integer));
        }

        return node;
    }

    /// <summary>Recognizes the type of a value written as text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value, with its text trimmed and its recognized type.</returns>
    public static Value Infer(string text)
    {
        string trimmed = text.Trim();
        var value = new Value { Text = trimmed };
        if (trimmed == "true" || trimmed == "false")
        {
            value.Boolean = trimmed == "true";
        }
        else if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            value.Integer = integer;
        }
        else if (trimmed.Length > 0 && (char.IsDigit(trimmed[0]) || ((trimmed[0] == '-' || trimmed[0] == '+' || trimmed[0] == '.') && trimmed.Length > 1))
            && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
        {
            value.Number = number;
        }
        else if (Identifiers.IsTagId(trimmed))
        {
            value.TagId = trimmed;
        }
        else if (Identifiers.IsResourceId(trimmed))
        {
            value.ResourceId = trimmed;
        }
        else if (Identifiers.IsObjectId(trimmed))
        {
            value.ObjectId = trimmed;
        }

        return value;
    }

    /// <summary>Gets the source position of an XML node.</summary>
    /// <param name="file">The path recorded in the position.</param>
    /// <param name="node">An element or an attribute.</param>
    /// <returns>The position, with line 0 when the node has no line information.</returns>
    public static SourcePosition Position(string file, XObject node)
    {
        var info = (IXmlLineInfo)node;
        return info.HasLineInfo()
            ? new SourcePosition { File = file, Line = (uint)info.LineNumber, Column = (uint)info.LinePosition }
            : new SourcePosition { File = file };
    }

    private static bool IsVector(GenericNode node)
    {
        if (node.Attributes.Count > 0 || node.Children.Count < 2 || node.Children.Count > 4)
        {
            return false;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            GenericNode child = node.Children[i];
            bool numeric = child.Value is not null
                && (child.Value.TypedCase == Value.TypedOneofCase.Integer || child.Value.TypedCase == Value.TypedOneofCase.Number);
            if (child.Name != VectorNames[i] || !numeric || child.Children.Count > 0 || child.Attributes.Count > 0)
            {
                return false;
            }
        }

        return true;
    }
}
