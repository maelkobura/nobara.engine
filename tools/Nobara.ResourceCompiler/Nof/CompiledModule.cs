// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Google.Protobuf;
using Nobara.Formats;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>
/// What a compiled module exposes to the modules that depend on it: its
/// manifest, its schemas, and its resources, read from the manifest resources
/// of its assembly without loading it.
/// </summary>
public sealed class CompiledModule
{
    /// <summary>The manifest resource that holds the compiled module manifest.</summary>
    public const string ManifestResource = "nobara/module.pb";

    /// <summary>The manifest resource that holds the schemas, a <c>FileDescriptorSet</c>.</summary>
    public const string SchemasResource = "nobara/schemas.pb";

    /// <summary>The manifest resource that holds the compiled resources.</summary>
    public const string ResourcesResource = "nobara/resources.pb";

    /// <summary>The prefix of the manifest resources of the embedded binary files.</summary>
    public const string FilesPrefix = "nobara/files/";

    /// <summary>Creates a compiled module from its parts.</summary>
    /// <param name="manifest">The compiled module manifest.</param>
    /// <param name="schemas">The serialized descriptor set, or <see langword="null"/> when the module has no schema.</param>
    /// <param name="resources">The compiled resources.</param>
    public CompiledModule(ModuleManifest manifest, byte[]? schemas, ResourceBundle resources)
    {
        Manifest = manifest;
        Schemas = schemas;
        Resources = resources;
    }

    /// <summary>Gets the identifier of the module.</summary>
    public string Id => Manifest.Id;

    /// <summary>Gets the compiled module manifest.</summary>
    public ModuleManifest Manifest { get; }

    /// <summary>Gets the serialized descriptor set of its schemas, or <see langword="null"/>.</summary>
    public byte[]? Schemas { get; }

    /// <summary>Gets its compiled resources.</summary>
    public ResourceBundle Resources { get; }

    /// <summary>Reads a module from its assembly without loading it for execution.</summary>
    /// <param name="path">The path of the assembly.</param>
    /// <returns>The module, or <see langword="null"/> when the assembly is not a Nobara module.</returns>
    /// <exception cref="InvalidDataException">The assembly is a module whose data cannot be decoded.</exception>
    public static CompiledModule? FromAssembly(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = pe.GetMetadataReader();
        byte[]? manifest = Read(pe, metadata, ManifestResource);
        if (manifest is null)
        {
            return null;
        }

        byte[]? resources = Read(pe, metadata, ResourcesResource);
        try
        {
            return new CompiledModule(
                ModuleManifest.Parser.ParseFrom(manifest),
                Read(pe, metadata, SchemasResource),
                resources is null ? new ResourceBundle() : ResourceBundle.Parser.ParseFrom(resources));
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException($"the Nobara data of '{path}' cannot be decoded: {exception.Message}", exception);
        }
    }

    private static byte[]? Read(PEReader pe, MetadataReader metadata, string name)
    {
        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil || metadata.GetString(resource.Name) != name)
            {
                continue;
            }

            PEMemoryBlock block = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
            BlobReader reader = block.GetReader((int)resource.Offset, block.Length - (int)resource.Offset);
            int length = reader.ReadInt32();
            return reader.ReadBytes(length);
        }

        return null;
    }
}
