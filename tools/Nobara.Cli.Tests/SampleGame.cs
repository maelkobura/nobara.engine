// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nobara.Cli.Tests;

/// <summary>A copy of the sample game of <c>SampleGame/</c>, which the tool builds.</summary>
internal sealed class SampleGameCopy : IDisposable
{
    public SampleGameCopy()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-cli-tests", "game-" + Guid.NewGuid().ToString("N"));
        Copy(Path.Combine(AppContext.BaseDirectory, "SampleGame"), Root);
        File.WriteAllText(Path.Combine(Root, "NuGet.config"), EngineFeed.NuGetConfig());
    }

    public string Root { get; }

    public string Path_(string relative) => Path.Combine(Root, relative);

    /// <summary>Runs the tool in the game.</summary>
    public CliResult Run(params string[] args) => GameFolder.RunIn(Root, args);

    /// <summary>Starts an executable of the built game and reads its output.</summary>
    public static RunningGame Start(string executable, params string[] args) => new(executable, args);

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

    public static void Copy(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}

/// <summary>A process of the built game whose output is collected line by line.</summary>
internal sealed class RunningGame : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();

    public RunningGame(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) => Add(e.Data);
        _process.ErrorDataReceived += (_, e) => Add(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public string Output => string.Join(Environment.NewLine, _lines);

    /// <summary>Waits until a line contains a text.</summary>
    public void WaitFor(string text, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!_lines.Any(l => l.Contains(text, StringComparison.Ordinal)))
        {
            Assert.True(watch.Elapsed < timeout && !_process.HasExited, $"No output line contains '{text}':{Environment.NewLine}{Output}");
            Thread.Sleep(50);
        }
    }

    /// <summary>Waits for the process to end and returns its exit code.</summary>
    public int Exit(TimeSpan timeout)
    {
        Assert.True(_process.WaitForExit(timeout), $"The game did not stop:{Environment.NewLine}{Output}");
        _process.WaitForExit();
        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    private void Add(string? line)
    {
        if (line is not null)
        {
            _lines.Enqueue(line);
        }
    }
}
