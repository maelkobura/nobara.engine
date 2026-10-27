// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Nobara.Core.Loading;

/// <summary>
/// Loads one assembly per module from the folders of an installed game:
/// <c>system/</c> for platform modules, <c>game/</c> for business modules,
/// and <c>lib/</c> for the third-party libraries they share.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Discover"/> hashes the assembly and reads its manifest from its
/// metadata, without loading it. <see cref="Load"/> loads it into an
/// <see cref="AssemblyLoadContext"/> shared by every module of the source, so
/// each assembly is loaded once per process. An assembly that the default
/// context already holds, such as the core, is shared with it.
/// </para>
/// <para>File names are matched without regard to case: the assembly of
/// <c>example.blocks</c> may be <c>Example.Blocks.dll</c>.</para>
/// </remarks>
public sealed class AssemblyModuleSource : IModuleSource
{
    /// <summary>The name of the manifest resource that holds the compiled module manifest.</summary>
    public const string ManifestResource = "nobara/module.pb";

    private readonly string[] _moduleFolders;
    private readonly ModuleLoadContext _loadContext;

    /// <summary>Initializes a source over the folders of an installed game.</summary>
    /// <param name="installDirectory">The folder that contains <c>system/</c>, <c>game/</c>, and <c>lib/</c>.</param>
    public AssemblyModuleSource(string installDirectory)
    {
        InstallDirectory = Path.GetFullPath(installDirectory);
        _moduleFolders = [Path.Combine(InstallDirectory, "system"), Path.Combine(InstallDirectory, "game")];
        _loadContext = new ModuleLoadContext();
    }

    /// <summary>Gets the folder that contains <c>system/</c>, <c>game/</c>, and <c>lib/</c>.</summary>
    public string InstallDirectory { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Every library is hashed. The modules then load from <c>lib/</c> only
    /// the libraries of the list, so a file added to the folder is never
    /// loaded.
    /// </remarks>
    public IReadOnlyList<CompositionProblem> CheckLibraries(IReadOnlyList<ExpectedLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var problems = new List<CompositionProblem>();
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string folder = Path.Combine(InstallDirectory, "lib") + Path.DirectorySeparatorChar;
        foreach (ExpectedLibrary library in libraries)
        {
            string path = Path.GetFullPath(Path.Combine(InstallDirectory, library.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new CompositionProblem(
                    CompositionProblemKind.InvalidDistribution, string.Empty, $"The distribution records the library '{library.Path}', which is not in lib/."));
                continue;
            }

            byte[] content;
            try
            {
                content = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problems.Add(new CompositionProblem(
                    CompositionProblemKind.MissingAssembly, string.Empty, $"Library '{library.Path}': '{path}' cannot be read: {exception.Message}"));
                continue;
            }

            if (!SHA256.HashData(content).AsSpan().SequenceEqual(library.Hash.Span))
            {
                problems.Add(new CompositionProblem(
                    CompositionProblemKind.IntegrityMismatch, string.Empty, $"Library '{library.Path}': '{path}' differs from the file the distribution was built with."));
                continue;
            }

            allowed[Path.GetFileNameWithoutExtension(path)] = path;
        }

        _loadContext.AllowLibraries(allowed);
        return problems;
    }

    /// <inheritdoc/>
    public ModuleImage Discover(ExpectedModule module)
    {
        string path = FindAssembly(module.Id)
            ?? throw new ModuleDiscoveryException(
                CompositionProblemKind.MissingAssembly,
                $"Module '{module.Id}': its assembly is not in system/ or game/ of '{InstallDirectory}'.");

        byte[] image;
        try
        {
            image = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModuleDiscoveryException(
                CompositionProblemKind.MissingAssembly, $"Module '{module.Id}': '{path}' cannot be read: {exception.Message}", exception);
        }

        // The hash and the manifest come from the same bytes.
        if (module.Hash.IsEmpty)
        {
            throw new ModuleDiscoveryException(
                CompositionProblemKind.IntegrityMismatch, $"Module '{module.Id}': the distribution records no hash for its assembly.");
        }

        if (!SHA256.HashData(image).AsSpan().SequenceEqual(module.Hash.Span))
        {
            throw new ModuleDiscoveryException(
                CompositionProblemKind.IntegrityMismatch,
                $"Module '{module.Id}': '{path}' differs from the assembly the distribution was built with.");
        }

        byte[]? manifest;
        try
        {
            manifest = ReadManifestResource(image);
        }
        catch (BadImageFormatException exception)
        {
            throw new ModuleDiscoveryException(
                CompositionProblemKind.InvalidManifest, $"Module '{module.Id}': '{path}' is not a valid assembly: {exception.Message}", exception);
        }

        if (manifest is null)
        {
            throw new ModuleDiscoveryException(
                CompositionProblemKind.InvalidManifest,
                $"Module '{module.Id}': '{path}' has no module manifest ({ManifestResource}); it was not built as a module.");
        }

        return new ModuleImage(module.Id, path, manifest);
    }

    /// <inheritdoc/>
    public LoadedModule Load(ModuleImage image)
    {
        Assembly assembly;
        try
        {
            assembly = _loadContext.LoadModule(image.Location);
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException)
        {
            throw new ModuleLoadException(image.Id, $"Module '{image.Id}': '{image.Location}' cannot be loaded: {exception.Message}", exception);
        }

        ModuleEntryAttribute? entry;
        try
        {
            entry = assembly.GetCustomAttribute<ModuleEntryAttribute>();
        }
        catch (Exception exception) when (exception is TypeLoadException or FileNotFoundException or FileLoadException)
        {
            throw new ModuleLoadException(image.Id, $"Module '{image.Id}': its entry class cannot be loaded: {exception.Message}", exception);
        }

        if (entry is null)
        {
            return new LoadedModule(assembly, null);
        }

        Type type = entry.ModuleType;
        if (!typeof(IModule).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new ModuleLoadException(
                image.Id,
                $"Module '{image.Id}': its entry class '{type.FullName}' must implement {nameof(IModule)}, be concrete, and have a public parameterless constructor.");
        }

        return new LoadedModule(assembly, () => (IModule)Activator.CreateInstance(type)!);
    }

    /// <summary>Reads the compiled module manifest from the image of an assembly, without loading it.</summary>
    /// <param name="image">The bytes of the assembly file.</param>
    /// <returns>The manifest, or <see langword="null"/> when the assembly has none.</returns>
    /// <exception cref="BadImageFormatException">The bytes are not a .NET assembly.</exception>
    public static byte[]? ReadManifestResource(byte[] image)
    {
        using var reader = new PEReader(new MemoryStream(image, writable: false));
        if (!reader.HasMetadata || reader.PEHeaders.CorHeader is null)
        {
            return null;
        }

        MetadataReader metadata = reader.GetMetadataReader();
        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil || !metadata.StringComparer.Equals(resource.Name, ManifestResource))
            {
                continue;
            }

            // An embedded resource is a 32-bit length followed by its bytes,
            // at its offset in the resources directory.
            DirectoryEntry directory = reader.PEHeaders.CorHeader.ResourcesDirectory;
            PEMemoryBlock block = reader.GetSectionData(directory.RelativeVirtualAddress);
            BlobReader blob = block.GetReader((int)resource.Offset, block.Length - (int)resource.Offset);
            int length = blob.ReadInt32();
            return blob.ReadBytes(length);
        }

        return null;
    }

    private string? FindAssembly(string moduleId)
    {
        string fileName = moduleId + ".dll";
        foreach (string folder in _moduleFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.dll"))
            {
                if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
