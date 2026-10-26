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
        _loadContext = new ModuleLoadContext(Path.Combine(InstallDirectory, "lib"));
    }

    /// <summary>Gets the folder that contains <c>system/</c>, <c>game/</c>, and <c>lib/</c>.</summary>
    public string InstallDirectory { get; }

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

        byte[] manifest = ReadManifestResource(image)
            ?? throw new ModuleDiscoveryException(
                CompositionProblemKind.InvalidManifest,
                $"Module '{module.Id}': '{path}' has no module manifest ({ManifestResource}); it was not built as a module.");
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
