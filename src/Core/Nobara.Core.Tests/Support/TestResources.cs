// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Core.Tests.Schemas;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Nof;
using F = Nobara.Formats;

namespace Nobara.Core.Tests.Support;

/// <summary>Compiles <c>.nof</c> text with the resource compiler and the test schemas.</summary>
internal static class TestResources
{
    /// <summary>Gets the test schemas as a serialized descriptor set, imports included.</summary>
    public static byte[] Schemas { get; } = Set(BlocksReflection.Descriptor);

    /// <summary>Wraps resource elements in a root element.</summary>
    public static string Nof(string ns, string body) => $"<Nobara version=\"1.0.0\" namespace=\"{ns}\">\n{body}\n</Nobara>";

    /// <summary>Compiles the resources of a module; the compilation must succeed.</summary>
    /// <param name="moduleId">The module.</param>
    /// <param name="files">The files of its resources folder, by relative path: <c>.nof</c> files and binary files.</param>
    /// <param name="dependencies">The compiled dependencies of the module.</param>
    /// <param name="ownSchemas">Whether the module defines the test schemas itself.</param>
    public static F.ResourceBundle Compile(string moduleId, IReadOnlyDictionary<string, string> files, IReadOnlyList<CompiledModule>? dependencies = null, bool ownSchemas = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-core-tests", "nof-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach ((string path, string content) in files)
            {
                string full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            var compiler = new ResourceCompiler.ResourceCompiler(new ResourceCompilerOptions
            {
                ModuleId = moduleId,
                ResourcesDirectory = root,
                Schemas = ownSchemas ? Schemas : null,
                Dependencies = dependencies ?? [],
            });
            CompilationResult result = compiler.Compile(Directory.EnumerateFiles(root, "*.nof", SearchOption.AllDirectories));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return result.Bundle!;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Compiles one <c>.nof</c> file of a module.</summary>
    public static F.ResourceBundle Compile(string moduleId, string nof, IReadOnlyList<CompiledModule>? dependencies = null, bool ownSchemas = true) =>
        Compile(moduleId, new Dictionary<string, string> { ["resources.nof"] = nof }, dependencies, ownSchemas);

    /// <summary>Turns compiled resources into a dependency of another compilation.</summary>
    public static CompiledModule Dependency(string moduleId, F.ResourceBundle bundle, bool ownSchemas = true) =>
        new(new F.ModuleManifest { Format = 1, Id = moduleId, Version = "1.0.0" }, ownSchemas ? Schemas : null, bundle);

    private static byte[] Set(params FileDescriptor[] files)
    {
        var set = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(FileDescriptor file)
        {
            foreach (FileDescriptor dependency in file.Dependencies)
            {
                Add(dependency);
            }

            if (seen.Add(file.Name))
            {
                set.File.Add(FileDescriptorProto.Parser.ParseFrom(file.SerializedData));
            }
        }

        foreach (FileDescriptor file in files)
        {
            Add(file);
        }

        return set.ToByteArray();
    }
}
