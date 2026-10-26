// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Formats;
using Nobara.ResourceCompiler.Caching;
using Nobara.ResourceCompiler.Formats;
using Nobara.Schema;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>What the conversion of XML content needs and what it collects, for one resource.</summary>
internal sealed class WriteContext
{
    public WriteContext(SourceFile source, DiagnosticBag diagnostics, string owner)
    {
        Source = source;
        Diagnostics = diagnostics;
        Owner = owner;
    }

    public SourceFile Source { get; }

    public DiagnosticBag Diagnostics { get; }

    /// <summary>Gets the object identifier of the resource being written.</summary>
    public string Owner { get; }

    public List<PendingReference> References { get; } = [];

    /// <summary>Gets the binary files referenced, relative to the resources folder.</summary>
    public List<string> Files { get; } = [];

    /// <summary>Gets or sets whether references found from now on may be missing.</summary>
    public bool OptionalReferences { get; set; }

    public void Error(string code, string message, XObject at) => Diagnostics.Error(code, message, Source.At(at));

    public void Reference(PendingReference.Types.Kind kind, string value, string expectedType, XObject at, bool optional = false) =>
        References.Add(new PendingReference
        {
            Owner = Owner,
            Kind = kind,
            Value = value,
            ExpectedType = expectedType,
            Optional = optional || OptionalReferences,
            Source = Source.Position(at),
        });
}

/// <summary>A <c>.nof</c> file being compiled.</summary>
internal sealed class SourceFile
{
    public SourceFile(string fullPath, string relativePath, string resourcesDirectory)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        ResourcesDirectory = resourcesDirectory;
    }

    /// <summary>Gets the path used in diagnostics.</summary>
    public string FullPath { get; }

    /// <summary>Gets the path relative to the resources folder, with forward slashes, recorded in the compiled data.</summary>
    public string RelativePath { get; }

    public string ResourcesDirectory { get; }

    public Location At(XObject node) => Location.Of(FullPath, node);

    public SourcePosition Position(XObject node) => GenericTree.Position(RelativePath, node);
}

/// <summary>
/// Writes XML content as the protobuf encoding of a message, by convention:
/// an element or an attribute is the field of the same name, a repeated
/// element is a repeated field, a nested element is a sub-message.
/// </summary>
internal static class MessageWriter
{
    public const string UnknownField = "NRC306";
    public const string DuplicateField = "NRC307";
    public const string MissingField = "NRC308";
    public const string InvalidValue = "NRC309";
    public const string OutOfBounds = "NRC310";
    public const string InvalidIdentifier = "NRC311";
    public const string MissingFile = "NRC312";
    public const string InvalidProperty = "NRC314";

    public static bool IsNumeric(FieldType type) => type is FieldType.Double or FieldType.Float or FieldType.Int32 or FieldType.Int64
        or FieldType.UInt32 or FieldType.UInt64 or FieldType.SInt32 or FieldType.SInt64 or FieldType.Fixed32 or FieldType.Fixed64
        or FieldType.SFixed32 or FieldType.SFixed64;

    /// <summary>Writes an element as a message; returns <see langword="null"/> when an error was reported.</summary>
    public static byte[]? Write(MessageDescriptor message, XElement element, WriteContext context)
    {
        int errors = context.Diagnostics.ErrorCount;
        List<(FieldDescriptor Field, object Value)> values = Collect(message, element, context);
        return context.Diagnostics.ErrorCount == errors ? Serialize(values) : null;
    }

    /// <summary>
    /// Writes only the fields given, for the simple fields of an override:
    /// each element must be a singular scalar field of the message.
    /// </summary>
    public static byte[] WriteFields(IReadOnlyList<(FieldDescriptor Field, XElement Element)> fields, WriteContext context)
    {
        var values = new List<(FieldDescriptor, object)>();
        foreach ((FieldDescriptor field, XElement element) in fields)
        {
            if (ScalarText(field, element, context) is string text && Parse(field, text, element, context, isEmptyElement: IsEmpty(element)) is object value)
            {
                values.Add((field, value));
            }
        }

        return Serialize(values, keepDefaults: true);
    }

