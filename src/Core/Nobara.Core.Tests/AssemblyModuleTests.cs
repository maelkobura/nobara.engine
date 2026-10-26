// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.Loader;
using System.Security.Cryptography;
using Google.Protobuf;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>Modules built with the SDK, loaded from their assemblies.</summary>
public sealed class AssemblyModuleTests : IClassFixture<SampleModules>, IDisposable
{
    private readonly SampleModules _samples;
    private readonly MemoryLogSink _log = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "nobara-core-tests", Guid.NewGuid().ToString("N"));

    public AssemblyModuleTests(SampleModules samples)
    {
        _samples = samples;
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private Engine Load(string install, byte[] distribution) => Engine.Load(new EngineOptions
    {
        Side = Side.Client,
        InstallDirectory = install,
        DistributionManifest = distribution,
        Paths = EnginePaths.At(_data),
        LogToConsole = false,
        LogToFile = false,
        LogSinks = [_log],
        MinimumLogLevel = LogLevel.Debug,
    });

    private static int LoadedCopies(string assemblyName) =>
        AssemblyLoadContext.All.SelectMany(c => c.Assemblies).Count(a => a.GetName().Name == assemblyName);

    [Fact]
    public void Starts_and_stops_three_modules_built_with_the_sdk()
    {
        (string install, byte[] distribution) = _samples.Install(["Example.Extra", "Example.Base", "Example.Feature"]);

        using Engine engine = Load(install, distribution);
        engine.MainContext.Start();
        engine.MainContext.Stop();

        Assert.Equal(["example.base", "example.feature", "example.extra"], engine.Modules.Select(m => m.Id));
        Assert.Contains(_log.Entries, e => e.Source == "example.feature" && e.Phase == "Start" && e.Message == "Hello, feature");
        Assert.Contains(_log.Entries, e => e.Source == "example.extra" && e.Phase == "Stop" && e.Message == "extra stopped");

        // The modules live in the load context of the modules, the core in the default one.
        var context = AssemblyLoadContext.GetLoadContext(typeof(Engine).Assembly);
        Assert.Same(AssemblyLoadContext.Default, context);
        Assert.Contains(AssemblyLoadContext.All, c => c.Name == "Nobara modules" && c.Assemblies.Any(a => a.GetName().Name == "Example.Feature"));
    }

    [Fact]
    public void Never_loads_a_module_refused_for_a_conflict()
    {
        (string install, byte[] distribution) = _samples.Install(["Example.Base", "Example.Rogue"]);
        int baseCopies = LoadedCopies("Example.Base");

        var exception = Assert.Throws<ModuleCompositionException>(() => Load(install, distribution));

        CompositionProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(CompositionProblemKind.Conflict, problem.Kind);
        Assert.Equal("Module 'example.rogue' conflicts with 'example.base', and both run on both sides.", problem.Message);

        // No code of any module ran: their assemblies were read, never loaded.
        Assert.Equal(0, LoadedCopies("Example.Rogue"));
        Assert.Equal(baseCopies, LoadedCopies("Example.Base"));
        Assert.Null(AppContext.GetData("Example.Rogue.Ran"));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("example.rogue"));
    }

    [Fact]
    public void Refuses_an_assembly_that_differs_from_the_distribution()
    {
        (string install, byte[] distribution) = _samples.Install(["Example.Base"], d => d.Modules[0].Hash = ByteString.CopyFrom(new byte[32]));

        var exception = Assert.Throws<ModuleCompositionException>(() => Load(install, distribution));

        CompositionProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(CompositionProblemKind.IntegrityMismatch, problem.Kind);
        Assert.Contains("'example.base'", problem.Message);
        Assert.Contains("Example.Base.dll", problem.Message);
    }

    [Fact]
    public void Refuses_a_module_whose_assembly_is_missing()
    {
        (string install, byte[] distribution) = _samples.Install(["Example.Base"], d => d.Modules.Add(new F.DistributionModule
        {
            Id = "example.feature",
            Version = "1.0.0",
            Hash = ByteString.CopyFrom(new byte[32]),
        }));

        var exception = Assert.Throws<ModuleCompositionException>(() => Load(install, distribution));

        CompositionProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(CompositionProblemKind.MissingAssembly, problem.Kind);
        Assert.Equal("example.feature", problem.ModuleId);
        Assert.Contains("Module 'example.feature': its assembly is not in system/ or game/", problem.Message);
    }

    [Fact]
    public void Refuses_an_assembly_without_a_module_manifest()
    {
        (string install, byte[] distribution) = _samples.Install([]);
        string fake = Path.Combine(install, "game", "example.fake.dll");
        File.Copy(typeof(Assert).Assembly.Location, fake);
        var manifest = F.DistributionManifest.Parser.ParseFrom(distribution);
        manifest.Modules.Add(new F.DistributionModule { Id = "example.fake", Version = "1.0.0", Hash = ByteString.CopyFrom(SHA256.HashData(File.ReadAllBytes(fake))) });

        var exception = Assert.Throws<ModuleCompositionException>(() => Load(install, manifest.ToByteArray()));

        CompositionProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(CompositionProblemKind.InvalidManifest, problem.Kind);
        Assert.Contains("has no module manifest", problem.Message);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_an_assembly()
    {
        (string install, byte[] distribution) = _samples.Install([]);
        string fake = Path.Combine(install, "game", "example.fake.dll");
        File.WriteAllText(fake, "not an assembly");
        var manifest = F.DistributionManifest.Parser.ParseFrom(distribution);
        manifest.Modules.Add(new F.DistributionModule { Id = "example.fake", Version = "1.0.0", Hash = ByteString.CopyFrom(SHA256.HashData(File.ReadAllBytes(fake))) });

        var exception = Assert.Throws<ModuleCompositionException>(() => Load(install, manifest.ToByteArray()));

        CompositionProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(CompositionProblemKind.InvalidManifest, problem.Kind);
        Assert.Contains("Module 'example.fake'", problem.Message);
    }

    [Fact]
    public void Blames_the_module_whose_service_threw_and_disables_its_dependents()
    {
        (string install, byte[] distribution) = _samples.Install(["Example.Base", "Example.Feature", "Example.Extra"], d =>
        {
            foreach (string id in new[] { "example.base", "example.feature", "example.extra" })
            {
                d.ErrorPolicy.Modules.Add(new F.ModulePolicy { Module = id, Policy = F.Policy.Disable });
            }
        });
        AppContext.SetSwitch("Example.Base.Fail", true);
        try
        {
            using Engine engine = Load(install, distribution);
            engine.MainContext.Start();

            Assert.Equal(ContextState.Running, engine.MainContext.State);
            Assert.False(engine.MainContext.IsModuleActive("example.base"));
            Assert.False(engine.MainContext.IsModuleActive("example.feature"));
            Assert.False(engine.MainContext.IsModuleActive("example.extra"));
            LogEntry error = Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
            Assert.Equal("example.base", error.Source);
            Assert.Contains("(called by 'example.feature')", error.Message);
            Assert.Equal("The greeter failed.", error.Exception?.Message);
        }
        finally
        {
            AppContext.SetSwitch("Example.Base.Fail", false);
        }
    }
}
