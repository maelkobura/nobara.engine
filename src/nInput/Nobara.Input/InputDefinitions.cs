// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml.Linq;
using Nobara.Core.Resources;
using S = Nobara.Input.Schemas;

namespace Nobara.Input;

/// <summary>
/// The content of an action resource, decoded once per process and shared by
/// every context.
/// </summary>
internal sealed class ActionDefinition
{
    /// <summary>The threshold of an action that declares none.</summary>
    public const float DefaultThreshold = 0.5f;

    /// <summary>The dead zone of an action that declares none.</summary>
    public const float DefaultDeadZone = 0.15f;

    public required ResourceData Data { get; init; }

    public required string DisplayName { get; init; }

    public required InputValueKind ValueKind { get; init; }

    public required string MapId { get; init; }

    public required IReadOnlyList<InputBinding> Bindings { get; init; }

    public required InputInteraction Interaction { get; init; }

    public required float Threshold { get; init; }

    public required float DeadZone { get; init; }

    /// <summary>Decodes a resource of type <c>inputaction</c>.</summary>
    /// <param name="data">The resource.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="InvalidDataException">A binding or an interaction is not valid; the build checks them, so only a resource built by another tool fails.</exception>
    public static ActionDefinition Decode(ResourceData data)
    {
        S.InputAction content = S.InputAction.Parser.ParseFrom(data.Content.Span);
        return new ActionDefinition
        {
            Data = data,
            DisplayName = content.DisplayName.Length > 0 ? content.DisplayName : InputNames.DerivedDisplayName(data.ObjectId),
            ValueKind = (InputValueKind)content.Value,
            MapId = content.Map,
            Bindings = InputXml.Bindings(content.Bindings, data.ObjectId),
            Interaction = InputXml.Interaction(content.Interaction, data.ObjectId),
            Threshold = content.Threshold > 0 ? content.Threshold : DefaultThreshold,
            DeadZone = content.DeadZone > 0 ? content.DeadZone : DefaultDeadZone,
        };
    }
}

/// <summary>The content of a map resource.</summary>
/// <param name="Data">The resource.</param>
/// <param name="DisplayName">The localization key of the name.</param>
/// <param name="Active">Whether the map is active when the game starts.</param>
internal sealed record MapDefinition(ResourceData Data, string DisplayName, bool Active)
{
    /// <summary>Decodes a resource of type <c>inputmap</c>.</summary>
    /// <param name="data">The resource.</param>
    /// <returns>The definition.</returns>
    public static MapDefinition Decode(ResourceData data)
    {
        S.InputMap content = S.InputMap.Parser.ParseFrom(data.Content.Span);
        return new MapDefinition(data, content.DisplayName.Length > 0 ? content.DisplayName : InputNames.DerivedDisplayName(data.ObjectId), content.Active);
    }
}

/// <summary>The names of the resource types and of the configuration.</summary>
internal static class InputNames
{
    /// <summary>The type prefix of the actions.</summary>
    public const string ActionType = "inputaction";

    /// <summary>The type prefix of the maps.</summary>
    public const string MapType = "inputmap";

    /// <summary>The localization key of a resource without one: the type, then the identifier without its type, <c>inputaction.example.input.jump</c>.</summary>
    public static string DerivedDisplayName(string objectId)
    {
        int colon = objectId.IndexOf(':', StringComparison.Ordinal);
        return objectId[..colon] + "." + objectId[(colon + 1)..];
    }
}

