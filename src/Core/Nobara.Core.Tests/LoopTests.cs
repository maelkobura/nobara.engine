// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>The loop of a context: phases, fixed tick, and subscriptions.</summary>
public sealed class LoopTests : IDisposable
{
    private static readonly TimeSpan Tick30 = TimeSpan.FromSeconds(1.0 / 30);

    private readonly TestDistribution _distribution = new() { Loop = StandardLoop() };

    public void Dispose() => _distribution.Dispose();

    internal static F.Loop StandardLoop(uint tickRate = 30)
    {
        var loop = new F.Loop { TickRate = tickRate };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "PostSimulation" });
        loop.Phases.Add(new F.Phase { Name = "Render", Side = F.Side.Client });
        return loop;
    }

    // A module that writes "id:Phase tick" each time one of its phases runs.
    private static Action<TestModule> Logs(Journal journal, params string[] phases) => module => module.OnRegister = context =>
    {
        foreach (string phase in phases)
        {
            context.Loop.Subscribe(phase, new PhaseHandler(time => journal.Add($"{module.Id}:{phase} {time.Tick}")));
        }
    };

    [Fact]
    public void Runs_three_modules_for_ten_ticks_at_30_Hz_then_stops_in_reverse_order()
    {
        Journal journal = _distribution.Journal;
        using Engine engine = _distribution
            .Module("example.time", m => m.Level = F.Level.Platform, Logs(journal, "Simulation"))
            .Module("example.creatures", m => m.Requires("example.time"), Logs(journal, "Simulation"))
            .Module("example.mobs", m => m.Requires("example.creatures"), Logs(journal, "Simulation"))
            .Load();

        engine.MainContext.Start();
        for (int frame = 0; frame < 10; frame++)
        {
            _distribution.Clock.Advance(Tick30);
            engine.MainContext.RunFrame();
        }

        engine.MainContext.Stop();

        string[] order = ["example.time", "example.creatures", "example.mobs"];
        Assert.Equal(
            [.. order.Select(m => m + ":Registration"), .. order.Select(m => m + ":Initialization"), .. order.Select(m => m + ":Start"),
             .. Enumerable.Range(1, 10).SelectMany(tick => order.Select(m => $"{m}:Simulation {tick}")),
             .. order.Reverse().Select(m => m + ":Stop"), .. order.Reverse().Select(m => m + ":Release")],
            journal.Lines);
        Assert.Equal(10UL, engine.MainContext.Tick);
        Assert.Contains(_distribution.Log.Entries, e => e.Message == "The client context is running: Input, Simulation (30 Hz), PostSimulation, Render.");
        Assert.Equal(
            ["Registration", "Initialization", "Start", "Stop", "Release"],
            _distribution.Log.Entries.Where(e => e.Source == "core" && e.Level == LogLevel.Information && e.Phase.Length > 0).Select(e => e.Phase).Distinct());
    }

    [Fact]
    public void Runs_phases_in_declared_order_and_skips_the_phases_of_the_other_side()
    {
        Journal journal = _distribution.Journal;
        using Engine client = _distribution.Module("example.all", behavior: Logs(journal, "Input", "Simulation", "PostSimulation")).Load();
        client.MainContext.Start();
        _distribution.Clock.Advance(Tick30);
        client.MainContext.RunFrame();

        Assert.Equal(["Input", "Simulation", "PostSimulation", "Render"], client.MainContext.Phases.Select(p => p.Name));
        Assert.Equal(["example.all:Input 0", "example.all:Simulation 1", "example.all:PostSimulation 1"], journal.Lines.Where(l => l.Contains(' ', StringComparison.Ordinal)));
    }

    [Fact]
    public void A_server_context_has_no_client_phase_and_refuses_a_subscription_to_one()
    {
        using Engine server = _distribution.Module("example.all", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Render", new PhaseHandler(_ => { }))).Load(Side.Server);

        EngineStopException stop = Assert.Throws<EngineStopException>(server.MainContext.Start);

        Assert.Equal(["Input", "Simulation", "PostSimulation"], server.MainContext.Phases.Select(p => p.Name));
        Assert.Contains("phase 'Render' runs only on the client side, and this is the server context", stop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_distribution_without_a_loop_runs_the_default_loop_at_30_Hz()
    {
        _distribution.Loop = null;
        using Engine engine = _distribution.Module("example.all").Load();

        Assert.Equal(["Input", "Simulation", "PostSimulation", "Render"], engine.MainContext.Phases.Select(p => p.Name));
        Assert.True(engine.MainContext.Phases[1].IsFixed);
        engine.MainContext.Start();
        _distribution.Clock.Advance(TimeSpan.FromSeconds(0.2));
        engine.MainContext.RunFrame();
        Assert.Equal(6UL, engine.MainContext.Tick);
    }

    [Fact]
    public void Refuses_an_unknown_phase_and_names_the_module_and_the_phases()
    {
        using Engine engine = _distribution.Module("example.all", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Physics", new PhaseHandler(_ => { }))).Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal(
            "Module 'example.all' cannot use phase 'Physics': the loop has no phase 'Physics'; its phases are Input, Simulation, PostSimulation, Render.",
            stop.InnerException!.Message);
    }

    [Fact]
    public void Refuses_a_subscription_after_registration()
    {
        using Engine engine = _distribution.Module("example.all", behavior: m => m.OnInitialize = c => c.Loop.Subscribe("Input", new PhaseHandler(_ => { }))).Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal(LifecycleStep.Initialization, stop.Step);
        Assert.Contains("cannot subscribe to phase 'Input' during initialization", stop.InnerException!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 0, 0.3)]
    [InlineData(1000.0 / 30, 1, 0)]
    [InlineData(100, 3, 0)]
    [InlineData(150, 4, 0.5)]
    public void Runs_as_many_ticks_as_the_accumulated_time_allows(double milliseconds, int ticks, double alpha)
    {
        double seenAlpha = -1;
        using Engine engine = _distribution.Module("example.all", behavior: m => m.OnRegister = c =>
            c.Loop.Subscribe("Render", new PhaseHandler(t => seenAlpha = t.Alpha))).Load();
        engine.MainContext.Start();

        _distribution.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        engine.MainContext.RunFrame();

        Assert.Equal((ulong)ticks, engine.MainContext.Tick);
        Assert.Equal(alpha, seenAlpha, 3);
    }

    [Fact]
    public void Carries_the_remainder_of_the_accumulator_to_the_next_frames()
    {
        using Engine engine = _distribution.Module("example.all").Load();
        engine.MainContext.Start();

        var ticksPerFrame = new List<ulong>();
        ulong previous = 0;
        for (int frame = 0; frame < 6; frame++)
        {
            _distribution.Clock.Advance(TimeSpan.FromMilliseconds(20));
            engine.MainContext.RunFrame();
            ticksPerFrame.Add(engine.MainContext.Tick - previous);
            previous = engine.MainContext.Tick;
        }

        // 120 ms at 30 Hz: 3 ticks, spread over the frames where they fall due.
        Assert.Equal([0UL, 1, 0, 1, 1, 0], ticksPerFrame);
        Assert.Equal(6UL, engine.MainContext.Frame);
    }

    [Fact]
    public void Gives_a_fixed_phase_the_tick_duration_and_other_phases_the_real_frame_time()
    {
        var deltas = new List<(string Phase, TimeSpan Delta)>();
        using Engine engine = _distribution.Module("example.all", behavior: m => m.OnRegister = c =>
        {
            c.Loop.Subscribe("Simulation", new PhaseHandler(t => deltas.Add(("Simulation", t.Delta))));
            c.Loop.Subscribe("Render", new PhaseHandler(t => deltas.Add(("Render", t.Delta))));
        }).Load();
        engine.MainContext.Start();

        _distribution.Clock.Advance(TimeSpan.FromMilliseconds(70));
        engine.MainContext.RunFrame();

        Assert.Equal([("Simulation", Tick30), ("Simulation", Tick30), ("Render", TimeSpan.FromMilliseconds(70))], deltas);
    }

    [Fact]
    public void Catches_up_at_most_the_maximum_frame_time_after_a_long_pause()
    {
        using Engine engine = _distribution.Module("example.all").Load();
        engine.MainContext.Start();

        _distribution.Clock.Advance(TimeSpan.FromSeconds(10));
        engine.MainContext.RunFrame();

        Assert.Equal(7UL, engine.MainContext.Tick); // 250 ms at 30 Hz
        Assert.Contains(_distribution.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Frame 1 came 10000 ms after", StringComparison.Ordinal));
    }

    [Fact]
    public void Runs_the_handlers_of_the_start_of_the_frame_before_the_first_phase()
    {
        Journal journal = _distribution.Journal;
        using Engine engine = _distribution
            .Module("example.input", behavior: Logs(journal, "Input"))
            .Module("example.states", behavior: m => m.OnRegister = c => c.Loop.SubscribeFrameStart(new PhaseHandler(t => journal.Add($"example.states:FrameStart {t.Frame}"))))
            .Load();
        engine.MainContext.Start();

        engine.MainContext.RunFrame();

        Assert.Equal(["example.states:FrameStart 1", "example.input:Input 0"], journal.Lines.Where(l => l.Contains(' ', StringComparison.Ordinal)));
    }

    [Fact]
    public void A_disposed_subscription_no_longer_runs()
    {
        int runs = 0;
        LoopSubscription? subscription = null;
        using Engine engine = _distribution.Module("example.all", behavior: m => m.OnRegister = c =>
            subscription = c.Loop.Subscribe("Input", new PhaseHandler(_ => runs++))).Load();
        engine.MainContext.Start();

        engine.MainContext.RunFrame();
        subscription!.Dispose();
        engine.MainContext.RunFrame();

        Assert.Equal(1, runs);
        Assert.False(subscription.IsActive);
    }

    [Fact]
    public void A_frame_allocates_no_managed_memory()
    {
        var handler = new CountingHandler();
        using Engine engine = _distribution
            .Module("example.a", behavior: m => m.OnRegister = c =>
            {
                foreach (LoopPhase phase in c.Loop.Phases)
                {
                    c.Loop.Subscribe(phase.Name, handler);
                }

                c.Loop.SubscribeFrameStart(handler);
            })
            .Module("example.b", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Simulation", handler))
            .Load();
        engine.MainContext.Start();
        for (int i = 0; i < 10; i++)
        {
            _distribution.Clock.Advance(Tick30);
            engine.MainContext.RunFrame();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _distribution.Clock.Advance(TimeSpan.FromMilliseconds(50));
            engine.MainContext.RunFrame();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(handler.Runs > 100);
    }

    [Fact]
    public void Run_runs_frames_until_a_module_requests_a_stop_then_stops_the_context()
    {
        Journal journal = _distribution.Journal;
        using Engine engine = _distribution.Module("example.all", behavior: m => m.OnRegister = c =>
            c.Loop.Subscribe("Input", new PhaseHandler(t =>
            {
                _distribution.Clock.Advance(Tick30);
                if (t.Frame == 5)
                {
                    c.RequestStop();
                }
            }))).Load();

        engine.MainContext.Run();

        Assert.Equal(5UL, engine.MainContext.Frame);
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
        Assert.Equal(["example.all:Stop", "example.all:Release"], journal.Lines.TakeLast(2));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public void Run_with_tick_pacing_waits_for_each_tick(double timeScale)
    {
        using Engine engine = Engine.Load(_distribution
            .Module("example.game", m => m.Reserves.Add("loop.simulation"), m =>
            {
                m.OnRegister = c => c.Loop.Subscribe("Simulation", new PhaseHandler(t =>
                {
                    if (t.Tick == 10)
                    {
                        c.RequestStop();
                    }
                }));
                m.OnInitialize = c => c.Loop.GetTickControl().TimeScale = timeScale;
            })
            .Options(pacing: FramePacing.Tick));

        engine.MainContext.Run();

        // The first frame comes at once; each later one waits for its tick.
        Assert.Equal(10UL, engine.MainContext.Tick);
        Assert.Equal(11UL, engine.MainContext.Frame);
    }

    [Fact]
    public void Each_fixed_phase_runs_its_ticks_at_its_place_in_the_loop()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Physics", Fixed = true });
        loop.Phases.Add(new F.Phase { Name = "Between" });
        loop.Phases.Add(new F.Phase { Name = "Logic", Fixed = true });
        _distribution.Loop = loop;
        Journal journal = _distribution.Journal;
        using Engine engine = _distribution.Module("example.all", behavior: Logs(journal, "Physics", "Between", "Logic")).Load();
        engine.MainContext.Start();

        _distribution.Clock.Advance(TimeSpan.FromMilliseconds(70));
        engine.MainContext.RunFrame();

        Assert.Equal(
            ["example.all:Physics 1", "example.all:Physics 2", "example.all:Between 2", "example.all:Logic 1", "example.all:Logic 2"],
            journal.Lines.Where(l => l.Contains(' ', StringComparison.Ordinal)));
    }

    [Fact]
    public void Refuses_a_frame_inside_a_frame_and_a_stop_during_a_frame()
    {
        using Engine engine = _distribution
            .Module("example.all", behavior: m => m.OnRegister = c => c.Loop.Subscribe("Input", new PhaseHandler(_ =>
            {
                Assert.Throws<InvalidOperationException>(_distribution.Engine!.MainContext.RunFrame);
                Assert.Throws<InvalidOperationException>(_distribution.Engine!.MainContext.Stop);
                _distribution.Journal.Add("example.all:checked");
            })))
            .Load();
        _distribution.Engine = engine;
        engine.MainContext.Start();

        engine.MainContext.RunFrame();

        Assert.Contains("example.all:checked", _distribution.Journal.Lines);
        Assert.Equal(ContextState.Running, engine.MainContext.State);
    }

    [Fact]
    public void Refuses_a_declared_tick_rate_above_the_limit()
    {
        _distribution.Loop = StandardLoop(5000);
        ModuleCompositionException error = Assert.Throws<ModuleCompositionException>(() => _distribution.Module("example.all").Load());

        Assert.Contains("The loop declares 5000 ticks per second; the core runs at most 1000.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_context_does_not_run_again()
    {
        using Engine engine = _distribution.Module("example.all").Load();
        engine.MainContext.Start();
        engine.MainContext.Stop();

        Assert.Throws<InvalidOperationException>(() => engine.MainContext.Run());
    }

    [Fact]
    public void The_start_up_point_returns_0_after_a_stop_and_1_when_the_game_cannot_start()
    {
        _distribution.Module("example.all", behavior: m => m.OnRegister = c =>
            c.Loop.Subscribe("Input", new PhaseHandler(t => c.RequestStop())));
        Assert.Equal(0, Engine.Run(_distribution.Options()));

        using var broken = new TestDistribution();
        broken.Module("example.mobs", m => m.Requires("example.creatures"));
        Assert.Equal(1, Engine.Run(broken.Options()));
    }

    [Fact]
    public void The_start_up_point_returns_1_when_a_module_stops_the_game()
    {
        _distribution.Module("example.all", behavior: m => m.OnRegister = c =>
            c.Loop.Subscribe("Simulation", new PhaseHandler(_ => throw new InvalidOperationException("broken tick"))));
        int code = Engine.Run(_distribution.Options(clock: new AdvancingClock(_distribution.Clock, Tick30)));

        Assert.Equal(1, code);
        Assert.Contains(_distribution.Log.Entries, e => e.Level == LogLevel.Critical && e.Phase == "Simulation" && e.Message.Contains("module 'example.all' threw during execution", StringComparison.Ordinal));
    }

    private sealed class CountingHandler : IPhaseHandler
    {
        public int Runs { get; private set; }

        public void Run(in PhaseTime time) => Runs++;
    }

    // A clock that moves by a fixed step each time it is read.
    private sealed class AdvancingClock(ManualClock clock, TimeSpan step) : TimeProvider
    {
        public override long TimestampFrequency => clock.TimestampFrequency;

        public override long GetTimestamp()
        {
            clock.Advance(step);
            return clock.GetTimestamp();
        }
    }
}
