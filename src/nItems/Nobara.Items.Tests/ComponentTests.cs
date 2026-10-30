// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Items.Tests.Support;
using static Nobara.Items.Tests.Support.ItemsHarness;
using F = Nobara.Formats;

namespace Nobara.Items.Tests;

public sealed class ComponentTests : IDisposable
{
    private readonly ItemsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    internal sealed record Edible(int Food, long Duration, bool Raw, string Sound);

    private static ItemComponentType<Edible> RegisterEdible(Probe probe) =>
        probe.Registry.RegisterComponent(
            probe.Context,
            "example.food.edible",
            v => new Edible(v.GetInt("Food"), v.GetTicks("Duration", fallbackSeconds: 1), v.GetBool("Raw", false), v.GetString("Sound", "none")),
            EqualityComparer<Edible>.Default);

    private static string Steak(string values, string attributes = "") => Nof("Example/Food", $"""
        <Item>
          <Name>Steak</Name>
          <Content>
            <Component ref="example.food.edible"{attributes}>
              {values}
            </Component>
          </Content>
        </Item>
        """);

    private static ItemLoadException StartFails(Engine engine)
    {
        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);
        Assert.Equal(ItemsModule.ModuleId, stop.ModuleId);
        Assert.Equal(LifecycleStep.Start, stop.Step);
        return Assert.IsType<ItemLoadException>(stop.InnerException);
    }

    [Fact]
    public void ValuesAreConvertedOnceWhenTheRegistryFreezes()
    {
        ItemComponentType<Edible>? edible = null;
        IItemRegistry registry = null!;
        int reads = 0;
        _harness.Module("example.food", Steak("<Food>3</Food><Duration>1.5</Duration><Raw>true</Raw>"), p =>
        {
            p.OnInitialize = probe =>
            {
                edible = probe.Registry.RegisterComponent(
                    probe.Context,
                    "example.food.edible",
                    v =>
                    {
                        reads++;
                        return new Edible(v.GetInt("Food"), v.GetTicks("Duration"), v.GetBool("Raw"), v.GetString("Sound", "none"));
                    },
                    EqualityComparer<Edible>.Default);
            };
            p.OnStart = probe => registry = probe.Registry;
        });
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 3);

        ItemType steak = registry.Get("item:example.food.steak");
        Assert.Equal(new Edible(3, 45, true, "none"), steak.Components.Get(edible!));
        Assert.Equal(1, reads);
        Assert.True(registry.TryGetComponentType("example.food.edible", out ItemComponentType? type));
        Assert.Same(edible, type);
        Assert.Equal("example.food", type.ModuleId);
        Assert.Equal(typeof(Edible), type.ValueType);
    }

    [Fact]
    public void ANameMustStartWithTheModuleThatRegistersIt()
    {
        Exception? wrongPrefix = null;
        Exception? uppercase = null;
        Exception? twice = null;
        _harness.Module("example.food", behavior: p => p.OnInitialize = probe =>
        {
            wrongPrefix = Record.Exception(() => probe.Registry.RegisterComponent(probe.Context, "example.tools.edible", _ => 1, EqualityComparer<int>.Default));
            uppercase = Record.Exception(() => probe.Registry.RegisterComponent(probe.Context, "example.food.Edible", _ => 1, EqualityComparer<int>.Default));
            probe.Registry.RegisterComponent(probe.Context, "example.food.edible", _ => 1, EqualityComparer<int>.Default);
            twice = Record.Exception(() => probe.Registry.RegisterComponent(probe.Context, "example.food.edible", _ => 1, EqualityComparer<int>.Default));
        });
        using Engine engine = _harness.Start();

        Assert.IsType<ArgumentException>(wrongPrefix);
        Assert.Contains("'example.food.'", wrongPrefix!.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(uppercase);
        Assert.IsType<ArgumentException>(twice);
    }

    [Theory]
    [InlineData("<Food>three</Food>", "<Food> is 'three', expected a whole number.", 6, 8)]
    [InlineData("<Food>3</Food><Fod>4</Fod>", "<Fod> is not a value of the component.", 6, 22)]
    [InlineData("<Duration>2</Duration>", "<Food> is missing.", 5, 6)]
    [InlineData("<Food>3</Food><Food>4</Food>", "<Food> is written twice.", 6, 22)]
    [InlineData("<Food>3</Food><Duration>-1</Duration>", "<Duration> is -1 seconds, expected a duration of at least 0.", 6, 22)]
    public void AnInvalidValueFailsTheStartAndCitesTheFile(string values, string message, int line, int column)
    {
        _harness.Module("example.food", Steak(values), p => p.OnInitialize = probe => RegisterEdible(probe));
        using Engine engine = _harness.Load();

        ItemLoadException exception = StartFails(engine);

        ItemProblem problem = Assert.Single(exception.Problems);
        Assert.Equal("item:example.food.steak", problem.ItemId);
        Assert.Equal("component 'example.food.edible': " + message, problem.Message);
        Assert.EndsWith("food.nof", problem.Source.File, StringComparison.Ordinal);
        Assert.Equal((line, column), (problem.Source.Line, problem.Source.Column));
        Assert.Contains($"food.nof({line},{column}): item:example.food.steak: component 'example.food.edible'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AComponentThatNoModuleRegistersFailsTheStart()
    {
        _harness.Module("example.food", Steak("<Food>3</Food>"));
        using Engine engine = _harness.Load();

        ItemProblem problem = Assert.Single(StartFails(engine).Problems);

        Assert.Equal("module 'example.food' registers no item component 'example.food.edible'.", problem.Message);
        Assert.Equal(5, problem.Source.Line);
    }

    [Fact]
    public void AnOptionalComponentOfAnInactiveModuleIsIgnored()
    {
        IItemRegistry registry = null!;
        _harness
            .Module("example.server", side: F.Side.Server)
            .Module(
                "example.food",
                Nof("Example/Food", """
                    <Item><Name>Steak</Name><Content>
                      <Component ref="example.server.loot" optional="true"><Weight>2</Weight></Component>
                    </Content></Item>
                    """),
                p => p.OnStart = probe => registry = probe.Registry);
        using Engine engine = _harness.Start(Side.Client);

        Assert.Equal(0, registry.Get("item:example.food.steak").Components.Count);
    }

    [Theory]
    [InlineData("example.food.loot", "module 'example.food' registers no item component 'example.food.loot'.")]
    [InlineData("example.unknown.loot", "the component 'example.unknown.loot' names no module of the distribution.")]
    public void AnOptionalComponentFailsWhenItIsATypingError(string name, string message)
    {
        _harness.Module("example.food", Nof("Example/Food", $"""
            <Item><Name>Steak</Name><Content>
              <Component ref="{name}" optional="true"/>
            </Content></Item>
            """));
        using Engine engine = _harness.Load();

        Assert.Equal(message, Assert.Single(StartFails(engine).Problems).Message);
    }

    [Fact]
    public void ARequiredComponentOfAnInactiveModuleFailsTheStart()
    {
        _harness
            .Module("example.server", side: F.Side.Server)
            .Module("example.food", Nof("Example/Food", """
                <Item><Name>Steak</Name><Content><Component ref="example.server.loot"/></Content></Item>
                """));
        using Engine engine = _harness.Load(Side.Client);

        Assert.Contains("which is not active", Assert.Single(StartFails(engine).Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComponentSetsAreImmutableAndCompareByValue()
    {
        ItemComponentType<Edible>? edible = null;
        ItemComponentType<int>? durability = null;
        IItemRegistry registry = null!;
        _harness.Module("example.food", Steak("<Food>3</Food>"), p =>
        {
            p.OnInitialize = probe =>
            {
                edible = RegisterEdible(probe);
                durability = probe.Registry.RegisterComponent(probe.Context, "example.food.durability", v => v.GetInt("Value"), EqualityComparer<int>.Default);
            };
            p.OnStart = probe => registry = probe.Registry;
        });
        using Engine engine = _harness.Start();

        ItemComponents defaults = registry.Get("item:example.food.steak").Components;
        ItemComponents worn = defaults.With(durability!, 37);
        ItemComponents richer = defaults.With(edible!, new Edible(5, 30, false, "none"));

        Assert.Equal(1, defaults.Count);
        Assert.Equal(2, worn.Count);
        Assert.Equal(37, worn.Get(durability!));
        Assert.False(defaults.Contains(durability!));
        Assert.Equal(5, richer.Get(edible!).Food);
        Assert.Equal(3, defaults.Get(edible!).Food);
        Assert.Same(defaults, defaults.With(edible!, new Edible(3, 30, false, "none")));
        Assert.Equal(defaults, worn.Without(durability!));
        Assert.Equal(defaults.GetHashCode(), worn.Without(durability!).GetHashCode());
        Assert.NotEqual(defaults, richer);
        Assert.Same(ItemComponents.Empty, defaults.Without(edible!));
        Assert.False(defaults.TryGet(durability!, out int missing));
        Assert.Equal(0, missing);
        Assert.Throws<KeyNotFoundException>(() => defaults.Get(durability!));
    }
}
