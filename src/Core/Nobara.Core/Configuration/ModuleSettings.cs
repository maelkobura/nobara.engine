// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;

namespace Nobara.Core.Configuration;

/// <summary>
/// The configuration keys as one module sees them in one context: the keys it
/// declares and those of its declared dependencies, on the side of the
/// context.
/// </summary>
/// <remarks>
/// <para>
/// A value comes from the settings file of the player for a user key that the
/// player changed, from the distribution (<c>&lt;Default&gt;</c>,
/// <c>&lt;Lock&gt;</c>, <c>&lt;Set&gt;</c> in <c>engine.nobara</c>), or from
/// the declaration of the key. The values are read once per process, before
/// any context starts; every context of the process sees the same values.
/// </para>
/// <para>Every member is thread-safe.</para>
/// </remarks>
/// <example>
/// <code>
/// public void Initialize(ModuleContext context)
/// {
///     _viewRadius = context.Settings.GetInt("voxel.viewRadius");
/// }
/// </code>
/// </example>
public sealed class ModuleSettings
{
    private readonly SettingStore _store;
    private readonly ModuleRuntime _module;
    private readonly Side _side;

    internal ModuleSettings(SettingStore store, ModuleRuntime module, Side side)
    {
        _store = store;
        _module = module;
        _side = side;
    }

    /// <summary>Gets the keys the module can read on this side, sorted.</summary>
    public IReadOnlyList<SettingInfo> Keys => _store.All.Where(Accessible).ToArray();

    /// <summary>Gets the path of the settings file of the player, in the configuration folder.</summary>
    public string FilePath => _store.FilePath;

    /// <summary>Gets the declaration of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The declaration.</returns>
    /// <exception cref="SettingException">The key is unknown, belongs to a module that is not a declared dependency, or does not exist on this side.</exception>
    public SettingInfo GetInfo(string key) => Find(key);

    /// <summary>Reads a <see cref="SettingType.Bool"/> key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SettingException">The key cannot be read, or is not a boolean.</exception>
    public bool GetBool(string key) => (bool)Read(key, SettingType.Bool);

    /// <summary>Reads an <see cref="SettingType.Int"/> key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SettingException">The key cannot be read, or is not a whole number.</exception>
    public int GetInt(string key) => (int)Read(key, SettingType.Int);

    /// <summary>Reads a <see cref="SettingType.Float"/> key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SettingException">The key cannot be read, or is not a number.</exception>
    public double GetFloat(string key) => (double)Read(key, SettingType.Float);

    /// <summary>Reads a <see cref="SettingType.String"/> key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SettingException">The key cannot be read, or is not text.</exception>
    public string GetString(string key) => (string)Read(key, SettingType.String);

    /// <summary>Reads an <see cref="SettingType.Enum"/> key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, spelled as the key declares it.</returns>
    /// <exception cref="SettingException">The key cannot be read, or is not an enum.</exception>
    public string GetEnum(string key) => (string)Read(key, SettingType.Enum);

    /// <summary>Gives a <see cref="SettingType.Bool"/> user key a value chosen by the player.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="SettingException">The key cannot be changed, or is not a boolean.</exception>
    public void Set(string key, bool value) => Write(key, SettingType.Bool, value ? "true" : "false");

    /// <summary>Gives an <see cref="SettingType.Int"/> user key a value chosen by the player.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="SettingException">The key cannot be changed, is not a whole number, or the value is out of bounds.</exception>
    public void Set(string key, int value) => Write(key, SettingType.Int, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Gives a <see cref="SettingType.Float"/> user key a value chosen by the player.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="SettingException">The key cannot be changed, is not a number, or the value is out of bounds.</exception>
    public void Set(string key, double value) => Write(key, SettingType.Float, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Gives a <see cref="SettingType.String"/> or <see cref="SettingType.Enum"/> user key a value chosen by the player.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value; for an enum, one of its values.</param>
    /// <exception cref="SettingException">The key cannot be changed, is not text or an enum, or the value is not one of the values of the enum.</exception>
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SettingInfo info = Find(key);
        Write(key, info.Type == SettingType.Enum ? SettingType.Enum : SettingType.String, value);
    }

    /// <summary>Gives a user key its default value back.</summary>
    /// <param name="key">The key.</param>
    /// <exception cref="SettingException">The key cannot be changed.</exception>
    public void Reset(string key)
    {
        Writable(Find(key));
        _store.Reset(key);
    }

    /// <summary>
    /// Writes the values chosen by the player to the settings file, and only
    /// those: a key that the player did not set, or that was reset, is not
    /// written.
    /// </summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public void Save() => _store.Save();

    private object Read(string key, SettingType type)
    {
        SettingInfo info = Find(key);
        if (info.Type != type)
        {
            throw new SettingException(key, SettingFailure.WrongType, $"Module '{_module.Info.Id}' reads '{key}' as {Name(type)}, but it is {Name(info.Type)}.");
        }

        return _store.Get(key);
    }

    private void Write(string key, SettingType type, string text)
    {
        SettingInfo info = Find(key);
        if (info.Type != type)
        {
            throw new SettingException(key, SettingFailure.WrongType, $"Module '{_module.Info.Id}' writes '{key}' as {Name(type)}, but it is {Name(info.Type)}.");
        }

        Writable(info);
        if (_store.Set(key, text) is string error)
        {
            throw new SettingException(key, SettingFailure.InvalidValue, $"Module '{_module.Info.Id}' cannot set '{key}': {error}.");
        }
    }

    private void Writable(SettingInfo info)
    {
        if (info.Scope == SettingScope.Distribution)
        {
            throw new SettingException(info.Key, SettingFailure.ReadOnly, $"'{info.Key}' is a distribution key; only the distribution sets it.");
        }

        if (info.IsLocked)
        {
            throw new SettingException(info.Key, SettingFailure.ReadOnly, $"The distribution locks '{info.Key}' to {info.Default}.");
        }
    }

    private SettingInfo Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        SettingInfo info = _store.Find(key)
            ?? throw new SettingException(key, SettingFailure.UnknownKey, $"Module '{_module.Info.Id}' asked for the configuration key '{key}', which no loaded module declares.");

        if (!string.Equals(info.ModuleId, _module.Info.Id, StringComparison.Ordinal) && !_module.DependsOn(info.ModuleId, requiredOnly: false))
        {
            throw new SettingException(
                key,
                SettingFailure.UndeclaredDependency,
                $"Module '{_module.Info.Id}' asked for '{key}' of module '{info.ModuleId}', which is not one of its declared dependencies.");
        }

        if ((info.Side & _side) == Side.None)
        {
            throw new SettingException(key, SettingFailure.WrongSide, $"'{key}' does not exist on the {EngineStopException.Describe(_side)} side.");
        }

        return info;
    }

    private bool Accessible(SettingInfo info) =>
        (string.Equals(info.ModuleId, _module.Info.Id, StringComparison.Ordinal) || _module.DependsOn(info.ModuleId, requiredOnly: false))
        && (info.Side & _side) != Side.None;

    private static string Name(SettingType type) => type switch
    {
        SettingType.Bool => "a boolean",
        SettingType.Int => "a whole number",
        SettingType.Float => "a number",
        SettingType.Enum => "an enum",
        _ => "text",
    };
}
