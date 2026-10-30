// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using Nobara.Event.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Event.Tests;

/// <summary>The event module inside an engine: modules, contexts, and the loop.</summary>
public sealed class ModuleTests : IDisposable
{
    private readonly EventHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static Action<ModuleContext, IEventBus> Listens(Probe probe, EventPriority priority = EventPriority.Normal) =>
        (context, bus) => bus.Subscribe<Ping>(context, (ref Ping e) => probe.Add($"{probe.Id} ping {e.Value}"), priority);

    // Phases are subscribed during registration, before the bus can be resolved: the handler reads it when it runs.
    private static void In(ModuleContext context, string phase, Action<PhaseTime> action) => context.Loop.Subscribe(phase, new PhaseHandler(action));

    [Fact]
    public void Runs_the_handlers_of_modules_in_load_order()
    {
        // Both modules subscribe while they initialize, in the order they load.
        _harness
            .Module("example.first", p => p.OnInitialize = Listens(p))
            .Module("example.second", p => p.OnInitialize = Listens(p))
            .Module("example.emitter", p => p.OnStart = (_, bus) =>
            {
                var ping = new Ping { Value = 1 };
                bus.Emit(ref ping);
            });
        using Engine engine = _harness.Load();

        engine.MainContext.Start();
        engine.MainContext.Stop();

        Assert.Equal(["example.first ping 1", "example.second ping 1"], _harness.Journal);
    }

    [Fact]
    public void Publishes_the_bus_of_the_context()
    {
        IEventBus? bus = null;
        _harness.Module("example.a", p => p.OnInitialize = (_, b) => bus = b);
        using Engine engine = _harness.Load();

        engine.MainContext.Start();
        engine.MainContext.Stop();

        Assert.NotNull(bus);
    }

    [Fact]
    public void Refuses_the_monitor_priority_for_a_handler_that_may_change_the_event()
    {
        ArgumentException? error = null;
        _harness.Module("example.a", p => p.OnInitialize = (context, bus) =>
        {
            error = Assert.Throws<ArgumentException>(() => bus.Subscribe<Ping>(context, (ref Ping e) => { }, EventPriority.Monitor));
        });
        using Engine engine = _harness.Load();

        engine.MainContext.Start();
        engine.MainContext.Stop();

        Assert.NotNull(error);
        Assert.Contains("SubscribeMonitor", error.Message);
    }

    [Fact]
    public void Hands_deferred_events_out_after_the_simulation_of_the_tick()
    {
        _harness.Module("example.listener", p =>
        {
            p.OnRegister = context =>
            {
                In(context, "Simulation", time =>
                {
                    p.Add($"simulation {time.Tick}");
                    p.Bus.Post(new Ping { Value = (int)time.Tick });
                });
                In(context, "PostSimulation", time => p.Add("post-simulation"));
            };
            p.OnInitialize = (context, bus) => bus.Subscribe<Ping>(context, (ref Ping e) => p.Add($"event {e.Value}"));
        });
        using Engine engine = _harness.Load();
        engine.MainContext.Start();

        EventHarness.Frame(engine, _harness.Clock);
        engine.MainContext.Stop();

        // The event goes out in the phase after the simulation, before the handlers that other modules put there.
        Assert.Equal(["simulation 1", "event 1", "post-simulation"], _harness.Journal);
    }

    [Fact]
    public void Hands_out_together_the_events_of_a_frame_that_runs_several_ticks()
    {
        _harness.Module("example.listener", p =>
        {
            p.OnRegister = context => In(context, "Simulation", time =>
            {
                p.Add($"simulation {time.Tick}");
                p.Bus.Post(new Ping { Value = (int)time.Tick });
            });
            p.OnInitialize = (context, bus) => bus.Subscribe<Ping>(context, (ref Ping e) => p.Add($"event {e.Value}"));
        });
        using Engine engine = _harness.Load();
        engine.MainContext.Start();

        _harness.Clock.Advance(TimeSpan.FromSeconds(2.0 / 30));
        engine.MainContext.RunFrame();
        engine.MainContext.Stop();

        Assert.Equal(["simulation 1", "simulation 2", "event 1", "event 2"], _harness.Journal);
    }

    [Fact]
    public void Leaves_an_event_posted_by_a_handler_of_the_distribution_to_the_next_tick()
    {
        _harness.Module("example.listener", p =>
        {
            p.OnRegister = context => In(context, "Simulation", time =>
            {
                if (time.Tick == 1)
                {
                    p.Bus.Post(new Ping { Value = 1 });
                }
            });
            p.OnInitialize = (context, bus) => bus.Subscribe<Ping>(context, (ref Ping e) =>
            {
                p.Add($"event {e.Value} in frame {context.Loop.Frame}");
                if (e.Value < 2)
                {
                    bus.Post(new Ping { Value = e.Value + 1 });
                }
            });
        });
        using Engine engine = _harness.Load();
        engine.MainContext.Start();

        EventHarness.Frame(engine, _harness.Clock);
        EventHarness.Frame(engine, _harness.Clock);
        engine.MainContext.Stop();

        Assert.Equal(["event 1 in frame 1", "event 2 in frame 2"], _harness.Journal);
    }

