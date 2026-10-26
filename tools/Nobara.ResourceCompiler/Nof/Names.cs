// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Google.Protobuf.Reflection;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>
/// The naming convention between XML and protobuf: the field
/// <c>display_name</c> is written as the element <c>&lt;DisplayName&gt;</c> or
/// as the attribute <c>displayName</c>.
/// </summary>
public static class Names
{
    /// <summary>Gets the element name of a field: each underscore-separated word capitalized.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The element name: <c>DisplayName</c> for <c>display_name</c>.</returns>
    public static string Element(FieldDescriptor field) => Element(field.Name);

    /// <summary>Gets the attribute name of a field: the element name with a lowercase first letter.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The attribute name: <c>displayName</c> for <c>display_name</c>.</returns>
    public static string Attribute(FieldDescriptor field) => Attribute(field.Name);

    /// <summary>Gets the element name of a field name.</summary>
    /// <param name="fieldName">The name of a protobuf field.</param>
    /// <returns>The element name.</returns>
    public static string Element(string fieldName)
    {
        var builder = new StringBuilder(fieldName.Length);
        bool upper = true;
        foreach (char c in fieldName)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return builder.ToString();
    }

    /// <summary>Gets the attribute name of a field name.</summary>
    /// <param name="fieldName">The name of a protobuf field.</param>
    /// <returns>The attribute name.</returns>
    public static string Attribute(string fieldName)
    {
        string element = Element(fieldName);
        return element.Length == 0 ? element : char.ToLowerInvariant(element[0]) + element[1..];
    }

    /// <summary>Gets the XML spelling of an enum value: <c>SHAPE_MESH</c> of the enum <c>Shape</c> is <c>Mesh</c>.</summary>
    /// <param name="value">The enum value.</param>
    /// <returns>The value name without the prefix of its enum, each word capitalized.</returns>
    public static string EnumValue(EnumValueDescriptor value)
    {
        string prefix = UpperSnake(value.EnumDescriptor.Name) + "_";
        string name = value.Name.StartsWith(prefix, StringComparison.Ordinal) ? value.Name[prefix.Length..] : value.Name;
        return Element(name.ToLowerInvariant());
    }

    private static string UpperSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
