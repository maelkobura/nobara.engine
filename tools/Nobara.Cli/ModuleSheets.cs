// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Cli;

/// <summary>
/// <c>nobara docs</c>: rewrites the sections of the module sheets
/// (<c>src/&lt;folder&gt;/README.md</c>) that sit between
/// <c>nobara:generated</c> markers, from the <c>module.nobara</c> of the
/// module of the folder.
/// </summary>
/// <remarks>
/// A marker names its section, and optionally a module of the folder:
/// <c>&lt;!-- nobara:generated:begin dependencies example.voxel.renderer --&gt;</c>.
/// Without a module, the section describes the main module of the folder:
/// the one whose identifier starts every other identifier of the folder.
/// </remarks>
internal static partial class ModuleSheets
{
    public const string Level = "level";
    public const string Dependencies = "dependencies";
    public const string States = "states";
    public const string Configuration = "configuration";

    private const string End = "<!-- nobara:generated:end -->";

    /// <summary>Updates, or in check mode compares, every module sheet under <c>src/</c>.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool Update(string root, bool check, Reporter reporter)
    {
        string src = Path.Combine(root, "src");
        int sheets = 0;
        if (Directory.Exists(src))
        {
            foreach (string folder in Directory.EnumerateDirectories(src).Order(StringComparer.Ordinal))
            {
                string readme = Path.Combine(folder, "README.md");
                if (File.Exists(readme) && UpdateSheet(folder, readme, check, reporter))
                {
                    sheets++;
                }
            }
        }

        if (reporter.Errors > 0)
        {
            return false;
        }

        reporter.Info(check ? $"{sheets} module sheets are up to date." : $"Updated the generated sections of {sheets} module sheets.");
        return true;
    }

    // Returns whether the sheet has generated sections.
    private static bool UpdateSheet(string folder, string readme, bool check, Reporter reporter)
    {
        string text = File.ReadAllText(readme).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!Begin().IsMatch(text))
        {
            return false;
        }

        var diagnostics = new DiagnosticBag();
        List<ModuleManifest> modules = FolderModules(folder, diagnostics);
        if (!reporter.Report(diagnostics.Items))
        {
            return true;
        }

        bool core = modules.Count == 0 && File.Exists(Path.Combine(folder, "Nobara.Core", "Nobara.Core.csproj"));
        ModuleManifest? main = MainModule(modules);
        var result = new StringBuilder();
        int position = 0;
        foreach (Match match in Begin().Matches(text))
        {
            int end = text.IndexOf(End, match.Index + match.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                reporter.Error($"{readme}: the section '{match.Groups["section"].Value}' has no end marker.");
                return true;
            }

            string section = match.Groups["section"].Value;
            string? id = match.Groups["module"].Success ? match.Groups["module"].Value : null;
            ModuleManifest? module = id is null ? main : modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            string? content = core && id is null ? CoreSection(section)
                : module is null ? null
                : Section(section, module);
            if (content is null)
            {
                string what = !IsSection(section) ? $"the section '{section}' is unknown; sections: {Level}, {Dependencies}, {States}, {Configuration}"
                    : id is not null ? $"no module '{id}' is in '{folder}'"
                    : $"no module.nobara is in '{folder}' or its subfolders";
                reporter.Error($"{readme}: {what}.");
                return true;
            }

            result.Append(text, position, match.Index + match.Length - position);
            result.Append('\n').Append(content).Append('\n');
            position = end;
        }

