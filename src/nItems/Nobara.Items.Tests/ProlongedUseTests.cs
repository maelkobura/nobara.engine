// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Game;
using Nobara.Items.Tests.Support;
using static Nobara.Items.Tests.Support.ItemsHarness;

namespace Nobara.Items.Tests;

public sealed class ProlongedUseTests : IDisposable
{
    private static readonly ItemActor Player = new(42, DimensionId.Default);

    private readonly ItemsHarness _harness = new();
    private readonly List<string> _calls = [];
    private Probe _probe = null!;
    private ItemComponentType<long> _edible = null!;

    public ProlongedUseTests()
    {
        // Eating a steak lasts 0.1 second: 3 ticks at 30 ticks per second.
        _harness.Module(
            "example.food",
            Nof("Example/Food", """
                <Item><Name>Steak</Name><Content>
                  <Component ref="example.food.edible"><Duration>0.1</Duration></Component>
                </Content></Item>
                <Item><Name>Bread</Name><Content>
                  <Component ref="example.food.edible"><Duration>1</Duration></Component>
                </Content></Item>
                """),
            p => p.OnInitialize = probe =>
            {
                _probe = probe;
                _edible = probe.Registry.RegisterComponent(probe.Context, "example.food.edible", v => v.GetTicks("Duration"), EqualityComparer<long>.Default);
                probe.Usage.AddHandler(probe.Context, _edible, probe.Usage.Use, new Eater(_calls, everyTick: true));
            });
    }

    public void Dispose() => _harness.Dispose();

    private ItemStackView Stack(string name) => new(_probe.Registry.Get("item:example.food." + name));

    [Fact]
    public void AProlongedUseEndsOnceItsDurationHasElapsedOnTheDimensionClock()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        ulong start = _probe.Time.GetClock(DimensionId.Default);

        Assert.Equal(UseOutcome.Started, _probe.Usage.UseItem(Player, Stack("steak"), _probe.Usage.Use, UseTarget.None));
        Assert.True(_probe.Usage.TryGetProlongedUse(42, out ProlongedUse use));
        Assert.Equal(3, use.Duration);
        Assert.Equal(0UL, use.Elapsed);
        Assert.Same(_edible, use.Component);
        _harness.Tick(engine, 3);

        Assert.Equal(start + 3, _probe.Time.GetClock(DimensionId.Default));
        Assert.Equal(["use:3", "tick:1", "tick:2", "finished:3"], _calls);
        Assert.False(_probe.Usage.TryGetProlongedUse(42, out _));
    }

    [Fact]
    public void ThePauseAndAFrozenDimensionSuspendTheUse()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        _probe.Usage.UseItem(Player, Stack("steak"), _probe.Usage.Use, UseTarget.None);

        _probe.Time.Paused = true;
        _harness.Tick(engine, 5);
        _probe.Time.Paused = false;
        _harness.Tick(engine);
        _probe.Time.SetFrozen(DimensionId.Default, true);
        _harness.Tick(engine, 5);
        _probe.Time.SetFrozen(DimensionId.Default, false);
        _harness.Tick(engine, 2);

        Assert.Equal(["use:3", "tick:1", "tick:2", "finished:3"], _calls);
    }

    [Fact]
    public void AJumpOfTheClockEndsTheUseAtTheNextTick()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        _probe.Usage.UseItem(Player, Stack("bread"), _probe.Usage.Use, UseTarget.None);

        _probe.Time.Jump(DimensionId.Default, 100);
        _harness.Tick(engine);

        Assert.Equal(["use:30", "finished:101"], _calls);
    }

    [Fact]
    public void TheGameInterruptsAUseAndAnotherUseReplacesIt()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        _probe.Usage.UseItem(Player, Stack("bread"), _probe.Usage.Use, UseTarget.None);
        _harness.Tick(engine);

        _probe.Usage.UseItem(Player, Stack("steak"), _probe.Usage.Use, UseTarget.None);
        _harness.Tick(engine);
        Assert.True(_probe.Usage.Interrupt(42));
        Assert.False(_probe.Usage.Interrupt(42));
        _harness.Tick(engine, 5);

        Assert.Equal(["use:30", "tick:1", "use:3", "interrupted:1:Replaced", "tick:1", "interrupted:1:Interrupted"], _calls);
    }

    [Fact]
    public void EachActorHasItsOwnUse()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        var other = new ItemActor(7, DimensionId.Default);

        _probe.Usage.UseItem(Player, Stack("steak"), _probe.Usage.Use, UseTarget.None);
        _probe.Usage.UseItem(other, Stack("bread"), _probe.Usage.Use, UseTarget.None);
        _harness.Tick(engine, 3);

        Assert.False(_probe.Usage.TryGetProlongedUse(42, out _));
        Assert.True(_probe.Usage.TryGetProlongedUse(7, out ProlongedUse use));
        Assert.Equal(3UL, use.Elapsed);
    }

    [Fact]
    public void AUseEndsWhenItsDimensionLosesItsClock()
    {
        using Engine engine = _harness.Start();
        _harness.Tick(engine);
        var dimension = new DimensionId(3);
        _probe.Time.AddDimension(dimension);
        _probe.Usage.UseItem(new ItemActor(42, dimension), Stack("bread"), _probe.Usage.Use, UseTarget.None);

        _probe.Time.RemoveDimension(dimension);
        _harness.Tick(engine);

        Assert.Equal(["use:30", "interrupted:0:DimensionRemoved"], _calls);
        Assert.Throws<ArgumentException>(() => _probe.Usage.UseItem(new ItemActor(42, dimension), Stack("bread"), _probe.Usage.Use, UseTarget.None));
    }

    /// <summary>Eats for the duration of the component, and writes each call.</summary>
    private sealed class Eater(List<string> calls, bool everyTick) : IItemHandler<long>
    {
        public UseResult Use(in ItemUse<long> use)
        {
            calls.Add($"use:{use.Value}");
            return UseResult.Prolong(use.Value, everyTick);
        }

        public void UseTick(in ItemUse<long> use) => calls.Add($"tick:{use.Elapsed}");

        public void UseFinished(in ItemUse<long> use) => calls.Add($"finished:{use.Elapsed}");

        public void UseInterrupted(in ItemUse<long> use, UseInterruption reason) => calls.Add($"interrupted:{use.Elapsed}:{reason}");
    }
}
