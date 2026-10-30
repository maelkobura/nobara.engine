// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Game;
using Nobara.Items.Tests.Support;
using static Nobara.Items.Tests.Support.ItemsHarness;
using F = Nobara.Formats;

namespace Nobara.Items.Tests;

public sealed class UsageTests : IDisposable
{
    private const string Items = """
        <Item><Name>Steak</Name><Content>
          <Component ref="example.food.edible"><Food>3</Food></Component>
          <Component ref="example.food.spicy"/>
        </Content></Item>
        <Item><Name>Stone</Name><Content/></Item>
        """;

    private static readonly ItemActor Player = new(42, DimensionId.Default);

    private readonly ItemsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>The module that registers the components; the handlers come from other modules.</summary>
    private sealed class Food
    {
        public ItemComponentType<int> Edible { get; set; } = null!;

        public ItemComponentType<bool> Spicy { get; set; } = null!;

        public IItemUsage Usage { get; set; } = null!;

        public IItemRegistry Registry { get; set; } = null!;
    }

    private Food AddFood()
    {
        var food = new Food();
        _harness.Module("example.food", Nof("Example/Food", Items), p =>
        {
            p.OnInitialize = probe =>
            {
                food.Edible = probe.Registry.RegisterComponent(probe.Context, "example.food.edible", v => v.GetInt("Food"), EqualityComparer<int>.Default);
                food.Spicy = probe.Registry.RegisterComponent(probe.Context, "example.food.spicy", _ => true, EqualityComparer<bool>.Default);
                food.Usage = probe.Usage;
                food.Registry = probe.Registry;
            };
        });
        return food;
    }

