// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// This file is also compiled into Nobara.Sdk (netstandard2.0), which reads
// module.nobara with the same code. Keep it free of newer APIs.

using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Nobara.ResourceCompiler.Manifests;

/// <summary>Where a module, a state, a setting, or a target runs.</summary>
[Flags]
public enum ModuleSide
{
    /// <summary>No side.</summary>
    None = 0,

    /// <summary>The client.</summary>
    Client = 1,

    /// <summary>The server.</summary>
    Server = 2,

    /// <summary>The client and the server.</summary>
    Both = Client | Server,
}

/// <summary>The level of a module.</summary>
public enum ModuleLevel
{
    /// <summary>A reusable foundation: stored in <c>system/</c>, never depends on a business module.</summary>
    Platform,

    /// <summary>A feature of a game: stored in <c>game/</c>.</summary>
    Business,
}

/// <summary>The scope of a configuration key.</summary>
public enum SettingScope
{
    /// <summary>The player changes the value, within the limits set by the distribution.</summary>
    User,

    /// <summary>Only the distribution sets the value, when it is built.</summary>
    Distribution,
}

/// <summary>The type of the value of a configuration key.</summary>
public enum SettingType
{
    /// <summary><c>true</c> or <c>false</c>.</summary>
    Bool,

    /// <summary>A 64-bit integer.</summary>
    Int,

    /// <summary>A floating-point number.</summary>
    Float,

    /// <summary>Any text.</summary>
    String,

    /// <summary>One of the words listed by the <c>values</c> attribute.</summary>
    Enum,
}

/// <summary>How the core reacts when a module throws.</summary>
public enum ErrorPolicyKind
{
    /// <summary>Stop the game.</summary>
    Stop,

    /// <summary>Disable the module and every module that requires it.</summary>
    Disable,

    /// <summary>Log the error and continue.</summary>
    Log,
}

/// <summary>What a setting element of <c>engine.nobara</c> does.</summary>
public enum SettingOperation
{
    /// <summary><c>&lt;Default&gt;</c>: changes the default of a user key.</summary>
    Default,

    /// <summary><c>&lt;Lock&gt;</c>: fixes a user key.</summary>
    Lock,

    /// <summary><c>&lt;Set&gt;</c>: gives its value to a distribution key.</summary>
    Set,
}

/// <summary>A dependency declared in <c>module.nobara</c>.</summary>
public sealed class ModuleDependency
{
    /// <summary>Gets the identifier of the module depended on.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the accepted versions.</summary>
    public VersionRange Range { get; init; } = null!;

    /// <summary>Gets whether the module works without the dependency.</summary>
    public bool Optional { get; init; }

    /// <summary>Gets the position of the declaration.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A third-party package declared in <c>module.nobara</c>.</summary>
public sealed class ModuleLibrary
{
    /// <summary>Gets the package identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the minimum version, in NuGet notation.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the position of the declaration.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A name with the position where it is declared.</summary>
public sealed class Declared
{
    /// <summary>Gets the name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the position of the declaration.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A context state that a module provides.</summary>
public sealed class StateDeclaration
{
    /// <summary>Gets the qualified identifier: <c>state:example.menu</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the sides of the state; <see cref="ModuleSide.None"/> when the attribute is absent, meaning every side of the module.</summary>
    public ModuleSide Side { get; init; }

    /// <summary>Gets the position of the declaration.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A configuration key declared by a module.</summary>
public sealed class SettingDeclaration
{
    /// <summary>Gets the key: <c>voxel.viewRadius</c>.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets who sets the value.</summary>
    public SettingScope Scope { get; init; }

    /// <summary>Gets the type of the value.</summary>
    public SettingType Type { get; init; }

    /// <summary>Gets the default value as written.</summary>
    public string Default { get; init; } = string.Empty;

    /// <summary>Gets the smallest accepted number, or <see langword="null"/>.</summary>
    public double? Min { get; init; }

    /// <summary>Gets the largest accepted number, or <see langword="null"/>.</summary>
    public double? Max { get; init; }

    /// <summary>Gets the accepted words of an enum key.</summary>
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    /// <summary>Gets the side the key applies to; <see cref="ModuleSide.None"/> for both.</summary>
    public ModuleSide Side { get; init; }

    /// <summary>Gets the position of the declaration.</summary>
    public Location Location { get; init; } = null!;

