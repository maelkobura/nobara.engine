// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Loading;
using Nobara.Core.Loop;
using Nobara.Core.Tests.Support;

namespace Nobara.Core.Tests;

/// <summary>Reserved phases and the levers of the core.</summary>
public sealed class LeverTests : IDisposable
{
    private readonly TestDistribution _distribution = new() { Loop = LoopTests.StandardLoop() };

    public void Dispose() => _distribution.Dispose();

    // A module that reserves the simulation phase and keeps its levers.
    private TestDistribution WithMaster(Action<TickControl> keep, Action<ModuleContext>? register = null) => _distribution.Module(
        "example.game",
        m => m.Reserves.Add("loop.simulation"),
        m =>
        {
            m.OnRegister = register;
            m.OnInitialize = c => keep(c.Loop.GetTickControl());
        });

    private void Frame(Engine engine, double milliseconds)
    {
        _distribution.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        engine.MainContext.RunFrame();
    }

    [Fact]
    public void Only_the_module_that_reserves_a_phase_subscribes_to_it()
    {
        int runs = 0;
        using Engine engine = WithMaster(_ => { }, c => c.Loop.Subscribe("Simulation", new PhaseHandler(_ => runs++)))
            .Module("example.mobs", m => m.Requires("example.game"), m => m.OnRegister = c => c.Loop.Subscribe("Simulation", new PhaseHandler(_ => { })))
            .Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal("example.mobs", stop.ModuleId);
        Assert.Equal(
            "Module 'example.mobs' cannot subscribe to phase 'Simulation': module 'example.game' reserves it (loop.simulation). Register with that module instead.",
            stop.InnerException!.Message);
    }

