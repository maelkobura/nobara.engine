// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Cli;

/// <summary>The files at the root of a game, and the paths the tool writes to.</summary>
internal sealed class GameFiles
{
    public const string ProjectFile = "project.nobara";
    public const string EngineFile = "engine.nobara";
    public const string BuildFile = "build.nobara";
    public const string LockFile = "project.nobara.lock";

    private GameFiles(string root, ProjectManifest project)
    {
        Root = root;
        Project = project;
    }

    /// <summary>Gets the folder of the game: where <c>project.nobara</c> is.</summary>
    public string Root { get; }

    public ProjectManifest Project { get; }

    public EngineManifest? Engine { get; private set; }

    public BuildManifest? Build { get; private set; }

    /// <summary>Gets the folder of generated files: <c>obj/nobara/</c>.</summary>
    public string Generated => Path.Combine(Root, "obj", "nobara");

    /// <summary>Gets the name of the game as a file name: <c>&lt;Name&gt;</c> without the characters a file name cannot hold.</summary>
    public string FileName => Names.FileName(Project.Name, Project.Id);

    /// <summary>Gets the name of the entry assembly: the file name without spaces.</summary>
    public string EntryName => Names.AssemblyName(Project.Name, Project.Id);

    public string EntryDirectory => Path.Combine(Generated, "entry");

    public string EntryProject => Path.Combine(EntryDirectory, EntryName + ".csproj");

    public string Solution => Path.Combine(Root, FileName + ".slnx");

    /// <summary>Reads <c>project.nobara</c>, and <c>engine.nobara</c> and <c>build.nobara</c> when asked.</summary>
    /// <returns>The files, or <see langword="null"/> when one cannot be read; the problems are in <paramref name="diagnostics"/>.</returns>
    public static GameFiles? Read(string root, bool engine, bool build, DiagnosticBag diagnostics)
    {
        ProjectManifest? project = ReadFile(root, ProjectFile, ProjectManifestReader.Read, required: true, diagnostics);
        if (project is null)
        {
            return null;
        }

        var files = new GameFiles(root, project);
        if (engine)
        {
            files.Engine = ReadFile(root, EngineFile, EngineManifestReader.Read, required: true, diagnostics);
        }

        if (build)
        {
            files.Build = ReadFile(root, BuildFile, BuildManifestReader.Read, required: true, diagnostics);
        }

        return diagnostics.HasErrors ? null : files;
    }

    /// <summary>Reads <c>module.nobara</c> in a folder.</summary>
    public static ModuleManifest? ReadModule(string folder, DiagnosticBag diagnostics) =>
        ReadFile(folder, "module.nobara", ModuleManifestReader.Read, required: true, diagnostics);

    private static T? ReadFile<T>(string folder, string name, Func<string, string, DiagnosticBag, T?> read, bool required, DiagnosticBag diagnostics)
        where T : class
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path))
        {
            if (required)
            {
                diagnostics.Error("NRC001", $"{name} not found in '{folder}'", new Location(path, 0, 0));
            }

            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("NRC001", $"{name} cannot be read: {exception.Message}", new Location(path, 0, 0));
            return null;
        }

        return read(text, path, diagnostics);
    }
}

/// <summary>Names derived from the name of a game.</summary>
internal static class Names
{
    /// <summary>The name as a file name: characters that a file name cannot hold are removed.</summary>
    public static string FileName(string name, string fallback)
    {
        char[] invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
        string result = new(name.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray());
        result = result.Trim().TrimEnd('.');
        return result.Length > 0 ? result : fallback;
    }

    /// <summary>The name as an assembly name: letters, digits, dots, dashes, and underscores.</summary>
    public static string AssemblyName(string name, string fallback)
    {
        string result = new(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        result = result.Trim('.');
        return result.Length > 0 && !char.IsAsciiDigit(result[0]) ? result : new string(fallback.Where(c => char.IsAsciiLetterOrDigit(c) || c == '.').ToArray());
    }

    /// <summary>A path relative to a folder, with forward slashes.</summary>
    public static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');
}
