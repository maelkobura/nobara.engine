// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;

namespace Nobara.Core.Loading;

/// <summary>
/// Where the core finds the modules of a distribution: the single loading
/// interface of the core.
/// </summary>
/// <remarks>
/// <para>
/// The core calls <see cref="Discover"/> for every module of the distribution
/// first, checks the whole composition, and only then calls
/// <see cref="Load"/> for the accepted modules, in load order. A source never
/// runs code of a module in <see cref="Discover"/>, so a refused module never
/// runs.
/// </para>
/// <para>
/// <see cref="AssemblyModuleSource"/> loads one assembly per module from the
/// <c>system/</c> and <c>game/</c> folders. A static source, for platforms
/// that compile every module into the executable, implements this interface
/// with code generated when the game is built; none is provided yet.
/// </para>
/// </remarks>
public interface IModuleSource
{
    /// <summary>
    /// Checks the third-party libraries that the distribution records, before
    /// any module is discovered, and limits the libraries the modules can load
    /// to them.
    /// </summary>
    /// <param name="libraries">The libraries as the distribution records them; empty when it records none.</param>
    /// <returns>
    /// One problem per library that is missing, outside the library folder, or
    /// different from the file the distribution was built with; empty when
    /// every library is accepted.
    /// </returns>
    /// <remarks>A source that has no library files, such as a static source, accepts every list.</remarks>
    IReadOnlyList<CompositionProblem> CheckLibraries(IReadOnlyList<ExpectedLibrary> libraries) => [];

    /// <summary>Locates a module, checks its integrity, and reads its manifest without running its code.</summary>
    /// <param name="module">The module as the distribution records it.</param>
    /// <returns>The module and its compiled manifest.</returns>
    /// <exception cref="ModuleDiscoveryException">The module is missing, altered, or has no readable manifest.</exception>
    ModuleImage Discover(ExpectedModule module);

    /// <summary>Loads the code of an accepted module.</summary>
    /// <param name="image">A module returned by <see cref="Discover"/>.</param>
    /// <returns>The loaded module.</returns>
    /// <exception cref="ModuleLoadException">The code of the module cannot be loaded.</exception>
    LoadedModule Load(ModuleImage image);
}

/// <summary>A module as the distribution manifest records it.</summary>
/// <param name="Id">The identifier of the module.</param>
/// <param name="Version">The version the distribution was built with.</param>
/// <param name="Hash">The SHA-256 of the assembly of the module, empty when the distribution records none.</param>
public sealed record ExpectedModule(string Id, string Version, ReadOnlyMemory<byte> Hash);

/// <summary>A third-party library as the distribution manifest records it.</summary>
/// <param name="Path">The path of the file relative to the folder of the installed game, with forward slashes: <c>lib/Example.Library.dll</c>.</param>
/// <param name="Hash">The SHA-256 of the file.</param>
public sealed record ExpectedLibrary(string Path, ReadOnlyMemory<byte> Hash);

/// <summary>A module found by a <see cref="IModuleSource"/>, before any of its code runs.</summary>
/// <param name="Id">The identifier of the module.</param>
/// <param name="Location">Where the module was found, for messages: the path of its assembly.</param>
/// <param name="Manifest">The compiled module manifest, <c>nobara/module.pb</c>.</param>
public sealed record ModuleImage(string Id, string Location, ReadOnlyMemory<byte> Manifest);

/// <summary>A module whose code is loaded.</summary>
public sealed class LoadedModule
{
    private readonly Func<IModule>? _factory;
    private readonly Func<string, Stream?>? _openResource;

    /// <summary>Initializes a loaded module whose data is in the manifest resources of its assembly.</summary>
    /// <param name="assembly">
    /// The assembly that holds the code of the module, or <see langword="null"/>.
    /// The core uses it to tell which module an exception comes from.
    /// </param>
    /// <param name="factory">Creates the instance of the module for a context, or <see langword="null"/> for a module without code.</param>
    public LoadedModule(Assembly? assembly, Func<IModule>? factory)
        : this(assembly, factory, null)
    {
    }

    /// <summary>Initializes a loaded module whose data comes from elsewhere than its assembly.</summary>
    /// <param name="assembly">
    /// The assembly that holds the code of the module, or <see langword="null"/>.
    /// The core uses it to tell which module an exception comes from.
    /// </param>
    /// <param name="factory">Creates the instance of the module for a context, or <see langword="null"/> for a module without code.</param>
    /// <param name="openResource">
    /// Opens a data resource of the module by name, such as
    /// <c>nobara/resources.pb</c>, or returns <see langword="null"/> when the
    /// module has none. When it is <see langword="null"/>, the manifest
    /// resources of <paramref name="assembly"/> are used.
    /// </param>
    public LoadedModule(Assembly? assembly, Func<IModule>? factory, Func<string, Stream?>? openResource)
    {
        Assembly = assembly;
        _factory = factory;
        _openResource = openResource;
    }

    /// <summary>Gets the assembly that holds the code of the module, or <see langword="null"/>.</summary>
    public Assembly? Assembly { get; }

    /// <summary>Gets whether the module has code to run.</summary>
    public bool HasCode => _factory is not null;

    /// <summary>Creates a new instance of the module, for one context.</summary>
    /// <returns>The instance, or <see langword="null"/> for a module without code.</returns>
    public IModule? CreateInstance() => _factory?.Invoke();

    /// <summary>
    /// Opens a data resource of the module: its compiled resources
    /// (<c>nobara/resources.pb</c>), its schemas (<c>nobara/schemas.pb</c>), or
    /// one of its binary files (<c>nobara/files/</c> and the path).
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A stream over the resource, or <see langword="null"/> when the module has none of that name.</returns>
    public Stream? OpenResource(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _openResource is not null ? _openResource(name) : Assembly?.GetManifestResourceStream(name);
    }
}

/// <summary>Thrown by <see cref="IModuleSource.Discover"/> when a module cannot be accepted.</summary>
public sealed class ModuleDiscoveryException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="kind">The cause.</param>
    /// <param name="message">A message that names the module or the file.</param>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public ModuleDiscoveryException(CompositionProblemKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Gets the cause.</summary>
    public CompositionProblemKind Kind { get; }
}

/// <summary>Thrown when the code of an accepted module cannot be loaded.</summary>
public sealed class ModuleLoadException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="moduleId">The module whose code cannot be loaded.</param>
    /// <param name="message">A message that names the module and the cause.</param>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public ModuleLoadException(string moduleId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ModuleId = moduleId;
    }

    /// <summary>Gets the module whose code cannot be loaded.</summary>
    public string ModuleId { get; }
}