    [Fact]
    public void Without_a_reservation_the_simulation_phase_is_open_and_runs_in_real_time()
    {
        string? reservedBy = "?";
        using Engine engine = _distribution
            .Module("example.a", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Simulation", new PhaseHandler(_ => { })))
            .Module("example.b", behavior: m => m.OnRegister = c =>
            {
                c.Loop.Subscribe("Simulation", new PhaseHandler(_ => { }));
                reservedBy = c.Loop.ReservedBy(c.Loop.FindPhase("Simulation")!);
            })
            .Load();
        engine.MainContext.Start();

        Frame(engine, 1000);
        Assert.Null(reservedBy);
        Assert.Equal(7UL, engine.MainContext.Tick); // 250 ms caught up at 30 Hz, as declared
    }

    [Fact]
    public void Refuses_the_tick_levers_to_a_module_that_reserves_no_fixed_phase()
    {
        using Engine engine = _distribution.Module("example.mobs", behavior: m => m.OnInitialize = c => c.Loop.GetTickControl()).Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal(
            "Module 'example.mobs' cannot control the tick: only the module that reserves a fixed phase can (here loop.simulation).",
            stop.InnerException!.Message);
    }

    [Fact]
    public void Pausing_suspends_the_fixed_phases_and_keeps_the_others()
    {
        TickControl control = null!;
        int inputs = 0;
        using Engine engine = WithMaster(c => control = c, c => c.Loop.Subscribe("Input", new PhaseHandler(_ => inputs++))).Load();
        engine.MainContext.Start();

        Frame(engine, 100);
        control.Paused = true;
        Frame(engine, 100);
        Frame(engine, 100);
        control.Paused = false;
        Frame(engine, 100);

        Assert.Equal(6UL, control.Tick);
        Assert.Equal(4, inputs);
    }

    [Fact]
    public void The_time_scale_changes_the_number_of_ticks_and_not_their_duration()
    {
        TickControl control = null!;
        var deltas = new List<TimeSpan>();
        using Engine engine = WithMaster(c => control = c, c => c.Loop.Subscribe("Simulation", new PhaseHandler(t => deltas.Add(t.Delta)))).Load();
        engine.MainContext.Start();

        control.TimeScale = 2;
        Frame(engine, 100);
        Assert.Equal(6UL, control.Tick);

        control.TimeScale = 0.5;
        Frame(engine, 200);
        Assert.Equal(9UL, control.Tick);
        Assert.All(deltas, d => Assert.Equal(TimeSpan.FromSeconds(1.0 / 30), d));
    }

    [Fact]
    public void The_tick_rate_changes_the_duration_of_a_tick()
    {
        TickControl control = null!;
        using Engine engine = WithMaster(c => control = c).Load();
        engine.MainContext.Start();

        control.TickRate = 60;
        Frame(engine, 100);

        Assert.Equal(TimeSpan.FromSeconds(1.0 / 60), control.TickDuration);
        Assert.Equal(6UL, control.Tick);
        Assert.Throws<ArgumentOutOfRangeException>(() => control.TickRate = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => control.TickRate = 1e8);
        Assert.Throws<ArgumentOutOfRangeException>(() => control.TimeScale = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => control.TimeScale = 1e9);
    }

    [Fact]
    public void The_context_lever_needs_its_reservation_and_cannot_create_an_integrated_server_yet()
    {
        ContextControl? control = null;
        using Engine engine = _distribution
            .Module("example.flow", m => m.Reserves.Add(LoopReservations.Contexts), m => m.OnInitialize = c => control = c.GetContextControl())
            .Module("example.other", behavior: m => m.OnInitialize = c => c.GetContextControl())
            .Policy("example.other", Nobara.Formats.Policy.Log)
            .Load();
        engine.MainContext.Start();

        Assert.NotNull(control);
        Assert.Equal(Side.Client, control.MainSide);
        Assert.False(control.HasIntegratedServer);
        Assert.False(control.CanCreateIntegratedServer);
        Assert.Throws<NotSupportedException>(control.CreateIntegratedServer);
        Assert.Contains(_distribution.Log.Entries, e => e.Source == "example.other" && e.Exception?.Message ==
            "Module 'example.other' cannot create or destroy contexts: only the module that reserves 'core.contexts' can.");
    }

    [Fact]
    public void Gives_the_initial_state_of_each_side()
    {
        DistributionInfo? info = null;
        _distribution.Configure = d => d.States = new Nobara.Formats.InitialStates { Client = "state:example.boot" };
        using Engine engine = _distribution.Module("example.flow", behavior: m => m.OnInitialize = c => info = c.Distribution).Load();
        engine.MainContext.Start();

        Assert.Equal("state:example.boot", info!.GetInitialState(Side.Client));
        Assert.Null(info.GetInitialState(Side.Server));
        Assert.Null(info.GetInitialState(Side.Both));
    }

    [Fact]
    public void Lists_the_modules_of_the_distribution_active_or_not()
    {
        DistributionInfo? info = null;
        using Engine engine = _distribution
            .Module("example.flow", behavior: m => m.OnInitialize = c => info = c.Distribution)
            .Module("example.dedicated", m => m.Side = Nobara.Formats.Side.Server)
            .Load(Side.Client);
        engine.MainContext.Start();

        Assert.Equal(["example.flow", "example.dedicated"], info!.Modules);
    }

    [Fact]
    public void An_intercepted_stop_request_reaches_the_module_at_the_next_frame_and_the_context_goes_on()
    {
        ModuleContext flow = null!;
        var received = new List<ulong>();
        using Engine engine = _distribution
            .Module("example.flow", m => m.Reserves.Add(LoopReservations.Contexts), m => m.OnRegister = c =>
            {
                flow = c;
                c.InterceptStopRequests(() => received.Add(flow.Loop.Frame));
            })
            .Load();
        engine.MainContext.Start();

        engine.MainContext.RequestStop();
        engine.MainContext.RequestStop();
        Assert.False(engine.MainContext.IsStopRequested);
        Assert.Empty(received);

        engine.MainContext.RunFrame();
        Assert.Equal([1UL], received);
        Assert.False(engine.MainContext.IsStopRequested);

        engine.MainContext.RequestStop();
        engine.MainContext.RunFrame();
        Assert.Equal([1UL, 2UL], received);

        // The module stops the context itself.
        flow.RequestStop();
        Assert.True(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void Run_ends_when_the_module_that_intercepts_stop_requests_stops_the_context()
    {
        ModuleContext flow = null!;
        using var cancellation = new CancellationTokenSource();
        int frames = 0;
        using Engine engine = _distribution
            .Module("example.flow", m => m.Reserves.Add(LoopReservations.Contexts), m => m.OnRegister = c =>
            {
                flow = c;
                c.InterceptStopRequests(() => flow.RequestStop());
                c.Loop.SubscribeFrameStart(new PhaseHandler(_ =>
                {
                    if (++frames == 3)
                    {
                        cancellation.Cancel();
                    }
                }));
            })
            .Load();

        engine.MainContext.Run(cancellation.Token);

        // The cancellation at frame 3 reaches the module at frame 4, which stops the context after it.
        Assert.Equal(4, frames);
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
    }

    [Fact]
    public void Only_the_module_that_reserves_the_contexts_intercepts_stop_requests()
    {
        using Engine engine = _distribution.Module("example.other", behavior: m => m.OnRegister = c => c.InterceptStopRequests(() => { })).Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal(
            "Module 'example.other' cannot intercept stop requests: only the module that reserves 'core.contexts' can.",
            stop.InnerException!.Message);
    }

    [Fact]
    public void Stop_requests_stop_the_context_again_once_the_module_that_intercepts_them_is_disabled()
    {
        using Engine engine = _distribution
            .Module("example.flow", m => m.Reserves.Add(LoopReservations.Contexts), m => m.OnRegister = c =>
            {
                c.InterceptStopRequests(() => { });
                c.Loop.Subscribe("Input", new PhaseHandler(_ => throw new InvalidOperationException("broken")));
            })
            .Policy("example.flow", Nobara.Formats.Policy.Disable)
            .Load();
        engine.MainContext.Start();

        engine.MainContext.RunFrame();
        engine.MainContext.RequestStop();

        Assert.False(engine.MainContext.IsModuleActive("example.flow"));
        Assert.True(engine.MainContext.IsStopRequested);
    }
}

/// <summary>The synchronization point and the errors that modules throw in the loop.</summary>
public sealed class LoopErrorTests : IDisposable
{
    private readonly TestDistribution _distribution = new() { Loop = LoopTests.StandardLoop() };

    public void Dispose() => _distribution.Dispose();

    [Fact]
    public void Runs_posted_callbacks_on_the_loop_thread_at_the_start_of_the_next_frame()
    {
        Journal journal = _distribution.Journal;
        ModuleContext module = null!;
        using Engine engine = _distribution.Module("example.net", behavior: m => m.OnRegister = c =>
        {
            module = c;
            c.Loop.SubscribeFrameStart(new PhaseHandler(_ => journal.Add("example.net:FrameStart")));
        }).Load();
        engine.MainContext.Start();
        int loopThread = Environment.CurrentManagedThreadId;

        int postedFrom = 0;
        var thread = new Thread(() =>
        {
            postedFrom = Environment.CurrentManagedThreadId;
            module.Post(_ => journal.Add($"example.net:posted on loop thread {Environment.CurrentManagedThreadId == loopThread}"));
        });
        thread.Start();
        thread.Join();
        Assert.DoesNotContain(journal.Lines, l => l.Contains("posted", StringComparison.Ordinal));

        engine.MainContext.RunFrame();

        Assert.NotEqual(loopThread, postedFrom);
        Assert.Equal(["example.net:posted on loop thread True", "example.net:FrameStart"], journal.Lines.Skip(3));
    }

    [Fact]
    public void Every_await_of_a_lifecycle_method_resumes_on_the_loop_thread()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var resumedOn = new List<int>();
        Task? work = null;
        using Engine engine = _distribution.Module("example.io", behavior: m => m.OnInitialize = _ => work = Load()).Load();
        engine.MainContext.Start();
        int loopThread = Environment.CurrentManagedThreadId;

        CompleteOnAnotherThread(first);
        Assert.Empty(resumedOn);
        engine.MainContext.RunFrame();
        Assert.Equal([loopThread], resumedOn);

        CompleteOnAnotherThread(second);
        engine.MainContext.RunFrame();

        Assert.Equal([loopThread, loopThread], resumedOn);
        Assert.True(work!.IsCompletedSuccessfully);

        async Task Load()
        {
            await first.Task;
            resumedOn.Add(Environment.CurrentManagedThreadId);
            await second.Task;
            resumedOn.Add(Environment.CurrentManagedThreadId);
        }
    }

    [Fact]
    public void A_failed_continuation_is_charged_to_the_module_that_awaited()
    {
        var gate = new TaskCompletionSource();
        SynchronizationContext? testContext = SynchronizationContext.Current;
        using Engine engine = _distribution
            .Module("example.net", behavior: m => m.OnInitialize = _ => Connect())
            .Module("example.disk")
            .Policy("example.net", Nobara.Formats.Policy.Disable)
            .Policy("example.disk", Nobara.Formats.Policy.Disable)
            .Load();
        engine.MainContext.Start();

        CompleteOnAnotherThread(gate);

        // The continuation throws, and the method posts its exception to the
        // same synchronization point, which rethrows it at the next frame.
        engine.MainContext.RunFrame();
        engine.MainContext.RunFrame();

        Assert.False(engine.MainContext.IsModuleActive("example.net"));
        Assert.True(engine.MainContext.IsModuleActive("example.disk"));
        Assert.Same(testContext, SynchronizationContext.Current);

        async void Connect()
        {
            await gate.Task;
            throw new IOException("connection lost");
        }
    }

    [Fact]
    public void A_callback_posted_while_the_point_runs_waits_for_the_next_frame()
    {
        Journal journal = _distribution.Journal;
        ModuleContext module = null!;
        using Engine engine = _distribution.Module("example.net", behavior: m => m.OnRegister = c => module = c).Load();
        engine.MainContext.Start();

        module.Post(_ =>
        {
            journal.Add($"example.net:first {engine.MainContext.Frame}");
            module.Post(_ => journal.Add($"example.net:second {engine.MainContext.Frame}"));
        });
        engine.MainContext.RunFrame();
        engine.MainContext.RunFrame();

        Assert.Equal(["example.net:first 1", "example.net:second 2"], journal.Lines.Skip(3));
    }

    [Fact]
    public void The_callbacks_of_a_disabled_module_are_dropped()
    {
        int runs = 0;
        ModuleContext net = null!;
        using Engine engine = _distribution
            .Module("example.net", behavior: m => m.OnRegister = c =>
            {
                net = c;
                c.Loop.Subscribe("Input", new PhaseHandler(_ => throw new InvalidOperationException("broken")));
            })
            .Policy("example.net", Nobara.Formats.Policy.Disable)
            .Load();
        engine.MainContext.Start();

        engine.MainContext.RunFrame();
        net.Post(_ => runs++);
        engine.MainContext.RunFrame();

        Assert.False(engine.MainContext.IsModuleActive("example.net"));
        Assert.Equal(0, runs);
    }

    private static void CompleteOnAnotherThread(TaskCompletionSource source)
    {
        var thread = new Thread(() => source.SetResult());
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void A_module_in_disable_that_throws_in_a_phase_is_disabled_with_its_dependents_and_the_game_goes_on()
    {
        Journal journal = _distribution.Journal;
        using Engine engine = _distribution
            .Module("example.time", behavior: Ticks(journal))
            .Module("example.weather", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Simulation", new PhaseHandler(t =>
            {
                if (t.Tick == 2)
                {
                    throw new InvalidOperationException("no clouds");
                }
            })))
            .Module("example.crops", m => m.Requires("example.weather"), Ticks(journal))
            .Module("example.birds", m => m.Uses("example.weather"), Ticks(journal))
            .Policy("example.weather", Nobara.Formats.Policy.Disable)
            .Policy("example.crops", Nobara.Formats.Policy.Disable)
            .Policy("example.birds", Nobara.Formats.Policy.Disable)
            .Load();
        engine.MainContext.Start();

        _distribution.Clock.Advance(TimeSpan.FromMilliseconds(100));
        engine.MainContext.RunFrame();

        Assert.Equal(ContextState.Running, engine.MainContext.State);
        Assert.False(engine.MainContext.IsModuleActive("example.weather"));
        Assert.False(engine.MainContext.IsModuleActive("example.crops"));
        Assert.True(engine.MainContext.IsModuleActive("example.birds"));
        Assert.Equal(
            ["example.time:1", "example.crops:1", "example.birds:1", "example.time:2", "example.crops:Stop", "example.crops:Release",
             "example.weather:Stop", "example.weather:Release", "example.birds:lost example.weather", "example.birds:2", "example.time:3", "example.birds:3"],
            journal.Lines.SkipWhile(l => !l.EndsWith(":1", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_module_in_stop_that_throws_in_a_phase_stops_the_context_and_writes_a_crash_report()
    {
        Journal journal = _distribution.Journal;
        using Engine engine = _distribution
            .Module("example.time", behavior: Ticks(journal))
            .Module("example.weather", m => m.Requires("example.time"), m => m.OnRegister = c =>
            {
                c.AddEmergencyHook(_ => journal.Add("example.weather:emergency"));
                c.Loop.Subscribe("Simulation", new PhaseHandler(_ => throw new InvalidOperationException("no clouds")));
            })
            .Load(logToFile: true);
        engine.MainContext.Start();

        _distribution.Clock.Advance(TimeSpan.FromMilliseconds(40));
        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.RunFrame);

        Assert.Equal("example.weather", stop.ModuleId);
        Assert.Equal(LifecycleStep.Execution, stop.Step);
        Assert.True(File.Exists(stop.CrashReport));
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
        Assert.Equal(
            ["example.time:1", "example.weather:emergency", "example.weather:Stop", "example.time:Stop", "example.weather:Release", "example.time:Release"],
            journal.Lines.SkipWhile(l => l != "example.time:1"));
        Assert.Throws<InvalidOperationException>(engine.MainContext.RunFrame);
    }

    [Fact]
    public void A_posted_callback_that_throws_is_charged_to_the_module_that_posted_it()
    {
        ModuleContext module = null!;
        using Engine engine = _distribution
            .Module("example.net", behavior: m => m.OnRegister = c => module = c)
            .Policy("example.net", Nobara.Formats.Policy.Disable)
            .Load();
        engine.MainContext.Start();

        module.Post(_ => throw new IOException("connection lost"));
        engine.MainContext.RunFrame();

        Assert.False(engine.MainContext.IsModuleActive("example.net"));
        Assert.Contains(_distribution.Log.Entries, e => e.Phase == "Synchronization" && e.Message.StartsWith("Module 'example.net' threw during execution", StringComparison.Ordinal));
    }

    [Fact]
    public void A_module_in_log_that_throws_keeps_running()
    {
        int runs = 0;
        using Engine engine = _distribution
            .Module("example.stats", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Input", new PhaseHandler(_ =>
            {
                runs++;
                throw new InvalidOperationException("bad sample");
            })))
            .Policy("example.stats", Nobara.Formats.Policy.Log)
            .Load();
        engine.MainContext.Start();

        engine.MainContext.RunFrame();
        engine.MainContext.RunFrame();

        Assert.Equal(2, runs);
        Assert.True(engine.MainContext.IsModuleActive("example.stats"));
    }

    private static Action<TestModule> Ticks(Journal journal) => module => module.OnRegister = c =>
        c.Loop.Subscribe("Simulation", new PhaseHandler(t => journal.Add($"{module.Id}:{t.Tick}")));
}
