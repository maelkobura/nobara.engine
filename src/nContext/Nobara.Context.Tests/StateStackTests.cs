// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context.Tests.Support;
using Nobara.Core;
using F = Nobara.Formats;

namespace Nobara.Context.Tests;

/// <summary>The state stack: registration, transitions, and the run of a context from its first state to its stop.</summary>
public sealed class StateStackTests : IDisposable
{
    private const string Boot = "state:example.boot";
    private const string Menu = "state:example.menu";
    private const string Play = "state:example.play";
    private const string Pause = "state:example.pause";
    private const string Overlay = "state:example.overlay";
    private const string ServerBoot = "state:example.server_boot";
    private const string Running = "state:example.running";
    private const string Stopping = "state:example.stopping";

    private readonly ContextHarness _harness = new() { ClientState = Boot, ServerState = ServerBoot };
    private Probe _flow = null!;

    public void Dispose() => _harness.Dispose();

    // The state stack, as the module of the states resolved it.
    private IStateStack States => _flow.States;

    private static (string, F.Side)[] Declared(params string[] states) => states.Select(s => (s, F.Side.Unspecified)).ToArray();

    // A module that declares and registers the given states, with the code each one runs.
    private ContextHarness Flow(Dictionary<string, Action<JournalState>> states, F.Policy? policy = null, Action<Probe>? register = null) =>
        _harness.Module(
            "example.flow",
            Declared([.. states.Keys]),
            p =>
            {
                _flow = p;
                p.OnRegister = register;
                p.OnInitialize = probe =>
                {
                    foreach ((string id, Action<JournalState> setup) in states)
                    {
                        probe.State(id, setup, keepStatesBelowRunning: id == Overlay);
                    }
                };
            },
            policy: policy);

    [Theory]
    [InlineData(Side.Client)]
    [InlineData(Side.Server)]
    public void A_context_chains_its_states_until_the_process_stops_without_intervention(Side side)
    {
        // One module provides the states of both sides, as a game does.
        _harness.Module(
            "example.flow",
            [(Boot, F.Side.Client), (Menu, F.Side.Client), (Play, F.Side.Client), (Pause, F.Side.Client),
             (ServerBoot, F.Side.Server), (Running, F.Side.Server), (Stopping, F.Side.Server)],
            p => p.OnInitialize = probe =>
            {
                if (probe.Context.Side == Side.Client)
                {
                    probe.State(Boot, s => s.OnUpdate = (s, _) => s.States.Replace(Menu));
                    probe.State(Menu, s => s.OnUpdate = (s, _) => s.States.Replace(Play));
                    probe.State(Play, s => s.OnUpdate = (s, _) =>
                    {
                        if (s.Updates == 1)
                        {
                            s.States.Push(Pause);
                        }
                        else
                        {
                            s.States.PopAll();
                        }
                    });
                    probe.State(Pause, s => s.OnUpdate = (s, _) => s.States.Pop());
                }
                else
                {
                    probe.State(ServerBoot, s => s.OnUpdate = (s, _) => s.States.Replace(Running));
                    probe.State(Running, s => s.OnUpdate = (s, _) =>
                    {
                        if (s.Updates == 3)
                        {
                            s.States.Replace(Stopping);
                        }
                    });
                    probe.State(Stopping, s => s.OnUpdate = (s, _) => s.States.PopAll());
                }
            });

        int exitCode = Engine.Run(_harness.Options(side));

        Assert.Equal(0, exitCode);
        string[] expected = side == Side.Client
            ? ["boot:Enter", "boot:Exit", "menu:Enter", "menu:Exit", "play:Enter", "play:Suspend", "pause:Enter", "pause:Exit", "play:Resume", "play:Exit"]
            : ["server_boot:Enter", "server_boot:Exit", "running:Enter", "running:Exit", "stopping:Enter", "stopping:Exit"];
        Assert.Equal(expected, _harness.Journal);
        Assert.Contains(_harness.Log.Entries, e => e.Message == "The state stack is empty: the context stops.");
    }

