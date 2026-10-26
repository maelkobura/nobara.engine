// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Formats;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>
/// Compiled content decoded with its descriptor into elements and field
/// values, so that the queries of an override can be tried on it.
/// </summary>
internal sealed class ContentTree
{
    private ContentTree(string element)
    {
        Element = element;
    }

    public string Element { get; }

    /// <summary>Gets the text form of the scalar fields, by attribute name and by element name.</summary>
    public Dictionary<string, List<string>> Fields { get; } = new(StringComparer.Ordinal);

    public List<ContentTree> Children { get; } = [];

    public static ContentTree Decode(MessageDescriptor message, ByteString content) => Decode(message, content, "Content");

    /// <summary>Tells whether a query selects at least one element, nested queries included.</summary>
    public bool Matches(Query query)
    {
        List<ContentTree> selected = Descendants().Where(n => Accepts(n, query)).ToList();
        if (query.IndexCase == Query.IndexOneofCase.Position)
        {
            selected = selected.Count > query.Position ? [selected[(int)query.Position]] : [];
        }
        else if (query.IndexCase != Query.IndexOneofCase.All && selected.Count > 0)
        {
            selected = [selected[0]];
        }

        return selected.Count > 0 && query.Queries.All(nested => selected.Any(s => s.Matches(nested)));
    }

    private static bool Accepts(ContentTree node, Query query)
    {
        if (query.Type.Length > 0 && node.Element != query.Type)
        {
            return false;
        }

        return query.Field.Length == 0 || (node.Fields.TryGetValue(query.Field, out List<string>? values) && values.Any(v => Same(v, query.Value)));
    }

    private static bool Same(string actual, string expected)
    {
        if (actual == expected)
        {
            return true;
        }

        return double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
            && a == b;
    }

    private IEnumerable<ContentTree> Descendants()
    {
        foreach (ContentTree child in Children)
        {
            yield return child;
            foreach (ContentTree nested in child.Descendants())
            {
                yield return nested;
            }
        }
    }

    private static ContentTree Decode(MessageDescriptor message, ByteString content, string element)
    {
        var node = new ContentTree(element);
        var input = new CodedInputStream(content.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            FieldDescriptor? field = message.FindFieldByNumber(WireFormat.GetTagFieldNumber(tag));
            if (field is null)
            {
                input.SkipLastField();
                continue;
            }

            if (field.FieldType == FieldType.Message)
            {
                node.Children.Add(Decode(field.MessageType, input.ReadBytes(), Names.Element(field)));
                continue;
            }

            if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited && field.FieldType is not (FieldType.String or FieldType.Bytes))
            {
                var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
                while (!packed.IsAtEnd)
                {
                    node.Add(field, ReadScalar(packed, field));
                }

                continue;
            }

            node.Add(field, ReadScalar(input, field));
        }

        return node;
    }

    private void Add(FieldDescriptor field, string value)
    {
        foreach (string name in new[] { Names.Attribute(field), Names.Element(field) })
        {
            if (!Fields.TryGetValue(name, out List<string>? list))
            {
                Fields[name] = list = [];
            }

            list.Add(value);
        }
    }

    private static string ReadScalar(CodedInputStream input, FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Double => input.ReadDouble().ToString("R", CultureInfo.InvariantCulture),
        FieldType.Float => input.ReadFloat().ToString("R", CultureInfo.InvariantCulture),
        FieldType.Int64 => input.ReadInt64().ToString(CultureInfo.InvariantCulture),
        FieldType.UInt64 => input.ReadUInt64().ToString(CultureInfo.InvariantCulture),
        FieldType.Int32 => input.ReadInt32().ToString(CultureInfo.InvariantCulture),
        FieldType.Fixed64 => input.ReadFixed64().ToString(CultureInfo.InvariantCulture),
        FieldType.Fixed32 => input.ReadFixed32().ToString(CultureInfo.InvariantCulture),
        FieldType.Bool => input.ReadBool() ? "true" : "false",
        FieldType.String => input.ReadString(),
        FieldType.Bytes => input.ReadBytes().ToBase64(),
        FieldType.UInt32 => input.ReadUInt32().ToString(CultureInfo.InvariantCulture),
        FieldType.Enum => EnumName(field, input.ReadEnum()),
        FieldType.SFixed32 => input.ReadSFixed32().ToString(CultureInfo.InvariantCulture),
        FieldType.SFixed64 => input.ReadSFixed64().ToString(CultureInfo.InvariantCulture),
        FieldType.SInt32 => input.ReadSInt32().ToString(CultureInfo.InvariantCulture),
        FieldType.SInt64 => input.ReadSInt64().ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static string EnumName(FieldDescriptor field, int number)
    {
        EnumValueDescriptor? value = field.EnumType.FindValueByNumber(number);
        return value is null ? number.ToString(CultureInfo.InvariantCulture) : Names.EnumValue(value);
    }
}
