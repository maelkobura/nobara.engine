// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.ResourceCompiler;
using Nobara.ResourceCompiler.Distribution;
using Nobara.ResourceCompiler.Manifests;
using Nobara.Voxel;

namespace Nobara.Lighting.Tests;

public sealed class ModuleTests
{
    [Fact]
    public void TheSourcesAreTheStatesThatEmitWithTheirVariants()
    {
        const string blocks = """
            <Block><Name>Lantern</Name><Content><Shape>shape:nobara.cube</Shape>
              <States><Bool name="lit" default="false"/></States>
              <Variants><Variant when="lit=true"><Properties><LightEmission>12</LightEmission></Properties></Variant></Variants>
            </Content></Block>
            <Block><Name>Fence</Name><Content><Shape>shape:nobara.model</Shape></Content></Block>
            <Block><Name>Glass</Name><Content><Shape>shape:nobara.cube</Shape><Opacity>translucent</Opacity></Content></Block>
            <Block><Name>Stone</Name><Content><Shape>shape:nobara.cube</Shape></Content></Block>
            """;
        using var harness = new LightingHarness(blocks: blocks);
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        BlockType lantern = probe.Blocks.Get("block:example.light.lantern");
        uint lit = probe.Blocks.With(lantern.DefaultState, lantern.GetProperty("lit"), 1);

        Assert.Equal([lit], probe.Sources.StaticSources);
        Assert.Equal(new LightColor(12, 12, 12), probe.Sources.GetEmission(lit));
        Assert.True(probe.Sources.GetEmission(lantern.DefaultState).IsNone);
        Assert.False(probe.Sources.IsOpen(probe.State("stone"), BlockFace.Up));
        Assert.True(probe.Sources.IsOpen(probe.State("glass"), BlockFace.Up));
        Assert.True(probe.Sources.IsOpen(probe.State("fence"), BlockFace.Up));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Sources.GetEmission(uint.MaxValue));
    }

    [Theory]
    [InlineData("16")]
    [InlineData("15 15")]
    [InlineData("bright")]
    public void ALevelThatIsNotFromZeroToFifteenStopsTheContext(string emission)
    {
        using var harness = new LightingHarness(blocks: $"<Block><Name>Bad</Name><Content><Shape>shape:nobara.cube</Shape></Content><Properties><LightEmission>{emission}</LightEmission></Properties></Block>");

        EngineStopException stop = Assert.Throws<EngineStopException>(() => harness.Start());
        BlockLoadException error = Assert.IsType<BlockLoadException>(stop.InnerException);
        Assert.Contains("block:example.light.bad", error.Message, StringComparison.Ordinal);
        Assert.Contains("LightEmission", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLightOfASessionIsCountedAndReleasedWhenItEnds()
    {
        using var harness = new LightingHarness();
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        Grid grid = probe.CreateGrid();
        probe.Set(grid, (16, 16, 16, probe.State("torch")), (40, 0, 0, 0));
        harness.Tick(engine);
        Assert.Equal(2, probe.Lighting.Statistics.Chunks);

        probe.Sessions.End();
        harness.RunUntil(engine, () => probe.Sessions.State == SessionState.None);

        Assert.Equal(0, probe.Lighting.Statistics.Chunks);
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Exception is WorldStateLeakException);
    }

    [Fact]
    public void LightKeptAfterTheEndOfASessionIsReportedAsALeak()
    {
        using var harness = new LightingHarness();
        using Engine engine = harness.Start();
        Probe probe = harness.Owner;
        Grid terrain = probe.Terrain;
        probe.Tickets.Add(probe.Loading.AddTicket(probe.Context, terrain, new ChunkCoordinate(0, 0, 0), TicketLevel.Loaded));
        harness.RunUntil(engine, () => probe.Loading.GetStatus(terrain, new ChunkCoordinate(0, 0, 0)).IsLoaded);
        probe.Set(terrain, (16, 16, 16, probe.State("torch")));
        harness.Tick(engine);
        probe.Internal.KeepChunksAtSessionEnd = true;

        // The distribution of the tests stops at the first fault of a module.
        probe.Sessions.End();
        EngineStopException stop = Assert.Throws<EngineStopException>(() => harness.Tick(engine, 2));
        WorldStateLeakException leak = Assert.IsType<WorldStateLeakException>(stop.InnerException);
        Assert.Contains(LightingModule.Id, leak.Message, StringComparison.Ordinal);
        Assert.Contains("'light chunks' is 1", leak.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerTargetMayDisableLightingUnlessAModuleNeedsIt()
    {
        string root = RepositoryRoot();
        var modules = new List<ModuleManifest> { Read(Path.Combine(root, "src", "nLighting", "Nobara.Lighting", "module.nobara")) };
        foreach (string id in new[] { "nobara.voxel", "nobara.blocks", "nobara.game", "nobara.context" })
        {
            modules.Add(Inline($"<NobaraModule format=\"1\"><Id>{id}</Id><Version>1.0.0</Version><Level>platform</Level><Side>both</Side></NobaraModule>", id));
        }

        string build = "<Target name=\"server\"><Platform>win-x64</Platform><Side>server</Side><Disable module=\"nobara.lighting\" /></Target>";
        Assert.True(Check(modules, build, "server").Success);

        modules.Add(Inline("<NobaraModule format=\"1\"><Id>example.spawns</Id><Version>1.0.0</Version><Level>business</Level><Side>both</Side><Dependencies><Module id=\"nobara.lighting\" version=\"^1.0\" /></Dependencies></NobaraModule>", "example.spawns"));
        DistributionCheckResult refused = Check(modules, build, "server");
        Assert.False(refused.Success);
        Assert.Contains(refused.Diagnostics, d => d.Code == "NRC201" && d.Message.Contains("example.spawns", StringComparison.Ordinal) && d.Message.Contains("nobara.lighting", StringComparison.Ordinal));
    }

    private static DistributionCheckResult Check(List<ModuleManifest> modules, string build, string target)
    {
        string root = Path.Combine(Path.GetTempPath(), "nobara-lighting-distribution");
        var diagnostics = new DiagnosticBag();
        string packages = string.Concat(modules.Select(m => $"<Package id=\"{m.Id}\" version=\">= 0.0\" required=\"false\" />"));
        ProjectManifest project = ProjectManifestReader.Read(
            $"<NobaraProject format=\"1\"><Id>example</Id><Name>Example</Name><Version>1.0.0</Version><Modules>{packages}</Modules></NobaraProject>",
            Path.Combine(root, "project.nobara"),
            diagnostics)!;
        EngineManifest engine = EngineManifestReader.Read("<NobaraEngine format=\"1\"></NobaraEngine>", Path.Combine(root, "engine.nobara"), diagnostics)!;
        BuildManifest? manifest = BuildManifestReader.Read($"<NobaraBuild format=\"1\">{build}</NobaraBuild>", Path.Combine(root, "build.nobara"), diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items));
        return DistributionChecker.Check(new DistributionInput { Project = project, Engine = engine, Build = manifest, Target = target, Modules = modules });
    }

    private static ModuleManifest Read(string path) => Inline(File.ReadAllText(path), path);

    private static ModuleManifest Inline(string text, string path)
    {
        var diagnostics = new DiagnosticBag();
        ModuleManifest? manifest = ModuleManifestReader.Read(text, path, diagnostics);
        Assert.True(manifest is not null, string.Join(Environment.NewLine, diagnostics.Items));
        return manifest;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nobara.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
