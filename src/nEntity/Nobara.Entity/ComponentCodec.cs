// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nobara.Core;

namespace Nobara.Entity;

/// <summary>Converts the values that a definition writes for a component into the bytes of the component.</summary>
internal abstract class ComponentCodec
{
    /// <summary>Reads the initial value of the component.</summary>
    /// <param name="element">The <c>&lt;Component&gt;</c> element.</param>
    /// <param name="definitionId">The definition, for the messages.</param>
    /// <param name="name">The qualified name of the component.</param>
    /// <returns>The value, as many bytes as the component has.</returns>
    /// <exception cref="DefinitionValueException">A value cannot be converted.</exception>
    public abstract byte[] Read(GenericElement element, string definitionId, string name);
}

/// <summary>The codec of a component of type <typeparamref name="T"/>: its reader, or its public fields.</summary>
internal sealed class ComponentCodec<T>(T defaultValue, ComponentReader<T>? reader) : ComponentCodec
    where T : unmanaged
{
    public override byte[] Read(GenericElement element, string definitionId, string name)
    {
        if (element.Children.Count == 0 && element.Value is { Text.Length: > 0 })
        {
            throw new DefinitionValueException(
                $"<{element.Name}> holds the text '{element.Value.Text}'; write the values of the component as child elements.",
                element.Source);
        }

        T value;
        if (reader is not null)
        {
            var values = new DefinitionValues(element, definitionId, name);
            value = reader(values, defaultValue);
            values.CheckAllRead();
        }
        else
        {
            object boxed = defaultValue;
            FieldReader.Read(boxed, typeof(T), element, element.Source);
            value = (T)boxed;
        }

        byte[] bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }
}

/// <summary>
/// Writes the values of an element into the public fields of a struct: each
/// child element names a field, a nested struct is a child element with its
/// own children.
/// </summary>
internal static class FieldReader
{
    /// <summary>Reads the child elements of an element into the public fields of a boxed struct.</summary>
    /// <param name="boxed">The boxed struct, changed in place.</param>
    /// <param name="type">The type of the struct.</param>
    /// <param name="element">The element whose children are the values.</param>
    /// <param name="fallback">The position of the messages that have no value to point at.</param>
    /// <exception cref="DefinitionValueException">A value cannot be converted.</exception>
    public static void Read(object boxed, Type type, GenericElement element, SourcePosition fallback)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GenericElement child in element.Children)
        {
            SourcePosition at = Position(child, fallback);
            if (!seen.Add(child.Name))
            {
                throw new DefinitionValueException($"<{child.Name}> is written twice.", at);
            }

            FieldInfo? field = type.GetField(child.Name, BindingFlags.Public | BindingFlags.Instance);
            if (field is null)
            {
                FieldInfo[] fields = Writable(type);
                string known = fields.Length == 0
                    ? $"'{type.Name}' has no public field"
                    : $"the fields of '{type.Name}' are " + string.Join(", ", fields.Select(f => f.Name));
                throw new DefinitionValueException($"<{child.Name}> is not a field of the component; {known}.", at);
            }

            object current = field.GetValue(boxed)!;
            field.SetValue(boxed, Convert(field, field.FieldType, child, current, at));
        }
    }

    private static object Convert(FieldInfo field, Type type, GenericElement element, object current, SourcePosition at)
    {
        if (element.Attributes.Count > 0)
        {
            throw new DefinitionValueException($"<{element.Name}> takes no attributes.", at);
        }

        if (field.IsDefined(typeof(FixedBufferAttribute)))
        {
            throw Unsupported(field, element, at);
        }

        if (type.IsEnum)
        {
            string? text = element.Value?.Text;
            string[] names = Enum.GetNames(type);
            return text is not null && names.Contains(text, StringComparer.Ordinal)
                ? Enum.Parse(type, text)
                : throw new DefinitionValueException(
                    $"<{element.Name}> is '{DefinitionValues.Describe(element)}', expected one of {string.Join(", ", names)}.", at);
        }

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean:
                return element.Value?.TryGetBoolean(out bool flag) == true
                    ? flag
                    : throw new DefinitionValueException($"<{element.Name}> is '{DefinitionValues.Describe(element)}', expected true or false.", at);
            case TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64:
                if (element.Value?.TryGetInteger(out long integer) != true)
                {
                    throw new DefinitionValueException($"<{element.Name}> is '{DefinitionValues.Describe(element)}', expected a whole number.", at);
                }

                try
                {
                    return System.Convert.ChangeType(integer, type, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    throw new DefinitionValueException($"<{element.Name}> is {integer}, which does not fit in a {type.Name}.", at);
                }

            case TypeCode.Single or TypeCode.Double:
                if (element.Value?.TryGetNumber(out double number) != true)
                {
                    throw new DefinitionValueException($"<{element.Name}> is '{DefinitionValues.Describe(element)}', expected a number.", at);
                }

                return type == typeof(float) ? (object)(float)number : number;
        }

        if (!type.IsValueType || type.IsPrimitive || type.IsPointer || Type.GetTypeCode(type) != TypeCode.Object || Writable(type).Length == 0)
        {
            throw Unsupported(field, element, at);
        }

        if (element.Children.Count == 0 && element.Value is { Text.Length: > 0 })
        {
            throw new DefinitionValueException(
                $"<{element.Name}> is '{element.Value.Text}', expected child elements: the fields {string.Join(", ", Writable(type).Select(f => f.Name))}.", at);
        }

        Read(current, type, element, at);
        return current;
    }

    private static DefinitionValueException Unsupported(FieldInfo field, GenericElement element, SourcePosition at) =>
        new($"<{element.Name}> is a field of type '{field.FieldType.Name}', which a file cannot write; the module that registers the component can read it with a component reader.", at);

    private static FieldInfo[] Writable(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsDefined(typeof(FixedBufferAttribute))).ToArray();

    private static SourcePosition Position(GenericElement element, SourcePosition fallback) =>
        string.IsNullOrEmpty(element.Source.File) ? fallback : element.Source;
}
