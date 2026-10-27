// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Cli;

/// <summary>Reads the command line and runs a command.</summary>
internal static class CommandLine
{
    /// <summary>Exit code of a command that succeeded.</summary>
    public const int Success = 0;

    /// <summary>Exit code of a command that found problems.</summary>
    public const int Failure = 1;

    /// <summary>Exit code of a command line that cannot be understood.</summary>
    public const int Usage = 2;

    public const string Help = """
        Usage: nobara <command> [options]

        Commands:
          generate              Generate the solution, the module map, the lock file, and
                                the entry assembly of the game; in the engine repository,
                                the solution and the module map.
          build [target]        Build a target of build.nobara, or every target, into
                                out/<target>/, and pack it into out/<target>.zip.
          package [target]      Pack a built target, or every target, into out/<target>.zip.
          docs                  Update the generated sections of the module sheets.

        Options:
          -C, --directory <dir> Run in this folder instead of the current one.
          --check               generate, docs: fail when a file kept in version control
                                is out of date, without changing it.
          --development         build: a development build (debug, no ReadyToRun, no
                                archive) whose executable accepts --hot-reload.
          -h, --help            Show this help.
          --version             Show the version of the tool.
        """;

    /// <summary>Runs a command.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="currentDirectory">The folder the tool runs in.</param>
    /// <param name="reporter">Receives the output.</param>
    /// <returns>The exit code: <see cref="Success"/>, <see cref="Failure"/>, or <see cref="Usage"/>.</returns>
    public static int Run(string[] args, string currentDirectory, Reporter reporter)
    {
        string? command = null;
        string? target = null;
        string directory = currentDirectory;
        bool check = false;
        bool development = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "help":
                    reporter.Info(Help);
                    return Success;
                case "--version":
                    reporter.Info(GameGenerator.EngineVersion);
                    return Success;
                case "--check":
                    check = true;
                    break;
                case "--development":
                    development = true;
                    break;
                case "-C" or "--directory":
                    if (++i == args.Length)
                    {
                        return UsageError(reporter, $"{args[i - 1]} needs a folder.");
                    }

                    directory = Path.GetFullPath(Path.Combine(currentDirectory, args[i]));
                    break;
                case var option when option.StartsWith('-'):
                    return UsageError(reporter, $"unknown option '{option}'.");
                case var word when command is null:
                    command = word;
                    break;
                case var word when target is null && command is "build" or "package":
                    target = word;
                    break;
                default:
                    return UsageError(reporter, $"unexpected argument '{args[i]}'.");
            }
        }

        if (command is null)
        {
            reporter.Info(Help);
            return Usage;
        }

        if (!Directory.Exists(directory))
        {
            return UsageError(reporter, $"the folder '{directory}' does not exist.");
        }

        if ((check && command is not ("generate" or "docs")) || (development && command != "build"))
        {
            return UsageError(reporter, $"{(check ? "--check" : "--development")} does not apply to '{command}'.");
        }

        bool ok = command switch
        {
            "generate" => Generate(directory, check, reporter),
            "build" => Build(directory, target, development, reporter),
            "package" => Package(directory, target, reporter),
            "docs" => ModuleSheets.Update(directory, check, reporter),
            _ => UsageCommand(reporter, command),
        };

        return command is "generate" or "build" or "package" or "docs" ? (ok ? Success : Failure) : Usage;
    }

    private static bool Generate(string directory, bool check, Reporter reporter)
    {
        if (RepositoryGenerator.Applies(directory))
        {
            return RepositoryGenerator.Generate(directory, check, reporter);
        }

        GameFiles? files = Read(directory, engine: false, build: false, reporter);
        return files is not null && GameGenerator.Generate(files, check, reporter) is not null;
    }

    private static bool Build(string directory, string? target, bool development, Reporter reporter)
    {
        // Step 1: the three files and the lock file, then the modules.
        GameFiles? files = Read(directory, engine: true, build: true, reporter);
        if (files is null)
        {
            return false;
        }

        Resolution? resolution = GameGenerator.Generate(files, check: false, reporter);
        return resolution is not null && new TargetBuilder(files, resolution, reporter) { Development = development }.Build(target);
    }

    private static bool Package(string directory, string? target, Reporter reporter)
    {
        GameFiles? files = Read(directory, engine: false, build: true, reporter);
        if (files is null)
        {
            return false;
        }

        var builder = new TargetBuilder(files, new Resolution { Modules = [] }, reporter);
        IEnumerable<BuildTarget> targets = files.Build!.Targets.Where(t => target is null || string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!targets.Any())
        {
            reporter.Error($"{files.Build.File}: target '{target}' is not declared.");
            return false;
        }

        return targets.All(builder.Package);
    }

    private static GameFiles? Read(string directory, bool engine, bool build, Reporter reporter)
    {
        var diagnostics = new DiagnosticBag();
        GameFiles? files = GameFiles.Read(directory, engine, build, diagnostics);
        reporter.Report(diagnostics.Items);
        return files;
    }

    private static bool UsageCommand(Reporter reporter, string command)
    {
        UsageError(reporter, $"unknown command '{command}'.");
        return false;
    }

    private static int UsageError(Reporter reporter, string message)
    {
        reporter.Error(message + " Run nobara --help.");
        return Usage;
    }
}
