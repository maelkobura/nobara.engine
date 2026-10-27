// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using F = Nobara.Formats;

namespace Nobara.Core.Configuration;

/// <summary>
/// The configuration keys of the process: the declarations of the loaded
/// modules, the values of the distribution, and the values of the player,
/// read from and written to the settings file.
/// </summary>
/// <remarks>
/// One store serves every context of the process, so an integrated server
/// reads the settings of the player of its client. Every member is
/// thread-safe.
/// </remarks>
internal sealed class SettingStore
{
    /// <summary>The name of the settings file of the player, in the configuration folder.</summary>
    public const string FileName = "settings.xml";

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private SettingStore(string file)
    {
        FilePath = file;
    }

    /// <summary>Gets the path of the settings file of the player.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Builds the store from the keys that the modules declare and the values
    /// of the distribution, then reads the settings file of the player.
    /// </summary>
    /// <exception cref="ModuleCompositionException">A declaration or a value of the distribution is invalid.</exception>
    public static SettingStore Create(IEnumerable<ModuleInfo> modules, F.DistributionManifest distribution, EnginePaths paths, Logger logger)
    {
        var store = new SettingStore(Path.Combine(paths.Config, FileName));
        var problems = new List<CompositionProblem>();
        var declarations = new Dictionary<string, (F.SettingDeclaration Declaration, ModuleInfo Module)>(StringComparer.Ordinal);
        foreach (ModuleInfo module in modules)
        {
            foreach (F.SettingDeclaration declaration in module.SettingDeclarations)
            {
                if (declarations.TryGetValue(declaration.Key, out var existing))
                {
                    problems.Add(Problem(module.Id, $"Module '{module.Id}' declares the configuration key '{declaration.Key}', which module '{existing.Module.Id}' declares too."));
                    continue;
                }

                declarations[declaration.Key] = (declaration, module);
            }
        }

        var adjustments = new Dictionary<string, F.SettingValue>(StringComparer.Ordinal);
        foreach (F.SettingValue value in distribution.Settings)
        {
            if (!declarations.TryGetValue(value.Key, out var target))
            {
                // The build checked the key: it belongs to a module that this
                // process does not load, such as a client module on a
                // dedicated server.
                logger.Debug($"The distribution sets '{value.Key}', which no loaded module declares.");
                continue;
            }

            bool user = target.Declaration.Scope == F.SettingDeclaration.Types.Scope.User;
            if (value.Operation == F.SettingValue.Types.Operation.Set ? user : !user)
            {
                string operation = value.Operation == F.SettingValue.Types.Operation.Set ? "<Set>" : value.Operation == F.SettingValue.Types.Operation.Lock ? "<Lock>" : "<Default>";
                problems.Add(Problem(target.Module.Id, $"The distribution uses {operation} on '{value.Key}', a {(user ? "user" : "distribution")} key of module '{target.Module.Id}'; {(user ? "<Default> and <Lock> change user keys" : "<Set> gives the value of distribution keys")}."));
                continue;
            }

            if (!adjustments.TryAdd(value.Key, value))
            {
                problems.Add(Problem(target.Module.Id, $"The distribution gives two values to '{value.Key}'."));
            }
        }

        foreach ((string key, (F.SettingDeclaration declaration, ModuleInfo module)) in declarations)
        {
            Entry? entry = Entry.Create(declaration, module, adjustments.GetValueOrDefault(key), problems);
            if (entry is not null)
            {
                store._entries[key] = entry;
            }
        }

        if (problems.Count > 0)
        {
            throw new ModuleCompositionException(problems);
        }

        store.ReadPlayerFile(logger);
        return store;
    }

    /// <summary>Gets a key, or <see langword="null"/> when no loaded module declares it.</summary>
    public SettingInfo? Find(string key) => _entries.TryGetValue(key, out Entry? entry) ? entry.Info : null;

    /// <summary>Gets every key, sorted.</summary>
    public IEnumerable<SettingInfo> All => _entries.Values.Select(e => e.Info).OrderBy(i => i.Key, StringComparer.Ordinal);

    /// <summary>Gets the current value of a key: a boxed <see cref="bool"/>, <see cref="int"/>, <see cref="double"/>, or a <see cref="string"/>.</summary>
    public object Get(string key)
    {
        lock (_lock)
        {
            return _entries[key].Value;
        }
    }

    /// <summary>Gives a user key a value chosen by the player.</summary>
    /// <returns>An error message, or <see langword="null"/> when the value is set.</returns>
    public string? Set(string key, string text)
    {
        Entry entry = _entries[key];
        if (Parse(entry.Info, text, out object? value) is string error)
        {
            return error;
        }

        lock (_lock)
        {
            entry.Value = value!;
            entry.ChosenByPlayer = true;
        }

        return null;
    }

    /// <summary>Gives a user key its default value back; the settings file no longer lists it.</summary>
    public void Reset(string key)
    {
        Entry entry = _entries[key];
        lock (_lock)
        {
            entry.Value = entry.Default;
            entry.ChosenByPlayer = false;
        }
    }

