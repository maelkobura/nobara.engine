// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nobara.Sdk.Tasks;

/// <summary>A problem found while reading a manifest.</summary>
/// <param name="Code">The error code, <c>NSDK</c> followed by three digits.</param>
/// <param name="Message">The message, without the code and the position.</param>
/// <param name="Line">The 1-based line, or 0 when unknown.</param>
/// <param name="Column">The 1-based column, or 0 when unknown.</param>
internal sealed record ManifestDiagnostic(string Code, string Message, int Line, int Column);

/// <summary>A module this module depends on.</summary>
/// <param name="Id">The module identifier.</param>
/// <param name="Range">The version range as written.</param>
/// <param name="NuGetRange">The same range in NuGet notation.</param>
/// <param name="Optional">Whether the dependency is optional.</param>
internal sealed record ModuleDependency(string Id, string Range, string NuGetRange, bool Optional);

/// <summary>A third-party NuGet package a module uses.</summary>
/// <param name="Id">The package identifier.</param>
/// <param name="Version">The version.</param>
internal sealed record ModuleLibrary(string Id, string Version);

/// <summary>The parts of <c>module.nobara</c> that the SDK reads.</summary>
/// <param name="Id">The module identifier.</param>
/// <param name="Version">The module version.</param>
/// <param name="Dependencies">The declared module dependencies.</param>
/// <param name="Libraries">The declared third-party packages.</param>
internal sealed record ModuleManifest(
    string Id,
    string Version,
    IReadOnlyList<ModuleDependency> Dependencies,
    IReadOnlyList<ModuleLibrary> Libraries);

/// <summary>
/// Reads the identity, version, dependencies, and libraries of a
/// <c>module.nobara</c> file. Every other element is ignored.
/// </summary>
internal static class ModuleManifestReader
{
    private static readonly Regex SemVer = new(
        @"^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?(\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Parses manifest text.</summary>
    /// <param name="text">The content of the file.</param>
    /// <param name="diagnostics">Receives the problems found.</param>
    /// <returns>The manifest, or <see langword="null"/> when a problem prevents reading it.</returns>
    public static ModuleManifest? Parse(string text, ICollection<ManifestDiagnostic> diagnostics)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            diagnostics.Add(new("NSDK002", $"the file is not well-formed XML: {exception.Message}", exception.LineNumber, exception.LinePosition));
            return null;
        }

        XElement root = document.Root!;
        if (root.Name.LocalName != "NobaraModule")
        {
            Report(diagnostics, "NSDK002", $"the root element is '{root.Name.LocalName}', expected 'NobaraModule'", root);
            return null;
        }

        string? format = root.Attribute("format")?.Value;
        if (format is not null && format.Trim() != "1")
        {
            Report(diagnostics, "NSDK002", $"unsupported format '{format}'; this SDK reads format 1", root);
            return null;
        }

        int errorsBefore = diagnostics.Count;
        string id = ChildText(root, "Id");
        if (id.Length == 0)
        {
            Report(diagnostics, "NSDK003", "the required <Id> element is missing or empty", root);
        }

        string version = ChildText(root, "Version");
        if (version.Length == 0)
        {
            Report(diagnostics, "NSDK004", "the required <Version> element is missing or empty", root);
        }
        else if (!SemVer.IsMatch(version))
        {
            Report(diagnostics, "NSDK004", $"'{version}' is not a semantic version (major.minor.patch)", root.Element("Version")!);
        }

        List<ModuleDependency> dependencies = ReadDependencies(root, id, diagnostics);
        List<ModuleLibrary> libraries = ReadLibraries(root, diagnostics);

        return diagnostics.Count == errorsBefore ? new ModuleManifest(id, version, dependencies, libraries) : null;
    }

    private static List<ModuleDependency> ReadDependencies(XElement root, string moduleId, ICollection<ManifestDiagnostic> diagnostics)
    {
        var result = new List<ModuleDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in root.Elements("Dependencies").Elements("Module"))
        {
            string id = element.Attribute("id")?.Value.Trim() ?? string.Empty;
            string range = element.Attribute("version")?.Value.Trim() ?? string.Empty;
            string optionalText = element.Attribute("optional")?.Value.Trim() ?? "false";

            if (id.Length == 0)
            {
                Report(diagnostics, "NSDK006", "a <Module> dependency has no 'id' attribute", element);
                continue;
            }

            if (string.Equals(id, moduleId, StringComparison.OrdinalIgnoreCase))
            {
                Report(diagnostics, "NSDK006", $"module '{id}' depends on itself", element);
                continue;
            }

            if (!seen.Add(id))
            {
                Report(diagnostics, "NSDK006", $"module '{id}' is declared twice in <Dependencies>", element);
                continue;
            }

            if (!TryBoolean(optionalText, out bool optional))
            {
                Report(diagnostics, "NSDK006", $"the 'optional' attribute of '{id}' is '{optionalText}', expected 'true' or 'false'", element);
                continue;
            }

            if (range.Length == 0)
            {
                Report(diagnostics, "NSDK007", $"dependency '{id}' has no 'version' range", element);
                continue;
            }

            if (!VersionRangeConverter.TryConvert(range, out string nuget, out string error))
            {
                Report(diagnostics, "NSDK007", $"dependency '{id}': {error}", element);
                continue;
            }

            result.Add(new ModuleDependency(id, range, nuget, optional));
        }

        return result;
    }

    private static List<ModuleLibrary> ReadLibraries(XElement root, ICollection<ManifestDiagnostic> diagnostics)
    {
        var result = new List<ModuleLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in root.Elements("Libraries").Elements("Package"))
        {
            string id = element.Attribute("id")?.Value.Trim() ?? string.Empty;
            string version = element.Attribute("version")?.Value.Trim() ?? string.Empty;
            if (id.Length == 0 || version.Length == 0)
            {
                Report(diagnostics, "NSDK008", "a <Package> library needs both an 'id' and a 'version' attribute", element);
                continue;
            }

            if (!seen.Add(id))
            {
                Report(diagnostics, "NSDK008", $"library '{id}' is declared twice in <Libraries>", element);
                continue;
            }

            result.Add(new ModuleLibrary(id, version));
        }

        return result;
    }

    private static string ChildText(XElement parent, string name)
    {
        return parent.Element(name)?.Value.Trim() ?? string.Empty;
    }

    private static bool TryBoolean(string text, out bool value)
    {
        value = false;
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        return string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static void Report(ICollection<ManifestDiagnostic> diagnostics, string code, string message, XElement at)
    {
        var info = (IXmlLineInfo)at;
        bool known = info.HasLineInfo();
        diagnostics.Add(new ManifestDiagnostic(code, message, known ? info.LineNumber : 0, known ? info.LinePosition : 0));
    }
}
