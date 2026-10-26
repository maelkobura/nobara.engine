// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>Reads and validates <c>build.nobara</c>.</summary>
public static class BuildManifestReader
{
    private static readonly Regex TargetName = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

    private static readonly Regex RuntimeIdentifier = new(@"^[a-z0-9]+(\.[0-9]+)*(-[a-z0-9]+)+$", RegexOptions.CultureInvariant);

    private static readonly string[] Variants = ["BattlEye", "Play"];

    /// <summary>Reads a build file.</summary>
    /// <param name="text">The content of the file.</param>
    /// <param name="file">The path of the file, used in diagnostics.</param>
    /// <param name="diagnostics">Receives the problems found.</param>
    /// <returns>The manifest, or <see langword="null"/> when an error was found.</returns>
    public static BuildManifest? Read(string text, string file, DiagnosticBag diagnostics)
    {
        var xml = new ManifestXml(file, diagnostics);
        int errors = diagnostics.ErrorCount;
        XElement? root = xml.Root(text, "NobaraBuild");
        if (root is null)
        {
            return null;
        }

        xml.Allow(root, ["format"], ["Target"]);
        var targets = new List<BuildTarget>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in root.Elements("Target"))
        {
            xml.Allow(element, ["name"], ["Platform", "Side", "Disable", "Variants"]);
            string? name = xml.Attribute(element, "name", required: true);
            string? platform = xml.ChildText(element, "Platform", required: true);
            string? sideText = xml.ChildText(element, "Side", required: true);
            ModuleSide? side = sideText is null ? null : xml.Choice(element.Element("Side")!, "<Side>", sideText, ModuleManifestReader.SingleSides);

            if (name is not null && !TargetName.IsMatch(name))
            {
                xml.Error(ManifestCodes.InvalidIdentifier, $"'{name}' is not a target name: letters, digits, dots, dashes, and underscores", element.Attribute("name")!);
            }
            else if (name is not null && !names.Add(name))
            {
                xml.Error(ManifestCodes.Duplicate, $"target '{name}' is declared twice", element);
            }

            if (platform is not null && !RuntimeIdentifier.IsMatch(platform))
            {
                xml.Error(ManifestCodes.InvalidValue, $"'{platform}' is not a .NET runtime identifier such as 'win-x64'", element.Element("Platform")!);
            }

            var disabled = new List<Declared>();
            var disabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement disable in element.Elements("Disable"))
            {
                xml.Allow(disable, ["module"], []);
                string? module = xml.Attribute(disable, "module", required: true);
                if (module is null)
                {
                    continue;
                }

                if (!ManifestXml.ModuleId.IsMatch(module))
                {
                    xml.Error(ManifestCodes.InvalidIdentifier, $"'{module}' is not a module identifier", disable.Attribute("module")!);
                }
                else if (!disabledIds.Add(module))
                {
                    xml.Error(ManifestCodes.Duplicate, $"module '{module}' is disabled twice", disable);
                }
                else
                {
                    disabled.Add(new Declared { Name = module, Location = xml.At(disable) });
                }
            }

            XElement? variants = xml.Single(element, "Variants", required: false);
            if (variants is not null)
            {
                xml.Allow(variants, [], Variants);
                foreach (XElement variant in variants.Elements())
                {
                    xml.Allow(variant, [], []);
                    xml.Diagnostics.Warning(ManifestCodes.Reserved,$"the {variant.Name.LocalName} variant is reserved and not built yet; only the main executable is produced", xml.At(variant));
                }
            }

            if (name is not null && platform is not null && side is not null)
            {
                targets.Add(new BuildTarget { Name = name, Platform = platform, Side = side.Value, Disabled = disabled, Location = xml.At(element) });
            }
        }

        if (diagnostics.ErrorCount != errors)
        {
            return null;
        }

        return new BuildManifest { File = file, Targets = targets, Location = xml.At(root) };
    }
}