    [Fact]
    public void The_initial_state_enters_at_the_first_frame()
    {
        Flow(new() { [Boot] = _ => { } });
        using Engine engine = _harness.Start();
        IStateStack states = States;

        Assert.Empty(_harness.Journal);
        Assert.Empty(states.States);
        Assert.True(states.HasPendingTransitions);
        Assert.Equal(Boot, states.InitialState);

        _harness.Frame(engine);

        Assert.Equal(["boot:Enter"], _harness.Journal);
        Assert.Equal([Boot], states.States);
        Assert.Equal(Boot, states.Active);
        Assert.False(states.HasPendingTransitions);
    }

    [Fact]
    public void A_pause_pushed_over_the_game_suspends_it_and_resumes_it_when_popped()
    {
        Flow(new()
        {
            [Boot] = s => s.OnEnter = s => s.States.Replace(Play),
            [Play] = s => s.LogUpdates = true,
            [Pause] = s => s.LogUpdates = true,
        });
        using Engine engine = _harness.Start();
        IStateStack states = States;
        _harness.Frame(engine, 2);

        states.Push(Pause);
        _harness.Frame(engine);
        Assert.Equal([Play, Pause], states.States);
        Assert.False(states.IsRunning(Play));
        Assert.True(states.IsRunning(Pause));

        states.Pop();
        _harness.Frame(engine);

        Assert.Equal(
            ["boot:Enter", "boot:Exit", "play:Enter", "play:Update@2", "play:Suspend", "pause:Enter", "pause:Update@3", "pause:Exit", "play:Resume", "play:Update@4"],
            _harness.Journal);
        Assert.Equal(Play, states.Active);
    }

    [Fact]
    public void A_state_that_keeps_the_states_below_running_neither_suspends_nor_stops_their_updates()
    {
        Flow(new()
        {
            [Boot] = s => s.LogUpdates = true,
            [Overlay] = s => s.LogUpdates = true,
        });
        using Engine engine = _harness.Start();
        IStateStack states = States;
        _harness.Frame(engine);

        states.Push(Overlay);
        _harness.Frame(engine);

        Assert.True(states.IsRunning(Boot));
        Assert.True(states.IsRunning(Overlay));
        Assert.Equal(["boot:Enter", "boot:Update@1", "overlay:Enter", "boot:Update@2", "overlay:Update@2"], _harness.Journal);
    }

    [Fact]
    public void A_transition_requested_during_a_tick_applies_at_the_next_frame()
    {
        Probe flow = null!;
        Flow(
            new() { [Boot] = _ => { }, [Pause] = _ => { } },
            register: p =>
            {
                flow = p;
                p.Context.Loop.Subscribe("Simulation", new PhaseHandler(t =>
                {
                    if (t.Tick == 1)
                    {
                        _harness.Add($"tick {t.Tick}: push over {flow.States.Active}");
                        flow.States.Push(Pause);
                    }
                }));
                p.Context.Loop.Subscribe("PostSimulation", new PhaseHandler(t => _harness.Add($"frame {t.Frame} after the tick: {flow.States.Active}")));
            });
        using Engine engine = _harness.Start();

        _harness.Frame(engine, 2);

        Assert.Equal(
            ["boot:Enter", $"tick 1: push over {Boot}", $"frame 1 after the tick: {Boot}", "boot:Suspend", "pause:Enter", $"frame 2 after the tick: {Pause}"],
            _harness.Journal);
    }

    [Fact]
    public void A_transition_requested_while_transitions_apply_waits_for_the_next_frame()
    {
        Flow(new()
        {
            [Boot] = s => s.OnEnter = s => s.States.Replace(Menu),
            [Menu] = _ => { },
        });
        using Engine engine = _harness.Start();
        IStateStack states = States;

        _harness.Frame(engine);
        Assert.Equal(Boot, states.Active);
        Assert.True(states.HasPendingTransitions);

        _harness.Frame(engine);
        Assert.Equal(Menu, states.Active);
    }