    /// <summary>Checks a value against the type, the bounds, and the accepted words.</summary>
    /// <param name="value">The value as written.</param>
    /// <param name="error">The reason the value is rejected, or an empty string.</param>
    /// <returns><see langword="true"/> when the value is accepted.</returns>
    public bool Accepts(string value, out string error)
    {
        error = string.Empty;
        switch (Type)
        {
            case SettingType.Bool:
                if (value != "true" && value != "false")
                {
                    error = $"'{value}' is not a bool; expected 'true' or 'false'";
                }

                break;
            case SettingType.Int:
            case SettingType.Float:
                bool isInt = Type == SettingType.Int;
                if (isInt ? !long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _) : !ManifestXml.TryNumber(value, out _))
                {
                    error = $"'{value}' is not {(isInt ? "an int" : "a float")}";
                    break;
                }

                ManifestXml.TryNumber(value, out double number);
                if (Min is double min && number < min)
                {
                    error = $"{value} is below the minimum {Min}";
                }
                else if (Max is double max && number > max)
                {
                    error = $"{value} is above the maximum {Max}";
                }

                break;
            case SettingType.Enum:
                bool found = false;
                foreach (string accepted in Values)
                {
                    found |= accepted == value;
                }

                if (!found)
                {
                    error = $"'{value}' is not one of {string.Join(", ", Values)}";
                }

                break;
        }

        return error.Length == 0;
    }
}

/// <summary>The content of a <c>module.nobara</c> file.</summary>
public sealed class ModuleManifest
{
    /// <summary>Gets the path of the file the manifest was read from.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>Gets the hierarchical identifier of the module.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the short name, or <see langword="null"/>.</summary>
    public string? ShortName { get; init; }

    /// <summary>Gets the version.</summary>
    public SemanticVersion Version { get; init; } = null!;

    /// <summary>Gets the level.</summary>
    public ModuleLevel Level { get; init; }

    /// <summary>Gets the sides where the module runs.</summary>
    public ModuleSide Side { get; init; }

    /// <summary>Gets the module dependencies.</summary>
    public IReadOnlyList<ModuleDependency> Dependencies { get; init; } = Array.Empty<ModuleDependency>();

    /// <summary>Gets the third-party packages.</summary>
    public IReadOnlyList<ModuleLibrary> Libraries { get; init; } = Array.Empty<ModuleLibrary>();

    /// <summary>Gets the identifiers of the modules this module cannot run with.</summary>
    public IReadOnlyList<Declared> Conflicts { get; init; } = Array.Empty<Declared>();

    /// <summary>Gets the names of the exclusive resources the module holds.</summary>
    public IReadOnlyList<Declared> Reserves { get; init; } = Array.Empty<Declared>();

    /// <summary>Gets the context states the module provides.</summary>
    public IReadOnlyList<StateDeclaration> States { get; init; } = Array.Empty<StateDeclaration>();

    /// <summary>Gets the configuration keys.</summary>
    public IReadOnlyList<SettingDeclaration> Settings { get; init; } = Array.Empty<SettingDeclaration>();

    /// <summary>Gets the sides where this base module needs an active backend; <see cref="ModuleSide.None"/> when it needs none.</summary>
    public ModuleSide BackendRequired { get; init; }

    /// <summary>Gets the identifier of the base module this module is a backend of, or <see langword="null"/>.</summary>
    public string? BackendOf { get; init; }

    /// <summary>Gets the position of the root element.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>Whether an entry of <c>&lt;Modules&gt;</c> is a local project or a package.</summary>
public enum ModuleEntryKind
{
    /// <summary><c>&lt;Local path&gt;</c>: a project of the solution.</summary>
    Local,

    /// <summary><c>&lt;Package id version&gt;</c>: a NuGet package.</summary>
    Package,
}

/// <summary>An entry of <c>&lt;Modules&gt;</c> in <c>project.nobara</c>.</summary>
public sealed class ModuleEntry
{
    /// <summary>Gets whether the entry is a local project or a package.</summary>
    public ModuleEntryKind Kind { get; init; }

    /// <summary>Gets the folder of a local module, relative to the project file, or <see langword="null"/>.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the identifier of a package, or <see langword="null"/>.</summary>
    public string? Id { get; init; }

    /// <summary>Gets the accepted versions of a package, or <see langword="null"/>.</summary>
    public VersionRange? Range { get; init; }

    /// <summary>Gets whether the module is required.</summary>
    public bool Required { get; init; }

    /// <summary>Gets the position of the entry.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>An entry of <c>&lt;Overrides&gt;</c> in <c>project.nobara</c>.</summary>
public sealed class ProjectOverride
{
    /// <summary>Gets the identifier of the module whose package is replaced.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the path of the project that replaces it, relative to the project file.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the position of the entry.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>The content of a <c>project.nobara</c> file.</summary>
public sealed class ProjectManifest
{
    /// <summary>Gets the path of the file the manifest was read from.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>Gets the identifier of the game.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the display name of the game.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the version of the game.</summary>
    public SemanticVersion Version { get; init; } = null!;

