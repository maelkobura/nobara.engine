// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Entity;

/// <summary>
/// Reads the initial value of a component from an entity definition.
/// </summary>
/// <typeparam name="T">The type of the component.</typeparam>
/// <param name="values">The values that the <c>.nof</c> file writes for the component.</param>
/// <param name="defaultValue">The default value of the component, registered with it.</param>
/// <returns>The initial value of the component on the entities of the definition.</returns>
/// <remarks>
/// A reader runs once per definition and per context, when the definitions
/// are loaded and when they are reloaded; it may allocate. It throws the
/// exception that <see cref="DefinitionValues.Error"/> returns for a value
/// that breaks a rule of the component.
/// </remarks>
public delegate T ComponentReader<T>(DefinitionValues values, T defaultValue)
    where T : unmanaged;

/// <summary>
/// The values that an entity definition writes for one component, as its
/// <c>.nof</c> file writes them: the child elements of
/// <c>&lt;Component&gt;</c>, given to a <see cref="ComponentReader{T}"/>.
/// </summary>
/// <remarks>
/// A value of the wrong type, a missing required value, a value written twice,
/// and a child element that the reader never reads (a typing error such as
/// <c>&lt;Hp&gt;</c>) fail the load of the definition, with the position of
/// the value in the file.
/// </remarks>
/// <example>
/// <code>
/// worlds.RegisterComponent&lt;Sound&gt;(context, "example.animals.sound", (values, fallback) => new Sound
/// {
///     Id = sounds.IndexOf(values.GetObjectId("Sound")),
///     Volume = (float)values.GetFloat("Volume", fallback.Volume),
/// });
/// </code>
/// </example>
public sealed class DefinitionValues
{
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    internal DefinitionValues(GenericElement element, string definitionId, string componentName)
    {
        Element = element;
        DefinitionId = definitionId;
        ComponentName = componentName;
    }

    /// <summary>Gets the <c>&lt;Component&gt;</c> element, in the generic representation.</summary>
    public GenericElement Element { get; }

    /// <summary>Gets the object identifier of the definition: <c>entity:example.animals.pig</c>.</summary>
    public string DefinitionId { get; }

    /// <summary>Gets the qualified name of the component.</summary>
    public string ComponentName { get; }

