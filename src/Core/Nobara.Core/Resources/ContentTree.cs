// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.ResourceCompiler.Nof;
using Nobara.Schema;
using F = Nobara.Formats;

namespace Nobara.Core.Resources;

/// <summary>
/// An element of compiled content, decoded with the descriptor of its message
/// so that the queries of an override can select it and change it. A tag uses
/// the same tree, without descriptor: its content is a list of
/// <c>&lt;Member&gt;</c> elements whose fields are <c>id</c> and
/// <c>optional</c>.
/// </summary>
/// <remarks>
/// Only message elements can be selected by a query; scalar fields are the
/// fields that a query compares and that <c>&lt;Set&gt;</c> changes, under
/// their element name or their attribute name.
/// </remarks>
internal sealed class ContentNode
{
    public const string MemberElement = "Member";

    public ContentNode(string element, MessageDescriptor? message)
    {
        Element = element;
        Message = message;
    }

    /// <summary>Gets the element name: the root is <c>Content</c>.</summary>
    public string Element { get; }

    /// <summary>Gets the message of the element, or <see langword="null"/> in a tag.</summary>
    public MessageDescriptor? Message { get; }

    public ContentNode? Parent { get; set; }

    /// <summary>Gets the fields of the element, in the order they are encoded.</summary>
    public List<ContentEntry> Entries { get; } = [];

    /// <summary>Decodes a message.</summary>
    /// <exception cref="InvalidProtocolBufferException">The bytes are not a message of that type.</exception>
    public static ContentNode Decode(MessageDescriptor message, ReadOnlySpan<byte> content, string element = "Content")
    {
        var node = new ContentNode(element, message);
        var input = new CodedInputStream(content.ToArray());
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
                node.Add(new ContentEntry(field, Decode(field.MessageType, input.ReadBytes().Span, Names.Element(field))));
                continue;
            }

