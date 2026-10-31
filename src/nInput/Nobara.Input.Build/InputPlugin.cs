// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml.Linq;
using Nobara.ResourceCompiler.Plugins;

[assembly: ResourcePlugin(typeof(Nobara.Input.Build.InputPlugin))]

namespace Nobara.Input.Build;

/// <summary>
/// The resource plugin of the type <c>InputAction</c>: the rules that the
/// schema of the type cannot declare.
/// </summary>
/// <remarks>
/// The schema already checks the form: a <c>&lt;Value&gt;</c> among
/// <c>Button</c>, <c>Float</c>, <c>Axis1D</c>, and <c>Axis2D</c>, a
/// <c>&lt;Map&gt;</c> that names an <c>inputmap:</c> object, the elements of
/// <c>&lt;Bindings&gt;</c> and <c>&lt;Interaction&gt;</c>, and the bounds of
/// the numbers. The plugin checks the names of the controls, the form of each
/// binding, and what fits the kind of value of the action.
/// </remarks>
public sealed class InputPlugin : ResourcePlugin
{
    /// <summary>The diagnostic of a name that is not an input control.</summary>
    public const string UnknownControl = "NINP001";

    /// <summary>The diagnostic of a binding whose controls do not fit its form, such as an axis in <c>&lt;Key&gt;</c>.</summary>
    public const string InvalidBinding = "NINP002";

    /// <summary>The diagnostic of a binding that cannot give the value of the action.</summary>
    public const string IncompatibleBinding = "NINP003";

    /// <summary>The diagnostic of an interaction or a threshold on an axis.</summary>
    public const string NotForAxis = "NINP004";

    /// <summary>The diagnostic of a duration or a threshold of 0.</summary>
    public const string ZeroValue = "NINP005";

    /// <summary>The diagnostic of a binding written twice in one action.</summary>
    public const string DuplicateBinding = "NINP006";

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> ResourceTypes { get; } = ["InputAction"];

    /// <inheritdoc/>
    public override void Validate(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        XElement content = context.Content;

        // An override changes some fields only; the core checks the result when it loads it.
        InputValueKind? kind = ValueKind(content.Element("Value")?.Value.Trim());
        bool axis = kind is InputValueKind.Axis1D or InputValueKind.Axis2D;
        if (axis && content.Element("Interaction") is { } interaction)
        {
            context.Error(NotForAxis, $"an axis has no interaction: remove <Interaction> from '{context.ObjectId}', or make it a Button or a Float", interaction);
        }

        if (Number(content, "Threshold") is { } threshold)
        {
            if (axis)
            {
                context.Error(NotForAxis, $"an axis has no threshold: remove it from '{context.ObjectId}'", threshold.At);
            }
            else if (threshold.Value == 0)
            {
                context.Error(ZeroValue, "the threshold is above 0; remove it for the default 0.5", threshold.At);
            }
        }

        if (content.Element("Interaction")?.Elements().FirstOrDefault() is { } choice)
        {
            string? attribute = choice.Name.LocalName switch
            {
                "Hold" => "duration",
                "Tap" => "max",
                "DoubleTap" => "window",
                _ => null,
            };
            if (attribute is not null && Number(choice, attribute) is { Value: 0 } zero)
            {
                context.Error(ZeroValue, $"'{attribute}' of <{choice.Name.LocalName}> is a number of seconds above 0", zero.At);
            }
        }

        if (content.Element("Bindings") is { } bindings)
        {
            CheckBindings(context, bindings, kind);
        }
    }

    private static void CheckBindings(ResourceContext context, XElement bindings, InputValueKind? kind)
    {
        var seen = new List<InputBinding>();
        foreach (XElement element in bindings.Elements())
        {
            (BindingKind form, (string Name, XObject At)[] names) = Read(element);

            var controls = new InputControl[names.Length];
            bool known = true;
            for (int i = 0; i < names.Length; i++)
            {
                if (!InputControl.TryParse(names[i].Name, out controls[i]))
                {
                    known = false;
                    context.Error(UnknownControl, $"'{names[i].Name}' is not an input control: a key such as 'Key.W' or 'Key.Control', a mouse control such as 'Mouse.Left' or 'Mouse.Delta', or a gamepad control such as 'Gamepad.South'", names[i].At);
                }
            }

            if (!known)
            {
                continue;
            }

            if (!InputBinding.TryCreate(form, controls, out InputBinding? binding, out string? error))
            {
                context.Error(InvalidBinding, $"<{element.Name.LocalName}> is not a valid binding: {error}", element);
                continue;
            }

            if (kind is { } value && !binding.Accepts(value))
            {
                context.Error(IncompatibleBinding, $"'{binding}' gives a {Describe(binding.Shape)}, which cannot be the value of a {value}: {Expected(value)}", element);
            }

            if (seen.Contains(binding))
            {
                context.Error(DuplicateBinding, $"'{binding}' is bound twice in '{context.ObjectId}'", element);
            }

            seen.Add(binding);
        }
    }

    private static (BindingKind Form, (string Name, XObject At)[] Names) Read(XElement element)
    {
        (string Name, XObject At)[] text = [(element.Value.Trim(), element)];
        return element.Name.LocalName switch
        {
            "Key" => (BindingKind.Key, text),
            "Axis" => (BindingKind.Axis, text),
            "Composite1D" => (BindingKind.Composite1D, Attributes(element, "negative", "positive")),
            "Composite2D" => (BindingKind.Composite2D, Attributes(element, "up", "down", "left", "right")),
            _ => (BindingKind.Combo, element.Elements("Key").Select(k => (k.Value.Trim(), (XObject)k)).ToArray()),
        };
    }

    private static (string Name, XObject At)[] Attributes(XElement element, params string[] names) =>
        names.Select(n => element.Attribute(n) is { } attribute ? (attribute.Value.Trim(), (XObject)attribute) : (string.Empty, (XObject)element))
            .Where(p => p.Item1.Length > 0)
            .ToArray();

    private static InputValueKind? ValueKind(string? text) => text switch
    {
        "Button" or "VALUE_KIND_BUTTON" => InputValueKind.Button,
        "Float" or "VALUE_KIND_FLOAT" => InputValueKind.Float,
        "Axis1D" or "VALUE_KIND_AXIS1_D" => InputValueKind.Axis1D,
        "Axis2D" or "VALUE_KIND_AXIS2_D" => InputValueKind.Axis2D,
        _ => null,
    };

    private static (XObject At, double Value)? Number(XElement element, string name)
    {
        XObject? at = (XObject?)element.Attribute(char.ToLowerInvariant(name[0]) + name[1..]) ?? element.Element(name) ?? (XObject?)element.Attribute(name);
        string? text = at switch
        {
            XAttribute attribute => attribute.Value,
            XElement child => child.Value,
            _ => null,
        };
        return text is not null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? (at!, value) : null;
    }

    private static string Describe(InputControlKind shape) => shape switch
    {
        InputControlKind.Button => "button",
        InputControlKind.Axis1D => "1D axis",
        _ => "2D axis",
    };

    private static string Expected(InputValueKind kind) => kind switch
    {
        InputValueKind.Axis2D => "bind a 2D axis such as 'Mouse.Delta', or a <Composite2D>",
        InputValueKind.Axis1D => "bind a key, a <Combo>, a 1D axis such as 'Mouse.Wheel', or a <Composite1D>",
        _ => "bind a key, a <Combo>, or a 1D axis such as 'Mouse.Wheel'",
    };
}