    /// <summary>Gets a field by element name, or reports an unknown element.</summary>
    public static FieldDescriptor? FieldByElement(MessageDescriptor message, string name) =>
        message.Fields.InFieldNumberOrder().FirstOrDefault(f => Names.Element(f) == name);

    private static List<(FieldDescriptor Field, object Value)> Collect(MessageDescriptor message, XElement element, WriteContext context)
    {
        var values = new List<(FieldDescriptor Field, object Value)>();
        var seen = new Dictionary<FieldDescriptor, XObject>();
        var oneofs = new Dictionary<OneofDescriptor, FieldDescriptor>();
        IList<FieldDescriptor> fields = message.Fields.InFieldNumberOrder();

        void Mark(FieldDescriptor field, XObject at)
        {
            if (!field.IsRepeated && seen.TryGetValue(field, out XObject? first))
            {
                string firstForm = first is XAttribute ? "attribute" : "element";
                context.Error(DuplicateField, $"field '{Names.Element(field)}' of <{element.Name.LocalName}> is set twice (first as an {firstForm} at line {context.Source.At(first).Line})", at);
            }

            seen.TryAdd(field, at);
            OneofDescriptor? oneof = field.RealContainingOneof;
            if (oneof is not null)
            {
                if (oneofs.TryGetValue(oneof, out FieldDescriptor? other) && other != field)
                {
                    context.Error(DuplicateField, $"'{Names.Element(field)}' and '{Names.Element(other)}' of <{element.Name.LocalName}> exclude each other; set only one", at);
                }

                oneofs.TryAdd(oneof, field);
            }
        }

        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            string name = attribute.Name.LocalName;
            FieldDescriptor? field = attribute.Name.NamespaceName.Length == 0 ? fields.FirstOrDefault(f => Names.Attribute(f) == name) : null;
            if (field is null)
            {
                context.Error(UnknownField, Unknown("attribute", name, element.Name.LocalName, fields.Select(Names.Attribute)), attribute);
                continue;
            }

            if (field.IsRepeated || field.FieldType == FieldType.Message || IsGeneric(field))
            {
                context.Error(UnknownField, $"'{name}' of <{element.Name.LocalName}> is a repeated or structured field; write it as the element <{Names.Element(field)}>", attribute);
                continue;
            }

            Mark(field, attribute);
            if (Parse(field, attribute.Value.Trim(), attribute, context, isEmptyElement: false) is object value)
            {
                values.Add((field, value));
            }
        }

        foreach (XNode node in element.Nodes())
        {
            if (node is XText text && text.Value.Trim().Length > 0)
            {
                context.Error(UnknownField, $"<{element.Name.LocalName}> contains text; its content is made of elements, one per field", text);
                break;
            }
        }

        foreach (XElement child in element.Elements())
        {
            string name = child.Name.LocalName;
            FieldDescriptor? field = child.Name.NamespaceName.Length == 0 ? fields.FirstOrDefault(f => Names.Element(f) == name) : null;
            if (field is null)
            {
                context.Error(UnknownField, Unknown("element", name, element.Name.LocalName, fields.Select(Names.Element)), child);
                continue;
            }

            Mark(field, child);
            object? value = null;
            if (IsGeneric(field))
            {
                value = GenericValue(field, child, context);
            }
            else if (field.FieldType == FieldType.Message)
            {
                value = Write(field.MessageType, child, context);
            }
            else if (ScalarText(field, child, context) is string scalar)
            {
                value = Parse(field, scalar, child, context, isEmptyElement: IsEmpty(child));
            }

            if (value is not null)
            {
                values.Add((field, value));
            }
        }

        foreach (FieldDescriptor field in fields)
        {
            if (field.GetOptions()?.GetExtension(OptionsExtensions.Required) == true && !seen.ContainsKey(field))
            {
                context.Error(MissingField, $"<{element.Name.LocalName}> needs the field '{Names.Element(field)}'", element);
            }
        }

