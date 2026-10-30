// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Event;
using Nobara.Game.Tests.Support;

namespace Nobara.Game.Tests;

/// <summary>Game time: tick count under the levers, dimension clocks, and the events of the levers.</summary>
public sealed class TimeTests : IDisposable
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(10);

    private readonly GameHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    // Starts a game whose single system counts the ticks, and returns its game time.
    private (Engine Engine, IGameTime Time, Func<int> Ticks) Counting()
    {
        IGameTime? time = null;
        int ticks = 0;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            time = p.Time;
            p.Schedule.AddSystem(p.Context, "example.farm.count", TickStages.Entities, new CountSystem(() => ticks++));
        });
        Engine engine = _harness.Start();
        return (engine, time!, () => ticks);
    }

    [Fact]
    public void At_30_ticks_per_second_pause_double_scale_and_20_ticks_per_second_give_the_expected_ticks()
    {
        (Engine engine, IGameTime time, Func<int> ticks) = Counting();
        using (engine)
        {
            Assert.Equal(30, time.TickRate);

            _harness.Run(engine, TimeSpan.FromSeconds(2), Frame);
            Assert.Equal(60, ticks());

            time.Paused = true;
            _harness.Run(engine, TimeSpan.FromSeconds(2), Frame);
            Assert.Equal(60, ticks());

            time.Paused = false;
            time.TimeScale = 2;
            _harness.Run(engine, TimeSpan.FromSeconds(2), Frame);
            Assert.Equal(180, ticks());

            time.TimeScale = 1;
            time.TickRate = 20;
            _harness.Run(engine, TimeSpan.FromSeconds(2), Frame);
            Assert.Equal(220, ticks());
            Assert.Equal(TimeSpan.FromMilliseconds(50), time.TickDuration);

            // The tick number counts ticks that ran; real time counts every frame.
            Assert.Equal(220UL, time.Tick);
            Assert.Equal(TimeSpan.FromSeconds(8), time.RealTime);
        }
    }

    [Fact]
    public void Each_tick_moves_the_clocks_that_are_not_frozen()
    {
        (Engine engine, IGameTime time, _) = Counting();
        using (engine)
        {
            var cave = new DimensionId(2);
            time.AddDimension(cave, clock: 1000);
            Assert.Equal([DimensionId.Default, cave], time.Dimensions);

            _harness.Tick(engine, 3);
            Assert.Equal(3UL, time.GetClock(DimensionId.Default));
            Assert.Equal(1003UL, time.GetClock(cave));

            time.SetFrozen(cave, true);
            _harness.Tick(engine, 2);
            Assert.True(time.IsFrozen(cave));
            Assert.Equal(5UL, time.GetClock(DimensionId.Default));
            Assert.Equal(1003UL, time.GetClock(cave));

            time.Jump(cave, 500);
            time.SetFrozen(cave, false);
            _harness.Tick(engine);
            Assert.Equal(1504UL, time.GetClock(cave));
            Assert.Equal(6UL, time.Tick);
        }
    }

    [Fact]
    public void Clocks_are_added_and_removed_except_the_default_one()
    {
        (Engine engine, IGameTime time, _) = Counting();
        using (engine)
        {
            var cave = new DimensionId(2);
            Assert.True(time.HasDimension(DimensionId.Default));
            Assert.Throws<ArgumentException>(() => time.GetClock(cave));

            time.AddDimension(cave);
            Assert.Throws<ArgumentException>(() => time.AddDimension(cave));
            time.RemoveDimension(cave);

            Assert.False(time.HasDimension(cave));
            Assert.Throws<ArgumentException>(() => time.RemoveDimension(cave));
            Assert.Throws<ArgumentException>(() => time.RemoveDimension(DimensionId.Default));
            time.Jump(DimensionId.Default, 1);
            Assert.Throws<OverflowException>(() => time.Jump(DimensionId.Default, ulong.MaxValue));
            Assert.Equal(1UL, time.GetClock(DimensionId.Default));
        }
    }

    [Fact]
    public void The_levers_check_their_values()
    {
        (Engine engine, IGameTime time, _) = Counting();
        using (engine)
        {
            Assert.True(time.CanPause);
            Assert.Throws<ArgumentOutOfRangeException>(() => time.TimeScale = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => time.TimeScale = 101);
            Assert.Throws<ArgumentOutOfRangeException>(() => time.TickRate = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => time.TickRate = 1001);
            Assert.Equal(1, time.TimeScale);
        }
    }

    [Fact]
    public void Without_the_event_module_the_game_starts_and_runs()
    {
        _harness.WithEvents = false;
        (Engine engine, IGameTime time, Func<int> ticks) = Counting();
        using (engine)
        {
            time.Paused = true;
            time.Paused = false;
            time.TimeScale = 3;
            time.Jump(DimensionId.Default, 10);
            _harness.Tick(engine, 2);

            Assert.False(engine.MainContext.IsModuleActive("nobara.event"));
            Assert.Equal(6, ticks());
        }
    }

    [Fact]
    public void With_the_event_module_the_levers_emit_their_events()
    {
        _harness.WithEvents = true;
        var events = new List<string>();
        IGameTime? time = null;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            time = p.Time;
            IEventBus bus = p.Context.Resolve<IEventBus>();
            bus.Subscribe(p.Context, (ref GamePauseChanged e) => events.Add($"pause {e.Paused}"));
            bus.Subscribe(p.Context, (ref TimeScaleChanged e) => events.Add($"scale {e.Previous} {e.Current}"));
            bus.Subscribe(p.Context, (ref TickRateChanged e) => events.Add($"rate {e.Previous} {e.Current}"));
            bus.Subscribe(p.Context, (ref DimensionClockJumped e) => events.Add($"jump {e.Dimension} {e.From} {e.To}"));
            bus.Subscribe(p.Context, (ref DimensionFreezeChanged e) => events.Add($"freeze {e.Dimension} {e.Frozen}"));
        });
        using Engine engine = _harness.Start();
        _harness.Tick(engine);

        time!.Paused = true;
        time.Paused = true;
        time.Paused = false;
        time.TimeScale = 2;
        time.TickRate = 20;
        time.Jump(DimensionId.Default, 100);
        time.Jump(DimensionId.Default, 0);
        time.SetFrozen(DimensionId.Default, true);
        time.SetFrozen(DimensionId.Default, true);

        Assert.Equal(
            ["pause True", "pause False", "scale 1 2", "rate 30 20", "jump dimension 0 1 101", "freeze dimension 0 True"],
            events);
    }

    private sealed class CountSystem(Action count) : ITickSystem
    {
        public void Run(in TickTime time) => count();
    }
}
