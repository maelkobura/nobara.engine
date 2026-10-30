// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Event.Tests.Support;

namespace Nobara.Event.Tests;

internal struct Ping
{
    public int Value;
}

internal struct Pong
{
    public int Value;
}

internal struct Request : ICancellable
{
    public int Value;

    public bool Cancelled { get; set; }
}

/// <summary>The bus on its own: modules are stand-ins, so that every rule can be checked without an engine.</summary>
public sealed class BusTests
{
    private readonly List<string> _calls = [];
    private readonly List<string> _failures = [];

    private Subscriber Module(string id, int rank, Func<bool>? isActive = null) =>
        new(id, rank, isActive ?? (() => true), (message, exception) => _failures.Add(message + " " + exception.Message));

    private EventHandle On(EventBus bus, Subscriber who, string name, EventPriority priority = EventPriority.Normal, bool ignoreCancelled = false) =>
        bus.Add<Ping>(who, (ref Ping e) => _calls.Add(name), null, priority, ignoreCancelled);

    [Fact]
    public void Runs_handlers_from_lowest_to_highest_then_monitors()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Ping>(module, null, (in Ping e) => _calls.Add("monitor"), EventPriority.Monitor, false);
        On(bus, module, "highest", EventPriority.Highest);
        On(bus, module, "normal");
        On(bus, module, "lowest", EventPriority.Lowest);
        On(bus, module, "high", EventPriority.High);
        On(bus, module, "low", EventPriority.Low);

        var ping = new Ping();
        bus.Emit(ref ping);

