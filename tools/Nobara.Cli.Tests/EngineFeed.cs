// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nobara.Cli.Tests;

/// <summary>
/// The packages a sample game builds against: the core, the resource
/// compiler, and the hot reload library, packed once per test run from the
/// builds the tests run against, and the SDK of <c>artifacts/packages</c>.
/// </summary>
/// <remarks>
/// Every <c>dotnet</c> that the tool starts in the tests uses a package cache
/// of its own: the packed engine changes between runs without changing its
/// version. The cache of the machine is a source, so nothing is downloaded
/// twice; nuget.org provides what it lacks.
/// </remarks>
internal static class EngineFeed
{
    private static readonly Lazy<string> Packed = new(Pack);

    /// <summary>Gets the cache of the machine, before the tests replaced it.</summary>
    public static string MachinePackages { get; private set; } = string.Empty;

    /// <summary>Gets the root of the repository.</summary>
    public static string Repository { get; } = FindRepositoryRoot();

    /// <summary>Gets the folder of the packed engine.</summary>
    public static string Folder => Packed.Value;

    [ModuleInitializer]
    internal static void IsolatePackages()
    {
        MachinePackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", Path.Combine(Path.GetTempPath(), "nobara-cli-tests", "packages-" + Guid.NewGuid().ToString("N")));
    }

    /// <summary>The <c>NuGet.config</c> of a sample game.</summary>
    public static string NuGetConfig() => $"""
        <configuration>
          <packageSources>
            <clear />
            <add key="engine" value="{Folder}" />
            <add key="sdk" value="{Path.Combine(Repository, "artifacts", "packages")}" />
            <add key="machine" value="{MachinePackages}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="engine"><package pattern="Nobara.*" /></packageSource>
            <packageSource key="sdk"><package pattern="Nobara.Sdk" /></packageSource>
            <packageSource key="machine"><package pattern="*" /></packageSource>
            <packageSource key="nuget.org"><package pattern="*" /></packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    private static string Pack()
    {
        string sdk = Path.Combine(Repository, "artifacts", "packages");
        Assert.True(
            Directory.Exists(sdk) && Directory.EnumerateFiles(sdk, "Nobara.Sdk.*.nupkg").Any(),
            $"Pack the SDK first: dotnet pack tools/Nobara.Sdk -c Release -o artifacts/packages (nothing in {sdk}).");

        string feed = Path.Combine(Path.GetTempPath(), "nobara-cli-tests", "feed-" + Guid.NewGuid().ToString("N"));
        foreach (string project in new[] { "src/Core/Nobara.Core", "tools/Nobara.ResourceCompiler", "tools/Nobara.HotReload" })
        {
            (int exit, string output) = Dotnet(
                Repository,
                "pack", project, "-c", BuildConfiguration, "--no-build", "--no-restore", "-o", feed, "--nologo", "-p:IsPackable=true",
                "-p:NuspecOutputPath=" + Path.Combine(feed, "nuspec", Path.GetFileName(project)) + Path.DirectorySeparatorChar);
            Assert.True(exit == 0, $"Packing {project} failed:{Environment.NewLine}{output}");
        }

        return feed;
    }

    /// <summary>Runs <c>dotnet</c> outside the MSBuild of the test host.</summary>
    public static (int ExitCode, string Output) Dotnet(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }

    // The configuration this test project, and so the engine, was built with.
    private static string BuildConfiguration =>
        typeof(EngineFeed).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } configuration ? configuration : "Debug";

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Nobara.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
