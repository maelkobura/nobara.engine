// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Blocks.Tests.Support;
using Nobara.Core;
using Nobara.Core.Logging;
using static Nobara.Blocks.Tests.Support.BlocksHarness;

namespace Nobara.Blocks.Tests;

public sealed class ReloadTests : IDisposable
{
    private const string Module = "example.blocks";

    private readonly BlocksHarness _harness = new() { DevelopmentMode = true };
    private readonly List<string> _calls = [];
    private Probe _probe = null!;

    public ReloadTests()
    {
        _harness.Module(Module, Blocks(hardness: "2"), p => p.OnInitialize = probe =>
        {
            _probe = probe;
            Assert.True(probe.Blocks.TryGetTag("#example.lights", out BlockTag? lights));
            probe.Hooks.Subscribe(probe.Context, probe.Hooks.RandomTick, lights, new UnitRecorder<BlockTick>(_calls, "glow"));
        });
    }

    public void Dispose() => _harness.Dispose();

    private static string Blocks(string hardness, string states = "<States><Bool name=\"lit\"/></States>", string lampTags = "<Tags><Tag>#example.lights</Tag></Tags>", string stoneTags = "", string extra = "") => Nof("Example/Blocks", $"""
        <Block><Name>Lamp</Name><Content><Hardness>{hardness}</Hardness>{states}<Variants><Variant when="lit=true" opacity="cutout"/></Variants></Content>{lampTags}</Block>
        <Block><Name>Stone</Name><Content/>{stoneTags}</Block>
        {extra}
        """);

    private void Reload(Engine engine, string nof)
    {
        engine.ReloadResources(Module, Bundle(Module, nof).ToByteArray());
        _harness.Tick(engine);
    }

    private bool Refused(string objectId, string text) =>
        _harness.Log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(objectId, StringComparison.Ordinal) && e.Message.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void ANativeFieldChangesInPlace()
    {
        using Engine engine = _harness.Start();
        BlockType lamp = _probe.Blocks.Get("block:example.blocks.lamp");
        uint lit = _probe.Blocks.With(lamp.DefaultState, lamp.GetProperty("lit"), 1);

        Reload(engine, Blocks(hardness: "7").Replace("opacity=\"cutout\"", "opacity=\"translucent\"", StringComparison.Ordinal));

        Assert.Same(lamp, _probe.Blocks.Get("block:example.blocks.lamp"));
        Assert.Equal(7f, lamp.Hardness);
        Assert.Equal(7f, _probe.Blocks.GetHardness(lamp.DefaultState));
        Assert.Equal(BlockOpacity.Translucent, _probe.Blocks.GetOpacity(lit));
    }

    [Fact]
    public void AChangeOfTheStatesIsRefused()
    {
        using Engine engine = _harness.Start();
        BlockType lamp = _probe.Blocks.Get("block:example.blocks.lamp");

        Reload(engine, Blocks(hardness: "7", states: "<States><Bool name=\"lit\"/><Bool name=\"powered\"/></States>"));

        Assert.Equal(2, lamp.StateCount);
        Assert.Equal(2f, lamp.Hardness);
        Assert.True(Refused("block:example.blocks.lamp", "property 'powered' added"));
    }

    [Fact]
    public void AnAddedTypeIsRefusedAndNeedsARestart()
    {
        using Engine engine = _harness.Start();

        Reload(engine, Blocks(hardness: "2", extra: "<Block><Name>Glass</Name><Content/></Block>"));

        Assert.False(_probe.Blocks.TryGet("block:example.blocks.glass", out _));
        Assert.True(Refused("block:example.blocks.glass", "restart"));
    }

    [Fact]
    public void TagsChangeInPlaceAndTheHooksFollow()
    {
        using Engine engine = _harness.Start();
        Assert.True(_probe.Blocks.TryGetTag("#example.lights", out BlockTag? lights));
        BlockType lamp = _probe.Blocks.Get("block:example.blocks.lamp");
        BlockType stone = _probe.Blocks.Get("block:example.blocks.stone");

        Reload(engine, Blocks(hardness: "2", lampTags: string.Empty, stoneTags: "<Tags><Tag>#example.lights</Tag></Tags>"));
        _probe.Hooks.Fire(_probe.Hooks.RandomTick, lamp.DefaultState, new BlockTick(default, lamp.DefaultState));
        _probe.Hooks.Fire(_probe.Hooks.RandomTick, stone.DefaultState, new BlockTick(default, stone.DefaultState));

        Assert.Equal([stone], lights.Members);
        Assert.False(_probe.Hooks.HasSubscribers(_probe.Hooks.RandomTick, lamp.DefaultState));
        Assert.Equal(["glow"], _calls);
    }
}