            if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited && IsPackable(field.FieldType))
            {
                var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
                while (!packed.IsAtEnd)
                {
                    node.Add(new ContentEntry(field, ReadScalar(packed, field.FieldType)));
                }

                continue;
            }

            node.Add(new ContentEntry(field, ReadScalar(input, field.FieldType)));
        }

        return node;
    }

    /// <summary>Builds the tree of a tag from its members.</summary>
    public static ContentNode FromMembers(IEnumerable<F.TagMember> members)
    {
        var root = new ContentNode("Content", null);
        foreach (F.TagMember member in members)
        {
            root.Add(new ContentEntry(MemberElement, Member(member.Id, member.Optional)));
        }

        return root;
    }

    /// <summary>Reads the members back from the tree of a tag.</summary>
    public IEnumerable<F.TagMember> Members() => Entries.Where(e => e.Node is not null).Select(e => new F.TagMember
    {
        Id = (string?)e.Node!.Entries.FirstOrDefault(m => m.Attribute == "id")?.Scalar ?? string.Empty,
        Optional = (bool?)e.Node!.Entries.FirstOrDefault(m => m.Attribute == "optional")?.Scalar ?? false,
    });

    /// <summary>Encodes the element as its message; repeated scalars are packed.</summary>
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        foreach (IGrouping<int, ContentEntry> group in Entries.Where(e => e.Field is not null).GroupBy(e => e.Field!.FieldNumber).OrderBy(g => g.Key))
        {
            FieldDescriptor field = group.First().Field!;
            if (field.IsRepeated && IsPackable(field.FieldType))
            {
                using var packed = new MemoryStream();
                var inner = new CodedOutputStream(packed);
                foreach (ContentEntry entry in group)
                {
                    WriteScalar(inner, field.FieldType, entry.Scalar!);
                }

                inner.Flush();
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(packed.ToArray()));
                continue;
            }

            foreach (ContentEntry entry in group)
            {
                if (entry.Node is not null)
                {
                    output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(ByteString.CopyFrom(entry.Node.Encode()));
                }
                else
                {
                    output.WriteTag(field.FieldNumber, WireType(field.FieldType));
                    WriteScalar(output, field.FieldType, entry.Scalar!);
                }
            }
        }

        output.Flush();
        return stream.ToArray();
    }

    /// <summary>Gets every element under this one, depth first, in order.</summary>
    public IEnumerable<ContentNode> Descendants()
    {
        foreach (ContentEntry entry in Entries.ToArray())
        {
            if (entry.Node is ContentNode child)
            {
                yield return child;
                foreach (ContentNode nested in child.Descendants())
                {
                    yield return nested;
                }
            }
        }
    }

    public int IndexInParent() => Parent!.Entries.FindIndex(e => ReferenceEquals(e.Node, this));

    public void Add(ContentEntry entry) => Insert(Entries.Count, entry);

    public void Insert(int index, ContentEntry entry)
    {
        if (entry.Node is not null)
        {
            entry.Node.Parent = this;
        }

        Entries.Insert(index, entry);
    }

    /// <summary>Turns an element of an operation into a field of this element.</summary>
    /// <returns>An error message, or <see langword="null"/> when <paramref name="entry"/> is set.</returns>
    public string? Convert(F.GenericNode element, out ContentEntry? entry)
    {
        entry = null;
        if (Message is null)
        {
            if (element.Name != MemberElement || Element != "Content")
            {
                return $"a tag holds <{MemberElement}> elements, not <{element.Name}>";
            }

            bool optional = element.Attributes.FirstOrDefault(a => a.Name == "optional")?.Value?.Text == "true";
            entry = new ContentEntry(MemberElement, Member(element.Value?.Text ?? string.Empty, optional));
            return null;
        }

        FieldDescriptor? field = Message.Fields.InFieldNumberOrder().FirstOrDefault(f => Names.Element(f) == element.Name);
        if (field is null)
        {
            return $"<{Element}> has no field <{element.Name}>";
        }

        if (field.GetOptions()?.HasExtension(OptionsExtensions.Generic) == true)
        {
            entry = new ContentEntry(field, element.ToByteString());
            return null;
        }

        if (field.FieldType == FieldType.Message)
        {
            var node = new ContentNode(element.Name, field.MessageType);
            foreach (F.GenericAttribute attribute in element.Attributes)
            {
                FieldDescriptor? scalar = field.MessageType.Fields.InFieldNumberOrder().FirstOrDefault(f => Names.Attribute(f) == attribute.Name);
                if (scalar is null || scalar.FieldType == FieldType.Message)
                {
                    return $"<{element.Name}> has no scalar field '{attribute.Name}'";
                }

                if (ParseScalar(scalar, attribute.Value?.Text ?? string.Empty, emptyElement: false, out object? value) is string error)
                {
                    return error;
                }

                node.Add(new ContentEntry(scalar, value!));
            }

            foreach (F.GenericNode child in element.Children)
            {
                if (node.Convert(child, out ContentEntry? nested) is string error)
                {
                    return error;
                }

                node.Add(nested!);
            }

            entry = new ContentEntry(field, node);
            return null;
        }

        string text = element.Value?.Text ?? string.Empty;
        if (ParseScalar(field, text, emptyElement: element.Children.Count == 0 && text.Length == 0, out object? parsed) is string invalid)
        {
            return invalid;
        }

        entry = new ContentEntry(field, parsed!);
        return null;
    }

    /// <summary>Finds a scalar field of this element by its element or attribute name.</summary>
    public (FieldDescriptor? Field, string Element, string Attribute)? ScalarField(string name)
    {
        if (Message is null)
        {
            return Element == MemberElement && name is "id" or "Id" ? (null, "Id", "id")
                : Element == MemberElement && name is "optional" or "Optional" ? (null, "Optional", "optional")
                : null;
        }

        FieldDescriptor? field = Message.Fields.InFieldNumberOrder().FirstOrDefault(f => Names.Attribute(f) == name || Names.Element(f) == name);
        return field is null || field.FieldType == FieldType.Message || field.GetOptions()?.HasExtension(OptionsExtensions.Generic) == true
            ? null
            : (field, Names.Element(field), Names.Attribute(field));
    }

    /// <summary>Parses a value written as text for a scalar field.</summary>
    /// <returns>An error message, or <see langword="null"/> when <paramref name="value"/> is set.</returns>
    public static string? ParseScalar(FieldDescriptor field, string text, bool emptyElement, out object? value)
    {
        value = null;
        string name = Names.Element(field);
        switch (field.FieldType)
        {
            case FieldType.String:
                value = text;
                return null;
            case FieldType.Bytes:
                try
                {
                    value = ByteString.FromBase64(text);
                    return null;
                }
                catch (FormatException)
                {
                    return $"'{name}' is a bytes field and its value is not base64";
                }

            case FieldType.Bool:
                if (emptyElement)
                {
                    value = true;
                    return null;
                }

                if (text is "true" or "false")
                {
                    value = text == "true";
                    return null;
                }

                return $"'{name}' is '{text}', expected 'true' or 'false'";
            case FieldType.Enum:
                EnumValueDescriptor? match = field.EnumType.Values.FirstOrDefault(v => v.Name == text || Names.EnumValue(v) == text);
                if (match is null)
                {
                    return $"'{name}' is '{text}', expected one of {string.Join(", ", field.EnumType.Values.Select(v => "'" + Names.EnumValue(v) + "'"))}";
                }

                value = (long)match.Number;
                return null;
            case FieldType.Double:
            case FieldType.Float:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) || !double.IsFinite(real))
                {
                    return $"'{name}' is '{text}', expected a number";
                }

                value = field.FieldType == FieldType.Float ? (object)(float)real : real;
                return null;
            case FieldType.UInt32:
            case FieldType.Fixed32:
            case FieldType.UInt64:
            case FieldType.Fixed64:
                ulong max = field.FieldType is FieldType.UInt32 or FieldType.Fixed32 ? uint.MaxValue : ulong.MaxValue;
                if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong unsigned) || unsigned > max)
                {
                    return $"'{name}' is '{text}', expected a whole number from 0";
                }

                value = unsigned;
                return null;
            default:
                bool narrow = field.FieldType is FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32;
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long signed)
                    || (narrow && (signed < int.MinValue || signed > int.MaxValue)))
                {
                    return $"'{name}' is '{text}', expected a whole number";
                }

                value = signed;
                return null;
        }
    }

    /// <summary>Writes a scalar value as text, the way queries compare it.</summary>
    public static string Text(ContentEntry entry) => entry.Scalar switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => s,
        ByteString bytes => bytes.ToBase64(),
        long l when entry.Field?.FieldType == FieldType.Enum => EnumText(entry.Field, (int)l),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    /// <summary>Tells whether the value of a scalar field equals the value of a query.</summary>
    public static bool Same(ContentEntry entry, string expected)
    {
        string actual = Text(entry);
        if (actual == expected)
        {
            return true;
        }

        if (entry.Field?.FieldType == FieldType.Enum && entry.Field.EnumType.FindValueByNumber((int)(long)entry.Scalar!) is EnumValueDescriptor value)
        {
            return value.Name == expected;
        }

        return double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
            && a == b;
    }

    private static ContentNode Member(string id, bool optional)
    {
        var node = new ContentNode(MemberElement, null);
        node.Add(new ContentEntry("Id", "id", id));
        node.Add(new ContentEntry("Optional", "optional", optional));
        return node;
    }

    private static string EnumText(FieldDescriptor field, int number) =>
        field.EnumType.FindValueByNumber(number) is EnumValueDescriptor value ? Names.EnumValue(value) : number.ToString(CultureInfo.InvariantCulture);

    private static bool IsPackable(FieldType type) => type is not (FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group);

    private static WireFormat.WireType WireType(FieldType type) => type switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => WireFormat.WireType.Fixed64,
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => WireFormat.WireType.Fixed32,
        FieldType.String or FieldType.Bytes or FieldType.Message => WireFormat.WireType.LengthDelimited,
        _ => WireFormat.WireType.Varint,
    };

    private static object ReadScalar(CodedInputStream input, FieldType type) => type switch
    {
        FieldType.Double => input.ReadDouble(),
        FieldType.Float => input.ReadFloat(),
        FieldType.Int64 => input.ReadInt64(),
        FieldType.UInt64 => input.ReadUInt64(),
        FieldType.Int32 => (long)input.ReadInt32(),
        FieldType.Fixed64 => input.ReadFixed64(),
        FieldType.Fixed32 => (ulong)input.ReadFixed32(),
        FieldType.Bool => input.ReadBool(),
        FieldType.String => input.ReadString(),
        FieldType.Bytes => input.ReadBytes(),
        FieldType.UInt32 => (ulong)input.ReadUInt32(),
        FieldType.Enum => (long)input.ReadEnum(),
        FieldType.SFixed32 => (long)input.ReadSFixed32(),
        FieldType.SFixed64 => input.ReadSFixed64(),
        FieldType.SInt32 => (long)input.ReadSInt32(),
        FieldType.SInt64 => input.ReadSInt64(),
        _ => throw new InvalidOperationException($"unsupported field type {type}"),
    };

    private static void WriteScalar(CodedOutputStream output, FieldType type, object value)
    {
        switch (type)
        {
            case FieldType.Double: output.WriteDouble((double)value); break;
            case FieldType.Float: output.WriteFloat((float)value); break;
            case FieldType.Int64: output.WriteInt64((long)value); break;
            case FieldType.UInt64: output.WriteUInt64((ulong)value); break;
            case FieldType.Int32: output.WriteInt32((int)(long)value); break;
            case FieldType.Fixed64: output.WriteFixed64((ulong)value); break;
            case FieldType.Fixed32: output.WriteFixed32((uint)(ulong)value); break;
            case FieldType.Bool: output.WriteBool((bool)value); break;
            case FieldType.String: output.WriteString((string)value); break;
            case FieldType.Bytes: output.WriteBytes((ByteString)value); break;
            case FieldType.UInt32: output.WriteUInt32((uint)(ulong)value); break;
            case FieldType.Enum: output.WriteEnum((int)(long)value); break;
            case FieldType.SFixed32: output.WriteSFixed32((int)(long)value); break;
            case FieldType.SFixed64: output.WriteSFixed64((long)value); break;
            case FieldType.SInt32: output.WriteSInt32((int)(long)value); break;
            case FieldType.SInt64: output.WriteSInt64((long)value); break;
            default: throw new InvalidOperationException($"unsupported field type {type}");
        }
    }
}

