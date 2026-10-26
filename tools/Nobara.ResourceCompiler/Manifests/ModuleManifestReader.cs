// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// This file is also compiled into Nobara.Sdk (netstandard2.0), which reads
// module.nobara with the same code. Keep it free of newer APIs.

using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>Reads and validates <c>module.nobara</c>.</summary>
public static class ModuleManifestReader
{
    internal static readonly Dictionary<string, ModuleSide> Sides = new Dictionary<string, ModuleSide>
    {
        ["client"] = ModuleSide.Client,
        ["server"] = ModuleSide.Server,
        ["both"] = ModuleSide.Both,
    };

    internal static readonly Dictionary<string, ModuleSide> SingleSides = new Dictionary<string, ModuleSide>
    {
        ["client"] = ModuleSide.Client,
        ["server"] = ModuleSide.Server,
    };

    private static readonly Dictionary<string, ModuleLevel> Levels = new Dictionary<string, ModuleLevel>
    {
        ["platform"] = ModuleLevel.Platform,
        ["business"] = ModuleLevel.Business,
    };

    private static readonly Dictionary<string, SettingScope> Scopes = new Dictionary<string, SettingScope>
    {
        ["user"] = SettingScope.User,
        ["distribution"] = SettingScope.Distribution,
    };

    private static readonly Dictionary<string, SettingType> Types = new Dictionary<string, SettingType>
    {
        ["bool"] = SettingType.Bool,
        ["int"] = SettingType.Int,
        ["float"] = SettingType.Float,
        ["string"] = SettingType.String,
        ["enum"] = SettingType.Enum,
    };

    private static readonly string[] RootElements =
    {
        "Id", "ShortName", "Version", "Level", "Side", "Dependencies", "Libraries", "Conflicts",
        "Reserves", "States", "Settings", "RequiresBackend", "BackendOf",
    };

    /// <summary>Reads a manifest.</summary>
    /// <param name="text">The content of the file.</param>
    /// <param name="file">The path of the file, used in diagnostics.</param>
    /// <param name="diagnostics">Receives the problems found.</param>
    /// <returns>The manifest, or <see langword="null"/> when an error was found.</returns>
    public static ModuleManifest? Read(string text, string file, DiagnosticBag diagnostics)
    {
        var xml = new ManifestXml(file, diagnostics);
        int errors = diagnostics.ErrorCount;
        XElement? root = xml.Root(text, "NobaraModule");
        if (root is null)
        {
            return null;
        }

        xml.Allow(root, new[] { "format" }, RootElements);

        string? id = xml.ChildText(root, "Id", required: true);
        if (id is not null && !ManifestXml.ModuleId.IsMatch(id))
        {
            xml.Error(ManifestCodes.InvalidIdentifier, $"'{id}' is not a module identifier: dot-separated names of letters, digits, and underscores, each starting with a letter", root.Element("Id")!);
        }

        string? shortName = xml.ChildText(root, "ShortName", required: false);
        if (shortName is not null && !ManifestXml.ShortName.IsMatch(shortName))
        {
            xml.Error(ManifestCodes.InvalidIdentifier, $"'{shortName}' is not a short name: letters, digits, and underscores, starting with a letter, without dots", root.Element("ShortName")!);
        }

        SemanticVersion? version = null;
        string? versionText = xml.ChildText(root, "Version", required: true);
        if (versionText is not null && !SemanticVersion.TryParse(versionText, out version))
        {
            xml.Error(ManifestCodes.InvalidVersion, $"'{versionText}' is not a semantic version (major.minor.patch)", root.Element("Version")!);
        }

        string? levelText = xml.ChildText(root, "Level", required: true);
        ModuleLevel? level = levelText is null ? null : xml.Choice(root.Element("Level")!, "<Level>", levelText, Levels);

        string? sideText = xml.ChildText(root, "Side", required: true);
        ModuleSide? side = sideText is null ? null : xml.Choice(root.Element("Side")!, "<Side>", sideText, Sides);

        List<ModuleDependency> dependencies = ReadDependencies(xml, root, id);
        List<ModuleLibrary> libraries = ReadLibraries(xml, root);
        List<Declared> conflicts = ReadNames(xml, root, "Conflicts", "Module", "id", isModule: true, ownId: id);
        List<Declared> reserves = ReadNames(xml, root, "Reserves", "Resource", "name", isModule: false, ownId: null);
        List<StateDeclaration> states = ReadStates(xml, root, side ?? ModuleSide.Both);
        List<SettingDeclaration> settings = ReadSettings(xml, root, id, shortName, side ?? ModuleSide.Both);
        ModuleSide backendRequired = ReadBackendRequired(xml, root, side ?? ModuleSide.Both);
        string? backendOf = ReadBackendOf(xml, root, id, dependencies);

        if (diagnostics.ErrorCount != errors)
        {
            return null;
        }

        return new ModuleManifest
        {
            File = file,
            Id = id!,
            ShortName = shortName,
            Version = version!,
            Level = level!.Value,
            Side = side!.Value,
            Dependencies = dependencies,
            Libraries = libraries,
            Conflicts = conflicts,
            Reserves = reserves,
            States = states,
            Settings = settings,
            BackendRequired = backendRequired,
            BackendOf = backendOf,
            Location = xml.At(root),
        };
    }

