// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Nobara.ResourceCompiler.Manifests;
using Nobara.ResourceCompiler.Nof;
using Nobara.ResourceCompiler.Plugins;

namespace Nobara.ResourceCompiler.MSBuild;

/// <summary>
/// Compiles the manifest and the <c>.nof</c> files of a module, and lists the
/// manifest resources to embed in its assembly.
/// </summary>
/// <remarks>
/// The list of manifest resources is also written to <c>embedded.txt</c> in
/// <see cref="OutputDirectory"/>, one <c>name|path</c> line per resource, so
/// that a build that skips this task still embeds them.
/// </remarks>
public sealed class CompileResources : Microsoft.Build.Utilities.Task
{
    /// <summary>Gets or sets the path of <c>module.nobara</c>.</summary>
    [Required]
    public string ModuleFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the resources folder of the module.</summary>
    [Required]
    public string ResourcesDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the <c>.nof</c> files.</summary>
    public ITaskItem[] ResourceFiles { get; set; } = [];

    /// <summary>Gets or sets the descriptor set of the module's schemas, or an empty string when it has none.</summary>
    public string SchemaFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the referenced assemblies; those that are Nobara modules are the dependencies.</summary>
    public ITaskItem[] References { get; set; } = [];

    /// <summary>Gets or sets the plugin assemblies of the dependencies.</summary>
    public ITaskItem[] Plugins { get; set; } = [];

    /// <summary>Gets or sets the folder of the compiled files and of the cache.</summary>
    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Gets the manifest resources to embed, with their name in the <c>LogicalName</c> metadata.</summary>
    [Output]
    public ITaskItem[] EmbeddedResources { get; private set; } = [];

    /// <inheritdoc/>
    public override bool Execute()
    {
        string output = Path.GetFullPath(OutputDirectory);
        Directory.CreateDirectory(output);
        var diagnostics = new DiagnosticBag();

        ModuleManifest? manifest = File.Exists(ModuleFile)
            ? ModuleManifestReader.Read(File.ReadAllText(ModuleFile), ModuleFile, diagnostics)
            : null;
        if (manifest is null)
        {
            Report(diagnostics);
            if (!diagnostics.HasErrors)
            {
                Log.LogError(null, "NRC001", null, ModuleFile, 0, 0, 0, 0, "module.nobara not found");
            }

            return false;
        }

        var embedded = new List<(string Name, string Path)>();
        string modulePath = Path.Combine(output, "module.pb");
        WriteIfChanged(modulePath, ManifestCompiler.Compile(manifest).ToByteArray());
        embedded.Add((CompiledModule.ManifestResource, modulePath));

        byte[]? schemas = null;
        if (SchemaFile.Length > 0 && File.Exists(SchemaFile))
        {
            schemas = File.ReadAllBytes(SchemaFile);
            embedded.Add((CompiledModule.SchemasResource, Path.GetFullPath(SchemaFile)));
        }

        // Only the declared dependencies provide types and resources: a module
        // referenced transitively would bring types without their plugins.
        var declared = new HashSet<string>(manifest.Dependencies.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        List<CompiledModule> dependencies = ReadDependencies(manifest.Id, diagnostics).Where(m => declared.Contains(m.Id)).ToList();
        List<string> probe = References.Select(r => Path.GetDirectoryName(Path.GetFullPath(r.ItemSpec))!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using PluginLoader plugins = PluginLoader.Load(Plugins.Select(p => p.ItemSpec), probe, diagnostics);
        Report(diagnostics);
        if (diagnostics.HasErrors)
        {
            return false;
        }

        var compiler = new ResourceCompiler(new ResourceCompilerOptions
        {
            ModuleId = manifest.Id,
            ResourcesDirectory = ResourcesDirectory,
            Schemas = schemas,
            Dependencies = dependencies,
            Plugins = plugins.Plugins,
            CacheFile = Path.Combine(output, "resources.cache"),
        });
        CompilationResult result = compiler.Compile(ResourceFiles.Select(f => f.GetMetadata("FullPath")));
        Report(result.Diagnostics);
        if (!result.Success)
        {
            return false;
        }

        string resourcesPath = Path.Combine(output, "resources.pb");
        WriteIfChanged(resourcesPath, result.Bundle!.ToByteArray());
        embedded.Add((CompiledModule.ResourcesResource, resourcesPath));
        foreach (EmbeddedFile file in result.Files)
        {
            string path = file.SourcePath;
            if (file.Content is not null)
            {
                path = Path.Combine(output, "files", file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteIfChanged(path, file.Content);
            }

            embedded.Add((CompiledModule.FilesPrefix + file.Path, path));
        }

        File.WriteAllLines(Path.Combine(output, "embedded.txt"), embedded.Select(e => e.Name + "|" + e.Path));

        // The target compares its inputs with this stamp, which changes on
        // every run even when the compiled files do not.
        File.WriteAllText(Path.Combine(output, "resources.stamp"), string.Empty);
        EmbeddedResources = embedded.Select(e => (ITaskItem)new TaskItem(e.Path, new Dictionary<string, string> { ["LogicalName"] = e.Name })).ToArray();
        Log.LogMessage(MessageImportance.Normal, $"Nobara resources of {manifest.Id}: {result.CompiledFiles.Count} file(s) compiled, {result.ReusedFiles.Count} reused, {result.Bundle!.Resources.Count} resource(s).");
        return true;
    }

    private List<CompiledModule> ReadDependencies(string moduleId, DiagnosticBag diagnostics)
    {
        var result = new List<CompiledModule>();
        foreach (ITaskItem reference in References)
        {
            string path = reference.GetMetadata("FullPath");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                CompiledModule? module = CompiledModule.FromAssembly(path);
                if (module is not null && !string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase) && result.All(m => !string.Equals(m.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(module);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or BadImageFormatException)
            {
                diagnostics.Error("NRC002", $"the dependency cannot be read: {exception.Message}", new Location(path, 0, 0));
            }
        }

        return result;
    }

    private void Report(DiagnosticBag diagnostics) => Report(diagnostics.Items);

    private void Report(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                Log.LogError(null, diagnostic.Code, null, diagnostic.File, diagnostic.Line, diagnostic.Column, 0, 0, diagnostic.Message);
            }
            else
            {
                Log.LogWarning(null, diagnostic.Code, null, diagnostic.File, diagnostic.Line, diagnostic.Column, 0, 0, diagnostic.Message);
            }
        }
    }

    // Unchanged outputs keep their time stamp, so the C# compiler that
    // embeds them stays up to date.
    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
        {
            return;
        }

        File.WriteAllBytes(path, content);
    }
}
