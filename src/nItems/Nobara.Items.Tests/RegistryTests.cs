// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Items.Tests.Support;
using static Nobara.Items.Tests.Support.ItemsHarness;

namespace Nobara.Items.Tests;

public sealed class RegistryTests : IDisposable
{
    private const string Food = """
        <Item>
          <Name>Steak</Name>
          <Content>
            <MaxStack>16</MaxStack>
            <Visual><Sprite>texture://Example/Art/Steak</Sprite></Visual>
          </Content>
          <Properties><Weight>2</Weight></Properties>
          <Tags><Tag>#example.meat</Tag></Tags>
        </Item>
        <Item>
          <Name>Apple</Name>
          <Content><DisplayName>item.food.apple</DisplayName></Content>
        </Item>
        <Item>
          <Name>Sword</Name>
          <Content><Visual><Model>mesh:example.art.sword</Model></Visual></Content>
        </Item>
        <Tag registry="item">
          <Name>Edible</Name>
          <Content>
            <Member>#example.meat</Member>
            <Member>item:example.food.apple</Member>
          </Content>
        </Tag>
        """;

    private readonly ItemsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ItemsAreLoadedAndResolvedByIdentifier()
    {
        IItemRegistry registry = null!;
        _harness.Module("example.food", Nof("Example/Food", Food), p => p.OnStart = probe => registry = probe.Registry);
        using Engine engine = _harness.Start();

        Assert.True(registry.IsFrozen);
        Assert.Equal(["item:example.food.apple", "item:example.food.steak", "item:example.food.sword"], registry.Items.Select(i => i.ObjectId));
        ItemType steak = registry.Get("item:example.food.steak");
        Assert.Equal(1, steak.Index);
        Assert.Same(steak, registry.GetByIndex(1));
        Assert.Equal(16, steak.MaxStack);
        Assert.Equal("item.example.food.steak", steak.DisplayName);
        Assert.Equal(new ItemVisual(ItemVisualKind.Sprite, "texture://Example/Art/Steak"), steak.Visual);
        Assert.Equal("example.food", steak.Data.ModuleId);
        Assert.EndsWith("food.nof", steak.Source.File, StringComparison.Ordinal);
        Assert.Equal(string.Empty, steak.DerivedFrom);
        Assert.Equal("2", steak.Data.Properties.Single().Value.Value!.Text);

        ItemType apple = registry.Get("item:example.food.apple");
        Assert.Equal(64, apple.MaxStack);
        Assert.Equal("item.food.apple", apple.DisplayName);
        Assert.Same(ItemVisual.None, apple.Visual);
        Assert.Equal(ItemVisualKind.Model, registry.Get("item:example.food.sword").Visual.Kind);
        Assert.Equal(64, registry.DefaultMaxStack);

        Assert.False(registry.TryGet("item:example.food.bread", out _));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("item:example.food.bread"));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetByIndex(3));
    }

    [Fact]
    public void TheDistributionChangesTheDefaultStackSize()
    {
        IItemRegistry registry = null!;
        _harness.DefaultMaxStack = 99;
        _harness.Module("example.food", Nof("Example/Food", Food), p => p.OnStart = probe => registry = probe.Registry);
        using Engine engine = _harness.Start();

        Assert.Equal(99, registry.DefaultMaxStack);
        Assert.Equal(99, registry.Get("item:example.food.apple").MaxStack);
        Assert.Equal(16, registry.Get("item:example.food.steak").MaxStack);
    }

    [Fact]
    public void TagsBecomeSetsOfItems()
    {
        IItemRegistry registry = null!;
        _harness.Module("example.food", Nof("Example/Food", Food), p => p.OnStart = probe => registry = probe.Registry);
        using Engine engine = _harness.Start();

        Assert.Equal(["#item:example.food.edible", "#item:example.meat"], registry.Tags.Select(t => t.Id));
        Assert.True(registry.TryGetTag("#example.food.edible", out ItemTag? food));
        Assert.True(registry.TryGetTag("#item:example.meat", out ItemTag? meat));
        ItemType steak = registry.Get("item:example.food.steak");
        ItemType apple = registry.Get("item:example.food.apple");
        ItemType sword = registry.Get("item:example.food.sword");
        Assert.True(steak.HasTag(meat));
        Assert.False(apple.HasTag(meat));
        Assert.True(food.Contains(steak));
        Assert.True(food.Contains(apple));
        Assert.False(food.Contains(sword));
        Assert.Equal([steak, apple], food.Members);
        Assert.False(registry.TryGetTag("#example.wood", out _));
        Assert.Throws<ArgumentException>(() => registry.TryGetTag("#block:example.meat", out _));
        Assert.Throws<ArgumentException>(() => registry.TryGetTag("example.meat", out _));
    }

    [Fact]
    public void AnItemDerivedFromAnotherResourceIsAnItemLikeAnother()
    {
        IItemRegistry registry = null!;
        ItemComponentType<int>? socket = null;
        _harness.Module("example.gems", Nof("Example/Gems", "<Gem><Name>Ruby</Name><Content><Hardness>7</Hardness><CreateItem/></Content></Gem>"), p =>
        {
            p.OnInitialize = probe => socket = probe.Registry.RegisterComponent(probe.Context, "example.gems.socket", v => v.GetInt("Hardness"), EqualityComparer<int>.Default);
            p.OnStart = probe => registry = probe.Registry;
        });
        using Engine engine = _harness.Start();

        ItemType ruby = registry.Get("item:example.gems.ruby");
        Assert.Equal("gem:example.gems.ruby", ruby.DerivedFrom);
        Assert.Equal(8, ruby.MaxStack);
        Assert.Same(ItemVisual.None, ruby.Visual);
        Assert.Equal(7, ruby.Components.Get(socket!));
    }

    [Fact]
    public void TheRegistryIsReadOnceFrozenAndRefusesLateRegistrations()
    {
        Exception? early = null;
        Exception? lateComponent = null;
        Exception? lateKind = null;
        _harness.Module("example.food", Nof("Example/Food", Food), p =>
        {
            p.OnInitialize = probe =>
            {
                Assert.False(probe.Registry.IsFrozen);
                early = Record.Exception(() => probe.Registry.Get("item:example.food.steak"));
            };
            p.OnStart = probe =>
            {
                lateComponent = Record.Exception(() => probe.Registry.RegisterComponent(probe.Context, "example.food.late", _ => 1, EqualityComparer<int>.Default));
                lateKind = Record.Exception(() => probe.Usage.DeclareInteraction(probe.Context, "example.food.late"));
            };
        });
        using Engine engine = _harness.Start();

        Assert.IsType<InvalidOperationException>(early);
        Assert.IsType<InvalidOperationException>(lateComponent);
        Assert.IsType<InvalidOperationException>(lateKind);
        Assert.Contains("froze", lateComponent!.Message, StringComparison.Ordinal);
    }
}
