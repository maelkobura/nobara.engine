// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// This file is also compiled into Nobara.Sdk (netstandard2.0), which reads
// module.nobara with the same code. Keep it free of newer APIs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>
/// Reads the XML of a manifest strictly: every element and attribute must be
/// known, and every problem is reported with its position.
/// </summary>
internal sealed class ManifestXml
{
    public static readonly Regex ModuleId = new Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);

    public static readonly Regex ShortName = new Regex(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    public static readonly Regex StateId = new Regex(@"^state:[a-z0-9_]+(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant);

    public static readonly Regex SettingKey = new Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant);

    public static readonly Regex OpaqueName = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

    public ManifestXml(string file, DiagnosticBag diagnostics)
    {
        File = file;
        Diagnostics = diagnostics;
    }

    public string File { get; }

    public DiagnosticBag Diagnostics { get; }

    public Location At(XObject node) => Location.Of(File, node);

    public void Error(string code, string message, XObject node) => Diagnostics.Error(code, message, At(node));

    /// <summary>Parses the text, checks the root element and its <c>format</c> attribute.</summary>
    public XElement? Root(string text, string rootName)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            Diagnostics.Error(ManifestCodes.Malformed, $"the file is not well-formed XML: {exception.Message}", new Location(File, exception.LineNumber, exception.LinePosition));
            return null;
        }

        XElement root = document.Root!;
        if (root.Name.NamespaceName.Length > 0 || root.Name.LocalName != rootName)
        {
            Error(ManifestCodes.Malformed, $"the root element is '{root.Name.LocalName}', expected '{rootName}'", root);
            return null;
        }

        XAttribute? format = root.Attribute("format");
        if (format is null)
        {
            Error(ManifestCodes.Missing, $"<{rootName}> needs a 'format' attribute; the current format is 1", root);
            return null;
        }

        if (format.Value.Trim() != "1")
        {
            Error(ManifestCodes.Malformed, $"unsupported format '{format.Value}'; this version reads format 1", format);
            return null;
        }

        return root;
    }

    /// <summary>Reports every attribute and child element that is not listed, and any text content.</summary>
    public void Allow(XElement element, string[] attributes, string[] children)
    {
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            if (attribute.Name.NamespaceName.Length > 0 || Array.IndexOf(attributes, attribute.Name.LocalName) < 0)
            {
                Error(ManifestCodes.Unknown, Unknown("attribute", attribute.Name.LocalName, element.Name.LocalName, attributes), attribute);
            }
        }

        foreach (XElement child in element.Elements())
        {
            if (child.Name.NamespaceName.Length > 0 || Array.IndexOf(children, child.Name.LocalName) < 0)
            {
                Error(ManifestCodes.Unknown, Unknown("element", child.Name.LocalName, element.Name.LocalName, children), child);
            }
        }

        if (children.Length > 0 || element.HasElements)
        {
            foreach (XText text in element.Nodes().OfType<XText>())
            {
                if (text.Value.Trim().Length > 0)
                {
                    Error(ManifestCodes.Unknown, $"<{element.Name.LocalName}> contains text; only elements are allowed here", text);
                    break;
                }
            }
        }
    }

    /// <summary>Gets the only child element with that name; reports a duplicate, and a missing required one.</summary>
    public XElement? Single(XElement parent, string name, bool required)
    {
        XElement? found = null;
        foreach (XElement child in parent.Elements(name))
        {
            if (found is null)
            {
                found = child;
            }
            else
            {
                Error(ManifestCodes.Duplicate, $"<{name}> appears more than once in <{parent.Name.LocalName}>", child);
            }
        }

        if (found is null && required)
        {
            Error(ManifestCodes.Missing, $"the required <{name}> element is missing from <{parent.Name.LocalName}>", parent);
        }

        return found;
    }

    /// <summary>Gets the trimmed text of the only child element with that name, or <see langword="null"/>.</summary>
    public string? ChildText(XElement parent, string name, bool required)
    {
        XElement? element = Single(parent, name, required);
        if (element is null)
        {
            return null;
        }

        Allow(element, Array.Empty<string>(), Array.Empty<string>());
        string text = element.Value.Trim();
        if (text.Length == 0)
        {
            Error(ManifestCodes.Missing, $"<{name}> is empty", element);
            return null;
        }

        return text;
    }

    /// <summary>Gets the trimmed value of an attribute, or <see langword="null"/>.</summary>
    public string? Attribute(XElement element, string name, bool required)
    {
        XAttribute? attribute = element.Attribute(name);
        if (attribute is null)
        {
            if (required)
            {
                Error(ManifestCodes.Missing, $"<{element.Name.LocalName}> needs a '{name}' attribute", element);
            }

            return null;
        }

        string value = attribute.Value.Trim();
        if (value.Length == 0)
        {
            Error(ManifestCodes.Missing, $"the '{name}' attribute of <{element.Name.LocalName}> is empty", attribute);
            return null;
        }

        return value;
    }

    /// <summary>Gets a boolean attribute: <c>true</c> or <c>false</c>, without regard to case.</summary>
    public bool Boolean(XElement element, string name, bool defaultValue)
    {
        XAttribute? attribute = element.Attribute(name);
        if (attribute is null)
        {
            return defaultValue;
        }

        string value = attribute.Value.Trim();
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Error(ManifestCodes.InvalidValue, $"the '{name}' attribute of <{element.Name.LocalName}> is '{value}', expected 'true' or 'false'", attribute);
        return defaultValue;
    }

    /// <summary>Parses one of a fixed set of words, without regard to case.</summary>
    public T? Choice<T>(XObject node, string what, string? value, IReadOnlyDictionary<string, T> choices)
        where T : struct
    {
        if (value is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, T> choice in choices)
        {
            if (string.Equals(choice.Key, value, StringComparison.OrdinalIgnoreCase))
            {
                return choice.Value;
            }
        }

        Error(ManifestCodes.InvalidValue, $"{what} is '{value}', expected {string.Join(", ", choices.Keys.Select(k => "'" + k + "'"))}", node);
        return null;
    }

    public XObject NodeOf(XElement element, string attribute) => (XObject?)element.Attribute(attribute) ?? element;

    public static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

    private static string Unknown(string kind, string name, string parent, string[] allowed)
    {
        string? hint = allowed.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        string message = $"unknown {kind} '{name}' in <{parent}>";
        if (hint is not null)
        {
            return message + $"; names are case-sensitive, did you mean '{hint}'?";
        }

        return allowed.Length == 0 ? message + $"; <{parent}> takes no {kind}s" : message + $"; expected {string.Join(", ", allowed.Select(a => "'" + a + "'"))}";
    }
}

/// <summary>The diagnostic codes of manifest reading.</summary>
internal static class ManifestCodes
{
    public const string Malformed = "NRC100";
    public const string Unknown = "NRC101";
    public const string Missing = "NRC102";
    public const string Duplicate = "NRC103";
    public const string InvalidIdentifier = "NRC104";
    public const string InvalidVersion = "NRC105";
    public const string InvalidRange = "NRC106";
    public const string InvalidValue = "NRC107";
    public const string InvalidSetting = "NRC108";
    public const string Inconsistent = "NRC109";
    public const string Reserved = "NRC110";
}