    [Fact]
    public void Several_transitions_of_one_frame_apply_in_order_and_the_stack_stops_the_context_only_when_it_stays_empty()
    {
        Flow(new() { [Boot] = _ => { }, [Menu] = _ => { }, [Pause] = _ => { } });
        using Engine engine = _harness.Start();
        IStateStack states = States;
        _harness.Frame(engine);

        states.PopAll();
        states.Push(Menu);
        states.Push(Pause);
        states.Pop();
        _harness.Frame(engine);

        Assert.Equal([Menu], states.States);
        Assert.False(engine.MainContext.IsStopRequested);
        Assert.Equal(["boot:Enter", "boot:Exit", "menu:Enter", "menu:Suspend", "pause:Enter", "pause:Exit", "menu:Resume"], _harness.Journal);

        states.Pop();
        _harness.Frame(engine);

        Assert.Empty(states.States);
        Assert.True(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void Wrong_requests_throw_in_the_code_that_makes_them()
    {
        Flow(new() { [Boot] = _ => { }, [Menu] = _ => { }, [Pause] = _ => { } });
        using Engine engine = _harness.Start();
        IStateStack states = States;
        _harness.Frame(engine);

        Assert.Equal(
            "Cannot push state 'state:example.credits': no module registers it on this side. (Parameter 'id')",
            Assert.Throws<ArgumentException>(() => states.Push("state:example.credits")).Message);
        Assert.Equal(
            "State 'state:example.boot' cannot be pushed: it is already in the stack (bottom to top: state:example.boot).",
            Assert.Throws<InvalidOperationException>(() => states.Push(Boot)).Message);

        states.Push(Pause);
        Assert.Equal(
            "State 'state:example.boot' cannot replace the active state: it is already in the stack below it (bottom to top: state:example.boot, state:example.pause).",
            Assert.Throws<InvalidOperationException>(() => states.Replace(Boot)).Message);

        states.Pop();
        states.Pop();
        Assert.Equal(
            "No state can be popped: the stack is empty once the transitions already requested apply.",
            Assert.Throws<InvalidOperationException>(states.Pop).Message);
        Assert.Throws<InvalidOperationException>(() => states.Replace(Menu));

        // The requests that were accepted still apply.
        _harness.Frame(engine);
        Assert.Equal(["boot:Enter", "boot:Suspend", "pause:Enter", "pause:Exit", "boot:Resume", "boot:Exit"], _harness.Journal);
    }

    [Fact]
    public void States_are_registered_while_modules_initialize()
    {
        Probe flow = null!;
        Flow(new() { [Boot] = _ => { } }, register: p => flow = p);
        using Engine engine = _harness.Start();

        var exception = Assert.Throws<InvalidOperationException>(() => flow.State(Menu));

        Assert.Equal("Module 'example.flow' cannot register state 'state:example.menu' during execution. States are registered while modules initialize.", exception.Message);
    }

    [Fact]
    public void A_state_declared_and_not_registered_refuses_the_start_and_names_the_module_and_the_state()
    {
        _harness.Module("example.flow", Declared(Boot, Menu), p => p.OnInitialize = probe => probe.State(Boot));
        using Engine engine = _harness.Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        var mismatch = Assert.IsType<StateMismatchException>(stop.InnerException);
        Assert.Equal(["State 'state:example.menu' is declared by module 'example.flow' and not registered on the client side."], mismatch.Differences);
        Assert.Contains(mismatch.Differences[0], stop.Message, StringComparison.Ordinal);
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
    }

    [Fact]
    public void A_state_registered_and_not_declared_refuses_the_start_and_names_the_module_and_the_state()
    {
        _harness.Module("example.flow", Declared(Boot), p => p.OnInitialize = probe =>
        {
            probe.State(Boot);
            probe.State("state:example.secret");
        });
        using Engine engine = _harness.Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        var mismatch = Assert.IsType<StateMismatchException>(stop.InnerException);
        Assert.Equal(["State 'state:example.secret' is registered by module 'example.flow', but no active module declares it on the client side."], mismatch.Differences);
    }

    [Fact]
    public void A_side_without_an_initial_state_refuses_the_start()
    {
        _harness.ServerState = null;
        Flow(new() { [Boot] = _ => { } });
        using Engine engine = _harness.Load(Side.Server);

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        Assert.Equal(
            "Module 'nobara.context' runs on the server side, and engine.nobara declares no initial server state. Add the 'server' attribute to <States>.",
            stop.InnerException!.Message);
    }

    [Fact]
    public void A_stop_request_that_the_active_state_does_not_handle_pops_every_state_and_stops_the_context()
    {
        Flow(new() { [Boot] = _ => { }, [Pause] = _ => { } });
        using Engine engine = _harness.Start();
        IStateStack states = States;
        _harness.Frame(engine);
        states.Push(Pause);
        _harness.Frame(engine);

        // Ctrl+C and the host call this method.
        engine.MainContext.RequestStop();
        Assert.False(engine.MainContext.IsStopRequested);
        _harness.Frame(engine);

        Assert.Equal(["boot:Enter", "boot:Suspend", "pause:Enter", "pause:StopRequested", "pause:Exit", "boot:Exit"], _harness.Journal);
        Assert.True(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void The_active_state_decides_what_a_stop_request_does()
    {
        using var cancellation = new CancellationTokenSource();
        _harness.ClientState = Running;
        Flow(new()
        {
            [Running] = s =>
            {
                s.OnUpdate = (s, _) =>
                {
                    if (s.Updates == 2)
                    {
                        cancellation.Cancel();
                    }
                };
                s.OnEvent = (s, e) =>
                {
                    s.States.Replace(Stopping);
                    return e is StopRequested;
                };
            },
            [Stopping] = s => s.OnUpdate = (s, _) => s.States.PopAll(),
        });
        using Engine engine = _harness.Start();

        engine.MainContext.Run(cancellation.Token);

        Assert.Equal(["running:Enter", "running:StopRequested", "running:Exit", "stopping:Enter", "stopping:Exit"], _harness.Journal);
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
    }

    [Fact]
    public void A_global_event_goes_to_the_active_state_then_to_its_default_action()
    {
        bool handle = true;
        Flow(new() { [Boot] = s => s.OnEvent = (_, _) => handle });
        using Engine engine = _harness.Start();
        IStateStack states = States;
        _harness.Frame(engine);

        states.Raise(new Ping(_harness));
        Assert.Equal(["boot:Enter"], _harness.Journal);
        _harness.Frame(engine);

        handle = false;
        states.Raise(new Ping(_harness));
        _harness.Frame(engine);

        Assert.Equal(["boot:Enter", "boot:Ping", "boot:Ping", "ping: default"], _harness.Journal);
    }

    [Fact]
    public void A_state_that_throws_follows_the_error_policy_of_its_module()
    {
        Flow(
            new()
            {
                [Boot] = s => s.OnUpdate = (s, _) => throw new InvalidOperationException("no logo"),
            },
            policy: F.Policy.Log);
        using Engine engine = _harness.Start();

        _harness.Frame(engine, 2);

        Assert.Equal(ContextState.Running, engine.MainContext.State);
        Assert.Equal(2, _harness.Log.Entries.Count(e => e.Source == "example.flow" && e.Exception?.Message == "no logo"));
    }

    [Fact]
    public void Updating_the_running_states_allocates_nothing()
    {
        Flow(new() { [Boot] = _ => { }, [Overlay] = _ => { } });
        using Engine engine = _harness.Start();
        _harness.Frame(engine);
        States.Push(Overlay);
        _harness.Frame(engine, 3);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _harness.Frame(engine, 100);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(States.IsRunning(Boot));
    }

    [Fact]
    public void A_context_stopped_by_a_module_leaves_its_states_without_exiting_them()
    {
        Probe flow = null!;
        Flow(new() { [Boot] = _ => { } }, register: p => flow = p);
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        flow.Context.RequestStop();
        engine.MainContext.Stop();

        Assert.Equal(["boot:Enter"], _harness.Journal);
        Assert.Contains(_harness.Log.Entries, e => e.Message == "The context stops with states in the stack, which do not exit: bottom to top: state:example.boot.");
    }

    /// <summary>A global event whose default action writes in the journal.</summary>
    private sealed class Ping(ContextHarness harness) : GlobalEvent
    {
        protected override void ApplyDefault(IStateStack states) => harness.Add("ping: default");
    }
}