    /// <summary>
    /// Writes the values chosen by the player to the settings file, and only
    /// those: a key that the player did not set is not written, so a new default of the
    /// distribution applies to them.
    /// </summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public void Save()
    {
        var root = new XElement("Settings");
        lock (_lock)
        {
            foreach (Entry entry in _entries.Values.Where(e => e.ChosenByPlayer).OrderBy(e => e.Info.Key, StringComparer.Ordinal))
            {
                root.Add(new XElement("Setting", new XAttribute("key", entry.Info.Key), new XAttribute("value", Format(entry.Value))));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        var document = new XDocument(
            new XComment(" Settings of the player. A key that is not listed has its default value. "),
            root);
        using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) }))
        {
            document.Save(writer);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>Parses a value written as text for a key, and checks its bounds.</summary>
    /// <returns>An error message, or <see langword="null"/> when <paramref name="value"/> is set.</returns>
    public static string? Parse(SettingInfo info, string text, out object? value)
    {
        value = null;
        string trimmed = text.Trim();
        switch (info.Type)
        {
            case SettingType.Bool:
                if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
                {
                    value = string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase);
                    return null;
                }

                return $"'{text}' is not 'true' or 'false'";
            case SettingType.Int:
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole))
                {
                    return $"'{text}' is not a whole number";
                }

                value = whole;
                return Bounds(info, whole, text);
            case SettingType.Float:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                {
                    return $"'{text}' is not a number";
                }

                value = number;
                return Bounds(info, number, text);
            case SettingType.Enum:
                string? match = info.Values.FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    return $"'{text}' is not one of {string.Join(", ", info.Values.Select(v => "'" + v + "'"))}";
                }

                value = match;
                return null;
            default:
                value = text;
                return null;
        }
    }

    /// <summary>Writes a value as the settings file and the manifests do.</summary>
    public static string Format(object value) => value switch
    {
        bool boolean => boolean ? "true" : "false",
        int whole => whole.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => (string)value,
    };

    private static string? Bounds(SettingInfo info, double value, string text)
    {
        if (info.Min is double min && value < min)
        {
            return $"{text} is below the minimum, {Format(min)}";
        }

        if (info.Max is double max && value > max)
        {
            return $"{text} is above the maximum, {Format(max)}";
        }

        return null;
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    // Invalid values, unknown keys, and values of locked or distribution keys
    // are ignored with a warning: the key keeps its default.
    private void ReadPlayerFile(Logger logger)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(FilePath, LoadOptions.SetLineInfo);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            logger.Warning($"The settings file '{FilePath}' cannot be read, so every key has its default value: {exception.Message}");
            return;
        }

        if (document.Root is not { Name.LocalName: "Settings" } root)
        {
            logger.Warning($"The settings file '{FilePath}' has no <Settings> root, so every key has its default value.");
            return;
        }

        foreach (XElement element in root.Elements())
        {
            string at = $"{FilePath}({((IXmlLineInfo)element).LineNumber},{((IXmlLineInfo)element).LinePosition})";
            string? key = element.Attribute("key")?.Value;
            string? text = element.Attribute("value")?.Value;
            if (element.Name.LocalName != "Setting" || key is null || text is null)
            {
                logger.Warning($"{at}: expected <Setting key=\"...\" value=\"...\" />; the element is ignored.");
                continue;
            }

            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                logger.Warning($"{at}: no module declares the key '{key}'; the value is ignored.");
                continue;
            }

            if (entry.Info.Scope == SettingScope.Distribution)
            {
                logger.Warning($"{at}: '{key}' is set by the distribution, not by the player; the value is ignored.");
                continue;
            }

            if (entry.Info.IsLocked)
            {
                logger.Info($"{at}: the distribution locks '{key}' to {entry.Info.Default}; the value of the file is ignored.");
                continue;
            }

            if (Parse(entry.Info, text, out object? value) is string error)
            {
                logger.Warning($"{at}: {error} for '{key}'; the key keeps its default value, {entry.Info.Default}.");
                continue;
            }

            entry.Value = value!;
            entry.ChosenByPlayer = true;
        }
    }

    private static CompositionProblem Problem(string moduleId, string message) =>
        new(CompositionProblemKind.InvalidConfiguration, moduleId, message);

    private sealed class Entry
    {
        private Entry(SettingInfo info, object value)
        {
            Info = info;
            Default = value;
            Value = value;
        }

        public SettingInfo Info { get; }

        public object Default { get; }

        public object Value { get; set; }

        public bool ChosenByPlayer { get; set; }

        public static Entry? Create(F.SettingDeclaration declaration, ModuleInfo module, F.SettingValue? adjustment, List<CompositionProblem> problems)
        {
            SettingType type = declaration.Type switch
            {
                F.SettingDeclaration.Types.Type.Bool => SettingType.Bool,
                F.SettingDeclaration.Types.Type.Int => SettingType.Int,
                F.SettingDeclaration.Types.Type.Float => SettingType.Float,
                F.SettingDeclaration.Types.Type.Enum => SettingType.Enum,
                _ => SettingType.String,
            };
            SettingScope scope = declaration.Scope == F.SettingDeclaration.Types.Scope.Distribution ? SettingScope.Distribution : SettingScope.User;
            Side side = declaration.Side switch
            {
                F.Side.Client => Side.Client,
                F.Side.Server => Side.Server,
                _ => module.Side,
            };
            string text = adjustment?.Value ?? declaration.DefaultValue;
            var info = new SettingInfo(
                declaration.Key,
                module.Id,
                scope,
                type,
                text,
                declaration.HasMin ? declaration.Min : null,
                declaration.HasMax ? declaration.Max : null,
                declaration.Values.ToArray(),
                side & module.Side,
                adjustment?.Operation == F.SettingValue.Types.Operation.Lock);

            if (type == SettingType.Enum && info.Values.Count == 0)
            {
                problems.Add(Problem(module.Id, $"Module '{module.Id}' declares the enum key '{info.Key}' without values."));
                return null;
            }

            if (Parse(info, text, out object? value) is string error)
            {
                string origin = adjustment is null ? $"the default of '{info.Key}' declared by module '{module.Id}'" : $"the value the distribution gives to '{info.Key}' of module '{module.Id}'";
                problems.Add(Problem(module.Id, $"{char.ToUpperInvariant(origin[0])}{origin[1..]} is invalid: {error}."));
                return null;
            }

            return new Entry(info, value!);
        }
    }
}
