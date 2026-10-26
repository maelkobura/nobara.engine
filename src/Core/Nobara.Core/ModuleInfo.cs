// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core;

/// <summary>The sides of a game: where a module runs, and which side a context serves.</summary>
[Flags]
public enum Side
{
    /// <summary>No side.</summary>
    None = 0,

    /// <summary>The client: the game the player sees.</summary>
    Client = 1,

    /// <summary>The server: the authority over the world.</summary>
    Server = 2,

    /// <summary>Both the client and the server.</summary>
    Both = Client | Server,
}

/// <summary>The level of a module, which only decides the load order and the dependency rule.</summary>
public enum ModuleLevel
{
    /// <summary>A reusable foundation, loaded from <c>system/</c> before every business module.</summary>
    Platform,

    /// <summary>A feature of a game, loaded from <c>game/</c>.</summary>
    Business,
}

/// <summary>What the core does when a module throws an exception.</summary>
public enum ErrorPolicy
{
    /// <summary>Stops the context: the game, or the integrated server.</summary>
    Stop,

    /// <summary>Disables the module and every module that requires it, and continues.</summary>
    Disable,

    /// <summary>Logs the exception and continues.</summary>
    Log,
}

/// <summary>A dependency declared in the manifest of a module.</summary>
/// <param name="ModuleId">The identifier of the module depended on.</param>
/// <param name="Range">The range of versions, as written: <c>^1.2</c>.</param>
/// <param name="Optional">Whether the dependent module works without it.</param>
public sealed record ModuleDependency(string ModuleId, string Range, bool Optional);

/// <summary>A state of the application that a module declares in its manifest.</summary>
/// <param name="StateId">The qualified identifier: <c>state:example.menu</c>.</param>
/// <param name="Side">The sides where the state is available.</param>
public sealed record StateDeclaration(string StateId, Side Side);

/// <summary>
/// What the core knows about a module of the distribution: its manifest, and
/// the choices of the distribution for it.
/// </summary>
/// <remarks>Instances are immutable and shared by every context of the process.</remarks>
public sealed class ModuleInfo
{
    internal ModuleInfo(
        string id,
        string shortName,
        string version,
        ModuleLevel level,
        Side side,
        IReadOnlyList<ModuleDependency> dependencies,
        IReadOnlyList<string> conflicts,
        IReadOnlyList<string> reservations,
        IReadOnlyList<StateDeclaration> states,
        Side backendRequired,
        string backendOf,
        bool required,
        ErrorPolicy errorPolicy)
    {
        Id = id;
        ShortName = shortName;
        Version = version;
        Level = level;
        Side = side;
        Dependencies = dependencies;
        Conflicts = conflicts;
        Reservations = reservations;
        States = states;
        BackendRequired = backendRequired;
        BackendOf = backendOf;
        Required = required;
        ErrorPolicy = errorPolicy;
    }

    /// <summary>Gets the identifier of the module, which is also the name of its assembly: <c>example.blocks</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the short name of the module, or an empty string when it has none.</summary>
    public string ShortName { get; }

    /// <summary>Gets the semantic version of the module.</summary>
    public string Version { get; }

    /// <summary>Gets the level of the module.</summary>
    public ModuleLevel Level { get; }

    /// <summary>Gets the sides where the module runs.</summary>
    public Side Side { get; }

    /// <summary>Gets the declared dependencies, required and optional.</summary>
    public IReadOnlyList<ModuleDependency> Dependencies { get; }

    /// <summary>Gets the identifiers of the modules this module cannot run with.</summary>
    public IReadOnlyList<string> Conflicts { get; }

    /// <summary>Gets the names of the exclusive resources the module holds, such as <c>window.main</c>.</summary>
    public IReadOnlyList<string> Reservations { get; }

    /// <summary>Gets the states of the application the module declares.</summary>
    public IReadOnlyList<StateDeclaration> States { get; }

    /// <summary>Gets the sides where this base module needs an active backend, or <see cref="Side.None"/>.</summary>
    public Side BackendRequired { get; }

    /// <summary>Gets the identifier of the base module this module is a backend of, or an empty string.</summary>
    public string BackendOf { get; }

    /// <summary>Gets whether the distribution requires the module.</summary>
    public bool Required { get; }

    /// <summary>
    /// Gets the error policy that applies to the module: the one the
    /// distribution chose, or <see cref="ErrorPolicy.Stop"/>, imposed on
    /// required and platform modules.
    /// </summary>
    public ErrorPolicy ErrorPolicy { get; }

    /// <summary>Tells whether the module runs on a side.</summary>
    /// <param name="side">A single side: <see cref="Side.Client"/> or <see cref="Side.Server"/>.</param>
    /// <returns><see langword="true"/> when the module runs on <paramref name="side"/>.</returns>
    public bool RunsOn(Side side) => (Side & side) == side && side != Side.None;

    /// <summary>Returns the identifier and the version of the module.</summary>
    /// <returns>For example <c>example.blocks 1.2.0</c>.</returns>
    public override string ToString() => $"{Id} {Version}";
}