        return values;
    }

    private static bool IsEmpty(XElement element) => !element.HasElements && element.Value.Trim().Length == 0;

    private static string? ScalarText(FieldDescriptor field, XElement element, WriteContext context)
    {
        if (element.HasAttributes && element.Attributes().Any(a => !a.IsNamespaceDeclaration) || element.HasElements)
        {
            context.Error(UnknownField, $"<{element.Name.LocalName}> is a {field.FieldType.ToString().ToLowerInvariant()} field: it takes text, not attributes or elements", element);
            return null;
        }

        return element.Value.Trim();
    }

    private static bool IsGeneric(FieldDescriptor field) => field.GetOptions()?.HasExtension(OptionsExtensions.Generic) == true;

    private static ByteString? GenericValue(FieldDescriptor field, XElement element, WriteContext context)
    {
        string kind = field.GetOptions()!.GetExtension(OptionsExtensions.Generic);
        int errors = context.Diagnostics.ErrorCount;
        bool previous = context.OptionalReferences;
        if (kind == "component")
        {
            XAttribute? reference = element.Attribute("ref");
            if (reference is null)
            {
                context.Error(MissingField, $"<{element.Name.LocalName}> needs a 'ref' attribute: the qualified name of the component, such as 'example.physics.rigidbody'", element);
            }
            else if (!Identifiers.IsComponentName(reference.Value.Trim()))
            {
                context.Error(InvalidIdentifier, $"'{reference.Value}' is not a qualified component name: the module identifier, then the component name, in lowercase and separated by dots", reference);
            }

            foreach (XAttribute attribute in element.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration && attribute.Name.LocalName != "ref" && attribute.Name.LocalName != "optional")
                {
                    context.Error(UnknownField, $"unknown attribute '{attribute.Name.LocalName}' in <{element.Name.LocalName}>; a component takes 'ref' and 'optional', and its values as elements", attribute);
                }
            }

            XAttribute? optional = element.Attribute("optional");
            if (optional is not null && optional.Value != "true" && optional.Value != "false")
            {
                context.Error(InvalidValue, $"'optional' is '{optional.Value}', expected 'true' or 'false'", optional);
            }

            context.OptionalReferences |= optional?.Value == "true";
        }
        else if (kind == "properties")
        {
            CheckProperties(element, context);
        }

        GenericNode node = GenericTree.FromElement(element, context.Source.RelativePath, (value, at) =>
        {
            if (value.TypedCase == Value.TypedOneofCase.ObjectId && kind != "properties")
            {
                context.Reference(PendingReference.Types.Kind.Object, value.ObjectId, string.Empty, at);
            }
        });
        context.OptionalReferences = previous;
        return context.Diagnostics.ErrorCount == errors ? node.ToByteString() : null;
    }

    /// <summary>Checks the children of a block of free properties: flat or qualified names, each once.</summary>
    public static void CheckProperties(XElement block, WriteContext context)
    {
        foreach (XAttribute attribute in block.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                context.Error(InvalidProperty, $"<{block.Name.LocalName}> takes no attributes", attribute);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement property in block.Elements())
        {
            string name = property.Name.LocalName;
            string[] parts = name.Split('.');
            bool valid = parts.Length <= 2 && parts.All(Identifiers.IsName) && char.IsUpper(parts[^1][0]);
            if (!valid)
            {
                context.Error(InvalidProperty, $"'{name}' is not a property name: a capitalized name such as 'Weight', or a short module name and a property such as 'Motion.Weight'", property);
                continue;
            }

            if (!seen.Add(name))
            {
                context.Error(InvalidProperty, $"property '{name}' is set twice", property);
            }
        }
    }

    private static object? Parse(FieldDescriptor field, string text, XObject at, WriteContext context, bool isEmptyElement)
    {
        string name = Names.Element(field);
        FieldOptions? options = field.GetOptions();
        switch (field.FieldType)
        {
            case FieldType.String:
                CheckString(field, options, text, at, context);
                return text;
            case FieldType.Bool:
                if (isEmptyElement && at is XElement)
                {
                    return true;
                }

                if (text == "true" || text == "false")
                {
                    return text == "true";
                }

                context.Error(InvalidValue, $"'{name}' is '{text}', expected 'true' or 'false'", at);
                return null;
            case FieldType.Bytes:
                try
                {
                    return ByteString.FromBase64(text);
                }
                catch (FormatException)
                {
                    context.Error(InvalidValue, $"'{name}' is a bytes field and '{Shorten(text)}' is not base64", at);
                    return null;
                }

            case FieldType.Enum:
                EnumValueDescriptor? value = field.EnumType.Values.FirstOrDefault(v => v.Name == text || Names.EnumValue(v) == text);
                if (value is null)
                {
                    context.Error(InvalidValue, $"'{name}' is '{text}', expected one of {string.Join(", ", field.EnumType.Values.Select(v => "'" + Names.EnumValue(v) + "'"))}", at);
                    return null;
                }

                return (long)value.Number;
            case FieldType.Double:
            case FieldType.Float:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) || !double.IsFinite(real)
                    || (field.FieldType == FieldType.Float && Math.Abs(real) > float.MaxValue))
                {
                    context.Error(InvalidValue, $"'{name}' is '{text}', expected a number such as 1.5", at);
                    return null;
                }

                return CheckBounds(field, options, real, text, at, context) ? (field.FieldType == FieldType.Float ? (object)(float)real : real) : null;
            default:
                return ParseInteger(field, options, text, at, context);
        }
    }

    private static object? ParseInteger(FieldDescriptor field, FieldOptions? options, string text, XObject at, WriteContext context)
    {
        string name = Names.Element(field);
        bool unsigned = field.FieldType is FieldType.UInt32 or FieldType.UInt64 or FieldType.Fixed32 or FieldType.Fixed64;
        bool wide = field.FieldType is FieldType.Int64 or FieldType.UInt64 or FieldType.SInt64 or FieldType.Fixed64 or FieldType.SFixed64;
        if (unsigned)
        {
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u) || (!wide && u > uint.MaxValue))
            {
                context.Error(InvalidValue, $"'{name}' is '{text}', expected a whole number from 0 to {(wide ? ulong.MaxValue : uint.MaxValue)}", at);
                return null;
            }

            return CheckBounds(field, options, u, text, at, context) ? u : null;
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) || (!wide && (l < int.MinValue || l > int.MaxValue)))
        {
            context.Error(InvalidValue, $"'{name}' is '{text}', expected a whole number from {(wide ? long.MinValue : int.MinValue)} to {(wide ? long.MaxValue : int.MaxValue)}", at);
            return null;
        }

        return CheckBounds(field, options, l, text, at, context) ? l : null;
    }

    private static bool CheckBounds(FieldDescriptor field, FieldOptions? options, double value, string text, XObject at, WriteContext context)
    {
        if (options is null)
        {
            return true;
        }

        if (options.HasExtension(OptionsExtensions.Min) && value < options.GetExtension(OptionsExtensions.Min))
        {
            context.Error(OutOfBounds, $"'{Names.Element(field)}' is {text}, below the minimum {Format(options.GetExtension(OptionsExtensions.Min))}", at);
            return false;
        }

        if (options.HasExtension(OptionsExtensions.Max) && value > options.GetExtension(OptionsExtensions.Max))
        {
            context.Error(OutOfBounds, $"'{Names.Element(field)}' is {text}, above the maximum {Format(options.GetExtension(OptionsExtensions.Max))}", at);
            return false;
        }

        return true;
    }

    private static void CheckString(FieldDescriptor field, FieldOptions? options, string text, XObject at, WriteContext context)
    {
        if (options is null)
        {
            return;
        }

        string name = Names.Element(field);
        if (options.HasExtension(OptionsExtensions.Ref))
        {
            string type = options.GetExtension(OptionsExtensions.Ref);
            if (!Identifiers.IsObjectId(text) || Identifiers.TypeOf(text) != type)
            {
                context.Error(InvalidIdentifier, $"'{name}' is '{text}', expected the object identifier of a {type}, such as '{type}:example.name'", at);
            }
            else
            {
                context.Reference(PendingReference.Types.Kind.Object, text, type, at);
            }
        }

        if (options.HasExtension(OptionsExtensions.Resource))
        {
            string type = options.GetExtension(OptionsExtensions.Resource);
            if (!Identifiers.IsResourceId(text) || Identifiers.TypeOf(text) != type)
            {
                context.Error(InvalidIdentifier, $"'{name}' is '{text}', expected the resource identifier of a {type}, such as '{type}://Example/Name'", at);
            }
            else
            {
                context.Reference(PendingReference.Types.Kind.Resource, text, type, at);
            }
        }

        if (options.GetExtension(OptionsExtensions.I18N) && !Identifiers.IsLocalizationKey(text))
        {
            context.Error(InvalidIdentifier, $"'{name}' is '{Shorten(text)}', expected a localization key such as 'block.natural.dirt', not text", at);
        }

        if (options.GetExtension(OptionsExtensions.File))
        {
            CheckFile(name, text, at, context);
        }
    }

    private static void CheckFile(string name, string path, XObject at, WriteContext context)
    {
        bool valid = path.Length > 0 && !path.Contains('\\', StringComparison.Ordinal) && !Path.IsPathRooted(path)
            && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        if (!valid)
        {
            context.Error(MissingFile, $"'{name}' is '{path}', expected a path relative to the resources folder, with forward slashes and without '..'", at);
            return;
        }

        string full = Path.Combine(context.Source.ResourcesDirectory, path);
        if (!File.Exists(full))
        {
            context.Error(MissingFile, $"the file '{path}' does not exist in the resources folder", at);
            return;
        }

        context.Files.Add(path);
    }

    private static string Unknown(string kind, string name, string parent, IEnumerable<string> known)
    {
        List<string> names = known.ToList();
        string? hint = names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        string message = $"unknown {kind} '{name}' in <{parent}>";
        if (hint is not null)
        {
            return message + $"; names are case-sensitive, did you mean '{hint}'?";
        }

        return names.Count == 0 ? message + $"; <{parent}> has no fields" : message + $"; the fields are {string.Join(", ", names.Select(n => "'" + n + "'"))}";
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "..." : text;

    /// <summary>Encodes field values in field-number order; repeated scalars are packed.</summary>
    private static byte[] Serialize(List<(FieldDescriptor Field, object Value)> values, bool keepDefaults = false)
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        foreach (IGrouping<FieldDescriptor, (FieldDescriptor Field, object Value)> group in values.GroupBy(v => v.Field).OrderBy(g => g.Key.FieldNumber))
        {
            FieldDescriptor field = group.Key;
            if (field.IsRepeated && IsPackable(field.FieldType))
            {
                using var packed = new MemoryStream();
                var inner = new CodedOutputStream(packed);
                foreach ((_, object value) in group)
                {
                    WriteValue(inner, field.FieldType, value);
                }

                inner.Flush();
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(packed.ToArray()));
                continue;
            }

            foreach ((_, object value) in group)
            {
                if (!field.IsRepeated && !field.HasPresence && !keepDefaults && IsDefault(value))
                {
                    continue;
                }

                output.WriteTag(field.FieldNumber, WireTypeOf(field.FieldType));
                WriteValue(output, field.FieldType, value);
            }
        }

        output.Flush();
        return stream.ToArray();
    }

    private static bool IsPackable(FieldType type) => type is not (FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group);

    private static bool IsDefault(object value) => value switch
    {
        string s => s.Length == 0,
        bool b => !b,
        long l => l == 0,
        ulong u => u == 0,
        double d => d == 0 && !double.IsNegative(d),
        float f => f == 0 && !float.IsNegative(f),
        ByteString bytes => bytes.IsEmpty,
        _ => false,
    };

    private static WireFormat.WireType WireTypeOf(FieldType type) => type switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => WireFormat.WireType.Fixed64,
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => WireFormat.WireType.Fixed32,
        FieldType.String or FieldType.Bytes or FieldType.Message => WireFormat.WireType.LengthDelimited,
        _ => WireFormat.WireType.Varint,
    };

    private static void WriteValue(CodedOutputStream output, FieldType type, object value)
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
            case FieldType.Message: output.WriteBytes(ByteString.CopyFrom((byte[])value)); break;
            default: throw new InvalidOperationException($"unsupported field type {type}");
        }
    }
}