    [Fact]
    public void HandlersOfAnotherModuleRunByPriorityUntilOneHandlesTheUse()
    {
        Food food = AddFood();
        var calls = new List<string>();
        _harness.Module("example.hunger", behavior: p => p.OnInitialize = probe =>
        {
            probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Use, new Recorder<int>(calls, "eat", UseResult.Handled), priority: 0);
            probe.Usage.AddHandler(probe.Context, food.Spicy, probe.Usage.Use, new Recorder<bool>(calls, "burn", UseResult.Pass), priority: 10);
            probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Use, new Recorder<int>(calls, "late", UseResult.Handled), priority: -5);
            probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Attack, new Recorder<int>(calls, "throw", UseResult.Handled));
        }, dependsOn: "example.food");
        using Engine engine = _harness.Start();

        ItemType steak = food.Registry.Get("item:example.food.steak");
        UseOutcome outcome = food.Usage.UseItem(Player, new ItemStackView(steak), food.Usage.Use, UseTarget.None);

        Assert.Equal(UseOutcome.Handled, outcome);
        Assert.Equal(["burn:True", "eat:3"], calls);
        Assert.Equal(UseOutcome.NotHandled, food.Usage.UseItem(Player, new ItemStackView(food.Registry.Get("item:example.food.stone")), food.Usage.Use, UseTarget.None));
    }

    [Fact]
    public void AtEqualPriorityHandlersRunInLoadOrderThenByComponentName()
    {
        Food food = AddFood();
        var calls = new List<string>();
        _harness
            .Module("example.hunger", behavior: p => p.OnInitialize = probe =>
                probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Use, new Recorder<int>(calls, "hunger", UseResult.Pass)), dependsOn: "example.food")
            .Module("example.zest", behavior: p => p.OnInitialize = probe =>
            {
                probe.Usage.AddHandler(probe.Context, food.Spicy, probe.Usage.Use, new Recorder<bool>(calls, "zest-spicy", UseResult.Pass));
                probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Use, new Recorder<int>(calls, "zest-edible", UseResult.Pass));
            }, dependsOn: ["example.food", "example.hunger"]);
        using Engine engine = _harness.Start();

        food.Usage.UseItem(Player, new ItemStackView(food.Registry.Get("item:example.food.steak")), food.Usage.Use, UseTarget.None);

        Assert.Equal(["hunger:3", "zest-edible:3", "zest-spicy:True"], calls);
    }

    [Fact]
    public void AStackWithItsOwnComponentsGoesThroughThemAndTheHandlersSeeItsValues()
    {
        Food food = AddFood();
        var calls = new List<string>();
        _harness.Module("example.hunger", behavior: p => p.OnInitialize = probe =>
        {
            probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.UseOnEntity, new Recorder<int>(calls, "feed", UseResult.Pass));
            probe.Usage.AddHandler(probe.Context, food.Spicy, probe.Usage.UseOnEntity, new Recorder<bool>(calls, "burn", UseResult.Pass));
        }, dependsOn: "example.food");
        using Engine engine = _harness.Start();

        ItemType steak = food.Registry.Get("item:example.food.steak");
        ItemType stone = food.Registry.Get("item:example.food.stone");
        food.Usage.UseItem(Player, new ItemStackView(steak, steak.Components.With(food.Edible, 9).Without(food.Spicy)), food.Usage.UseOnEntity, UseTarget.OnEntity(7));
        food.Usage.UseItem(Player, new ItemStackView(stone, stone.Components.With(food.Spicy, false)), food.Usage.UseOnEntity, UseTarget.OnEntity(7));

        Assert.Equal(["feed:9", "burn:False"], calls);
    }

    [Fact]
    public void TheHandlerReceivesTheActorTheKindAndTheTarget()
    {
        Food food = AddFood();
        ItemUse<int> received = default;
        _harness.Module("example.hunger", behavior: p => p.OnInitialize = probe =>
            probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.UseOnBlock, new Callback<int>(use =>
            {
                received = use;
                return UseResult.Handled;
            })), dependsOn: "example.food");
        using Engine engine = _harness.Start();

        ItemType steak = food.Registry.Get("item:example.food.steak");
        food.Usage.UseItem(Player, new ItemStackView(steak), food.Usage.UseOnBlock, UseTarget.OnBlock(5, 1, 2, 3, BlockFace.Up));

        Assert.Equal(Player, received.Actor);
        Assert.Same(steak, received.Stack.Item);
        Assert.Same(food.Usage.UseOnBlock, received.Kind);
        Assert.Equal(UseTargetKind.Block, received.Target.Kind);
        Assert.Equal((5UL, 1, 2, 3, BlockFace.Up), (received.Target.Grid, received.Target.X, received.Target.Y, received.Target.Z, received.Target.Face));
        Assert.Equal(3, received.Value);
        Assert.Equal(0UL, received.Elapsed);
    }

    [Fact]
    public void AModuleThatDoesNotDependOnTheComponentCannotHandleIt()
    {
        Food food = AddFood();
        Exception? refused = null;
        _harness.Module("example.stranger", behavior: p => p.OnInitialize = probe =>
            refused = Record.Exception(() => probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Use, new Recorder<int>([], "x", UseResult.Handled))));
        using Engine engine = _harness.Start();

        Assert.IsType<ArgumentException>(refused);
        Assert.Contains("not one of its dependencies", refused!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposingTheHandleRemovesTheHandlerAndAddingOneLaterRebuildsTheTables()
    {
        Food food = AddFood();
        var calls = new List<string>();
        ItemHandlerHandle handle = null!;
        Probe hunger = null!;
        _harness.Module("example.hunger", behavior: p => p.OnInitialize = probe =>
        {
            hunger = probe;
            handle = probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Use, new Recorder<int>(calls, "eat", UseResult.Handled));
        }, dependsOn: "example.food");
        using Engine engine = _harness.Start();
        var steak = new ItemStackView(food.Registry.Get("item:example.food.steak"));

        handle.Dispose();
        Assert.True(handle.IsRevoked);
        Assert.Equal(UseOutcome.NotHandled, food.Usage.UseItem(Player, steak, food.Usage.Use, UseTarget.None));
        food.Usage.AddHandler(hunger.Context, food.Spicy, food.Usage.Use, new Recorder<bool>(calls, "burn", UseResult.Handled));
        Assert.Equal(UseOutcome.Handled, food.Usage.UseItem(Player, steak, food.Usage.Use, UseTarget.None));

        Assert.Equal(["burn:True"], calls);
    }

    [Fact]
    public void AModuleDeclaresItsOwnKindOfInteraction()
    {
        Food food = AddFood();
        var calls = new List<string>();
        InteractionKind? reload = null;
        Exception? unqualified = null;
        _harness.Module("example.bows", behavior: p => p.OnInitialize = probe =>
        {
            reload = probe.Usage.DeclareInteraction(probe.Context, "example.bows.reload");
            unqualified = Record.Exception(() => probe.Usage.DeclareInteraction(probe.Context, "Reload"));
            probe.Usage.AddHandler(probe.Context, food.Edible, reload, new Recorder<int>(calls, "reload", UseResult.Handled));
        }, dependsOn: "example.food");
        using Engine engine = _harness.Start();

        Assert.Equal(["Use", "UseOnBlock", "UseOnEntity", "Attack", "example.bows.reload"], food.Usage.Kinds.Select(k => k.Name));
        Assert.True(food.Usage.TryGetInteraction("example.bows.reload", out InteractionKind? found));
        Assert.Same(reload, found);
        Assert.Equal("example.bows", found.ModuleId);
        Assert.IsType<ArgumentException>(unqualified);
        Assert.Equal(UseOutcome.Handled, food.Usage.UseItem(Player, new ItemStackView(food.Registry.Get("item:example.food.steak")), reload!, UseTarget.None));
        Assert.Equal(["reload:3"], calls);
    }

    [Fact]
    public void AHandlerThatThrowsFollowsThePolicyOfItsModuleAndTheNextOneRuns()
    {
        Food food = AddFood();
        var calls = new List<string>();
        _harness
            .Module("example.buggy", behavior: p => p.OnInitialize = probe =>
                probe.Usage.AddHandler(probe.Context, food.Spicy, probe.Usage.Use, new Callback<bool>(_ => throw new InvalidOperationException("boom")), priority: 5), policy: F.Policy.Log, dependsOn: "example.food")
            .Module("example.hunger", behavior: p => p.OnInitialize = probe =>
                probe.Usage.AddHandler(probe.Context, food.Edible, probe.Usage.Use, new Recorder<int>(calls, "eat", UseResult.Handled)), dependsOn: "example.food");
        using Engine engine = _harness.Start();

        UseOutcome outcome = food.Usage.UseItem(Player, new ItemStackView(food.Registry.Get("item:example.food.steak")), food.Usage.Use, UseTarget.None);

        Assert.Equal(UseOutcome.Handled, outcome);
        Assert.Equal(["eat:3"], calls);
        Assert.Contains(_harness.Log.Entries, e => e.Message.StartsWith("Module 'example.buggy'", StringComparison.Ordinal) && e.Message.Contains("its policy is Log", StringComparison.Ordinal));
    }

    [Fact]
    public void ItemsAreUsedOnceTheRegistryIsFrozen()
    {
        Food food = AddFood();
        Exception? early = null;
        _harness.Module("example.hunger", behavior: p => p.OnInitialize = probe =>
            early = Record.Exception(() => probe.Usage.UseItem(Player, default, probe.Usage.Use, UseTarget.None)), dependsOn: "example.food");
        using Engine engine = _harness.Start();

        Assert.IsType<InvalidOperationException>(early);
    }
}

/// <summary>A handler that writes its name and the value of its component, then answers.</summary>
internal sealed class Recorder<T>(List<string> calls, string name, UseResult result) : IItemHandler<T>
    where T : notnull
{
    public UseResult Use(in ItemUse<T> use)
    {
        calls.Add($"{name}:{use.Value}");
        return result;
    }
}

/// <summary>A handler that runs a function.</summary>
internal sealed class Callback<T>(Func<ItemUse<T>, UseResult> use) : IItemHandler<T>
    where T : notnull
{
    public UseResult Use(in ItemUse<T> value) => use(value);
}
