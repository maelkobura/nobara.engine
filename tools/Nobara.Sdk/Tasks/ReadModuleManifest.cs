// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Sdk.Tasks;

/// <summary>
/// Reads and validates <c>module.nobara</c>, and turns its dependencies and
/// libraries into project references and package references.
/// </summary>
/// <remarks>
/// The manifest is read by the same code as the resource compiler, compiled
/// into this assembly. A dependency becomes a project reference when
/// <see cref="LocalModules"/> maps its identifier to a project, and a package
/// reference otherwise.
/// </remarks>
public sealed class ReadModuleManifest : Microsoft.Build.Utilities.Task
{
    /// <summary>Gets or sets the path of the <c>module.nobara</c> file.</summary>
    [Required]
    public string ManifestFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the project, which must match the module identifier.</summary>
    [Required]
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local modules. Each item is a module identifier with a
    /// <c>Project</c> metadata that holds the full path of its project file.
    /// </summary>
    public ITaskItem[] LocalModules { get; set; } = [];

    /// <summary>Gets the module identifier, as written in the manifest.</summary>
    [Output]
    public string Id { get; private set; } = string.Empty;

    /// <summary>Gets the module version.</summary>
    [Output]
    public string Version { get; private set; } = string.Empty;

    /// <summary>Gets the project references, one item per local dependency, identified by the project path.</summary>
    [Output]
    public ITaskItem[] ProjectReferences { get; private set; } = [];

    /// <summary>
    /// Gets the package references: dependencies with no local project, with
    /// the NuGet range in <c>Version</c>, then the declared libraries.
    /// </summary>
    [Output]
    public ITaskItem[] PackageReferences { get; private set; } = [];

    /// <summary>Gets the identifiers of the optional dependencies, separated by semicolons.</summary>
    [Output]
    public string OptionalModules { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public override bool Execute()
    {
        if (!File.Exists(ManifestFile))
        {
            Log.LogError(null, "NSDK001", null, ManifestFile, 0, 0, 0, 0, "module.nobara not found; every project that is not an auxiliary project (.Build, .Generators, .Analyzers, .Scripting, .TypeGen, .Tests, .Benchmarks) needs one next to its project file");
            return false;
        }

        var diagnostics = new DiagnosticBag();
        ModuleManifest? manifest = ModuleManifestReader.Read(File.ReadAllText(ManifestFile), ManifestFile, diagnostics);
        foreach (Diagnostic diagnostic in diagnostics.Items)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                Log.LogError(null, diagnostic.Code, null, ManifestFile, diagnostic.Line, diagnostic.Column, 0, 0, diagnostic.Message);
            }
            else
            {
                Log.LogWarning(null, diagnostic.Code, null, ManifestFile, diagnostic.Line, diagnostic.Column, 0, 0, diagnostic.Message);
            }
        }

        if (manifest is null)
        {
            return false;
        }

        if (!string.Equals(manifest.Id, ProjectName, StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError(null, "NSDK005", null, ManifestFile, 0, 0, 0, 0, $"<Id> '{manifest.Id}' does not match the project name '{ProjectName}' (compared without case); the project name is the assembly name and the package identifier");
            return false;
        }

        Id = manifest.Id;
        Version = manifest.Version.Text;
        Resolve(manifest);
        return true;
    }

    private void Resolve(ModuleManifest manifest)
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem item in LocalModules)
        {
            string project = item.GetMetadata("Project");
            if (project.Length > 0)
            {
                local[item.ItemSpec] = project;
            }
        }

        var projects = new List<ITaskItem>();
        var packages = new List<ITaskItem>();
        var optional = new StringBuilder();
        foreach (ModuleDependency dependency in manifest.Dependencies)
        {
            if (local.TryGetValue(dependency.Id, out string? project))
            {
                projects.Add(new TaskItem(Path.GetFullPath(project)));
            }
            else
            {
                packages.Add(new TaskItem(dependency.Id, new Dictionary<string, string> { ["Version"] = dependency.Range.NuGet }));
            }

            if (dependency.Optional)
            {
                optional.Append(optional.Length == 0 ? "" : ";").Append(dependency.Id);
            }
        }

        foreach (ModuleLibrary library in manifest.Libraries)
        {
            packages.Add(new TaskItem(library.Id, new Dictionary<string, string> { ["Version"] = library.Version }));
        }

        ProjectReferences = projects.ToArray();
        PackageReferences = packages.ToArray();
        OptionalModules = optional.ToString();
    }
}