    /// <summary>Tells whether a value is written.</summary>
    /// <param name="name">The name of the child element: <c>Max</c>.</param>
    /// <returns><see langword="true"/> when it is written.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _read.Add(name);
        return Element.Children.Any(c => c.Name == name);
    }

    /// <summary>Reads a required boolean.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is missing or is not <c>true</c> or <c>false</c>.</exception>
    public bool GetBool(string name) => ReadBool(Required(name));

    /// <summary>Reads an optional boolean.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is not <c>true</c> or <c>false</c>.</exception>
    public bool GetBool(string name, bool fallback) => Optional(name) is { } element ? ReadBool(element) : fallback;

    /// <summary>Reads a required whole number on 32 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is missing, is not a whole number, or does not fit on 32 bits.</exception>
    public int GetInt(string name) => ReadInt(Required(name));

    /// <summary>Reads an optional whole number on 32 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is not a whole number, or does not fit on 32 bits.</exception>
    public int GetInt(string name, int fallback) => Optional(name) is { } element ? ReadInt(element) : fallback;

    /// <summary>Reads a required whole number on 64 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is missing or is not a whole number.</exception>
    public long GetLong(string name) => ReadLong(Required(name));

    /// <summary>Reads an optional whole number on 64 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is not a whole number.</exception>
    public long GetLong(string name, long fallback) => Optional(name) is { } element ? ReadLong(element) : fallback;

    /// <summary>Reads a required number; a whole number is accepted.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is missing or is not a number.</exception>
    public double GetFloat(string name) => ReadFloat(Required(name));

    /// <summary>Reads an optional number; a whole number is accepted.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DefinitionValueException">The value is not a number.</exception>
    public double GetFloat(string name, double fallback) => Optional(name) is { } element ? ReadFloat(element) : fallback;

    /// <summary>Reads required text: any value without child elements.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The text as written, trimmed.</returns>
    /// <exception cref="DefinitionValueException">The value is missing or has child elements.</exception>
    public string GetString(string name) => ReadString(Required(name));

    /// <summary>Reads optional text.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The text as written, trimmed.</returns>
    /// <exception cref="DefinitionValueException">The value has child elements.</exception>
    public string GetString(string name, string fallback) => Optional(name) is { } element ? ReadString(element) : fallback;

    /// <summary>Reads a required object identifier, such as <c>mesh:example.animals.pig</c>. The compiler checked that the object exists, except in an optional component.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="DefinitionValueException">The value is missing or is not an object identifier.</exception>
    public string GetObjectId(string name)
    {
        GenericElement element = Required(name);
        if (element.Value?.Kind != GenericValueKind.ObjectId)
        {
            throw Error($"<{name}> is '{Describe(element)}', expected an object identifier such as 'mesh:example.animals.pig'.", element);
        }

        return element.Value.Text;
    }

    /// <summary>Reads a required vector: child elements <c>X</c>, <c>Y</c>, and optionally <c>Z</c> and <c>W</c>.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The components.</returns>
    /// <exception cref="DefinitionValueException">The value is missing or is not a vector.</exception>
    public IReadOnlyList<double> GetVector(string name) => ReadVector(Required(name));

    /// <summary>Reads an optional vector.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The components, or <see langword="null"/> when it is not written.</returns>
    /// <exception cref="DefinitionValueException">The value is not a vector.</exception>
    public IReadOnlyList<double>? GetVectorOrNull(string name) => Optional(name) is { } element ? ReadVector(element) : null;

    /// <summary>Reads a value of any form, in the generic representation, for structured values.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The element, or <see langword="null"/> when it is not written.</returns>
    /// <exception cref="DefinitionValueException">The value is written twice.</exception>
    public GenericElement? GetElement(string name) => Optional(name);

    /// <summary>Creates the exception that reports a value that breaks a rule of the component.</summary>
    /// <param name="message">What is wrong, such as <c>&lt;Max&gt; is -1, expected at least 1.</c></param>
    /// <param name="at">The value at fault, or <see langword="null"/> for the component.</param>
    /// <returns>The exception, to throw.</returns>
    public DefinitionValueException Error(string message, GenericElement? at = null) =>
        new(message, at is not null && !string.IsNullOrEmpty(at.Source.File) ? at.Source : Element.Source);

    /// <summary>Reports the values that the reader never read.</summary>
    internal void CheckAllRead()
    {
        foreach (GenericElement child in Element.Children)
        {
            if (!_read.Contains(child.Name))
            {
                throw Error($"<{child.Name}> is not a value of the component.", child);
            }
        }
    }

    private GenericElement Required(string name) =>
        Optional(name) ?? throw Error($"<{name}> is missing.");

    private GenericElement? Optional(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _read.Add(name);
        GenericElement? found = null;
        foreach (GenericElement child in Element.Children)
        {
            if (child.Name != name)
            {
                continue;
            }

            if (found is not null)
            {
                throw Error($"<{name}> is written twice.", child);
            }

            found = child;
        }

        return found;
    }

    private bool ReadBool(GenericElement element) =>
        element.Value?.TryGetBoolean(out bool value) == true
            ? value
            : throw Error($"<{element.Name}> is '{Describe(element)}', expected true or false.", element);

    private int ReadInt(GenericElement element)
    {
        long value = ReadLong(element);
        return value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw Error($"<{element.Name}> is {value}, which does not fit on 32 bits.", element);
    }

    private long ReadLong(GenericElement element) =>
        element.Value?.TryGetInteger(out long value) == true
            ? value
            : throw Error($"<{element.Name}> is '{Describe(element)}', expected a whole number.", element);

    private double ReadFloat(GenericElement element) =>
        element.Value?.TryGetNumber(out double value) == true
            ? value
            : throw Error($"<{element.Name}> is '{Describe(element)}', expected a number.", element);

    private string ReadString(GenericElement element) =>
        element.Value?.Text ?? throw Error($"<{element.Name}> has child elements, expected text.", element);

    private IReadOnlyList<double> ReadVector(GenericElement element) =>
        element.Vector.Count > 0
            ? element.Vector
            : throw Error($"<{element.Name}> is not a vector; write <X>, <Y>, and optionally <Z> and <W>, all numbers.", element);

    internal static string Describe(GenericElement element) => element.Value?.Text ?? "<" + element.Name + "> with child elements";
}

/// <summary>A value of an entity definition that cannot be converted, with its position in the <c>.nof</c> file.</summary>
public sealed class DefinitionValueException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What is wrong with the value.</param>
    /// <param name="position">Where the value is written.</param>
    public DefinitionValueException(string message, SourcePosition position)
        : base(message)
    {
        Position = position;
    }

    /// <summary>Gets where the value is written.</summary>
    public SourcePosition Position { get; }
}