    /// <summary>Gets the path of the icon, relative to the project file, or <see langword="null"/>.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets the modules chosen by the game.</summary>
    public IReadOnlyList<ModuleEntry> Modules { get; init; } = Array.Empty<ModuleEntry>();

    /// <summary>Gets the packages replaced by projects.</summary>
    public IReadOnlyList<ProjectOverride> Overrides { get; init; } = Array.Empty<ProjectOverride>();

    /// <summary>Gets the protected namespaces, such as <c>Example/*</c>.</summary>
    public IReadOnlyList<Declared> ProtectedNamespaces { get; init; } = Array.Empty<Declared>();

    /// <summary>Gets whether modding is allowed.</summary>
    public bool ModdingEnabled { get; init; }

    /// <summary>Gets the position of the root element.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A phase of the loop.</summary>
public sealed class LoopPhase
{
    /// <summary>Gets the name of the phase.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets whether the phase runs at the fixed tick rate.</summary>
    public bool Fixed { get; init; }

    /// <summary>Gets the side the phase runs on; <see cref="ModuleSide.None"/> for both.</summary>
    public ModuleSide Side { get; init; }

    /// <summary>Gets the position of the phase.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>The error policy of one module.</summary>
public sealed class ModulePolicy
{
    /// <summary>Gets the identifier of the module.</summary>
    public string Module { get; init; } = string.Empty;

    /// <summary>Gets the policy.</summary>
    public ErrorPolicyKind Policy { get; init; }

    /// <summary>Gets the position of the entry.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A value given to a configuration key in <c>engine.nobara</c>.</summary>
public sealed class SettingValue
{
    /// <summary>Gets what the element does.</summary>
    public SettingOperation Operation { get; init; }

    /// <summary>Gets the key.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets the value as written.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Gets the position of the element.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A <c>&lt;ModuleConfig&gt;</c> block of <c>engine.nobara</c>.</summary>
public sealed class ModuleConfigBlock
{
    /// <summary>Gets the identifier of the module the block is meant for.</summary>
    public string Module { get; init; } = string.Empty;

    /// <summary>Gets the element, with its line information.</summary>
    public XElement Element { get; init; } = null!;

    /// <summary>Gets the position of the element.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>The content of an <c>engine.nobara</c> file.</summary>
public sealed class EngineManifest
{
    /// <summary>Gets the path of the file the manifest was read from.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>Gets the tick rate, in ticks per second, or <see langword="null"/> when <c>&lt;Loop&gt;</c> is absent.</summary>
    public int? TickRate { get; init; }

    /// <summary>Gets the phases of the loop, in order.</summary>
    public IReadOnlyList<LoopPhase> Phases { get; init; } = Array.Empty<LoopPhase>();

    /// <summary>Gets the initial state of the client, or <see langword="null"/>.</summary>
    public Declared? ClientState { get; init; }

    /// <summary>Gets the initial state of the server, or <see langword="null"/>.</summary>
    public Declared? ServerState { get; init; }

    /// <summary>Gets the policy of the modules without their own.</summary>
    public ErrorPolicyKind DefaultPolicy { get; init; }

    /// <summary>Gets the policies set module by module.</summary>
    public IReadOnlyList<ModulePolicy> Policies { get; init; } = Array.Empty<ModulePolicy>();

    /// <summary>Gets the values given to configuration keys.</summary>
    public IReadOnlyList<SettingValue> Settings { get; init; } = Array.Empty<SettingValue>();

    /// <summary>Gets the configuration blocks meant for modules.</summary>
    public IReadOnlyList<ModuleConfigBlock> ModuleConfigs { get; init; } = Array.Empty<ModuleConfigBlock>();

    /// <summary>Gets the position of the root element.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>A target of <c>build.nobara</c>.</summary>
public sealed class BuildTarget
{
    /// <summary>Gets the name of the target.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the .NET runtime identifier: <c>win-x64</c>.</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Gets the side the target builds: <see cref="ModuleSide.Client"/> or <see cref="ModuleSide.Server"/>.</summary>
    public ModuleSide Side { get; init; }

    /// <summary>Gets the identifiers of the modules disabled for this target.</summary>
    public IReadOnlyList<Declared> Disabled { get; init; } = Array.Empty<Declared>();

    /// <summary>Gets the position of the target.</summary>
    public Location Location { get; init; } = null!;
}

/// <summary>The content of a <c>build.nobara</c> file.</summary>
public sealed class BuildManifest
{
    /// <summary>Gets the path of the file the manifest was read from.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>Gets the targets.</summary>
    public IReadOnlyList<BuildTarget> Targets { get; init; } = Array.Empty<BuildTarget>();

    /// <summary>Gets the position of the root element.</summary>
    public Location Location { get; init; } = null!;
}