    [Fact]
    public void Hands_events_out_at_the_start_of_the_next_frame_when_no_open_phase_follows_the_simulation()
    {
        var loop = new F.Loop { TickRate = 30 };
        loop.Phases.Add(new F.Phase { Name = "Input" });
        loop.Phases.Add(new F.Phase { Name = "Simulation", Fixed = true });
        _harness.Loop = loop;
        _harness.Module("example.listener", p =>
        {
            p.OnRegister = context => In(context, "Simulation", time => p.Bus.Post(new Ping()));
            p.OnInitialize = (context, bus) => bus.Subscribe<Ping>(context, (ref Ping e) => p.Add($"event in frame {context.Loop.Frame}"));
        });
        using Engine engine = _harness.Load();
        engine.MainContext.Start();

        EventHarness.Frame(engine, _harness.Clock);
        Assert.Empty(_harness.Journal);
        EventHarness.Frame(engine, _harness.Clock);
        engine.MainContext.Stop();

        Assert.Equal(["event in frame 2"], _harness.Journal);
    }

    [Fact]
    public void Refuses_a_synchronous_emission_from_a_worker_thread()
    {
        Exception? error = null;
        _harness.Module("example.a", p => p.OnStart = (_, bus) =>
        {
            error = OtherThread.Run(() =>
            {
                var ping = new Ping();
                bus.Emit(ref ping);
            });
        });
        using Engine engine = _harness.Load();

        engine.MainContext.Start();
        engine.MainContext.Stop();

        InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("loop thread", invalid.Message);
    }

    [Fact]
    public void Accepts_a_deferred_event_posted_from_a_worker_thread()
    {
        _harness.Module("example.a", p =>
        {
            p.OnRegister = context => In(context, "Input", time =>
            {
                Assert.Null(OtherThread.Run(() => p.Bus.Post(new Ping { Value = 5 })));
            });
            p.OnInitialize = (context, bus) => bus.Subscribe<Ping>(context, (ref Ping e) => p.Add($"event {e.Value}"));
        });
        using Engine engine = _harness.Load();
        engine.MainContext.Start();

        EventHarness.Frame(engine, _harness.Clock);
        engine.MainContext.Stop();

        Assert.Equal(["event 5"], _harness.Journal);
    }

    [Fact]
    public void Follows_the_thread_that_runs_the_frames()
    {
        Exception? error = null;
        _harness.Module("example.a", p => p.OnRegister = context => In(context, "Input", time =>
        {
            var ping = new Ping();
            error = Record.Exception(() => p.Bus.Emit(ref ping));
        }));
        using Engine engine = _harness.Load();

        // The context starts on this thread, and another thread then runs the frames.
        engine.MainContext.Start();
        Assert.Null(OtherThread.Run(() => EventHarness.Frame(engine, _harness.Clock)));
        engine.MainContext.Stop();

        Assert.Null(error);
    }

    [Fact]
    public void Revokes_the_handles_of_every_module_when_the_context_stops()
    {
        EventHandle? handle = null;
        _harness.Module("example.a", p => p.OnInitialize = (context, bus) => handle = bus.Subscribe<Ping>(context, (ref Ping e) => { }));
        using Engine engine = _harness.Load();

        engine.MainContext.Start();
        Assert.False(handle!.IsRevoked);
        engine.MainContext.Stop();

        Assert.True(handle.IsRevoked);
    }

    [Fact]
    public void Sends_no_event_to_a_module_the_core_disabled()
    {
        _harness
            .Module(
                "example.faulty",
                p =>
                {
                    p.OnInitialize = Listens(p);
                    p.OnStart = (_, _) => throw new InvalidOperationException("boom");
                },
                F.Policy.Disable)
            .Module("example.healthy", p => p.OnInitialize = Listens(p))
            .Module("example.emitter", p => p.OnRegister = context => In(context, "Input", time =>
            {
                var ping = new Ping { Value = 1 };
                p.Bus.Emit(ref ping);
            }));
        using Engine engine = _harness.Load();
        engine.MainContext.Start();
        Assert.False(engine.MainContext.IsModuleActive("example.faulty"));

        EventHarness.Frame(engine, _harness.Clock);
        engine.MainContext.Stop();

        Assert.Equal(["example.healthy ping 1"], _harness.Journal);
    }

    [Fact]
    public void Charges_a_failing_handler_to_its_module_in_the_log_and_keeps_going()
    {
        _harness
            .Module("example.faulty", p => p.OnInitialize = (context, bus) => bus.Subscribe<Ping>(context, (ref Ping e) => throw new InvalidOperationException("boom")))
            .Module("example.healthy", p => p.OnInitialize = Listens(p))
            .Module("example.emitter", p => p.OnStart = (_, bus) =>
            {
                var ping = new Ping { Value = 1 };
                bus.Emit(ref ping);
            });
        using Engine engine = _harness.Load();

        engine.MainContext.Start();
        engine.MainContext.Stop();

        Assert.Equal(["example.healthy ping 1"], _harness.Journal);
        LogEntry entry = Assert.Single(_harness.Log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("example.faulty", entry.Source);
        Assert.Contains("example.faulty", entry.Message);
        Assert.Contains("boom", entry.Exception!.Message);
    }

    private sealed class PhaseHandler(Action<PhaseTime> action) : IPhaseHandler
    {
        public void Run(in PhaseTime time) => action(time);
    }
}