/// <summary>
/// The XML form of bindings and interactions, shared by the <c>.nof</c> schema
/// and by <c>config/input.xml</c>: <c>&lt;Key&gt;</c>, <c>&lt;Axis&gt;</c>,
/// <c>&lt;Composite1D&gt;</c>, <c>&lt;Composite2D&gt;</c>, <c>&lt;Combo&gt;</c>,
/// and one interaction element.
/// </summary>
internal static class InputXml
{
    /// <summary>Reads the compiled bindings of an action.</summary>
    public static IReadOnlyList<InputBinding> Bindings(S.Bindings? bindings, string owner)
    {
        if (bindings is null)
        {
            return [];
        }

        var result = new List<InputBinding>();
        foreach (string key in bindings.Key)
        {
            result.Add(Make(BindingKind.Key, owner, key));
        }

        foreach (string axis in bindings.Axis)
        {
            result.Add(Make(BindingKind.Axis, owner, axis));
        }

        foreach (S.Composite1D composite in bindings.Composite1D)
        {
            result.Add(Make(BindingKind.Composite1D, owner, composite.Negative, composite.Positive));
        }

        foreach (S.Composite2D composite in bindings.Composite2D)
        {
            result.Add(Make(BindingKind.Composite2D, owner, composite.Up, composite.Down, composite.Left, composite.Right));
        }

        foreach (S.Combo combo in bindings.Combo)
        {
            result.Add(Make(BindingKind.Combo, owner, [.. combo.Key]));
        }

        return result;
    }

    /// <summary>Reads the compiled interaction of an action.</summary>
    public static InputInteraction Interaction(S.Interaction? interaction, string owner)
    {
        try
        {
            return interaction?.KindCase switch
            {
                S.Interaction.KindOneofCase.Release => InputInteraction.Release,
                S.Interaction.KindOneofCase.Hold => InputInteraction.Hold(Seconds(interaction.Hold.Duration)),
                S.Interaction.KindOneofCase.Tap => InputInteraction.Tap(Seconds(interaction.Tap.Max)),
                S.Interaction.KindOneofCase.DoubleTap => InputInteraction.DoubleTap(Seconds(interaction.DoubleTap.Window)),
                S.Interaction.KindOneofCase.Toggle => InputInteraction.Toggle,
                _ => InputInteraction.Press,
            };
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException($"The interaction of '{owner}' is not valid: {exception.Message}", exception);
        }
    }

    /// <summary>Writes bindings as the children of a <c>&lt;Bindings&gt;</c> element.</summary>
    public static XElement ToXml(IReadOnlyList<InputBinding> bindings)
    {
        var element = new XElement("Bindings");
        foreach (InputBinding binding in bindings)
        {
            element.Add(binding.Kind switch
            {
                BindingKind.Key => new XElement("Key", binding.Controls[0].Name),
                BindingKind.Axis => new XElement("Axis", binding.Controls[0].Name),
                BindingKind.Composite1D => new XElement(
                    "Composite1D",
                    new XAttribute("negative", binding.Controls[0].Name),
                    new XAttribute("positive", binding.Controls[1].Name)),
                BindingKind.Composite2D => new XElement(
                    "Composite2D",
                    new XAttribute("up", binding.Controls[0].Name),
                    new XAttribute("down", binding.Controls[1].Name),
                    new XAttribute("left", binding.Controls[2].Name),
                    new XAttribute("right", binding.Controls[3].Name)),
                _ => new XElement("Combo", binding.Controls.Select(c => new XElement("Key", c.Name))),
            });
        }

        return element;
    }

    /// <summary>Writes an interaction as the child of an <c>&lt;Interaction&gt;</c> element.</summary>
    public static XElement ToXml(InputInteraction interaction)
    {
        string seconds = interaction.Seconds.ToString("R", CultureInfo.InvariantCulture);
        XElement kind = interaction.Kind switch
        {
            InteractionKind.Hold => new XElement("Hold", new XAttribute("duration", seconds)),
            InteractionKind.Tap => new XElement("Tap", new XAttribute("max", seconds)),
            InteractionKind.DoubleTap => new XElement("DoubleTap", new XAttribute("window", seconds)),
            _ => new XElement(interaction.Kind.ToString()),
        };
        return new XElement("Interaction", kind);
    }

