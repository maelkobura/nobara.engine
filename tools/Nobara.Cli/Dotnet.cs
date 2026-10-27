// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;

namespace Nobara.Cli;

/// <summary>Runs the <c>dotnet</c> command.</summary>
internal static class Dotnet
{
    /// <summary>Runs <c>dotnet</c> and writes its output through the reporter.</summary>
    /// <returns>The exit code.</returns>
    public static int Run(Reporter reporter, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // A tool started by MSBuild, as in the tests, inherits variables that
        // would send the child build to another SDK.
        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Write(reporter, e.Data);
        process.ErrorDataReceived += (_, e) => Write(reporter, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }

    // The path of the dotnet that runs this tool, so that both use the same SDK.
    private static string Executable =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private static void Write(Reporter reporter, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            reporter.Raw("  " + line.TrimEnd());
        }
    }
}
