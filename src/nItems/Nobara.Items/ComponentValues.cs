// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Nobara.Core;

namespace Nobara.Items;

/// <summary>
/// The values of one component of one item, as its <c>.nof</c> file writes
/// them, given to the reader of the component type to convert them.
/// </summary>
/// <remarks>
/// <para>
/// Each value is a child element of <c>&lt;Component&gt;</c>, read by name
/// with a typed method. A value of the wrong type, a missing required value,
/// a value written twice, and a child that the reader never reads (a typing
/// error such as <c>&lt;Fod&gt;</c>) fail the load, with the position of the
/// value in the file.
/// </para>
/// <para>
/// Readers run once per item and per context, when nItems starts and when
/// resources are reloaded; they may allocate. Throw the exception that
/// <see cref="Error"/> returns for a rule of the component, such as a bound.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// registry.RegisterComponent(context, "example.food.edible", values => new Edible(
///     values.GetInt("Food"),
///     values.GetTicks("Duration", fallbackSeconds: 1.6)), EqualityComparer&lt;Edible&gt;.Default);
/// </code>
/// </example>
public sealed class ComponentValues
{
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    internal ComponentValues(GenericElement element, string itemId, string componentName, double tickRate)
    {
        Element = element;
        ItemId = itemId;
        ComponentName = componentName;
        TickRate = tickRate;
    }

    /// <summary>Gets the <c>&lt;Component&gt;</c> element, in the generic representation.</summary>
    public GenericElement Element { get; }

    /// <summary>Gets the object identifier of the item.</summary>
    public string ItemId { get; }

    /// <summary>Gets the qualified name of the component.</summary>
    public string ComponentName { get; }

    /// <summary>Gets the number of ticks per second that <see cref="GetTicks(string)"/> uses.</summary>
    public double TickRate { get; }

    /// <summary>Tells whether a value is written.</summary>
    /// <param name="name">The name of the child element: <c>Food</c>.</param>
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
    /// <exception cref="ItemComponentException">The value is missing or is not <c>true</c> or <c>false</c>.</exception>
    public bool GetBool(string name) => ReadBool(Required(name));

    /// <summary>Reads an optional boolean.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ItemComponentException">The value is not <c>true</c> or <c>false</c>.</exception>
    public bool GetBool(string name, bool fallback) => Optional(name) is { } element ? ReadBool(element) : fallback;

    /// <summary>Reads a required whole number on 32 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ItemComponentException">The value is missing, is not a whole number, or does not fit on 32 bits.</exception>
    public int GetInt(string name) => ReadInt(Required(name));

    /// <summary>Reads an optional whole number on 32 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ItemComponentException">The value is not a whole number, or does not fit on 32 bits.</exception>
    public int GetInt(string name, int fallback) => Optional(name) is { } element ? ReadInt(element) : fallback;

    /// <summary>Reads a required whole number on 64 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ItemComponentException">The value is missing or is not a whole number.</exception>
    public long GetLong(string name) => ReadLong(Required(name));

    /// <summary>Reads an optional whole number on 64 bits.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ItemComponentException">The value is not a whole number.</exception>
    public long GetLong(string name, long fallback) => Optional(name) is { } element ? ReadLong(element) : fallback;

    /// <summary>Reads a required number; a whole number is accepted.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ItemComponentException">The value is missing or is not a number.</exception>
    public double GetFloat(string name) => ReadFloat(Required(name));

    /// <summary>Reads an optional number; a whole number is accepted.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ItemComponentException">The value is not a number.</exception>
    public double GetFloat(string name, double fallback) => Optional(name) is { } element ? ReadFloat(element) : fallback;

    /// <summary>Reads required text: any value without child elements.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The text as written, trimmed.</returns>
    /// <exception cref="ItemComponentException">The value is missing or has child elements.</exception>
    public string GetString(string name) => ReadString(Required(name));

    /// <summary>Reads optional text.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallback">The value when it is not written.</param>
    /// <returns>The text as written, trimmed.</returns>
    /// <exception cref="ItemComponentException">The value has child elements.</exception>
    public string GetString(string name, string fallback) => Optional(name) is { } element ? ReadString(element) : fallback;