    /// <summary>Reads the children of a <c>&lt;Bindings&gt;</c> element.</summary>
    /// <returns>The bindings, or <see langword="null"/> with the reason in <paramref name="error"/>.</returns>
    public static List<InputBinding>? FromXml(XElement bindings, out string? error)
    {
        var result = new List<InputBinding>();
        foreach (XElement element in bindings.Elements())
        {
            InputBinding? binding = element.Name.LocalName switch
            {
                "Key" => Read(BindingKind.Key, element, out error, element.Value.Trim()),
                "Axis" => Read(BindingKind.Axis, element, out error, element.Value.Trim()),
                "Composite1D" => Read(BindingKind.Composite1D, element, out error, Attribute(element, "negative"), Attribute(element, "positive")),
                "Composite2D" => Read(BindingKind.Composite2D, element, out error, Attribute(element, "up"), Attribute(element, "down"), Attribute(element, "left"), Attribute(element, "right")),
                "Combo" => Read(BindingKind.Combo, element, out error, [.. element.Elements("Key").Select(k => k.Value.Trim())]),
                _ => Fail($"unknown binding <{element.Name.LocalName}>; the bindings are <Key>, <Axis>, <Composite1D>, <Composite2D>, and <Combo>", out error),
            };
            if (binding is null)
            {
                return null;
            }

            result.Add(binding);
        }

        error = null;
        return result;
    }

    /// <summary>Reads an <c>&lt;Interaction&gt;</c> element.</summary>
    /// <returns>The interaction, or <see langword="null"/> with the reason in <paramref name="error"/>.</returns>
    public static InputInteraction? InteractionFromXml(XElement interaction, out string? error)
    {
        XElement[] kinds = interaction.Elements().ToArray();
        if (kinds.Length != 1)
        {
            error = "<Interaction> holds one element: <Press/>, <Release/>, <Hold duration=\"…\"/>, <Tap max=\"…\"/>, <DoubleTap window=\"…\"/>, or <Toggle/>";
            return null;
        }

        XElement kind = kinds[0];
        try
        {
            error = null;
            return kind.Name.LocalName switch
            {
                "Press" => InputInteraction.Press,
                "Release" => InputInteraction.Release,
                "Toggle" => InputInteraction.Toggle,
                "Hold" => InputInteraction.Hold(Seconds(kind, "duration")),
                "Tap" => InputInteraction.Tap(Seconds(kind, "max")),
                "DoubleTap" => InputInteraction.DoubleTap(Seconds(kind, "window")),
                _ => throw new FormatException($"unknown interaction <{kind.Name.LocalName}>"),
            };
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            error = exception.Message;
            return null;
        }
    }

    private static InputBinding Make(BindingKind kind, string owner, params string[] names)
    {
        var controls = new InputControl[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            if (!InputControl.TryParse(names[i], out controls[i]))
            {
                throw new InvalidDataException($"'{owner}' names the unknown input control '{names[i]}'.");
            }
        }

        return InputBinding.TryCreate(kind, controls, out InputBinding? binding, out string? error)
            ? binding
            : throw new InvalidDataException($"A binding of '{owner}' is not valid: {error}.");
    }

    private static InputBinding? Read(BindingKind kind, XElement element, out string? error, params string?[] names)
    {
        var controls = new InputControl[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            if (!InputControl.TryParse(names[i], out controls[i]))
            {
                error = names[i] is null ? $"<{element.Name.LocalName}> misses a control" : $"'{names[i]}' is not an input control, such as 'Key.W' or 'Mouse.Left'";
                return null;
            }
        }

        return InputBinding.TryCreate(kind, controls, out InputBinding? binding, out error) ? binding : null;
    }

    /// <summary>Gets the duration written in the file: 0.3 stays 0.3, not the double nearest to the float 0.3.</summary>
    private static double Seconds(float value) => double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string? Attribute(XElement element, string name) => element.Attribute(name)?.Value.Trim();

    private static InputBinding? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }

    private static double Seconds(XElement element, string attribute)
    {
        string? text = element.Attribute(attribute)?.Value;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new FormatException($"<{element.Name.LocalName}> needs '{attribute}', a number of seconds such as 0.3");
    }
}
