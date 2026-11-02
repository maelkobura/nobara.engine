// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context.Tests.Support;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;
using F = Nobara.Formats;

namespace Nobara.Context.Tests;

/// <summary>World sessions: their events, their loading tasks, the check of the world state left behind, and the states that require them.</summary>
public sealed class WorldSessionTests : IDisposable
{
    private const string Boot = "state:example.boot";
    private const string Loading = "state:example.loading";
    private const string Play = "state:example.play";
    private const string Disconnected = "state:example.disconnected";

    private readonly ContextHarness _harness = new() { ClientState = Boot, ServerState = Boot };
    private Probe _flow = null!;

    public void Dispose() => _harness.Dispose();

    private IWorldSessions Sessions => _flow.Sessions;

    private IStateStack States => _flow.States;

    // The module of the states: Boot, Play that requires a session, and the given ones.
    private void Flow(Dictionary<string, Action<JournalState>>? states = null)
    {
        states ??= [];
        states.TryAdd(Boot, _ => { });
        states.TryAdd(Play, _ => { });
        _harness.Module(
            "example.flow",
            states.Keys.Select(s => (s, F.Side.Unspecified)).ToArray(),
            p =>
            {
                _flow = p;
                p.OnInitialize = probe =>
                {
                    foreach ((string id, Action<JournalState> setup) in states)
                    {
                        probe.State(id, setup, requiresSession: id == Play);
                    }
                };
            });
    }

    // A module without states that writes the session events in the journal, then runs the given code.
    private void World(string id, Action<Probe>? setup = null, F.Policy? policy = null, string[]? dependsOn = null) =>
        _harness.Module(
            id,
            [],
            p => p.OnInitialize = probe =>
            {
                string name = probe.Id[(probe.Id.LastIndexOf('.') + 1)..];
                probe.Sessions.SubscribeStarting(probe.Context, s => probe.Harness.Add($"{name}:Starting {s.Number}"));
                probe.Sessions.SubscribeStarted(probe.Context, s => probe.Harness.Add($"{name}:Started {s.Number}"));
                probe.Sessions.SubscribeEnding(probe.Context, s => probe.Harness.Add($"{name}:Ending {s.Number}"));
                probe.Sessions.SubscribeEnded(probe.Context, s => probe.Harness.Add($"{name}:Ended {s.Number}"));
                setup?.Invoke(probe);
            },
            policy: policy,
            dependsOn: dependsOn);

