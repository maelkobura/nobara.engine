// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;
using Nobara.Core.Profiling;
using Nobara.Game.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Game.Tests;

/// <summary>How tick systems run: declared accesses, errors, the profiler, and allocations.</summary>
public sealed class SystemTests : IDisposable
{
    private readonly GameHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void In_development_mode_a_system_that_touches_undeclared_data_fails_under_the_policy_of_its_module()
    {
        _harness.DevelopmentMode = true;
        JobData? crops = null;
        JobData? soil = null;
        _harness.Module(
            "example.farm",
            m => m.OnInitialize = p =>
            {
                crops = p.Context.Jobs.CreateData("crops");
                soil = p.Context.Jobs.CreateData("soil");
                p.System("example.farm.growth", TickStages.Entities, new TickSystemOptions { Reads = [soil], Writes = [crops] }, _ =>
                {
                    soil.CheckRead();
                    crops.CheckWrite();
                });
                p.System("example.farm.erosion", TickStages.Entities, action: _ => soil.CheckWrite());
                p.System("example.farm.after", TickStages.Late);
            },
            policy: F.Policy.Log);
        using Engine engine = _harness.Start();

        _harness.Tick(engine);

        LogEntry error = Assert.Single(_harness.Log.Entries, e => e.Exception is JobAccessException);
        Assert.Equal("The tick system 'example.farm.erosion' of module 'example.farm' writes 'soil', which it does not declare as written. Declare it in its accesses.", error.Exception!.Message);
        Assert.Equal("example.farm", error.Source);

        // The policy is log: the tick goes on with the next systems.
        Assert.Equal(["example.farm.erosion@1", "example.farm.growth@1", "example.farm.after@1"], _harness.Journal);
    }

    [Fact]
    public void Without_development_mode_accesses_are_not_checked()
    {
        JobData? soil = null;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            soil = p.Context.Jobs.CreateData("soil");
            p.System("example.farm.erosion", TickStages.Entities, action: _ => soil.CheckWrite());
        });
        using Engine engine = _harness.Start();

        _harness.Tick(engine);

        Assert.DoesNotContain(_harness.Log.Entries, e => e.Exception is not null);
    }

    [Fact]
    public void A_system_that_throws_disables_its_module_and_its_other_systems_and_the_tick_goes_on()
    {
        _harness
            .Module("example.weeds", policy: F.Policy.Disable, behavior: m => m.OnInitialize = p =>
            {
                p.System("example.weeds.spread", TickStages.World, action: _ => throw new InvalidOperationException("no soil"));
                p.System("example.weeds.seed", TickStages.Entities);
            })
            .Module("example.farm", m => m.OnInitialize = p => p.System("example.farm.harvest", TickStages.Entities));
        ITickSchedule? schedule = null;
        _harness.Module("example.view", m => m.OnStart = p => schedule = p.Schedule);
        using Engine engine = _harness.Start();

        _harness.Tick(engine, 2);

        Assert.Equal(["example.weeds.spread@1", "example.farm.harvest@1", "example.farm.harvest@2"], _harness.Journal);
        Assert.False(engine.MainContext.IsModuleActive("example.weeds"));
        Assert.Equal(TickSystemState.ModuleDisabled, schedule!.GetOrder().SelectMany(s => s.Systems).Single(s => s.Name == "example.weeds.seed").State);
    }

    [Fact]
    public void A_system_that_throws_under_the_stop_policy_stops_the_context()
    {
        _harness.Module("example.farm", m => m.OnInitialize = p => p.System("example.farm.growth", TickStages.Entities, action: _ => throw new InvalidOperationException("drought")));
        using Engine engine = _harness.Start();

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Tick(engine));

        Assert.Equal("example.farm", stop.ModuleId);
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
    }

    [Fact]
    public void The_profiler_measures_each_stage_and_each_system()
    {
        _harness.Profiling = true;
        _harness.Module("example.farm", m => m.OnInitialize = p => p.System("example.farm.growth", TickStages.Entities));
        using Engine engine = _harness.Start();

        _harness.Tick(engine, 3);

        IReadOnlyList<ProfileEntry> entries = engine.Profiler.Snapshot();
        ProfileEntry system = Assert.Single(entries, e => e.Name == "Tick system example.farm.growth");
        Assert.Equal("example.farm", system.Module);
        Assert.Equal(ProfileKind.Scope, system.Kind);
        Assert.Equal(3, system.Calls);
        foreach (string stage in TickStages.Standard)
        {
            ProfileEntry measured = Assert.Single(entries, e => e.Name == "Tick stage " + stage);
            Assert.Equal("nobara.game", measured.Module);
            Assert.Equal(3, measured.Calls);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_tick_with_systems_tasks_and_clocks_allocates_nothing(bool profiling, bool developmentMode)
    {
        _harness.Profiling = profiling;
        _harness.DevelopmentMode = developmentMode;
        _harness.WithEvents = true;
        var growth = new CountingSystem();
        var task = new CountingTask();
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            JobData crops = p.Context.Jobs.CreateData("crops");
            growth.Data = crops;
            p.Schedule.AddSystem(p.Context, "example.farm.growth", TickStages.Entities, growth, new TickSystemOptions { Writes = [crops] });
            p.Time.AddDimension(new DimensionId(1));
            p.Scheduler.Every(p.Context, DimensionId.Default, 1, task);
            p.Scheduler.Every(p.Context, new DimensionId(1), 2, task);
            p.Scheduler.EveryRealTime(p.Context, TimeSpan.FromMilliseconds(20), task);
        });
        using Engine engine = _harness.Start();
        _harness.Tick(engine, 10);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _harness.Tick(engine, 100);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(110, growth.Runs);
        Assert.True(task.Runs > 200);
    }

    private sealed class CountingSystem : ITickSystem
    {
        public JobData? Data { get; set; }

        public int Runs { get; private set; }

        public void Run(in TickTime time)
        {
            Data?.CheckWrite();
            Runs++;
        }
    }

    private sealed class CountingTask : IScheduledTask
    {
        public int Runs { get; private set; }

        public void Run(in TaskRun run) => Runs++;
    }
}
