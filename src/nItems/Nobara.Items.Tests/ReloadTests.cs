// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Game;
using Nobara.Items.Tests.Support;
using static Nobara.Items.Tests.Support.ItemsHarness;

namespace Nobara.Items.Tests;

public sealed class ReloadTests : IDisposable
{
    private const string Module = "example.food";

    private readonly ItemsHarness _harness = new() { DevelopmentMode = true };
    private readonly List<string> _calls = [];
    private Probe _probe = null!;
    private ItemComponentType<int> _edible = null!;

    public ReloadTests()
    {
        _harness.Module(Module, Food(stack: 16, food: 3), p => p.OnInitialize = probe =>
        {
            _probe = probe;
            _edible = probe.Registry.RegisterComponent(probe.Context, "example.food.edible", v => v.GetInt("Food"), EqualityComparer<int>.Default);
            probe.Usage.AddHandler(probe.Context, _edible, probe.Usage.Use, new Recorder<int>(_calls, "eat", UseResult.Handled));
        });
    }

    public void Dispose() => _harness.Dispose();

    private static string Food(int stack, string food, string extra = "", string tags = "<Tags><Tag>#example.meat</Tag></Tags>", string appleTags = "") => Nof("Example/Food", $"""
        <Item><Name>Steak</Name><Content>
          <MaxStack>{stack}</MaxStack>
          <Component ref="example.food.edible"><Food>{food}</Food></Component>
        </Content>{tags}</Item>
        <Item><Name>Apple</Name><Content/>{appleTags}</Item>
        {extra}
        """);

    private static string Food(int stack, int food) => Food(stack, food.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void Reload(Engine engine, string nof)
    {
        engine.ReloadResources(Module, Bundle(Module, nof).ToByteArray());
        _harness.Tick(engine);
    }

    [Fact]
    public void ANativeFieldAndAComponentValueChangeInPlace()
    {
        using Engine engine = _harness.Start();
        ItemType steak = _probe.Registry.Get("item:example.food.steak");
        ItemComponents before = steak.Components;

        Reload(engine, Food(stack: 8, food: 5));

        Assert.Same(steak, _probe.Registry.Get("item:example.food.steak"));
        Assert.Equal(8, steak.MaxStack);
        Assert.Equal(5, steak.Components.Get(_edible));
        Assert.Equal(3, before.Get(_edible));
        _probe.Usage.UseItem(new ItemActor(1, DimensionId.Default), new ItemStackView(steak), _probe.Usage.Use, UseTarget.None);
        Assert.Equal(["eat:5"], _calls);
    }

    [Fact]
    public void TagsChangeInPlace()
    {
        using Engine engine = _harness.Start();
        Assert.True(_probe.Registry.TryGetTag("#example.meat", out ItemTag? meat));
        ItemType steak = _probe.Registry.Get("item:example.food.steak");
        ItemType apple = _probe.Registry.Get("item:example.food.apple");

        Reload(engine, Food(16, "3", tags: string.Empty, appleTags: "<Tags><Tag>#example.meat</Tag></Tags>"));

        Assert.False(steak.HasTag(meat));
        Assert.True(apple.HasTag(meat));
        Assert.Equal([apple], meat.Members);
    }

    [Fact]
    public void AnAddedItemIsReportedAndNeedsARestart()
    {
        using Engine engine = _harness.Start();

        Reload(engine, Food(16, "3", extra: "<Item><Name>Bread</Name><Content/></Item>"));

        Assert.False(_probe.Registry.TryGet("item:example.food.bread", out _));
        Assert.Equal(2, _probe.Registry.Items.Count);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("item:example.food.bread", StringComparison.Ordinal) && e.Message.Contains("restart", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInvalidValueIsRefusedAndTheItemKeepsItsValues()
    {
        using Engine engine = _harness.Start();
        ItemType steak = _probe.Registry.Get("item:example.food.steak");

        Reload(engine, Food(8, "lots"));

        Assert.Equal(16, steak.MaxStack);
        Assert.Equal(3, steak.Components.Get(_edible));
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("<Food> is 'lots'", StringComparison.Ordinal) && e.Message.Contains("food.nof(", StringComparison.Ordinal));
    }
}