    [Theory]
    [InlineData(Side.Client)]
    [InlineData(Side.Server)]
    public void Three_sessions_chain_in_one_context_and_the_modules_initialize_once(Side side)
    {
        _harness.DevelopmentMode = true;
        Flow();
        Probe world = null!;
        int chunks = 0;
        World("example.world", p =>
        {
            world = p;
            p.Sessions.SubscribeStarting(p.Context, _ => chunks = 4);
            p.Sessions.SubscribeEnded(p.Context, _ => chunks = 0);
            p.Sessions.RegisterWorldStateCounter(p.Context, "chunks", () => chunks);
        });
        using Engine engine = _harness.Start(side);
        _harness.Frame(engine);

        for (int number = 1; number <= 3; number++)
        {
            WorldSession session = Sessions.Start();
            Assert.Equal(SessionState.Starting, Sessions.State);
            _harness.Frame(engine);
            Assert.Equal(SessionState.Active, session.State);
            Assert.Equal(number, session.Number);
            Assert.Equal(4, chunks);

            Sessions.End();
            Assert.True(Sessions.HasPendingEnd);
            _harness.Frame(engine);
            Assert.Equal(SessionState.Ended, session.State);
            Assert.Equal(SessionEndReason.Requested, session.EndReason);
            Assert.Equal(SessionState.None, Sessions.State);
            Assert.Null(Sessions.Current);
        }

        Assert.Equal(1, world.Initializations);
        Assert.Equal(1, _flow.Initializations);
        Assert.Equal(
            ["boot:Enter",
             "world:Starting 1", "world:Started 1", "world:Ending 1", "world:Ended 1",
             "world:Starting 2", "world:Started 2", "world:Ending 2", "world:Ended 2",
             "world:Starting 3", "world:Started 3", "world:Ending 3", "world:Ended 3"],
            _harness.Journal);
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Handlers_run_in_the_load_order_of_their_modules_for_the_start_and_in_the_reverse_order_for_the_end()
    {
        Flow();
        Probe a = null!;

        // Declared first, but it requires example.a, which loads before it.
        World("example.b", dependsOn: ["example.a"]);
        World("example.a", p => a = p);
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        // Subscribed after example.b, still called before it.
        a.Sessions.SubscribeStarting(a.Context, _ => _harness.Add("a:Starting again"));
        a.Sessions.SubscribeEnded(a.Context, _ => _harness.Add("a:Ended again"));
        Sessions.Start();
        _harness.Frame(engine);
        Sessions.End();
        _harness.Frame(engine);

        Assert.Equal(
            ["boot:Enter",
             "a:Starting 1", "a:Starting again", "b:Starting 1", "a:Started 1", "b:Started 1",
             "b:Ending 1", "a:Ending 1", "b:Ended 1", "a:Ended again", "a:Ended 1"],
            _harness.Journal);
    }

    [Fact]
    public void The_session_is_active_once_an_async_task_and_a_job_finished_and_the_progress_is_weighted()
    {
        Flow();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var compute = new ManualResetEventSlim();
        JobHandle job = default;
        int resumedOn = 0;
        World("example.world", p => p.Sessions.SubscribeStarting(p.Context, s =>
        {
            s.Loading.AddAsync(
                "read the save",
                async (task, _) =>
                {
                    task.Report(0.5);
                    await read.Task;
                    resumedOn = Environment.CurrentManagedThreadId;
                },
                weight: 3);
            job = p.Context.Jobs.Schedule(new GateJob(compute));
            s.Loading.AddJob("generate", job, _ => _harness.Add("generate: applied"));
        }));
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        WorldSession session = Sessions.Start();
        _harness.Frame(engine);
        Assert.Equal(SessionState.Starting, session.State);
        Assert.Equal(new LoadingProgress(0.375, 0, 2), session.Loading.Progress);
        Assert.Equal(["read the save", "generate"], session.Loading.Tasks.Select(t => t.Name));
        Assert.All(session.Loading.Tasks, t => Assert.Equal("example.world", t.Module));

        compute.Set();
        Assert.True(SpinWait.SpinUntil(() => job.IsCompleted, TimeSpan.FromSeconds(10)));
        _harness.Frame(engine);
        Assert.Equal(new LoadingProgress(0.625, 1, 2), session.Loading.Progress);
        Assert.Equal(SessionState.Starting, session.State);

        // The input or output finishes on another thread.
        var io = new Thread(read.SetResult);
        io.Start();
        io.Join();
        Assert.Equal(0, resumedOn);
        _harness.Frame(engine);

        Assert.Equal(Environment.CurrentManagedThreadId, resumedOn);
        Assert.Equal(SessionState.Active, session.State);
        Assert.True(session.Loading.IsCompleted);
        Assert.Equal(new LoadingProgress(1, 2, 2), session.Loading.Progress);
        Assert.Equal(["boot:Enter", "world:Starting 1", "generate: applied", "world:Started 1"], _harness.Journal);
    }

    [Fact]
    public void A_failed_task_cancels_the_session_and_gives_the_reason_to_the_state_that_started_it()
    {
        WorldSession session = null!;
        Flow(new()
        {
            [Boot] = s => s.OnUpdate = (s, _) => s.States.Replace(Loading),
            [Loading] = s =>
            {
                s.OnEnter = _ => session = Sessions.Start();
                s.OnUpdate = (s, _) =>
                {
                    if (session.Failure is { } failure)
                    {
                        _harness.Add($"loading: {failure.Reason} ({failure.Module}, {failure.Task})");
                        s.States.Replace(Disconnected);
                    }
                    else if (session.State == SessionState.Active)
                    {
                        s.States.Replace(Play);
                    }
                };
            },
            [Disconnected] = _ => { },
        });
        World("example.a", p => p.Sessions.SubscribeStarting(p.Context, s => s.Loading.AddAsync("read the save", async (_, _) =>
        {
            await Task.Yield();
            throw new IOException("The save is missing.");
        })));
        LoadingTask mesh = null!;
        World("example.b", p => p.Sessions.SubscribeStarting(p.Context, s => mesh = s.Loading.Add("mesh")), dependsOn: ["example.a"]);
        using Engine engine = _harness.Start();

        _harness.Frame(engine, 6);

        Assert.Equal(
            ["boot:Enter", "boot:Exit", "loading:Enter", "a:Starting 1", "b:Starting 1",
             "b:Ending 1", "a:Ending 1", "b:Ended 1", "a:Ended 1",
             "loading: The save is missing. (example.a, read the save)", "loading:Exit", "disconnected:Enter"],
            _harness.Journal);
        Assert.Equal(SessionEndReason.Failed, session.EndReason);
        Assert.IsType<IOException>(session.Failure!.Exception);
        Assert.Equal(LoadingTaskState.Canceled, mesh.State);
        Assert.True(session.Loading.Cancellation.IsCancellationRequested);
        Assert.Equal(SessionState.None, Sessions.State);
        Assert.Equal(ContextState.Running, engine.MainContext.State);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
    }

    [Fact]
    public void A_session_ended_while_it_starts_cancels_its_tasks_and_skips_SessionStarted()
    {
        Flow();
        LoadingTask mesh = null!;
        World("example.world", p => p.Sessions.SubscribeStarting(p.Context, s => mesh = s.Loading.Add("mesh")));
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        WorldSession first = Sessions.Start();
        Sessions.End();
        _harness.Frame(engine);
        Assert.Equal(SessionState.Ended, first.State);

        WorldSession second = Sessions.Start();
        _harness.Frame(engine, 2);
        mesh.Report(0.25);
        Assert.Equal(0.25, second.Loading.Progress.Fraction);
        Sessions.End();
        _harness.Frame(engine);

        Assert.Equal(["boot:Enter", "world:Starting 2", "world:Ending 2", "world:Ended 2"], _harness.Journal);
        Assert.Equal(LoadingTaskState.Canceled, mesh.State);
        Assert.True(second.Loading.Cancellation.IsCancellationRequested);
        Assert.Equal(SessionEndReason.Requested, second.EndReason);
        Assert.Null(second.Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_world_state_counter_left_above_zero_is_reported_on_its_module_in_development_mode_only(bool developmentMode)
    {
        _harness.DevelopmentMode = developmentMode;
        Flow();
        int reads = 0;
        World("example.leaky", p => p.Sessions.RegisterWorldStateCounter(p.Context, "entities", () =>
        {
            reads++;
            return 1;
        }), policy: F.Policy.Log);
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        Sessions.Start();
        _harness.Frame(engine);
        Sessions.End();
        _harness.Frame(engine);

        LogEntry[] leaks = _harness.Log.Entries.Where(e => e.Exception is WorldStateLeakException).ToArray();
        if (developmentMode)
        {
            LogEntry leak = Assert.Single(leaks);
            Assert.Equal("example.leaky", leak.Source);
            var exception = (WorldStateLeakException)leak.Exception!;
            Assert.Equal(("example.leaky", "entities", 1L), (exception.Module, exception.Counter, exception.Count));
            Assert.Equal("Module 'example.leaky' keeps world state after the session ended: its counter 'entities' is 1, and should be 0.", exception.Message);
            Assert.Equal(1, reads);
        }
        else
        {
            Assert.Empty(leaks);
            Assert.Equal(0, reads);
        }

        Assert.Equal(ContextState.Running, engine.MainContext.State);
    }

    [Fact]
    public void A_leak_of_a_module_whose_policy_is_stop_stops_the_context()
    {
        _harness.DevelopmentMode = true;
        Flow();
        World("example.leaky", p => p.Sessions.RegisterWorldStateCounter(p.Context, "grids", () => 2));
        using Engine engine = _harness.Start();
        _harness.Frame(engine);
        Sessions.Start();
        _harness.Frame(engine);
        Sessions.End();

        Assert.Throws<EngineStopException>(() => _harness.Frame(engine));
        Assert.Contains(_harness.Log.Entries, e => e.Source == "example.leaky" && e.Exception is WorldStateLeakException);
    }

    [Fact]
    public void A_stop_request_during_a_session_ends_it_before_the_active_state_decides()
    {
        Flow(new() { [Boot] = s => s.OnUpdate = (s, _) => s.States.Replace(Play) });
        World("example.world");
        using Engine engine = _harness.Start();
        WorldSession session = Sessions.Start();
        _harness.Frame(engine, 2);
        Assert.Equal(Play, States.Active);

        engine.MainContext.RequestStop();
        _harness.Frame(engine);

        Assert.Equal(
            ["boot:Enter", "world:Starting 1", "world:Started 1", "boot:Exit", "play:Enter",
             "world:Ending 1", "world:Ended 1", "play:StopRequested", "play:Exit"],
            _harness.Journal);
        Assert.Equal(SessionEndReason.Stopping, session.EndReason);
        Assert.True(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void A_stack_that_becomes_empty_during_a_session_ends_it_after_the_states_exit()
    {
        Flow();
        World("example.world");
        using Engine engine = _harness.Start();
        WorldSession session = Sessions.Start();
        _harness.Frame(engine);

        States.PopAll();
        _harness.Frame(engine);

        Assert.Equal(["boot:Enter", "world:Starting 1", "world:Started 1", "boot:Exit", "world:Ending 1", "world:Ended 1"], _harness.Journal);
        Assert.Equal(SessionEndReason.Stopping, session.EndReason);
        Assert.True(engine.MainContext.IsStopRequested);
    }

    [Fact]
    public void Revoked_handlers_and_counters_are_no_longer_called_even_during_a_dispatch()
    {
        _harness.DevelopmentMode = true;
        Flow();
        SessionSubscription starting = null!;
        SessionSubscription ending = null!;
        SessionSubscription counter = null!;
        int reads = 0;
        _harness.Module("example.a", [], p => p.OnInitialize = probe =>
        {
            starting = probe.Sessions.SubscribeStarting(probe.Context, _ => _harness.Add("a:Starting"));
            ending = probe.Sessions.SubscribeEnding(probe.Context, _ => _harness.Add("a:Ending"));
            counter = probe.Sessions.RegisterWorldStateCounter(probe.Context, "chunks", () => ++reads);
        });

        // Loads after example.a, so its handler runs first at the end, and revokes the one of example.a.
        _harness.Module("example.b", [], p => p.OnInitialize = probe => probe.Sessions.SubscribeEnding(probe.Context, _ =>
        {
            _harness.Add("b:Ending");
            ending.Dispose();
        }), dependsOn: ["example.a"]);
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        starting.Dispose();
        counter.Dispose();
        counter.Dispose();
        Sessions.Start();
        _harness.Frame(engine);
        Sessions.End();
        _harness.Frame(engine);

        Assert.Equal(["boot:Enter", "b:Ending"], _harness.Journal);
        Assert.Equal(0, reads);
        Assert.False(starting.IsActive);
        Assert.False(ending.IsActive);
        Assert.False(counter.IsActive);
    }

    [Fact]
    public void A_state_that_requires_a_session_enters_only_while_one_is_active()
    {
        Flow();
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        const string Message = "State 'state:example.play' cannot be pushed: it requires a world session, and none is active. Start one with IWorldSessions.Start, and request the state once the session is active.";
        Assert.Equal(Message, Assert.Throws<InvalidOperationException>(() => States.Push(Play)).Message);

        Sessions.Start();
        Assert.Throws<InvalidOperationException>(() => States.Replace(Play));
        _harness.Frame(engine);
        Assert.Equal(SessionState.Active, Sessions.State);

        Sessions.End();
        Assert.Equal(Message, Assert.Throws<InvalidOperationException>(() => States.Push(Play)).Message);
        _harness.Frame(engine);

        Sessions.Start();
        _harness.Frame(engine);
        States.Replace(Play);
        _harness.Frame(engine);
        Assert.Equal(Play, States.Active);
    }

    [Fact]
    public void Wrong_uses_throw_in_the_code_that_makes_them()
    {
        Flow();
        LoadingTasks loading = null!;
        LoadingTask task = null!;
        World("example.world", p => p.Sessions.SubscribeStarting(p.Context, s =>
        {
            loading = s.Loading;
            task = s.Loading.Add("mesh");
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Loading.Add("light", weight: 0));
        }));
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        Assert.Equal("There is no world session to end.", Assert.Throws<InvalidOperationException>(Sessions.End).Message);
        Sessions.Start();
        Assert.Equal(
            "A world session cannot start: session 1 is starting. End it, and start the next one once State is None.",
            Assert.Throws<InvalidOperationException>(() => Sessions.Start()).Message);
        _harness.Frame(engine);

        Assert.Equal(
            "Loading task 'late' cannot be added: tasks are added by the SessionStarting handlers, while they run.",
            Assert.Throws<InvalidOperationException>(() => loading.Add("late")).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => task.Report(1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => task.Report(double.NaN));
        Assert.Single(loading.Tasks);
    }

    [Fact]
    public void A_handler_that_throws_follows_the_error_policy_of_its_module()
    {
        Flow();
        World("example.world", p => p.Sessions.SubscribeStarted(p.Context, _ => throw new InvalidOperationException("no sky")), policy: F.Policy.Log);
        using Engine engine = _harness.Start();
        _harness.Frame(engine);

        Sessions.Start();
        _harness.Frame(engine);

        Assert.Equal(SessionState.Active, Sessions.State);
        Assert.Contains(_harness.Log.Entries, e => e.Source == "example.world" && e.Exception?.Message == "no sky");
    }

    [Fact]
    public void Frames_of_a_starting_or_active_session_allocate_nothing()
    {
        Flow();
        LoadingTask mesh = null!;
        World("example.world", p => p.Sessions.SubscribeStarting(p.Context, s => mesh = s.Loading.Add("mesh")));
        using Engine engine = _harness.Start();
        _harness.Frame(engine);
        WorldSession session = Sessions.Start();
        _harness.Frame(engine, 3);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _harness.Frame(engine, 100);
        long starting = GC.GetAllocatedBytesForCurrentThread() - before;

        mesh.Complete();
        _harness.Frame(engine, 3);
        before = GC.GetAllocatedBytesForCurrentThread();
        _harness.Frame(engine, 100);
        long active = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, starting);
        Assert.Equal(0, active);
        Assert.Equal(SessionState.Active, session.State);
    }

    /// <summary>A job that waits until the test lets it finish.</summary>
    private readonly struct GateJob(ManualResetEventSlim gate) : IJob
    {
        public void Execute() => gate.Wait(TimeSpan.FromSeconds(10));
    }
}