/// <summary>One field value of an element: a scalar, or a child element.</summary>
internal sealed class ContentEntry
{
    public ContentEntry(FieldDescriptor field, object scalar)
    {
        Field = field;
        Element = Names.Element(field);
        Attribute = Names.Attribute(field);
        Scalar = scalar;
    }

    public ContentEntry(FieldDescriptor field, ContentNode node)
    {
        Field = field;
        Element = Names.Element(field);
        Attribute = Names.Attribute(field);
        Node = node;
    }

    // A field of a tag member.
    public ContentEntry(string element, string attribute, object scalar)
    {
        Element = element;
        Attribute = attribute;
        Scalar = scalar;
    }

    // A member of a tag.
    public ContentEntry(string element, ContentNode node)
    {
        Element = element;
        Attribute = string.Empty;
        Node = node;
    }

    /// <summary>Gets the field, or <see langword="null"/> in a tag.</summary>
    public FieldDescriptor? Field { get; }

    public string Element { get; }

    public string Attribute { get; }

    public object? Scalar { get; }

    public ContentNode? Node { get; }

    /// <summary>Gets whether the field holds several values: a repeated field, or the members of a tag.</summary>
    public bool IsRepeated => Field?.IsRepeated ?? Node is not null;

    public bool SameField(ContentEntry other) => Field is not null ? ReferenceEquals(Field, other.Field) || Field.FieldNumber == other.Field?.FieldNumber : Element == other.Element;
}
