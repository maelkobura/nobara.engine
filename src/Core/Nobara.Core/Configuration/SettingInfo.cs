// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Configuration;

/// <summary>Who changes the value of a configuration key.</summary>
public enum SettingScope
{
    /// <summary>The player, in the limits the distribution sets; the value lives in the settings file of the player.</summary>
    User,

    /// <summary>The distribution alone, when the game is built; the value lives in the distribution manifest.</summary>
    Distribution,
}

/// <summary>The type of the value of a configuration key.</summary>
public enum SettingType
{
    /// <summary><c>true</c> or <c>false</c>.</summary>
    Bool,

    /// <summary>A 32-bit whole number.</summary>
    Int,

    /// <summary>A double-precision number.</summary>
    Float,

    /// <summary>Text.</summary>
    String,

    /// <summary>One of the values the key declares.</summary>
    Enum,
}

/// <summary>
/// A configuration key, as a module declares it in its manifest and as the
/// distribution adjusts it.
/// </summary>
/// <remarks>Instances are immutable and shared by every context of the process.</remarks>
public sealed class SettingInfo
{
    internal SettingInfo(
        string key,
        string moduleId,
        SettingScope scope,
        SettingType type,
        string defaultValue,
        double? min,
        double? max,
        IReadOnlyList<string> values,
        Side side,
        bool isLocked)
    {
        Key = key;
        ModuleId = moduleId;
        Scope = scope;
        Type = type;
        Default = defaultValue;
        Min = min;
        Max = max;
        Values = values;
        Side = side;
        IsLocked = isLocked;
    }

    /// <summary>Gets the key, prefixed by the short name or the identifier of its module: <c>voxel.viewRadius</c>.</summary>
    public string Key { get; }

    /// <summary>Gets the identifier of the module that declares the key.</summary>
    public string ModuleId { get; }

    /// <summary>Gets who changes the value.</summary>
    public SettingScope Scope { get; }

    /// <summary>Gets the type of the value.</summary>
    public SettingType Type { get; }

    /// <summary>
    /// Gets the default value as text: the one the module declares, or the one
    /// the distribution gives with <c>&lt;Default&gt;</c>, <c>&lt;Lock&gt;</c>,
    /// or <c>&lt;Set&gt;</c>.
    /// </summary>
    public string Default { get; }

    /// <summary>Gets the lowest value of a number, included, or <see langword="null"/>.</summary>
    public double? Min { get; }

    /// <summary>Gets the highest value of a number, included, or <see langword="null"/>.</summary>
    public double? Max { get; }

    /// <summary>Gets the accepted values of an <see cref="SettingType.Enum"/> key; empty for other types.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>Gets the sides where the key exists.</summary>
    public Side Side { get; }

    /// <summary>Gets whether the distribution fixes the value of a user key, so the player cannot change it.</summary>
    public bool IsLocked { get; }

    /// <summary>Returns the key.</summary>
    /// <returns>The key.</returns>
    public override string ToString() => Key;
}

/// <summary>Why a configuration key cannot be used as asked.</summary>
public enum SettingFailure
{
    /// <summary>No module of the context declares the key.</summary>
    UnknownKey,

    /// <summary>The key belongs to a module that is not the caller nor one of its declared dependencies.</summary>
    UndeclaredDependency,

    /// <summary>The key does not exist on the side of the context.</summary>
    WrongSide,

    /// <summary>The key has another type than the one asked for.</summary>
    WrongType,

    /// <summary>The key cannot be changed: it is a distribution key, or the distribution locks it.</summary>
    ReadOnly,

    /// <summary>The value is out of bounds, or is not one of the values of an enum key.</summary>
    InvalidValue,
}

/// <summary>Thrown when a module uses a configuration key it cannot use that way.</summary>
public sealed class SettingException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="key">The key.</param>
    /// <param name="failure">Why the key cannot be used.</param>
    /// <param name="message">A message that names the key and the modules involved.</param>
    public SettingException(string key, SettingFailure failure, string message)
        : base(message)
    {
        Key = key;
        Failure = failure;
    }

    /// <summary>Gets the key.</summary>
    public string Key { get; }

    /// <summary>Gets why the key cannot be used.</summary>
    public SettingFailure Failure { get; }
}
