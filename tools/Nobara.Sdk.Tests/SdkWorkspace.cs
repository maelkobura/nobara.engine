// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text.Json;

namespace Nobara.Sdk.Tests;

/// <summary>The outcome of a <c>dotnet</c> invocation.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Output">Standard output and standard error.</param>
internal sealed record DotnetResult(int ExitCode, string Output);

/// <summary>
/// A scratch directory tree that holds sample projects built with the SDK.
/// The SDK is packed once per test run into a local feed, and every
/// invocation uses a package cache of its own, so no sample depends on, or
/// changes, the machine-wide cache.
/// </summary>
internal sealed class SdkWorkspace : IDisposable
{
    private static readonly Lazy<string> Feed = new(PackSdk);

    private readonly string _packages;

    public SdkWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "nobara-sdk-tests", Guid.NewGuid().ToString("N"));
        _packages = Path.Combine(Root, ".packages");
        Directory.CreateDirectory(Root);

        // Only the local feed is a source: samples need no network.
        File.WriteAllText(Path.Combine(Root, "NuGet.config"), $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{Feed.Value}" />
              </packageSources>
            </configuration>
            """);
    }

    /// <summary>Gets the root of the workspace; the nearest parent directory of every sample.</summary>
    public string Root { get; }

    /// <summary>Gets the version of the SDK package under test.</summary>
    public static string SdkVersion => "1.0.0";

    /// <summary>The project element that selects the SDK under test.</summary>
    public static string ProjectOpen => $"<Project Sdk=\"Nobara.Sdk/{SdkVersion}\">";

    /// <summary>Writes a file under the workspace and returns its full path.</summary>
    public string Write(string relativePath, string content)
    {
        string path = Path.GetFullPath(Path.Combine(Root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes a module: project file, <c>module.nobara</c>, and a source file.</summary>
    /// <returns>The full path of the project file.</returns>
    public string WriteModule(string name, string manifestBody, string? source = null, string projectBody = "")
    {
        Write($"{name}/module.nobara", $"""<NobaraModule format="1">{manifestBody}</NobaraModule>""");
        if (source is not null)
        {
            Write($"{name}/Source.cs", source);
        }

        return Write($"{name}/{Path.GetFileName(name)}.csproj", $"{ProjectOpen}{projectBody}</Project>");
    }

    /// <summary>Writes an auxiliary project, which has no manifest.</summary>
    public string WriteAuxiliary(string name, string projectBody = "", string? source = null)
    {
        if (source is not null)
        {
            Write($"{name}/Source.cs", source);
        }

        return Write($"{name}/{name}.csproj", $"{ProjectOpen}{projectBody}</Project>");
    }

    /// <summary>Writes the file that maps module identifiers to local projects.</summary>
    public void MapModules(params (string Id, string ProjectPath)[] modules)
    {
        string items = string.Concat(modules.Select(m => $"""<NobaraLocalModule Include="{m.Id}" Project="{m.ProjectPath}" />"""));
        Write("obj/nobara/modules.props", $"<Project><ItemGroup>{items}</ItemGroup></Project>");
    }

    /// <summary>Builds a project.</summary>
    public DotnetResult Build(string projectPath) => Dotnet("build", projectPath, "--nologo", "-nodeReuse:false");

    /// <summary>Evaluates properties and items of a project after the SDK read its manifest.</summary>
    public Evaluation Evaluate(string projectPath, string[] properties, string[] items)
    {
        DotnetResult result = Dotnet(
            "msbuild", projectPath, "-nologo", "-nodeReuse:false", "-t:NobaraReadModule",
            "-getProperty:" + string.Join(',', properties.Append("MSBuildProjectName")), "-getItem:" + string.Join(',', items.Append("None")));
        Assert.True(result.ExitCode == 0, result.Output);
        return new Evaluation(JsonDocument.Parse(result.Output).RootElement);
    }

    /// <summary>Evaluates a project that is expected to fail and returns its output.</summary>
    public string EvaluateFailure(string projectPath)
    {
        DotnetResult result = Dotnet("msbuild", projectPath, "-nologo", "-nodeReuse:false", "-t:NobaraReadModule");
        Assert.NotEqual(0, result.ExitCode);
        return result.Output;
    }

    /// <summary>Runs <c>dotnet</c> in the workspace.</summary>
    public DotnetResult Dotnet(string command, params string[] arguments)
    {
        var start = StartInfo(Root);
        start.ArgumentList.Add(command);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NUGET_PACKAGES"] = _packages;
        return Run(start);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A build server may still hold a file; the temp directory is disposable.
        }
    }

    private static ProcessStartInfo StartInfo(string workingDirectory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // The test host sets MSBuild variables that would redirect the child
        // process to the SDK that runs the tests.
        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        return start;
    }

    private static DotnetResult Run(ProcessStartInfo start)
    {
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new DotnetResult(process.ExitCode, output + error.Result);
    }

    private static string PackSdk()
    {
        string repository = FindRepositoryRoot();
        string feed = Path.Combine(Path.GetTempPath(), "nobara-sdk-tests", "feed-" + Guid.NewGuid().ToString("N"));
        var start = StartInfo(repository);
        foreach (string argument in new[] { "pack", Path.Combine("tools", "Nobara.Sdk"), "-c", "Release", "-o", feed, "--nologo" })
        {
            start.ArgumentList.Add(argument);
        }

        DotnetResult result = Run(start);
        Assert.True(result.ExitCode == 0, "Packing the SDK failed:" + Environment.NewLine + result.Output);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(feed, recursive: true);
        return feed;
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

/// <summary>Properties and items printed by <c>dotnet msbuild -getProperty -getItem</c>.</summary>
internal sealed class Evaluation(JsonElement root)
{
    /// <summary>Gets a property value, or an empty string.</summary>
    public string Property(string name) => root.GetProperty("Properties").GetProperty(name).GetString() ?? string.Empty;

    /// <summary>Gets the items of a type, as identity and metadata.</summary>
    public IReadOnlyList<JsonElement> Items(string type) =>
        root.GetProperty("Items").TryGetProperty(type, out JsonElement items) ? items.EnumerateArray().ToList() : [];
}
