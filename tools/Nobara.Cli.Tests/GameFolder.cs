// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Cli.Tests;

/// <summary>The outcome of a command of the tool.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Output">The standard output.</param>
/// <param name="Errors">The error output.</param>
internal sealed record CliResult(int ExitCode, string Output, string Errors)
{
    public override string ToString() => $"exit code {ExitCode}{Environment.NewLine}{Output}{Errors}";
}

/// <summary>A game in a scratch folder: root files and modules written by a test.</summary>
internal sealed class GameFolder : IDisposable
{
    public GameFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Writes a file and returns its full path.</summary>
    public string Write(string relativePath, string content)
    {
        string path = Path.GetFullPath(Path.Combine(Root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public bool Exists(string relativePath) => File.Exists(Path.Combine(Root, relativePath)) || Directory.Exists(Path.Combine(Root, relativePath));

    /// <summary>Writes <c>project.nobara</c>.</summary>
    public GameFolder Project(string modules, string extra = "", string name = "Example Game") =>
        Written("project.nobara", $"""<NobaraProject format="1"><Id>example.game</Id><Name>{name}</Name><Version>0.1.0</Version><Modules>{modules}</Modules>{extra}</NobaraProject>""");

    /// <summary>Writes <c>engine.nobara</c>.</summary>
    public GameFolder Engine(string body = "") => Written("engine.nobara", $"""<NobaraEngine format="1">{body}</NobaraEngine>""");

    /// <summary>Writes <c>build.nobara</c> with a client and a server target.</summary>
    public GameFolder Build(string clientExtra = "", string serverExtra = "") => Written("build.nobara", $"""
        <NobaraBuild format="1">
          <Target name="client"><Platform>win-x64</Platform><Side>client</Side>{clientExtra}</Target>
          <Target name="server"><Platform>win-x64</Platform><Side>server</Side>{serverExtra}</Target>
        </NobaraBuild>
        """);

    /// <summary>Writes a module: its <c>module.nobara</c> and a project file named after it.</summary>
    /// <param name="folder">The folder of the module, relative to the game.</param>
    /// <param name="id">The identifier, which names the project.</param>
    /// <param name="body">The elements of the manifest after the identifier and the version.</param>
    public GameFolder Module(string folder, string id, string body = "", string level = "business", string side = "both", string version = "1.0.0")
    {
        Write($"{folder}/module.nobara", $"""<NobaraModule format="1"><Id>{id}</Id><Version>{version}</Version><Level>{level}</Level><Side>{side}</Side>{body}</NobaraModule>""");
        Write($"{folder}/{id}.csproj", "<Project Sdk=\"Nobara.Sdk/1.0.0\">\n</Project>\n");
        return this;
    }

    /// <summary>Runs the tool in the game folder.</summary>
    public CliResult Run(params string[] args) => RunIn(Root, args);

    /// <summary>Runs the tool in a folder.</summary>
    public static CliResult RunIn(string directory, params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        int exit = CommandLine.Run(args, directory, new Reporter(output, errors));
        return new CliResult(exit, output.ToString(), errors.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A build node may still hold a file; the scratch folder is disposable.
        }
    }

    private GameFolder Written(string path, string content)
    {
        Write(path, content);
        return this;
    }
}
