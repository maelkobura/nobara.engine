// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Google.Protobuf;
using F = Nobara.Formats;

namespace Nobara.Core.Tests.Support;

/// <summary>
/// The sample modules of <c>TestModules/</c>, built once per test run with
/// the SDK from the local feed of the repository and the core under test.
/// </summary>
/// <remarks>
/// The SDK comes from <c>artifacts/packages</c>, as for every module of the
/// repository; pack it first. The core is packed from the build the tests
/// run against.
/// </remarks>
public sealed class SampleModules : IDisposable
{
    public SampleModules()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-core-tests", "modules-" + Guid.NewGuid().ToString("N"));
        string repository = FindRepositoryRoot();
        string feed = Path.Combine(Root, ".feed");
        string sdkFeed = Path.Combine(repository, "artifacts", "packages");
        Assert.True(
            Directory.EnumerateFiles(sdkFeed, "Nobara.Sdk.*.nupkg").Any(),
            $"Pack the SDK first: dotnet pack tools/Nobara.Sdk -c Release -o artifacts/packages (nothing in {sdkFeed}).");

        Run(repository, "pack", Path.Combine("src", "Core", "Nobara.Core"), "-c", BuildConfiguration, "--no-build", "--no-restore", "-o", feed, "--nologo",
            "-p:NuspecOutputPath=" + Path.Combine(feed, "nuspec") + Path.DirectorySeparatorChar);

        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "TestModules"), Root);
        File.WriteAllText(Path.Combine(Root, "NuGet.config"), $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="core" value="{feed}" />
                <add key="sdk" value="{sdkFeed}" />
                <add key="machine" value="{GlobalPackages}" />
              </packageSources>
            </configuration>
            """);

        string[] names = ["Example.Base", "Example.Feature", "Example.Extra", "Example.Rogue"];
        Directory.CreateDirectory(Path.Combine(Root, "obj", "nobara"));
        File.WriteAllText(Path.Combine(Root, "obj", "nobara", "modules.props"), "<Project><ItemGroup>"
            + string.Concat(names.Select(n => $"""<NobaraLocalModule Include="{n.ToLowerInvariant()}" Project="{Path.Combine(Root, n, n + ".csproj")}" />"""))
            + "</ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Root, "Samples.slnx"), "<Solution>" + string.Concat(names.Select(n => $"""<Project Path="{n}/{n}.csproj" />""")) + "</Solution>");

        Run(Root, "build", "Samples.slnx", "--nologo", "-nodeReuse:false", "-c", "Debug");
    }

    /// <summary>Gets the scratch folder of the samples.</summary>
    public string Root { get; }

    /// <summary>Gets the path of the assembly of a sample module.</summary>
    public string Assembly(string name) => Path.Combine(Root, name, "bin", "Debug", "net10.0", name + ".dll");

    /// <summary>
    /// Installs sample modules as a game: their assemblies in <c>game/</c>,
    /// and a distribution manifest that records their hashes.
    /// </summary>
    /// <returns>The install folder and the compiled distribution manifest.</returns>
    public (string InstallDirectory, byte[] Distribution) Install(IEnumerable<string> names, Action<F.DistributionManifest>? configure = null)
    {
        string install = Path.Combine(Root, "installs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(install, "game"));
        var distribution = new F.DistributionManifest
        {
            Format = 1,
            Id = "example",
            Name = "Example",
            Version = "0.1.0",
            ErrorPolicy = new F.ErrorPolicy { DefaultPolicy = F.Policy.Stop },
        };
        foreach (string name in names)
        {
            string target = Path.Combine(install, "game", name + ".dll");
            File.Copy(Assembly(name), target);
            distribution.Modules.Add(new F.DistributionModule
            {
                Id = name.ToLowerInvariant(),
                Version = "1.0.0",
                Hash = ByteString.CopyFrom(SHA256.HashData(File.ReadAllBytes(target))),
            });
        }

        configure?.Invoke(distribution);
        return (install, distribution.ToByteArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Loaded assemblies stay locked until the process ends; the folder is disposable.
        }
    }

    private static string GlobalPackages =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    // The configuration the tests, and so the core, were built with.
    private static string BuildConfiguration =>
        typeof(SampleModules).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } configuration
            ? configuration
            : "Debug";

    private void Run(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The test host sets MSBuild variables that would redirect the child process.
        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        // A package cache of its own: the core package changes between runs without changing its version.
        start.Environment["NUGET_PACKAGES"] = Path.Combine(Root, ".packages");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:{Environment.NewLine}{output}{error.Result}");
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Nobara.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Nobara.slnx not found above " + AppContext.BaseDirectory);
    }
}