        result.Append(text, position, text.Length - position);
        Files.Update(readme, result.ToString(), check, reporter);
        return true;
    }

    private static bool IsSection(string section) => section is Level or Dependencies or States or Configuration;

    // The modules of a folder: its own module.nobara, or those of its subfolders.
    private static List<ModuleManifest> FolderModules(string folder, DiagnosticBag diagnostics)
    {
        IEnumerable<string> folders = File.Exists(Path.Combine(folder, "module.nobara"))
            ? [folder]
            : Directory.EnumerateDirectories(folder).Where(f => File.Exists(Path.Combine(f, "module.nobara"))).Order(StringComparer.Ordinal);
        return folders.Select(f => GameFiles.ReadModule(f, diagnostics)).OfType<ModuleManifest>().ToList();
    }

    private static ModuleManifest? MainModule(List<ModuleManifest> modules) =>
        modules.FirstOrDefault(m => modules.All(o => o == m || o.Id.StartsWith(m.Id + ".", StringComparison.OrdinalIgnoreCase)))
        ?? modules.OrderBy(m => m.Id.Length).FirstOrDefault();

    /// <summary>The content of a section for a module.</summary>
    internal static string? Section(string section, ModuleManifest module) => section switch
    {
        Level => LevelText(module),
        Dependencies => DependencyText(module),
        States => StateText(module),
        Configuration => ConfigurationText(module),
        _ => null,
    };

    /// <summary>The content of a section for the core, which is not a module.</summary>
    internal static string? CoreSection(string section) => section switch
    {
        Level => "Core, on the client and on the server. The core is not a module: it has no `module.nobara`.",
        Dependencies => "None. The core depends on no module.",
        States => "States provided: none.",
        Configuration => "None.",
        _ => null,
    };

    private static string LevelText(ModuleManifest module)
    {
        string level = module.Level == ModuleLevel.Platform ? "Platform module" : "Business module";
        var text = new StringBuilder($"{level}, {Where(module.Side)}.");
        if (module.BackendRequired != ModuleSide.None)
        {
            text.Append($" Requires an active backend {Where(module.BackendRequired)}.");
        }

        if (module.BackendOf is not null)
        {
            text.Append($" Backend of `{module.BackendOf}`.");
        }

        return text.ToString();
    }

    private static string DependencyText(ModuleManifest module)
    {
        if (module.Dependencies.Count == 0 && module.Libraries.Count == 0)
        {
            return "None.";
        }

        var text = new StringBuilder();
        if (module.Dependencies.Count > 0)
        {
            text.Append("| Module | Versions | Kind |\n| --- | --- | --- |\n");
            foreach (ModuleDependency dependency in module.Dependencies.OrderBy(d => d.Optional).ThenBy(d => d.Id, StringComparer.Ordinal))
            {
                text.Append($"| `{dependency.Id}` | `{dependency.Range.Text}` | {(dependency.Optional ? "Optional" : "Required")} |\n");
            }
        }
        else
        {
            text.Append("No module.\n");
        }

        if (module.Libraries.Count > 0)
        {
            text.Append('\n').Append("Third-party libraries: ")
                .Append(string.Join(", ", module.Libraries.OrderBy(l => l.Id, StringComparer.Ordinal).Select(l => $"`{l.Id}` {l.Version}")))
                .Append(".\n");
        }

        return text.ToString().TrimEnd('\n');
    }

    private static string StateText(ModuleManifest module) =>
        module.States.Count == 0
            ? "States provided: none."
            : "States provided: " + string.Join(", ", module.States.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => $"`{s.Id}`" + (s.Side == ModuleSide.None ? string.Empty : $" ({Side(s.Side)})"))) + ".";

    private static string ConfigurationText(ModuleManifest module)
    {
        if (module.Settings.Count == 0)
        {
            return "None.";
        }

        var text = new StringBuilder("| Key | Scope | Type | Default | Values | Side |\n| --- | --- | --- | --- | --- | --- |\n");
        foreach (SettingDeclaration setting in module.Settings.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            string values = setting.Type == SettingType.Enum ? string.Join(", ", setting.Values.Select(v => $"`{v}`"))
                : setting.Min is not null || setting.Max is not null ? $"{Number(setting.Min) ?? "…"} to {Number(setting.Max) ?? "…"}"
                : string.Empty;
            string side = setting.Side == ModuleSide.None ? "both" : Side(setting.Side);
            text.Append($"| `{setting.Key}` | {setting.Scope.ToString().ToLowerInvariant()} | {setting.Type.ToString().ToLowerInvariant()} | `{setting.Default}` | {values} | {side} |\n");
        }

        return text.ToString().TrimEnd('\n');
    }

    private static string? Number(double? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Side(ModuleSide side) => side switch
    {
        ModuleSide.Client => "client",
        ModuleSide.Server => "server",
        _ => "both",
    };

    private static string Where(ModuleSide side) => side switch
    {
        ModuleSide.Client => "on the client",
        ModuleSide.Server => "on the server",
        _ => "on the client and on the server",
    };

    [GeneratedRegex(@"<!-- nobara:generated:begin (?<section>[a-z]+)(?: (?<module>[A-Za-z0-9._-]+))? -->", RegexOptions.CultureInvariant)]
    private static partial Regex Begin();
}