    private static List<ModuleDependency> ReadDependencies(ManifestXml xml, XElement root, string? ownId)
    {
        var result = new List<ModuleDependency>();
        XElement? list = xml.Single(root, "Dependencies", required: false);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, Array.Empty<string>(), new[] { "Module" });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in list.Elements("Module"))
        {
            xml.Allow(element, new[] { "id", "version", "optional" }, Array.Empty<string>());
            string? id = xml.Attribute(element, "id", required: true);
            string? rangeText = xml.Attribute(element, "version", required: true);
            bool optional = xml.Boolean(element, "optional", false);
            if (id is null)
            {
                continue;
            }

            if (!ManifestXml.ModuleId.IsMatch(id))
            {
                xml.Error(ManifestCodes.InvalidIdentifier, $"'{id}' is not a module identifier", element.Attribute("id")!);
                continue;
            }

            if (ownId is not null && string.Equals(id, ownId, StringComparison.OrdinalIgnoreCase))
            {
                xml.Error(ManifestCodes.Inconsistent, $"module '{id}' depends on itself", element);
                continue;
            }

            if (!seen.Add(id))
            {
                xml.Error(ManifestCodes.Duplicate, $"module '{id}' is declared twice in <Dependencies>", element);
                continue;
            }

            if (rangeText is null)
            {
                continue;
            }

            if (!VersionRange.TryParse(rangeText, out VersionRange? range, out string error))
            {
                xml.Error(ManifestCodes.InvalidRange, $"dependency '{id}': {error}", element.Attribute("version")!);
                continue;
            }

            result.Add(new ModuleDependency { Id = id, Range = range!, Optional = optional, Location = xml.At(element) });
        }

        return result;
    }

    private static List<ModuleLibrary> ReadLibraries(ManifestXml xml, XElement root)
    {
        var result = new List<ModuleLibrary>();
        XElement? list = xml.Single(root, "Libraries", required: false);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, Array.Empty<string>(), new[] { "Package" });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in list.Elements("Package"))
        {
            xml.Allow(element, new[] { "id", "version" }, Array.Empty<string>());
            string? id = xml.Attribute(element, "id", required: true);
            string? version = xml.Attribute(element, "version", required: true);
            if (id is null || version is null)
            {
                continue;
            }

            if (!seen.Add(id))
            {
                xml.Error(ManifestCodes.Duplicate, $"library '{id}' is declared twice in <Libraries>", element);
                continue;
            }

            result.Add(new ModuleLibrary { Id = id, Version = version, Location = xml.At(element) });
        }

        return result;
    }

    private static List<Declared> ReadNames(ManifestXml xml, XElement root, string listName, string itemName, string attribute, bool isModule, string? ownId)
    {
        var result = new List<Declared>();
        XElement? list = xml.Single(root, listName, required: false);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, Array.Empty<string>(), new[] { itemName });
        var seen = new HashSet<string>(isModule ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (XElement element in list.Elements(itemName))
        {
            xml.Allow(element, new[] { attribute }, Array.Empty<string>());
            string? name = xml.Attribute(element, attribute, required: true);
            if (name is null)
            {
                continue;
            }

            bool valid = isModule ? ManifestXml.ModuleId.IsMatch(name) : ManifestXml.OpaqueName.IsMatch(name);
            if (!valid)
            {
                xml.Error(ManifestCodes.InvalidIdentifier, isModule ? $"'{name}' is not a module identifier" : $"'{name}' is not a resource name: letters, digits, dots, dashes, and underscores", element.Attribute(attribute)!);
                continue;
            }

            if (ownId is not null && string.Equals(name, ownId, StringComparison.OrdinalIgnoreCase))
            {
                xml.Error(ManifestCodes.Inconsistent, $"module '{name}' cannot conflict with itself", element);
                continue;
            }

            if (!seen.Add(name))
            {
                xml.Error(ManifestCodes.Duplicate, $"'{name}' appears twice in <{listName}>", element);
                continue;
            }

            result.Add(new Declared { Name = name, Location = xml.At(element) });
        }

        return result;
    }

    private static List<StateDeclaration> ReadStates(ManifestXml xml, XElement root, ModuleSide moduleSide)
    {
        var result = new List<StateDeclaration>();
        XElement? list = xml.Single(root, "States", required: false);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, Array.Empty<string>(), new[] { "State" });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement element in list.Elements("State"))
        {
            xml.Allow(element, new[] { "id", "side" }, Array.Empty<string>());
            string? id = xml.Attribute(element, "id", required: true);
            ModuleSide side = xml.Choice(xml.NodeOf(element, "side"), "the 'side' attribute", xml.Attribute(element, "side", required: false), SingleSides) ?? ModuleSide.None;
            if (id is null)
            {
                continue;
            }

            if (!ManifestXml.StateId.IsMatch(id))
            {
                xml.Error(ManifestCodes.InvalidIdentifier, $"'{id}' is not a state identifier: 'state:' followed by a lowercase namespace and name, as in 'state:example.menu'", element.Attribute("id")!);
                continue;
            }

            if (side != ModuleSide.None && (side & moduleSide) == 0)
            {
                xml.Error(ManifestCodes.Inconsistent, $"state '{id}' is declared for the {side.ToString().ToLowerInvariant()} side, where the module does not run", element);
                continue;
            }

            if (!seen.Add(id))
            {
                xml.Error(ManifestCodes.Duplicate, $"state '{id}' is declared twice", element);
                continue;
            }

            result.Add(new StateDeclaration { Id = id, Side = side, Location = xml.At(element) });
        }

        return result;
    }

    private static List<SettingDeclaration> ReadSettings(ManifestXml xml, XElement root, string? moduleId, string? shortName, ModuleSide moduleSide)
    {
        var result = new List<SettingDeclaration>();
        XElement? list = xml.Single(root, "Settings", required: false);
        if (list is null)
        {
            return result;
        }

        xml.Allow(list, Array.Empty<string>(), new[] { "Setting" });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement element in list.Elements("Setting"))
        {
            int errors = xml.Diagnostics.ErrorCount;
            xml.Allow(element, new[] { "key", "scope", "type", "default", "min", "max", "values", "side" }, Array.Empty<string>());
            string? key = xml.Attribute(element, "key", required: true);
            SettingScope? scope = xml.Choice(xml.NodeOf(element, "scope"), "the 'scope' attribute", xml.Attribute(element, "scope", required: true), Scopes);
            SettingType? type = xml.Choice(xml.NodeOf(element, "type"), "the 'type' attribute", xml.Attribute(element, "type", required: true), Types);
            string? defaultValue = element.Attribute("default")?.Value.Trim();
            ModuleSide side = xml.Choice(xml.NodeOf(element, "side"), "the 'side' attribute", xml.Attribute(element, "side", required: false), SingleSides) ?? ModuleSide.None;

            if (key is not null)
            {
                if (!ManifestXml.SettingKey.IsMatch(key))
                {
                    xml.Error(ManifestCodes.InvalidIdentifier, $"'{key}' is not a configuration key: dot-separated names, as in 'voxel.viewRadius'", element.Attribute("key")!);
                }
                else if (!HasModulePrefix(key, moduleId, shortName))
                {
                    string expected = shortName is null ? $"'{moduleId}.'" : $"'{shortName.ToLowerInvariant()}.' or '{moduleId}.'";
                    xml.Error(ManifestCodes.InvalidSetting, $"key '{key}' must start with the short name or the identifier of the module: {expected}", element.Attribute("key")!);
                }
                else if (!seen.Add(key))
                {
                    xml.Error(ManifestCodes.Duplicate, $"key '{key}' is declared twice", element);
                }
            }

            if (side != ModuleSide.None && (side & moduleSide) == 0)
            {
                xml.Error(ManifestCodes.Inconsistent, $"key '{key}' is declared for the {side.ToString().ToLowerInvariant()} side, where the module does not run", element);
            }

            double? min = Bound(xml, element, "min", type);
            double? max = Bound(xml, element, "max", type);
            if (min is double low && max is double high && low > high)
            {
                xml.Error(ManifestCodes.InvalidSetting, $"the minimum {low} of key '{key}' is above its maximum {high}", element);
            }

            string[] values = Array.Empty<string>();
            XAttribute? valuesAttribute = element.Attribute("values");
            if (type == SettingType.Enum)
            {
                if (valuesAttribute is null || valuesAttribute.Value.Trim().Length == 0)
                {
                    xml.Error(ManifestCodes.InvalidSetting, $"enum key '{key}' needs a 'values' attribute: the accepted words separated by commas", element);
                }
                else
                {
                    values = Array.ConvertAll(valuesAttribute.Value.Split(','), v => v.Trim());
                    if (Array.Exists(values, v => v.Length == 0) || new HashSet<string>(values).Count != values.Length)
                    {
                        xml.Error(ManifestCodes.InvalidSetting, $"the 'values' of key '{key}' must be distinct non-empty words separated by commas", valuesAttribute);
                    }
                }
            }
            else if (valuesAttribute is not null && type is not null)
            {
                xml.Error(ManifestCodes.InvalidSetting, $"only an enum key takes a 'values' attribute", valuesAttribute);
            }

            if (defaultValue is null)
            {
                xml.Error(ManifestCodes.Missing, $"key '{key}' needs a 'default' attribute", element);
            }

            if (xml.Diagnostics.ErrorCount != errors || key is null || scope is null || type is null || defaultValue is null)
            {
                continue;
            }

            var setting = new SettingDeclaration
            {
                Key = key,
                Scope = scope.Value,
                Type = type.Value,
                Default = defaultValue,
                Min = min,
                Max = max,
                Values = values,
                Side = side,
                Location = xml.At(element),
            };
            if (!setting.Accepts(defaultValue, out string error))
            {
                xml.Error(ManifestCodes.InvalidSetting, $"the default of key '{key}' is invalid: {error}", element.Attribute("default")!);
                continue;
            }

            result.Add(setting);
        }

        return result;
    }

    private static double? Bound(ManifestXml xml, XElement element, string name, SettingType? type)
    {
        XAttribute? attribute = element.Attribute(name);
        if (attribute is null)
        {
            return null;
        }

        if (type is not null && type != SettingType.Int && type != SettingType.Float)
        {
            xml.Error(ManifestCodes.InvalidSetting, $"only an int or float key takes a '{name}' attribute", attribute);
            return null;
        }

        if (!ManifestXml.TryNumber(attribute.Value.Trim(), out double value))
        {
            xml.Error(ManifestCodes.InvalidSetting, $"'{attribute.Value}' is not a number", attribute);
            return null;
        }

        return value;
    }

    private static bool HasModulePrefix(string key, string? moduleId, string? shortName)
    {
        if (moduleId is null)
        {
            return true;
        }

        if (key.StartsWith(moduleId + ".", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return shortName is not null && key.StartsWith(shortName + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static ModuleSide ReadBackendRequired(ManifestXml xml, XElement root, ModuleSide moduleSide)
    {
        XElement? element = xml.Single(root, "RequiresBackend", required: false);
        if (element is null)
        {
            return ModuleSide.None;
        }

        xml.Allow(element, new[] { "side" }, Array.Empty<string>());
        ModuleSide side = xml.Choice(xml.NodeOf(element, "side"), "the 'side' attribute", xml.Attribute(element, "side", required: true), Sides) ?? ModuleSide.None;
        if (side != ModuleSide.None && (side & moduleSide) != side)
        {
            xml.Error(ManifestCodes.Inconsistent, $"<RequiresBackend> names a side where the module does not run", element);
        }

        return side;
    }

    private static string? ReadBackendOf(ManifestXml xml, XElement root, string? ownId, IReadOnlyList<ModuleDependency> dependencies)
    {
        XElement? element = xml.Single(root, "BackendOf", required: false);
        if (element is null)
        {
            return null;
        }

        xml.Allow(element, new[] { "module" }, Array.Empty<string>());
        string? module = xml.Attribute(element, "module", required: true);
        if (module is null)
        {
            return null;
        }

        if (ownId is not null && string.Equals(module, ownId, StringComparison.OrdinalIgnoreCase))
        {
            xml.Error(ManifestCodes.Inconsistent, "a module cannot be a backend of itself", element);
            return null;
        }

        ModuleDependency? dependency = null;
        foreach (ModuleDependency candidate in dependencies)
        {
            if (string.Equals(candidate.Id, module, StringComparison.OrdinalIgnoreCase))
            {
                dependency = candidate;
            }
        }

        if (dependency is null || dependency.Optional)
        {
            xml.Error(ManifestCodes.Inconsistent, $"a backend of '{module}' must declare a required dependency on it in <Dependencies>", element);
            return null;
        }

        return module;
    }
}
