// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Physics.Tests.Support;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Distribution;
using Nobara.ResourceCompiler.Manifests;

namespace Nobara.Physics.Tests;

/// <summary>The registry of backends, the choice of the backend, and the check of a distribution without backend.</summary>
public sealed class BackendTests : IDisposable
{
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TakesTheFirstBackendRegisteredByDefault()
    {
        _harness.Backends.Add(new TestPhysicsBackend("other"));
        using Engine engine = _harness.Start();

        Assert.Equal("test", _harness.Physics.BackendName);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Physics backend 'test' chosen; 8 substeps and 1 velocity iterations per step."));
    }

    [Fact]
    public void TakesTheBackendTheKeyNamesWithoutCase()
    {
        _harness.Backends.Add(new TestPhysicsBackend("other"));
        _harness.Settings[PhysicsSettings.Backend] = "OTHER";
        using Engine engine = _harness.Start();

        Assert.Equal("other", _harness.Physics.BackendName);
    }

    [Fact]
    public void FallsBackToTheFirstBackendWithAWarningForAnUnknownName()
    {
        _harness.Backends.Add(new TestPhysicsBackend("other"));
        _harness.Settings[PhysicsSettings.Backend] = "jolt";
        using Engine engine = _harness.Start();

        Assert.Equal("test", _harness.Physics.BackendName);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'jolt'") && e.Message.Contains("test, other"));
    }

    [Fact]
    public void RefusesTwoBackendsOfTheSameName()
    {
        _harness.Backends.Add(new TestPhysicsBackend("TEST"));

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Start());

        Assert.Contains("both register a physics backend named 'TEST'", stop.ToString());
    }

    [Fact]
    public void FailsToStartWithoutABackend()
    {
        _harness.Backends.Clear();

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Start());

        Assert.Contains("No physics backend is registered", stop.ToString());
    }

    [Fact]
    public void RefusesABackendRegisteredAfterTheStart()
    {
        using Engine engine = _harness.Start();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => _harness.Probe.Backends.Register(_harness.Probe.Context, new TestPhysicsBackend("late")));

        Assert.Contains("after nPhysics has started", error.Message);
    }

    [Fact]
    public void ADistributionWithoutABackendFailsToBuild()
    {
        string[] modules = ["nGame/Nobara.Game", "nContext/Nobara.Context", "nEntity/Nobara.Entity", "nItems/Nobara.Items", "nBlocks/Nobara.Blocks", "nVoxel/Nobara.Voxel", "nPhysics/Nobara.Physics"];
        var manifests = modules.Select(ReadManifest).ToList();

        DistributionCheckResult without = Check(manifests);
        Diagnostic[] errors = [.. without.Diagnostics.Where(d => d.Code == "NRC211")];
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, e => e.Message.Contains("module 'nobara.physics' requires a backend on the client side"));
        Assert.Contains(errors, e => e.Message.Contains("module 'nobara.physics' requires a backend on the server side"));

        manifests.Add(ParseManifest(
            "<NobaraModule format=\"1\"><Id>example.physics</Id><Version>1.0.0</Version><Level>platform</Level><Side>both</Side>"
            + "<Dependencies><Module id=\"nobara.physics\" version=\"^1.0\" /></Dependencies><BackendOf module=\"nobara.physics\" /></NobaraModule>",
            "example.physics"));
        DistributionCheckResult with = Check(manifests);
        Assert.True(with.Success, string.Join(Environment.NewLine, with.Diagnostics));
    }

    private static DistributionCheckResult Check(IReadOnlyList<ModuleManifest> modules)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-physics-distribution");
        var diagnostics = new DiagnosticBag();
        string entries = string.Concat(modules.Select(m => $"<Package id=\"{m.Id}\" version=\">= 0.0\" />"));
        ProjectManifest project = ProjectManifestReader.Read(
            $"<NobaraProject format=\"1\"><Id>example</Id><Name>Example</Name><Version>1.0.0</Version><Modules>{entries}</Modules></NobaraProject>",
            Path.Combine(root, "project.nobara"),
            diagnostics)!;
        EngineManifest engine = EngineManifestReader.Read("<NobaraEngine format=\"1\"></NobaraEngine>", Path.Combine(root, "engine.nobara"), diagnostics)!;
        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items));
        return DistributionChecker.Check(new DistributionInput { Project = project, Engine = engine, Modules = modules });
    }

    private static ModuleManifest ReadManifest(string project)
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Nobara.slnx")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("The root of the repository is not found.");
        }

        string path = Path.Combine(directory, "src", project, "module.nobara");
        return ParseManifest(File.ReadAllText(path), path);
    }

    private static ModuleManifest ParseManifest(string text, string path)
    {
        var diagnostics = new DiagnosticBag();
        ModuleManifest? manifest = ModuleManifestReader.Read(text, path, diagnostics);
        Assert.True(manifest is not null, string.Join(Environment.NewLine, diagnostics.Items));
        return manifest;
    }
}
