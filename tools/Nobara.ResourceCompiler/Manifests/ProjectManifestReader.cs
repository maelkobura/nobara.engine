// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>Reads and validates <c>project.nobara</c>.</summary>
public static class ProjectManifestReader
{
    private static readonly Regex GameId = new(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant);

    private static readonly Regex Namespace = new(@"^[A-Za-z][A-Za-z0-9_]*(/[A-Za-z][A-Za-z0-9_]*)*(/\*)?$", RegexOptions.CultureInvariant);

    /// <summary>Reads a project file.</summary>
    /// <param name="text">The content of the file.</param>
    /// <param name="file">The path of the file, used in diagnostics.</param>
    /// <param name="diagnostics">Receives the problems found.</param>
    /// <returns>The manifest, or <see langword="null"/> when an error was found.</returns>
    public static ProjectManifest? Read(string text, string file, DiagnosticBag diagnostics)
    {
        var xml = new ManifestXml(file, diagnostics);
        int errors = diagnostics.ErrorCount;
        XElement? root = xml.Root(text, "NobaraProject");
        if (root is null)
        {
            return null;
        }

        xml.Allow(root, ["format"], ["Id", "Name", "Version", "Icon", "Modules", "Overrides", "ProtectedNamespaces", "Modding"]);

        string? id = xml.ChildText(root, "Id", required: true);
        if (id is not null && !GameId.IsMatch(id))
        {
            xml.Error(ManifestCodes.InvalidIdentifier, $"'{id}' is not a game identifier: lowercase dot-separated names", root.Element("Id")!);
        }

        string? name = xml.ChildText(root, "Name", required: true);
        SemanticVersion? version = null;
        string? versionText = xml.ChildText(root, "Version", required: true);
        if (versionText is not null && !SemanticVersion.TryParse(versionText, out version))
        {
            xml.Error(ManifestCodes.InvalidVersion, $"'{versionText}' is not a semantic version (major.minor.patch)", root.Element("Version")!);
        }

        string? icon = xml.ChildText(root, "Icon", required: false);
        List<ModuleEntry> modules = ReadModules(xml, root);
        List<ProjectOverride> overrides = ReadOverrides(xml, root);
        List<Declared> namespaces = ReadNamespaces(xml, root);

        bool modding = false;
        XElement? moddingElement = xml.Single(root, "Modding", required: false);
        if (moddingElement is not null)
        {
            xml.Allow(moddingElement, ["enabled"], []);
            modding = xml.Boolean(moddingElement, "enabled", false);
        }

        if (diagnostics.ErrorCount != errors)
        {
            return null;
        }

        return new ProjectManifest
        {
            File = file,
            Id = id!,
            Name = name!,
            Version = version!,
            Icon = icon,
            Modules = modules,
            Overrides = overrides,
            ProtectedNamespaces = namespaces,
            ModdingEnabled = modding,
            Location = xml.At(root),
        };
    }

    private static List<ModuleEntry> ReadModules(ManifestXml xml, XElement root)
    {
        var result = new List<ModuleEntry>();
        XElement? list = xml.Single(root, "Modules", required: true);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, [], ["Local", "Package"]);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in list.Elements())
        {
            bool required = false;
            if (element.Name.LocalName == "Local")
            {
                xml.Allow(element, ["path", "required"], []);
                string? path = xml.Attribute(element, "path", required: true);
                required = xml.Boolean(element, "required", false);
                if (path is null)
                {
                    continue;
                }

                string normalized = NormalizePath(path);
                if (!paths.Add(normalized))
                {
                    xml.Error(ManifestCodes.Duplicate, $"the local module '{path}' is listed twice", element);
                    continue;
                }

                result.Add(new ModuleEntry { Kind = ModuleEntryKind.Local, Path = normalized, Required = required, Location = xml.At(element) });
            }
            else if (element.Name.LocalName == "Package")
            {
                xml.Allow(element, ["id", "version", "required"], []);
                string? id = xml.Attribute(element, "id", required: true);
                string? rangeText = xml.Attribute(element, "version", required: true);
                required = xml.Boolean(element, "required", false);
                if (id is null || rangeText is null)
                {
                    continue;
                }

                if (!ManifestXml.ModuleId.IsMatch(id))
                {
                    xml.Error(ManifestCodes.InvalidIdentifier, $"'{id}' is not a module identifier", element.Attribute("id")!);
                    continue;
                }

                if (!VersionRange.TryParse(rangeText, out VersionRange? range, out string error))
                {
                    xml.Error(ManifestCodes.InvalidRange, $"package '{id}': {error}", element.Attribute("version")!);
                    continue;
                }

                if (!ids.Add(id))
                {
                    xml.Error(ManifestCodes.Duplicate, $"package '{id}' is listed twice", element);
                    continue;
                }

                result.Add(new ModuleEntry { Kind = ModuleEntryKind.Package, Id = id, Range = range, Required = required, Location = xml.At(element) });
            }
        }

        return result;
    }

    private static List<ProjectOverride> ReadOverrides(ManifestXml xml, XElement root)
    {
        var result = new List<ProjectOverride>();
        XElement? list = xml.Single(root, "Overrides", required: false);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, [], ["Project"]);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in list.Elements("Project"))
        {
            xml.Allow(element, ["id", "path"], []);
            string? id = xml.Attribute(element, "id", required: true);
            string? path = xml.Attribute(element, "path", required: true);
            if (id is null || path is null)
            {
                continue;
            }

            if (!ManifestXml.ModuleId.IsMatch(id))
            {
                xml.Error(ManifestCodes.InvalidIdentifier, $"'{id}' is not a module identifier", element.Attribute("id")!);
                continue;
            }

            if (!ids.Add(id))
            {
                xml.Error(ManifestCodes.Duplicate, $"module '{id}' is overridden twice", element);
                continue;
            }

            result.Add(new ProjectOverride { Id = id, Path = NormalizePath(path), Location = xml.At(element) });
        }

        return result;
    }

    private static List<Declared> ReadNamespaces(ManifestXml xml, XElement root)
    {
        var result = new List<Declared>();
        XElement? list = xml.Single(root, "ProtectedNamespaces", required: false);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, [], ["Namespace"]);
        foreach (XElement element in list.Elements("Namespace"))
        {
            xml.Allow(element, [], []);
            string value = element.Value.Trim();
            if (!Namespace.IsMatch(value))
            {
                xml.Error(ManifestCodes.InvalidIdentifier, $"'{value}' is not a namespace pattern: names separated by '/', optionally ending with '/*'", element);
                continue;
            }

            result.Add(new Declared { Name = value, Location = xml.At(element) });
        }

        return result;
    }

    internal static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}
