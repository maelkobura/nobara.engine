// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Jobs;
using Nobara.Core.Profiling;
using Nobara.Core.Tests.Support;

namespace Nobara.Core.Tests;

/// <summary>The profiler: measures per context, module, phase, and job.</summary>
public sealed class ProfilerTests : IDisposable
{
    private static readonly TimeSpan Tick30 = TimeSpan.FromSeconds(1.0 / 30);

    private readonly TestDistribution _distribution = new() { Loop = LoopTests.StandardLoop(), Profiling = true };

    public void Dispose() => _distribution.Dispose();

    private void RunFrames(Engine engine, int frames)
    {
        engine.MainContext.Start();
        for (int i = 0; i < frames; i++)
        {
            _distribution.Clock.Advance(Tick30);
            engine.MainContext.RunFrame();
        }
    }

    [Fact]
    public void Reports_time_allocations_and_gc_pauses_per_module_and_per_phase()
    {
        using Engine engine = _distribution
            .Module("example.time", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Simulation", new PhaseHandler(_ => Thread.SpinWait(1000))))
            .Module("example.mobs", behavior: m => m.OnRegister = c =>
            {
                c.Loop.Subscribe("Simulation", new PhaseHandler(_ => GC.KeepAlive(new byte[4096])));
                c.Loop.Subscribe("Render", new PhaseHandler(t =>
                {
                    if (t.Frame == 2)
                    {
                        GC.Collect();
                    }
                }));
            })
            .Load();

        RunFrames(engine, 10);
        IReadOnlyList<ProfileEntry> entries = engine.Profiler.Snapshot();

        ProfileEntry time = entries.Single(e => e is { Module: "example.time", Kind: ProfileKind.Handler, Name: "Simulation" });
        Assert.Equal("client", time.Context);
        Assert.Equal(10, time.Calls);
        Assert.True(time.Time > TimeSpan.Zero);
        Assert.True(time.MaxTime > TimeSpan.Zero && time.MaxTime <= time.Time);

        ProfileEntry mobs = entries.Single(e => e is { Module: "example.mobs", Kind: ProfileKind.Handler, Name: "Simulation" });
        Assert.True(mobs.AllocatedBytes >= 10 * 4096);
        Assert.True(time.AllocatedBytes < 1024);

        ProfileEntry render = entries.Single(e => e is { Module: "example.mobs", Kind: ProfileKind.Handler, Name: "Render" });
        Assert.True(render.GcPause > TimeSpan.Zero);

        ProfileEntry simulation = entries.Single(e => e is { Module: "", Kind: ProfileKind.Phase, Name: "Simulation" });
        Assert.Equal(10, simulation.Calls);
        Assert.True(simulation.Time >= time.Time);
        Assert.True(simulation.AllocatedBytes >= mobs.AllocatedBytes);
        Assert.Contains(entries, e => e is { Kind: ProfileKind.Phase, Name: "Input", Calls: 10 });
        Assert.Contains(entries, e => e is { Kind: ProfileKind.Phase, Name: "FrameStart", Calls: 10 });

        string report = engine.Profiler.FormatReport();
        Assert.Contains("example.mobs", report, StringComparison.Ordinal);
        Assert.Contains("Simulation", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Measures_jobs_per_module_and_type_and_scopes_that_modules_create()
    {
        ModuleContext? context = null;
        using Engine engine = _distribution.Module("example.jobs", behavior: m => m.OnInitialize = c => context = c).Load();
        RunFrames(engine, 1);

        ProfilerMarker marker = context!.Profiler.CreateMarker("Crop growth");
        Assert.Same(marker, context.Profiler.CreateMarker("Crop growth"));
        for (int i = 0; i < 3; i++)
        {
            using (marker.Begin())
            {
                context.Jobs.ScheduleFor(new SpinJob(), 100, 10).Complete();
            }
        }

        IReadOnlyList<ProfileEntry> entries = engine.Profiler.Snapshot();
        ProfileEntry job = entries.Single(e => e.Kind == ProfileKind.Job);
        Assert.Equal(("client", "example.jobs", "Nobara.Core.Tests.ProfilerTests+SpinJob"), (job.Context, job.Module, job.Name));
        Assert.Equal(30, job.Calls);
        ProfileEntry scope = entries.Single(e => e.Kind == ProfileKind.Scope);
        Assert.Equal(("example.jobs", "Crop growth", 3L), (scope.Module, scope.Name, scope.Calls));
    }

    [Fact]
    public void Measures_callbacks_of_the_synchronization_point()
    {
        using Engine engine = _distribution.Module("example.net", behavior: m => m.OnStart = c => c.Post(_ => Thread.SpinWait(100))).Load();
        RunFrames(engine, 2);

        ProfileEntry entry = Assert.Single(engine.Profiler.Snapshot(), e => e.Kind == ProfileKind.Synchronization);
        Assert.Equal(("example.net", 1L), (entry.Module, entry.Calls));
    }

    [Fact]
    public void A_disabled_profiler_measures_nothing_and_reset_clears_the_measures()
    {
        _distribution.Profiling = false;
        using Engine engine = _distribution.Module("example.time", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Input", new PhaseHandler(_ => { }))).Load();
        RunFrames(engine, 3);
        Assert.Empty(engine.Profiler.Snapshot());

        engine.Profiler.Enabled = true;
        _distribution.Clock.Advance(Tick30);
        engine.MainContext.RunFrame();
        Assert.Contains(engine.Profiler.Snapshot(), e => e is { Module: "example.time", Calls: 1 });

        engine.Profiler.Reset();
        Assert.Empty(engine.Profiler.Snapshot());
    }

    [Fact]
    public void A_frame_allocates_nothing_while_the_profiler_measures()
    {
        var handler = new PhaseHandler(_ => { });
        using Engine engine = _distribution.Module("example.time", behavior: m => m.OnRegister = c =>
        {
            c.Loop.Subscribe("Input", handler);
            c.Loop.Subscribe("Simulation", handler);
            c.Loop.Subscribe("Render", handler);
        }).Load();
        RunFrames(engine, 10);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _distribution.Clock.Advance(Tick30);
            engine.MainContext.RunFrame();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Measuring_a_scope_allocates_nothing()
    {
        ModuleContext? context = null;
        using Engine engine = _distribution.Module("example.time", behavior: m => m.OnRegister = c => context = c).Load();
        engine.MainContext.Start();
        ProfilerMarker marker = context!.Profiler.CreateMarker("Scope");
        for (int i = 0; i < 10; i++)
        {
            marker.Begin().Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            using (marker.Begin())
            {
            }
        }

        engine.Profiler.Enabled = false;
        for (int i = 0; i < 1000; i++)
        {
            using (marker.Begin())
            {
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1010, engine.Profiler.Snapshot().Single().Calls);
    }

    private struct SpinJob : IJobFor
    {
        public readonly void Execute(int index) => Thread.SpinWait(10);
    }
}
