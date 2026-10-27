// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core.Logging;
using Nobara.Core.Resources;
using Nobara.Core.Tests.Schemas;
using Nobara.Core.Tests.Support;
using Nobara.ResourceCompiler.Nof;
using static Nobara.Core.Tests.Support.TestResources;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>Resources replaced while the game runs, in development mode.</summary>
public sealed class ReloadTests : IDisposable
{
    private const string Blocks = "example.blocks";

    private readonly TestDistribution _distribution = new() { DevelopmentMode = true };
    private readonly List<ResourceReload> _reloads = [];
    private CompiledModule _blocks = null!;

    public void Dispose() => _distribution.Dispose();

    private static string Dirt(int hardness, string extra = "") => $"""
        <Block><Name>Dirt</Name><Content><DisplayName>Dirt</DisplayName><Hardness>{hardness}</Hardness></Content><Tags><Tag>#example.soft</Tag></Tags></Block>
        <Block><Name>Stone</Name><Content><DisplayName>Stone</DisplayName><Hardness>30</Hardness></Content></Block>
        {extra}
        """;

    private static F.ResourceBundle BlocksBundle(string nof) => Compile(Blocks, Nof("Example/Blocks", nof));

    // The module that defines blocks; it reloads them in place, or refuses new ones.
    private void WithBlocks(string nof, Action<ModuleContext, ResourceReload>? reload = null, bool handler = true)
    {
        F.ResourceBundle bundle = BlocksBundle(nof);
        _blocks = Dependency(Blocks, bundle);
        _distribution.Module(Blocks, behavior: m => m.OnRegister = c =>
        {
            c.Resources.RegisterDecoder("block", data => Block.Parser.ParseFrom(data.Content.Span));
            if (handler)
            {
                c.Resources.RegisterReloadHandler("block", r =>
                {
                    _reloads.Add(r);
                    reload?.Invoke(c, r);
                });
            }
        });
        _distribution.Data(Blocks, bundle, TestResources.Schemas);
    }

    private static int Hardness(ModuleContext context, string name) => context.Resources.Get<Block>("block:example.blocks." + name).Hardness;