    /// <summary>Reads a required object identifier, such as <c>sound:example.food.crunch</c>. The compiler checked that the object exists.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="ItemComponentException">The value is missing or is not an object identifier.</exception>
    public string GetObjectId(string name)
    {
        GenericElement element = Required(name);
        if (element.Value?.Kind != GenericValueKind.ObjectId)
        {
            throw Error($"<{name}> is '{Describe(element)}', expected an object identifier such as 'sound:example.food.crunch'.", element);
        }

        return element.Value.Text;
    }

    /// <summary>
    /// Reads a required duration written in seconds, and converts it to ticks
    /// at <see cref="TickRate"/>, rounded to the nearest tick: definitions
    /// stay independent of the tick rate of the distribution.
    /// </summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The number of ticks.</returns>
    /// <exception cref="ItemComponentException">The value is missing, is not a number, or is negative.</exception>
    public long GetTicks(string name)
    {
        GenericElement element = Required(name);
        return ToTicks(name, ReadFloat(element), element);
    }

    /// <summary>Reads an optional duration written in seconds, and converts it to ticks.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <param name="fallbackSeconds">The duration in seconds when it is not written.</param>
    /// <returns>The number of ticks.</returns>
    /// <exception cref="ItemComponentException">The value is not a number, or is negative.</exception>
    public long GetTicks(string name, double fallbackSeconds)
    {
        GenericElement? element = Optional(name);
        return ToTicks(name, element is null ? fallbackSeconds : ReadFloat(element), element);
    }

    /// <summary>Reads a required vector: child elements <c>X</c>, <c>Y</c>, and optionally <c>Z</c> and <c>W</c>.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The components.</returns>
    /// <exception cref="ItemComponentException">The value is missing or is not a vector.</exception>
    public IReadOnlyList<double> GetVector(string name)
    {
        GenericElement element = Required(name);
        if (element.Vector.Count == 0)
        {
            throw Error($"<{name}> is not a vector; write <X>, <Y>, and optionally <Z> and <W>, all numbers.", element);
        }

        return element.Vector;
    }

    /// <summary>Reads a value of any form, in the generic representation, for structured values.</summary>
    /// <param name="name">The name of the child element.</param>
    /// <returns>The element, or <see langword="null"/> when it is not written.</returns>
    /// <exception cref="ItemComponentException">The value is written twice.</exception>
    public GenericElement? GetElement(string name) => Optional(name);

    /// <summary>Creates the exception that reports a value that breaks a rule of the component.</summary>
    /// <param name="message">What is wrong, such as <c>&lt;Food&gt; is -1, expected at least 0.</c></param>
    /// <param name="at">The value at fault, or <see langword="null"/> for the component.</param>
    /// <returns>The exception, to throw.</returns>
    public ItemComponentException Error(string message, GenericElement? at = null) =>
        new(message, at is not null && !string.IsNullOrEmpty(at.Source.File) ? at.Source : Element.Source);

    /// <summary>Reports the values that the reader never read, and the values written twice.</summary>
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

    private long ToTicks(string name, double seconds, GenericElement? at)
    {
        if (seconds < 0)
        {
            throw Error(string.Create(CultureInfo.InvariantCulture, $"<{name}> is {seconds} seconds, expected a duration of at least 0."), at);
        }

        return (long)Math.Round(seconds * TickRate, MidpointRounding.AwayFromZero);
    }

    private static string Describe(GenericElement element) => element.Value?.Text ?? "<" + element.Name + "> with child elements";
}

/// <summary>A value of an item component that cannot be converted, with its position in the <c>.nof</c> file.</summary>
public sealed class ItemComponentException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What is wrong with the value.</param>
    /// <param name="position">Where the value is written.</param>
    public ItemComponentException(string message, SourcePosition position)
        : base(message)
    {
        Position = position;
    }

    /// <summary>Gets where the value is written.</summary>
    public SourcePosition Position { get; }
}
