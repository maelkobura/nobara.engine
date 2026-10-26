// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>Reads and validates <c>engine.nobara</c>.</summary>
public static class EngineManifestReader
{
    private static readonly Regex PhaseName = new(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, ErrorPolicyKind> Policies = new()
    {
        ["stop"] = ErrorPolicyKind.Stop,
        ["disable"] = ErrorPolicyKind.Disable,
        ["log"] = ErrorPolicyKind.Log,
    };

    /// <summary>Reads an engine file.</summary>
    /// <param name="text">The content of the file.</param>
    /// <param name="file">The path of the file, used in diagnostics.</param>
    /// <param name="diagnostics">Receives the problems found.</param>
    /// <returns>The manifest, or <see langword="null"/> when an error was found.</returns>
    public static EngineManifest? Read(string text, string file, DiagnosticBag diagnostics)
    {
        var xml = new ManifestXml(file, diagnostics);
        int errors = diagnostics.ErrorCount;
        XElement? root = xml.Root(text, "NobaraEngine");
        if (root is null)
        {
            return null;
        }

        xml.Allow(root, ["format"], ["Loop", "States", "ErrorPolicy", "Settings", "ModuleConfig"]);

        (int? tickRate, List<LoopPhase> phases) = ReadLoop(xml, root);
        (Declared? client, Declared? server) = ReadStates(xml, root);
        (ErrorPolicyKind defaultPolicy, List<ModulePolicy> policies) = ReadPolicies(xml, root);
        List<SettingValue> settings = ReadSettings(xml, root);
        List<ModuleConfigBlock> configs = ReadModuleConfigs(xml, root);

        if (diagnostics.ErrorCount != errors)
        {
            return null;
        }

        return new EngineManifest
        {
            File = file,
            TickRate = tickRate,
            Phases = phases,
            ClientState = client,
            ServerState = server,
            DefaultPolicy = defaultPolicy,
            Policies = policies,
            Settings = settings,
            ModuleConfigs = configs,
            Location = xml.At(root),
        };
    }

    private static (int? TickRate, List<LoopPhase> Phases) ReadLoop(ManifestXml xml, XElement root)
    {
        var phases = new List<LoopPhase>();
        XElement? loop = xml.Single(root, "Loop", required: false);
        if (loop is null)
        {
            return (null, phases);
        }

        xml.Allow(loop, ["tickRate"], ["Phase"]);
        int? tickRate = null;
        string? rate = xml.Attribute(loop, "tickRate", required: true);
        if (rate is not null)
        {
            if (int.TryParse(rate, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0)
            {
                tickRate = value;
            }
            else
            {
                xml.Error(ManifestCodes.InvalidValue, $"the tick rate is '{rate}', expected a positive whole number of ticks per second", loop.Attribute("tickRate")!);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement element in loop.Elements("Phase"))
        {
            xml.Allow(element, ["name", "fixed", "side"], []);
            string? name = xml.Attribute(element, "name", required: true);
            bool isFixed = xml.Boolean(element, "fixed", false);
            ModuleSide side = xml.Choice(xml.NodeOf(element, "side"), "the 'side' attribute", xml.Attribute(element, "side", required: false), ModuleManifestReader.SingleSides) ?? ModuleSide.None;
            if (name is null)
            {
                continue;
            }

            if (!PhaseName.IsMatch(name))
            {
                xml.Error(ManifestCodes.InvalidIdentifier, $"'{name}' is not a phase name: letters, digits, and underscores, starting with a letter", element.Attribute("name")!);
                continue;
            }

            if (!names.Add(name))
            {
                xml.Error(ManifestCodes.Duplicate, $"phase '{name}' is declared twice", element);
                continue;
            }

            phases.Add(new LoopPhase { Name = name, Fixed = isFixed, Side = side, Location = xml.At(element) });
        }

        if (phases.Count == 0)
        {
            xml.Error(ManifestCodes.Missing, "<Loop> needs at least one <Phase>", loop);
        }

        return (tickRate, phases);
    }

    private static (Declared? Client, Declared? Server) ReadStates(ManifestXml xml, XElement root)
    {
        XElement? states = xml.Single(root, "States", required: false);
        if (states is null)
        {
            return (null, null);
        }

        xml.Allow(states, ["client", "server"], []);
        Declared? Read(string side)
        {
            string? id = xml.Attribute(states, side, required: false);
            if (id is null)
            {
                return null;
            }

            if (!ManifestXml.StateId.IsMatch(id))
            {
                xml.Error(ManifestCodes.InvalidIdentifier, $"'{id}' is not a state identifier, as in 'state:example.menu'", states.Attribute(side)!);
                return null;
            }

            return new Declared { Name = id, Location = xml.At(states.Attribute(side)!) };
        }

        Declared? client = Read("client");
        Declared? server = Read("server");
        if (states.Attribute("client") is null && states.Attribute("server") is null)
        {
            xml.Error(ManifestCodes.Missing, "<States> needs a 'client' or a 'server' attribute", states);
        }

        return (client, server);
    }

    private static (ErrorPolicyKind Default, List<ModulePolicy> Modules) ReadPolicies(ManifestXml xml, XElement root)
    {
        var modules = new List<ModulePolicy>();
        XElement? policy = xml.Single(root, "ErrorPolicy", required: false);
        if (policy is null)
        {
            return (ErrorPolicyKind.Stop, modules);
        }

        xml.Allow(policy, ["default"], ["Module"]);
        ErrorPolicyKind defaultPolicy = xml.Choice(xml.NodeOf(policy, "default"), "the 'default' attribute", xml.Attribute(policy, "default", required: false), Policies) ?? ErrorPolicyKind.Stop;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in policy.Elements("Module"))
        {
            xml.Allow(element, ["id", "policy"], []);
            string? id = xml.Attribute(element, "id", required: true);
            ErrorPolicyKind? kind = xml.Choice(xml.NodeOf(element, "policy"), "the 'policy' attribute", xml.Attribute(element, "policy", required: true), Policies);
            if (id is null || kind is null)
            {
                continue;
            }

            if (!seen.Add(id))
            {
                xml.Error(ManifestCodes.Duplicate, $"module '{id}' has two error policies", element);
                continue;
            }

            modules.Add(new ModulePolicy { Module = id, Policy = kind.Value, Location = xml.At(element) });
        }

        return (defaultPolicy, modules);
    }

    private static List<SettingValue> ReadSettings(ManifestXml xml, XElement root)
    {
        var result = new List<SettingValue>();
        XElement? settings = xml.Single(root, "Settings", required: false);
        if (settings is null)
        {
            return result;
        }

        xml.Allow(settings, [], ["Default", "Lock", "Set"]);
        var seen = new Dictionary<string, SettingOperation>(StringComparer.Ordinal);
        foreach (XElement element in settings.Elements())
        {
            SettingOperation? operation = element.Name.LocalName switch
            {
                "Default" => SettingOperation.Default,
                "Lock" => SettingOperation.Lock,
                "Set" => SettingOperation.Set,
                _ => null,
            };
            if (operation is null)
            {
                continue;
            }

            xml.Allow(element, ["key", "value"], []);
            string? key = xml.Attribute(element, "key", required: true);
            XAttribute? value = element.Attribute("value");
            if (value is null)
            {
                xml.Error(ManifestCodes.Missing, $"<{element.Name.LocalName}> needs a 'value' attribute", element);
            }

            if (key is null || value is null)
            {
                continue;
            }

            if (seen.ContainsKey(key))
            {
                xml.Error(ManifestCodes.Duplicate, $"key '{key}' is set twice", element);
                continue;
            }

            seen[key] = operation.Value;
            result.Add(new SettingValue { Operation = operation.Value, Key = key, Value = value.Value.Trim(), Location = xml.At(element) });
        }

        return result;
    }

    private static List<ModuleConfigBlock> ReadModuleConfigs(ManifestXml xml, XElement root)
    {
        var result = new List<ModuleConfigBlock>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in root.Elements("ModuleConfig"))
        {
            string? module = xml.Attribute(element, "module", required: true);
            foreach (XAttribute attribute in element.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration && attribute.Name.LocalName != "module")
                {
                    xml.Error(ManifestCodes.Unknown, $"unknown attribute '{attribute.Name.LocalName}' in <ModuleConfig>; expected 'module'", attribute);
                }
            }

            if (module is null)
            {
                continue;
            }

            if (!seen.Add(module))
            {
                xml.Error(ManifestCodes.Duplicate, $"module '{module}' has two <ModuleConfig> blocks", element);
                continue;
            }

            result.Add(new ModuleConfigBlock { Module = module, Element = element, Location = xml.At(element) });
        }

        return result;
    }
}
