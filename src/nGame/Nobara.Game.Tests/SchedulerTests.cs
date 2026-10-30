// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Game.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Game.Tests;

/// <summary>The scheduler: tasks of game time and of real time.</summary>
public sealed class SchedulerTests : IDisposable
{
    private readonly GameHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    // Starts a game with one module, and returns that module.
    private (Engine Engine, Probe Probe) Start(Action<Probe>? initialize = null)
    {
        Probe? probe = null;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            probe = p;
            initialize?.Invoke(p);
        });
        Engine engine = _harness.Start();
        return (engine, probe!);
    }

    [Fact]
    public void Runs_tasks_in_stage_schedule_by_due_tick_then_by_scheduling()
    {
        (Engine engine, Probe p) = Start(p =>
        {
            p.System("example.farm.before", TickStages.Intents);
            p.System("example.farm.after", TickStages.World);
        });
        using (engine)
        {
            p.Scheduler.At(p.Context, DimensionId.Default, 2, new JournalTask(p, "second"));
            p.Scheduler.After(p.Context, DimensionId.Default, 1, new JournalTask(p, "first"));
            p.Scheduler.At(p.Context, DimensionId.Default, 2, new JournalTask(p, "third"));
            p.Scheduler.At(p.Context, DimensionId.Default, 0, new JournalTask(p, "overdue"));

            _harness.Tick(engine, 2);

            Assert.Equal(
                [
                    "example.farm.before@1", "overdue@1", "first@1", "example.farm.after@1",
                    "example.farm.before@2", "second@2", "third@2", "example.farm.after@2",
                ],
                _harness.Journal);
        }
    }

    [Fact]
    public void Merges_the_tasks_of_several_dimensions_by_due_tick()
    {
        (Engine engine, Probe p) = Start();
        using (engine)
        {
            var cave = new DimensionId(1);
            p.Time.AddDimension(cave, clock: 10);
            p.Scheduler.At(p.Context, cave, 11, new JournalTask(p, "cave"));
            p.Scheduler.At(p.Context, DimensionId.Default, 1, new JournalTask(p, "surface"));

            _harness.Tick(engine);

            Assert.Equal(["surface@1", "cave@11"], _harness.Journal);
        }
    }

    [Fact]
    public void A_periodic_task_runs_at_its_period_and_once_for_the_periods_a_jump_skips()
    {
        (Engine engine, Probe p) = Start();
        using (engine)
        {
            TaskHandle handle = p.Scheduler.Every(p.Context, DimensionId.Default, 3, new JournalTask(p, "water"));

            _harness.Tick(engine, 6);
            p.Time.Jump(DimensionId.Default, 10);
            _harness.Tick(engine, 3);
            handle.Dispose();
            _harness.Tick(engine, 6);

            Assert.Equal(["water@3", "water@6", "water@17", "water@18"], _harness.Journal);
            Assert.False(handle.IsActive);
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Scheduler.Every(p.Context, DimensionId.Default, 0, new JournalTask(p, "never")));
        }
    }

    [Fact]
    public void Tasks_follow_pause_scale_and_frozen_dimensions()
    {
        (Engine engine, Probe p) = Start();
        using (engine)
        {
            var cave = new DimensionId(1);
            p.Time.AddDimension(cave);
            p.Scheduler.At(p.Context, DimensionId.Default, 2, new JournalTask(p, "surface"));
            p.Scheduler.At(p.Context, cave, 2, new JournalTask(p, "cave"));

            p.Time.Paused = true;
            _harness.Tick(engine, 5);
            Assert.Empty(_harness.Journal);

            p.Time.Paused = false;
            p.Time.SetFrozen(cave, true);
            p.Time.TimeScale = 2;
            _harness.Tick(engine);
            Assert.Equal(["surface@2"], _harness.Journal);

            _harness.Tick(engine, 3);
            p.Time.SetFrozen(cave, false);
            _harness.Tick(engine);
            Assert.Equal(["surface@2", "cave@2"], _harness.Journal);
        }
    }

    [Fact]
    public void Tasks_of_real_time_run_at_the_start_of_frames_even_when_paused()
    {
        (Engine engine, Probe p) = Start();
        using (engine)
        {
            p.Time.Paused = true;
            p.Scheduler.AfterRealTime(p.Context, TimeSpan.FromMilliseconds(100), new JournalTask(p, "timeout"));
            TaskHandle ping = p.Scheduler.EveryRealTime(p.Context, TimeSpan.FromMilliseconds(40), new JournalTask(p, "ping"));

            _harness.Run(engine, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
            ping.Dispose();
            _harness.Run(engine, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));

            Assert.Equal(["ping@40ms", "ping@80ms", "timeout@100ms"], _harness.Journal);
            Assert.Equal(0UL, p.Time.Tick);
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Scheduler.AfterRealTime(p.Context, TimeSpan.FromSeconds(-1), new JournalTask(p, "never")));
        }
    }

    [Fact]
    public void A_task_scheduled_by_a_task_runs_at_a_later_run_and_a_canceled_task_never_runs()
    {
        (Engine engine, Probe p) = Start();
        using (engine)
        {
            TaskHandle? later = null;
            p.Scheduler.At(p.Context, DimensionId.Default, 1, new JournalTask(p, "first", _ =>
            {
                p.Scheduler.After(p.Context, DimensionId.Default, 0, new JournalTask(p, "chained"));
                later!.Dispose();
            }));
            later = p.Scheduler.At(p.Context, DimensionId.Default, 1, new JournalTask(p, "canceled"));

            _harness.Tick(engine, 3);

            Assert.Equal(["first@1", "chained@2"], _harness.Journal);
        }
    }

    [Fact]
    public void Removing_a_dimension_cancels_its_tasks()
    {
        (Engine engine, Probe p) = Start();
        using (engine)
        {
            var cave = new DimensionId(1);
            p.Time.AddDimension(cave);
            TaskHandle task = p.Scheduler.At(p.Context, cave, 1, new JournalTask(p, "cave"));
            Assert.True(task.IsActive);

            p.Time.RemoveDimension(cave);
            _harness.Tick(engine, 2);

            Assert.Empty(_harness.Journal);
            Assert.False(task.IsActive);
            Assert.Throws<ArgumentException>(() => p.Scheduler.At(p.Context, cave, 1, new JournalTask(p, "lost")));
        }
    }

    [Fact]
    public void An_exception_in_a_task_follows_the_policy_of_its_module_and_the_other_tasks_run()
    {
        Probe? weeds = null;
        _harness.Module("example.weeds", m => m.OnInitialize = p => weeds = p, policy: F.Policy.Disable);
        (Engine engine, Probe p) = Start();
        using (engine)
        {
            weeds!.Scheduler.At(weeds.Context, DimensionId.Default, 1, new JournalTask(weeds, "spread", _ => throw new InvalidOperationException("no soil")));
            weeds.Scheduler.At(weeds.Context, DimensionId.Default, 1, new JournalTask(weeds, "seed"));
            p.Scheduler.At(p.Context, DimensionId.Default, 1, new JournalTask(p, "harvest"));

            _harness.Tick(engine);

            Assert.Equal(["spread@1", "harvest@1"], _harness.Journal);
            Assert.False(engine.MainContext.IsModuleActive("example.weeds"));
            Assert.True(engine.MainContext.IsModuleActive("nobara.game"));
        }
    }
}