        Assert.Equal(["lowest", "low", "normal", "high", "highest", "monitor"], _calls);
    }

    [Fact]
    public void Breaks_ties_by_rank_of_module_then_by_order_of_subscription()
    {
        var bus = new EventBus();
        Subscriber early = Module("example.early", 0);
        Subscriber late = Module("example.late", 1);

        // The late module subscribes first, and twice: its rank still puts it after the early module.
        On(bus, late, "late 1");
        On(bus, late, "late 2");
        On(bus, early, "early 1");
        On(bus, early, "early 2");

        var ping = new Ping();
        bus.Emit(ref ping);

        Assert.Equal(["early 1", "early 2", "late 1", "late 2"], _calls);
    }

    [Fact]
    public void Gives_the_event_by_reference_and_the_emitter_reads_the_changes()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Ping>(module, (ref Ping e) => e.Value += 10, null, EventPriority.Low, false);
        bus.Add<Ping>(module, (ref Ping e) => e.Value *= 2, null, EventPriority.High, false);
        int seenByMonitor = 0;
        bus.Add<Ping>(module, null, (in Ping e) => seenByMonitor = e.Value, EventPriority.Monitor, false);

        var ping = new Ping { Value = 1 };
        bus.Emit(ref ping);

        Assert.Equal(22, ping.Value);
        Assert.Equal(22, seenByMonitor);
    }

    [Fact]
    public void Delivers_only_the_exact_type()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        On(bus, module, "ping");
        bus.Add<Pong>(module, (ref Pong e) => _calls.Add("pong"), null, EventPriority.Normal, false);

        var pong = new Pong();
        bus.Emit(ref pong);

        Assert.Equal(["pong"], _calls);
    }

    [Fact]
    public void Skips_handlers_that_ignore_cancelled_events_once_a_handler_cancelled()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Request>(module, (ref Request e) => _calls.Add("low"), null, EventPriority.Low, false);
        bus.Add<Request>(module, (ref Request e) => { _calls.Add("normal cancels"); e.Cancelled = true; }, null, EventPriority.Normal, false);
        bus.Add<Request>(module, (ref Request e) => _calls.Add("high ignores"), null, EventPriority.High, true);
        bus.Add<Request>(module, (ref Request e) => _calls.Add("highest does not ignore"), null, EventPriority.Highest, false);
        bus.Add<Request>(module, null, (in Request e) => _calls.Add($"monitor sees cancelled={e.Cancelled}"), EventPriority.Monitor, false);
        bus.Add<Request>(module, null, (in Request e) => _calls.Add("monitor ignores"), EventPriority.Monitor, true);

        var request = new Request();
        bus.EmitCancellable(ref request);

        Assert.True(request.Cancelled);
        Assert.Equal(["low", "normal cancels", "highest does not ignore", "monitor sees cancelled=True"], _calls);
    }

    [Fact]
    public void Runs_every_handler_of_an_event_nobody_cancels()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Request>(module, (ref Request e) => _calls.Add("ignores"), null, EventPriority.Normal, true);

        var request = new Request();
        bus.EmitCancellable(ref request);

        Assert.False(request.Cancelled);
        Assert.Equal(["ignores"], _calls);
    }

    [Fact]
    public void Refuses_a_cancellable_event_emitted_without_subscribers()
    {
        var bus = new EventBus();
        var request = new Request();

        Assert.Throws<InvalidOperationException>(() => bus.Emit(ref request));
    }

    [Fact]
    public void Refuses_a_cancellable_event_emitted_without_cancellation_and_deferred()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Request>(module, (ref Request e) => { }, null, EventPriority.Normal, false);
        var request = new Request();

        InvalidOperationException emitted = Assert.Throws<InvalidOperationException>(() => bus.Emit(ref request));
        InvalidOperationException posted = Assert.Throws<InvalidOperationException>(() => bus.Post(in request));

        Assert.Contains("EmitCancellable", emitted.Message);
        Assert.Contains("cancellable", posted.Message);
    }

    [Fact]
    public void Logs_a_failing_handler_with_its_module_and_runs_the_next_ones()
    {
        var bus = new EventBus();
        Subscriber faulty = Module("example.faulty", 0);
        Subscriber healthy = Module("example.healthy", 1);
        bus.Add<Ping>(faulty, (ref Ping e) => throw new InvalidOperationException("boom"), null, EventPriority.Normal, false);
        On(bus, healthy, "healthy");

        var ping = new Ping();
        bus.Emit(ref ping);

        Assert.Equal(["healthy"], _calls);
        string failure = Assert.Single(_failures);
        Assert.Contains("example.faulty", failure);
        Assert.Contains(typeof(Ping).FullName!, failure);
        Assert.Contains("boom", failure);
    }

    [Fact]
    public void Allows_a_handler_to_emit_another_event()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Ping>(module, (ref Ping e) =>
        {
            _calls.Add("ping");
            var pong = new Pong { Value = e.Value };
            bus.Emit(ref pong);
            _calls.Add("ping done");
        }, null, EventPriority.Normal, false);
        bus.Add<Pong>(module, (ref Pong e) => _calls.Add("pong " + e.Value), null, EventPriority.Normal, false);

        var ping = new Ping { Value = 7 };
        bus.Emit(ref ping);

        Assert.Equal(["ping", "pong 7", "ping done"], _calls);
    }

    [Fact]
    public void Refuses_nesting_beyond_the_maximum_depth()
    {
        var bus = new EventBus(maxDepth: 3);
        Subscriber module = Module("example.a", 0);
        int runs = 0;
        bus.Add<Ping>(module, (ref Ping e) =>
        {
            runs++;
            var next = new Ping { Value = e.Value + 1 };
            bus.Emit(ref next);
        }, null, EventPriority.Normal, false);

        var ping = new Ping();
        bus.Emit(ref ping);

        Assert.Equal(3, runs);
        string failure = Assert.Single(_failures);
        Assert.Contains("maximum depth", failure);

        // The depth is back to zero: the bus works again.
        runs = 0;
        _failures.Clear();
        bus.Emit(ref ping);
        Assert.Equal(3, runs);
    }

    [Fact]
    public void Refuses_a_synchronous_emission_from_another_thread()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        On(bus, module, "ping");

        Exception? error = OtherThread.Run(() =>
        {
            var ping = new Ping();
            bus.Emit(ref ping);
        });

        InvalidOperationException inner = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("loop thread", inner.Message);
        Assert.Contains("Post", inner.Message);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Accepts_emissions_from_the_thread_it_is_bound_to()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        On(bus, module, "ping");

        Exception? error = OtherThread.Run(() =>
        {
            bus.BindToCurrentThread();
            var ping = new Ping();
            bus.Emit(ref ping);
        });

        Assert.Null(error);

        Assert.Equal(["ping"], _calls);
    }

    [Fact]
    public void Tells_whether_a_type_has_subscribers()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        Assert.False(bus.HasSubscribers<Ping>());

        EventHandle handle = On(bus, module, "ping");
        Assert.True(bus.HasSubscribers<Ping>());
        Assert.False(bus.HasSubscribers<Pong>());

        handle.Dispose();
        Assert.False(bus.HasSubscribers<Ping>());
    }

    [Fact]
    public void Stops_calling_a_handler_once_its_handle_is_disposed()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        EventHandle first = On(bus, module, "first");
        On(bus, module, "second");

        first.Dispose();
        first.Dispose();
        var ping = new Ping();
        bus.Emit(ref ping);

        Assert.True(first.IsRevoked);
        Assert.Equal(["second"], _calls);
    }

    [Fact]
    public void Does_not_call_a_handler_revoked_by_an_earlier_handler_of_the_same_dispatch()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        EventHandle? victim = null;
        bus.Add<Ping>(module, (ref Ping e) => victim!.Dispose(), null, EventPriority.Low, false);
        victim = On(bus, module, "victim");

        var ping = new Ping();
        bus.Emit(ref ping);

        Assert.Empty(_calls);
    }

    [Fact]
    public void Calls_a_handler_added_during_a_dispatch_from_the_next_one()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Ping>(module, (ref Ping e) => On(bus, module, "added"), null, EventPriority.Low, false);

        var ping = new Ping();
        bus.Emit(ref ping);
        Assert.Empty(_calls);

        bus.Emit(ref ping);
        Assert.Contains("added", _calls);
    }

    [Fact]
    public void Revokes_every_handle_of_a_module_and_only_of_that_module()
    {
        var bus = new EventBus();
        Subscriber leaving = Module("example.leaving", 0);
        Subscriber staying = Module("example.staying", 1);
        EventHandle a = On(bus, leaving, "leaving 1");
        EventHandle b = bus.Add<Pong>(leaving, (ref Pong e) => _calls.Add("leaving 2"), null, EventPriority.Normal, false);
        EventHandle c = On(bus, staying, "staying");

        leaving.RevokeAll();
        var ping = new Ping();
        var pong = new Pong();
        bus.Emit(ref ping);
        bus.Emit(ref pong);

        Assert.True(a.IsRevoked);
        Assert.True(b.IsRevoked);
        Assert.False(c.IsRevoked);
        Assert.Equal(["staying"], _calls);
    }

    [Fact]
    public void Revokes_every_handle_of_the_bus()
    {
        var bus = new EventBus();
        EventHandle a = On(bus, Module("example.a", 0), "a");
        EventHandle b = On(bus, Module("example.b", 1), "b");

        bus.RevokeAll();

        Assert.True(a.IsRevoked);
        Assert.True(b.IsRevoked);
        Assert.False(bus.HasSubscribers<Ping>());
    }

    [Fact]
    public void Skips_the_handlers_of_a_module_that_is_no_longer_active()
    {
        var bus = new EventBus();
        bool active = true;
        On(bus, Module("example.a", 0, () => active), "a");
        On(bus, Module("example.b", 1), "b");

        active = false;
        var ping = new Ping();
        bus.Emit(ref ping);

        Assert.Equal(["b"], _calls);
    }

    [Fact]
    public void Hands_deferred_events_out_when_asked_and_not_before()
    {
        var bus = new EventBus();
        bus.Add<Ping>(Module("example.a", 0), (ref Ping e) => _calls.Add("ping " + e.Value), null, EventPriority.Normal, false);

        bus.Post(new Ping { Value = 1 });
        bus.Post(new Ping { Value = 2 });
        Assert.Empty(_calls);

        bus.DistributeDeferred();
        Assert.Equal(["ping 1", "ping 2"], _calls);

        bus.DistributeDeferred();
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public void Hands_deferred_events_of_different_types_out_in_the_order_they_were_posted()
    {
        var bus = new EventBus();
        Subscriber module = Module("example.a", 0);
        bus.Add<Ping>(module, (ref Ping e) => _calls.Add("ping " + e.Value), null, EventPriority.Normal, false);
        bus.Add<Pong>(module, (ref Pong e) => _calls.Add("pong " + e.Value), null, EventPriority.Normal, false);

        bus.Post(new Pong { Value = 1 });
        bus.Post(new Ping { Value = 1 });
        bus.Post(new Pong { Value = 2 });
        bus.Post(new Ping { Value = 2 });
        bus.DistributeDeferred();

        Assert.Equal(["pong 1", "ping 1", "pong 2", "ping 2"], _calls);
    }

    [Fact]
    public void Leaves_an_event_posted_during_the_distribution_to_the_next_one()
    {
        var bus = new EventBus();
        bus.Add<Ping>(Module("example.a", 0), (ref Ping e) =>
        {
            _calls.Add("ping " + e.Value);
            if (e.Value < 3)
            {
                bus.Post(new Ping { Value = e.Value + 1 });
            }
        }, null, EventPriority.Normal, false);

        bus.Post(new Ping { Value = 1 });
        bus.DistributeDeferred();
        Assert.Equal(["ping 1"], _calls);

        bus.DistributeDeferred();
        Assert.Equal(["ping 1", "ping 2"], _calls);
    }

    [Fact]
    public void Accepts_deferred_events_posted_from_several_threads()
    {
        var bus = new EventBus(queueCapacity: 2048);
        var received = new List<int>();
        bus.Add<Ping>(Module("example.a", 0), (ref Ping e) => received.Add(e.Value), null, EventPriority.Normal, false);

        const int Threads = 4;
        const int PerThread = 250;
        Thread[] producers = Enumerable.Range(0, Threads)
            .Select(thread => new Thread(() =>
            {
                for (int i = 0; i < PerThread; i++)
                {
                    bus.Post(new Ping { Value = (thread * 1000) + i });
                }
            }))
            .ToArray();
        foreach (Thread producer in producers)
        {
            producer.Start();
        }

        foreach (Thread producer in producers)
        {
            producer.Join();
        }

        bus.DistributeDeferred();

        Assert.Equal(Threads * PerThread, received.Count);
        for (int thread = 0; thread < Threads; thread++)
        {
            int[] mine = received.Where(v => v / 1000 == thread).ToArray();
            Assert.Equal(Enumerable.Range(0, PerThread).Select(i => (thread * 1000) + i), mine);
        }
    }

    [Fact]
    public void Refuses_a_deferred_event_when_the_queue_is_full()
    {
        var bus = new EventBus(queueCapacity: 4);
        bus.Add<Ping>(Module("example.a", 0), (ref Ping e) => _calls.Add("ping"), null, EventPriority.Normal, false);

        for (int i = 0; i < 4; i++)
        {
            bus.Post(new Ping());
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => bus.Post(new Ping()));
        Assert.Contains("full", error.Message);

        // Handing the events out frees the queue.
        bus.DistributeDeferred();
        bus.Post(new Ping());
        bus.DistributeDeferred();
        Assert.Equal(5, _calls.Count);
    }

    [Fact]
    public void Drops_a_deferred_event_nobody_subscribed_to()
    {
        var bus = new EventBus();

        bus.Post(new Ping { Value = 1 });
        bus.Add<Ping>(Module("example.a", 0), (ref Ping e) => _calls.Add("ping"), null, EventPriority.Normal, false);
        bus.DistributeDeferred();

        Assert.Empty(_calls);
    }

    [Fact]
    public void Allocates_nothing_when_emitting_or_handing_out_events()
    {
        var bus = new EventBus(queueCapacity: 4096);
        Subscriber module = Module("example.a", 0);
        int sum = 0;
        bus.Add<Ping>(module, (ref Ping e) => sum += e.Value, null, EventPriority.Low, false);
        bus.Add<Ping>(module, (ref Ping e) => sum += e.Value, null, EventPriority.High, true);
        bus.Add<Ping>(module, null, (in Ping e) => sum += e.Value, EventPriority.Monitor, false);
        bus.Add<Request>(module, (ref Request e) => e.Cancelled = e.Value > 5, null, EventPriority.Normal, false);
        bus.Add<Request>(module, (ref Request e) => sum++, null, EventPriority.High, true);

        void Work()
        {
            var ping = new Ping { Value = 1 };
            var request = new Request { Value = 9 };
            for (int i = 0; i < 1000; i++)
            {
                bus.Emit(ref ping);
                bus.EmitCancellable(ref request);
                bus.HasSubscribers<Ping>();
                bus.Post(in ping);
                bus.Post(in ping);
            }

            bus.DistributeDeferred();
        }

        // The first round compiles the generic code paths and fills the caches.
        Work();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Work();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(sum > 0);
    }

    [Fact]
    public void Refuses_the_monitor_priority_for_a_handler_that_may_change_the_event()
    {
        var bus = new EventBus();

        // The module is checked after the priority, so no module is needed.
        Assert.Throws<ArgumentException>(() => bus.Subscribe<Ping>(null!, (ref Ping e) => { }, EventPriority.Monitor));
    }
}