    [Fact]
    public void Applies_a_changed_value_at_the_next_frame()
    {
        int hardness = 0;
        WithBlocks(Dirt(15), (c, r) => hardness = Hardness(c, "dirt"));
        using Engine engine = _distribution.Load();
        engine.MainContext.Start();
        engine.MainContext.RunFrame();

        engine.ReloadResources(Blocks, BlocksBundle(Dirt(20)).ToByteArray());
        Assert.Empty(_reloads);
        engine.MainContext.RunFrame();

        ResourceReload reload = Assert.Single(_reloads);
        Assert.Equal("block", reload.Type);
        Assert.Equal(["block:example.blocks.dirt"], reload.Changed.Select(r => r.ObjectId));
        Assert.Empty(reload.Added);
        Assert.Empty(reload.Removed);
        Assert.Equal(20, hardness);
        Assert.Contains(_distribution.Log.Entries, e => e.Message.StartsWith("Reloaded resources of type 'block': 0 added, 1 changed", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_added_and_removed_resources_and_changed_tags()
    {
        WithBlocks(Dirt(15));
        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        string sand = "<Block><Name>Sand</Name><Content><DisplayName>Sand</DisplayName><Hardness>5</Hardness></Content><Tags><Tag>#example.soft</Tag></Tags></Block>";
        engine.ReloadResources(Blocks, BlocksBundle(sand + "<Block><Name>Stone</Name><Content><DisplayName>Stone</DisplayName><Hardness>30</Hardness></Content></Block>").ToByteArray());
        engine.MainContext.RunFrame();

        ResourceReload reload = Assert.Single(_reloads);
        Assert.Equal(["block:example.blocks.sand"], reload.Added.Select(r => r.ObjectId));
        Assert.Equal(["block:example.blocks.dirt"], reload.Removed);
        Assert.Equal(["#block:example.soft"], reload.ChangedTags);
    }

    [Fact]
    public void Logs_the_changes_that_the_module_refuses()
    {
        WithBlocks(Dirt(15), (c, r) =>
        {
            foreach (ResourceData added in r.Added)
            {
                r.Refuse(added.ObjectId, "a new block needs a restart of the context");
            }
        });
        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        engine.ReloadResources(Blocks, BlocksBundle(Dirt(15, "<Block><Name>Sand</Name><Content><DisplayName>Sand</DisplayName><Hardness>5</Hardness></Content></Block>")).ToByteArray());
        engine.MainContext.RunFrame();

        LogEntry warning = Assert.Single(_distribution.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(Blocks, warning.Source);
        Assert.Equal("The change of 'block:example.blocks.sand' is not applied while the game runs: a new block needs a restart of the context", warning.Message);
    }

    [Fact]
    public void Warns_when_the_module_that_defines_the_type_does_not_reload_it()
    {
        WithBlocks(Dirt(15), handler: false);
        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        engine.ReloadResources(Blocks, BlocksBundle(Dirt(20)).ToByteArray());
        engine.MainContext.RunFrame();

        LogEntry warning = Assert.Single(_distribution.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("Resources of type 'block' changed (0 added, 1 changed, 0 removed, 0 tags changed), but module 'example.blocks' does not reload them: restart the context to apply the change.", warning.Message);
    }

    [Fact]
    public void Applies_the_overrides_again_on_the_reloaded_resources()
    {
        int hardness = 0;
        string display = string.Empty;
        WithBlocks(Dirt(15), (c, r) =>
        {
            hardness = Hardness(c, "dirt");
            display = c.Resources.Get<Block>("block:example.blocks.dirt").DisplayName;
        });
        F.ResourceBundle mod = Compile("example.mod", Nof("Example/Blocks", """<Block override="true"><Name>Dirt</Name><Content><Hardness>99</Hardness></Content></Block>"""), [_blocks], ownSchemas: false);
        _distribution.Module("example.mod", m => m.Dependencies.Add(new F.Dependency { Id = Blocks, Range = "^1.0" }));
        _distribution.Data("example.mod", mod);
        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        engine.ReloadResources(Blocks, BlocksBundle(Dirt(15).Replace("<DisplayName>Dirt</DisplayName>", "<DisplayName>Mud</DisplayName>", StringComparison.Ordinal)).ToByteArray());
        engine.MainContext.RunFrame();

        Assert.Equal(99, hardness);
        Assert.Equal("Mud", display);
    }

    [Fact]
    public void Keeps_the_previous_resources_when_the_new_ones_do_not_load()
    {
        WithBlocks(Dirt(15));
        F.ResourceBundle mod = Compile("example.mod", Nof("Example/Blocks", """<Block override="true"><Name>Stone</Name><Content><Hardness>99</Hardness></Content></Block>"""), [_blocks], ownSchemas: false);
        _distribution.Module("example.mod", m => m.Dependencies.Add(new F.Dependency { Id = Blocks, Range = "^1.0" }));
        _distribution.Data("example.mod", mod);
        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        // The override of the other module no longer finds its target.
        engine.ReloadResources(Blocks, BlocksBundle("<Block><Name>Dirt</Name><Content><DisplayName>Dirt</DisplayName><Hardness>1</Hardness></Content></Block>").ToByteArray());
        engine.MainContext.RunFrame();

        Assert.Empty(_reloads);
        LogEntry error = Assert.Single(_distribution.Log.Entries, e => e.Level == LogLevel.Error);
        Assert.StartsWith("The resources of example.blocks were not reloaded:", error.Message, StringComparison.Ordinal);
        Assert.Contains("block:example.blocks.stone", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reloads_only_in_development_mode()
    {
        _distribution.DevelopmentMode = false;
        WithBlocks(Dirt(15));
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<InvalidOperationException>(() => engine.ReloadResources(Blocks, BlocksBundle(Dirt(20)).ToByteArray()));
        Assert.Contains("development mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_module_that_is_not_loaded()
    {
        WithBlocks(Dirt(15));
        using Engine engine = _distribution.Load();

        Assert.Throws<ArgumentException>(() => engine.ReloadResources("example.missing", Array.Empty<byte>()));
    }
}
