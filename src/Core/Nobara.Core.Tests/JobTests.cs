// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;
using Nobara.Core.Memory;
using Nobara.Core.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>The job system: scheduling, dependencies, parallel jobs, priorities, accesses, and failures.</summary>
public sealed class JobTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TestDistribution _distribution = new() { Workers = 4 };

    public void Dispose() => _distribution.Dispose();

    // Loads a distribution with one module, starts it, and returns the context of the module.
    private (Engine Engine, ModuleContext Context) Start(string id = "example.jobs", Action<TestModule>? behavior = null)
    {
        ModuleContext? context = null;
        Engine engine = _distribution.Module(id, behavior: module =>
        {
            module.OnInitialize = c => context = c;
            behavior?.Invoke(module);
        }).Load();
        engine.MainContext.Start();
        return (engine, context!);
    }

    [Fact]
    public void A_job_runs_on_a_worker_thread()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            var record = new Record();
            JobHandle handle = context.Jobs.Schedule(new RecordJob { Record = record, Name = "a" });

            Assert.True(SpinWait.SpinUntil(() => handle.IsCompleted, Timeout));
            Assert.Equal(["a"], record.Names);
            Assert.NotEqual(Environment.CurrentManagedThreadId, record.Threads.Single());
        }
    }

    [Fact]
    public void Complete_waits_for_the_job_and_may_run_it_on_the_waiting_thread()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            var record = new Record();
            JobHandle handle = context.Jobs.Schedule(new RecordJob { Record = record, Name = "a", Delay = 20 });

            handle.Complete();

            Assert.True(handle.IsCompleted);
            Assert.Equal(["a"], record.Names);
        }
    }

    [Fact]
    public void A_job_waits_for_its_dependencies_and_combined_handles_wait_for_all_of_them()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            var record = new Record();
            JobHandle first = context.Jobs.Schedule(new RecordJob { Record = record, Name = "first", Delay = 30 });
            JobHandle second = context.Jobs.Schedule(new RecordJob { Record = record, Name = "second", Delay = 10 });
            JobHandle both = context.Jobs.Combine([first, second]);
            JobHandle last = context.Jobs.Schedule(new RecordJob { Record = record, Name = "last" }, both);

            last.Complete();

            Assert.True(first.IsCompleted);
            Assert.True(second.IsCompleted);
            Assert.Equal("last", record.Names[^1]);
        }
    }

    [Fact]
    public void The_default_handle_and_an_empty_combination_are_done()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            Assert.True(default(JobHandle).IsCompleted);
            default(JobHandle).Complete();
            Assert.True(context.Jobs.Combine([]).IsCompleted);
        }
    }

    [Fact]
    public void A_parallel_job_runs_every_index_once_in_batches()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        using (var counts = new NativeArray<int>(10_000))
        {
            var threads = new ConcurrentDictionary<int, bool>();
            JobHandle handle = context.Jobs.ScheduleFor(new CountJob { Counts = counts, Threads = threads }, counts.Length, 64);
            handle.Complete();

            Assert.All(counts.AsSpan().ToArray(), count => Assert.Equal(1, count));
            Assert.Equal(4, context.Jobs.WorkerCount);
        }
    }

    [Fact]
    public void A_parallel_job_of_length_zero_is_done_once_its_dependencies_are()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        using (var counts = new NativeArray<int>(1))
        {
            using var gate = new ManualResetEventSlim();
            JobHandle blocker = context.Jobs.Schedule(new GateJob { Gate = gate });
            JobHandle empty = context.Jobs.ScheduleFor(new CountJob { Counts = counts, Threads = new() }, 0, 16, blocker);

            Assert.False(empty.IsCompleted);
            gate.Set();
            empty.Complete();
            Assert.Equal(0, counts[0]);
        }
    }

    [Fact]
    public void A_loop_thread_that_waits_runs_tick_jobs_while_background_jobs_hold_the_workers()
    {
        _distribution.Workers = 1;
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            using var gate = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            var record = new Record();
            JobHandle background = context.Jobs.Schedule(new GateJob { Gate = gate, Started = started }, priority: JobPriority.Background);
            Assert.True(started.Wait(Timeout));

            JobHandle tick = context.Jobs.Schedule(new RecordJob { Record = record, Name = "tick" });
            tick.Complete();

            Assert.Equal(Environment.CurrentManagedThreadId, record.Threads.Single());
            Assert.False(background.IsCompleted);
            gate.Set();
            background.Complete();
        }
    }

    [Fact]
    public void Background_jobs_leave_one_worker_to_tick_jobs()
    {
        _distribution.Workers = 2;
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            using var gate = new ManualResetEventSlim();
            var record = new Record();
            JobHandle first = context.Jobs.Schedule(new GateJob { Gate = gate }, priority: JobPriority.Background);
            JobHandle second = context.Jobs.Schedule(new GateJob { Gate = gate }, priority: JobPriority.Background);

            JobHandle tick = context.Jobs.Schedule(new RecordJob { Record = record, Name = "tick" });

            // A worker runs the tick job without the loop thread waiting for it.
            Assert.True(SpinWait.SpinUntil(() => tick.IsCompleted, Timeout));
            Assert.NotEqual(Environment.CurrentManagedThreadId, record.Threads.Single());
            Assert.False(first.IsCompleted && second.IsCompleted);
            gate.Set();
            context.Jobs.Combine([first, second]).Complete();
        }
    }

    [Fact]
    public void Declared_accesses_order_a_reader_after_the_writer_and_a_writer_after_the_readers()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            JobData heights = context.Jobs.CreateData("heights");
            using var gate = new ManualResetEventSlim();
            var record = new Record();

            JobHandle writer = context.Jobs.Schedule(new GateJob { Gate = gate, Record = record, Name = "writer" }, new JobAccess([], [heights]));
            JobHandle reader1 = context.Jobs.Schedule(new RecordJob { Record = record, Name = "reader" }, new JobAccess([heights], []));
            JobHandle reader2 = context.Jobs.Schedule(new RecordJob { Record = record, Name = "reader" }, new JobAccess([heights], []));
            JobHandle rewriter = context.Jobs.Schedule(new RecordJob { Record = record, Name = "rewriter" }, new JobAccess([], [heights]));

            Thread.Sleep(50);
            Assert.False(reader1.IsCompleted || reader2.IsCompleted || rewriter.IsCompleted);
            gate.Set();
            rewriter.Complete();

            Assert.True(writer.IsCompleted && reader1.IsCompleted && reader2.IsCompleted);
            Assert.Equal(["writer", "reader", "reader", "rewriter"], record.Names);
        }
    }

    [Fact]
    public void Independent_readers_run_together()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            JobData terrain = context.Jobs.CreateData("terrain");
            using var barrier = new Barrier(2);

            // Each reader waits for the other: they finish only if they run at the same time.
            JobHandle first = context.Jobs.Schedule(new BarrierJob { Barrier = barrier }, new JobAccess([terrain], []));
            JobHandle second = context.Jobs.Schedule(new BarrierJob { Barrier = barrier }, new JobAccess([terrain], []));

            Assert.True(SpinWait.SpinUntil(() => first.IsCompleted && second.IsCompleted, Timeout));
        }
    }

    [Fact]
    public void In_development_mode_a_job_that_touches_undeclared_data_fails_with_a_clear_error()
    {
        _distribution.DevelopmentMode = true;
        _distribution.Policy("example.jobs", F.Policy.Log);
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            JobData heights = context.Jobs.CreateData("heights");
            JobData terrain = context.Jobs.CreateData("terrain");

            context.Jobs.Schedule(new TouchJob { Data = heights, Write = false }, new JobAccess([heights], [])).Complete();
            context.Jobs.Schedule(new TouchJob { Data = heights, Write = true }, new JobAccess([], [heights])).Complete();
            context.Jobs.Schedule(new TouchJob { Data = terrain, Write = false }, new JobAccess([heights], [])).Complete();
            context.Jobs.Schedule(new TouchJob { Data = heights, Write = true }, new JobAccess([heights], [])).Complete();
            engine.MainContext.RunFrame();

            string[] errors = _distribution.Log.Entries.Where(e => e.Exception is JobFailedException).Select(e => e.Exception!.InnerException!.Message).ToArray();
            Assert.Equal(2, errors.Length);
            Assert.Contains(errors, e => e.Contains("reads 'terrain', which it does not declare", StringComparison.Ordinal) && e.Contains("TouchJob", StringComparison.Ordinal));
            Assert.Contains(errors, e => e.Contains("writes 'heights', which it does not declare as written", StringComparison.Ordinal) && e.Contains("module 'example.jobs'", StringComparison.Ordinal));
            Assert.True(engine.MainContext.IsModuleActive("example.jobs"));
        }
    }

    [Fact]
    public void In_development_mode_the_loop_thread_cannot_touch_data_that_a_job_in_progress_uses()
    {
        _distribution.DevelopmentMode = true;
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            JobData heights = context.Jobs.CreateData("heights");
            using var gate = new ManualResetEventSlim();

            JobHandle reader = context.Jobs.Schedule(new GateJob { Gate = gate }, new JobAccess([heights], []));
            heights.CheckRead();
            JobAccessException exception = Assert.Throws<JobAccessException>(heights.CheckWrite);
            Assert.Contains("writes 'heights' while job", exception.Message, StringComparison.Ordinal);
            Assert.Contains("GateJob", exception.Message, StringComparison.Ordinal);

            gate.Set();
            reader.Complete();
            heights.CheckWrite();
        }
    }

    [Fact]
    public void Without_development_mode_accesses_are_not_checked()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            JobData terrain = context.Jobs.CreateData("terrain");
            Assert.False(context.Jobs.ChecksAccess);

            context.Jobs.Schedule(new TouchJob { Data = terrain, Write = true }).Complete();
            engine.MainContext.RunFrame();

            Assert.DoesNotContain(_distribution.Log.Entries, e => e.Level >= LogLevel.Error);
        }
    }

    [Fact]
    public void A_job_that_throws_cancels_its_dependents_and_disables_its_module_at_the_next_frame()
    {
        _distribution.Policy("example.jobs", F.Policy.Disable).Policy("example.user", F.Policy.Disable);
        _distribution.Module("example.user", m => m.Requires("example.jobs"));
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            var record = new Record();
            JobHandle failing = context.Jobs.Schedule(new ThrowJob());
            JobHandle dependent = context.Jobs.Schedule(new RecordJob { Record = record, Name = "dependent" }, failing);

            dependent.Complete();
            Assert.True(failing.IsCompleted);
            Assert.Empty(record.Names);
            Assert.True(engine.MainContext.IsModuleActive("example.jobs"));

            engine.MainContext.RunFrame();

            Assert.False(engine.MainContext.IsModuleActive("example.jobs"));
            Assert.False(engine.MainContext.IsModuleActive("example.user"));
            LogEntry error = Assert.Single(_distribution.Log.Entries, e => e.Exception is JobFailedException);
            Assert.Contains("Job 'Nobara.Core.Tests.JobTests+ThrowJob' of module 'example.jobs' threw: the harvest failed", error.Exception!.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => context.Jobs.Schedule(new RecordJob { Record = record }));
        }
    }

    [Fact]
    public void A_job_that_throws_stops_the_context_when_its_module_policy_is_stop()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            context.Jobs.Schedule(new ThrowJob()).Complete();

            EngineStopException exception = Assert.Throws<EngineStopException>(engine.MainContext.RunFrame);

            Assert.Equal("example.jobs", exception.ModuleId);
            Assert.IsType<JobFailedException>(exception.InnerException);
            Assert.Equal(ContextState.Stopped, engine.MainContext.State);
        }
    }

    [Fact]
    public void A_job_cannot_schedule_or_wait_for_jobs()
    {
        _distribution.Policy("example.jobs", F.Policy.Log);
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        {
            JobHandle other = context.Jobs.Schedule(new RecordJob { Record = new Record(), Delay = 50 });
            context.Jobs.Schedule(new NestedJob { Jobs = context.Jobs, Other = other, Wait = false }).Complete();
            context.Jobs.Schedule(new NestedJob { Jobs = context.Jobs, Other = other, Wait = true }).Complete();
            engine.MainContext.RunFrame();

            string[] errors = _distribution.Log.Entries.Where(e => e.Exception is JobFailedException).Select(e => e.Exception!.InnerException!.Message).ToArray();
            Assert.Contains(errors, e => e.StartsWith("A job cannot schedule a job.", StringComparison.Ordinal));
            Assert.Contains(errors, e => e.StartsWith("A job cannot wait for a job.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Jobs_in_progress_are_done_before_their_module_is_released()
    {
        var record = new Record();
        bool doneAtRelease = false;
        JobHandle handle = default;
        (Engine engine, ModuleContext _) = Start(behavior: module =>
        {
            module.OnStart = c => handle = c.Jobs.Schedule(new RecordJob { Record = record, Name = "slow", Delay = 100 }, priority: JobPriority.Background);
            module.OnRelease = _ => doneAtRelease = record.Names.Contains("slow");
        });
        using (engine)
        {
            engine.MainContext.Stop();
        }

        Assert.True(doneAtRelease);
        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void Jobs_in_progress_are_done_before_a_disabled_module_is_released()
    {
        _distribution.Policy("example.jobs", F.Policy.Disable);
        var record = new Record();
        bool doneAtRelease = false;
        (Engine engine, ModuleContext context) = Start(behavior: module =>
        {
            module.OnRegister = c => c.Loop.Subscribe("Input", new PhaseHandler(t =>
            {
                if (t.Frame == 1)
                {
                    c.Jobs.Schedule(new RecordJob { Record = record, Name = "slow", Delay = 100 }, priority: JobPriority.Background);
                    throw new InvalidOperationException("the field flooded");
                }
            }));
            module.OnRelease = _ => doneAtRelease = record.Names.Contains("slow");
        });
        using (engine)
        {
            engine.MainContext.RunFrame();

            Assert.False(engine.MainContext.IsModuleActive("example.jobs"));
            Assert.True(doneAtRelease);
            Assert.Throws<InvalidOperationException>(() => context.Jobs.Schedule(new RecordJob { Record = record }));
        }
    }

    [Fact]
    public void A_failure_noticed_while_the_context_stops_is_logged()
    {
        (Engine engine, ModuleContext _) = Start(behavior: module => module.OnStart = c => c.Jobs.Schedule(new ThrowJob()));
        using (engine)
        {
            engine.MainContext.Stop();
        }

        Assert.Contains(_distribution.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("ThrowJob", StringComparison.Ordinal) && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void Scheduling_and_waiting_allocate_nothing_once_warm()
    {
        (Engine engine, ModuleContext context) = Start();
        using (engine)
        using (var values = new NativeArray<int>(1024))
        {
            JobData data = context.Jobs.CreateData("values");
            void Frame()
            {
                JobHandle fill = context.Jobs.ScheduleFor(new FillJob { Values = values }, values.Length, 128, new JobAccess([], [data]));
                JobHandle sum = context.Jobs.Schedule(new SumJob { Values = values }, new JobAccess([data], []));
                JobHandle other = context.Jobs.Schedule(new SumJob { Values = values }, fill);
                context.Jobs.Combine([sum, other]).Complete();
            }

            for (int i = 0; i < 200; i++)
            {
                Frame();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                Frame();
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    private sealed class Record
    {
        private readonly List<string> _names = [];
        private readonly List<int> _threads = [];

        public string[] Names
        {
            get
            {
                lock (_names)
                {
                    return [.. _names];
                }
            }
        }

        public int[] Threads
        {
            get
            {
                lock (_names)
                {
                    return [.. _threads];
                }
            }
        }

        public void Add(string name)
        {
            lock (_names)
            {
                _names.Add(name);
                _threads.Add(Environment.CurrentManagedThreadId);
            }
        }
    }

    private struct RecordJob : IJob
    {
        public Record Record;
        public string Name;
        public int Delay;

        public readonly void Execute()
        {
            if (Delay > 0)
            {
                Thread.Sleep(Delay);
            }

            Record.Add(Name);
        }
    }

    private struct GateJob : IJob
    {
        public ManualResetEventSlim Gate;
        public ManualResetEventSlim? Started;
        public Record? Record;
        public string Name;

        public readonly void Execute()
        {
            Started?.Set();
            Gate.Wait(Timeout);
            Record?.Add(Name);
        }
    }

    private struct BarrierJob : IJob
    {
        public Barrier Barrier;

        public readonly void Execute() => Barrier.SignalAndWait(Timeout);
    }

    private struct CountJob : IJobFor
    {
        public NativeArray<int> Counts;
        public ConcurrentDictionary<int, bool> Threads;

        public readonly void Execute(int index)
        {
            Interlocked.Increment(ref Counts[index]);
            Threads.TryAdd(Environment.CurrentManagedThreadId, true);
        }
    }

    private struct FillJob : IJobFor
    {
        public NativeArray<int> Values;

        public readonly void Execute(int index) => Values[index] = index;
    }

    private struct SumJob : IJob
    {
        public NativeArray<int> Values;

        public readonly void Execute()
        {
            long total = 0;
            foreach (int value in Values.AsSpan())
            {
                total += value;
            }

            Values[0] = (int)(total & 1);
        }
    }

    private struct TouchJob : IJob
    {
        public JobData Data;
        public bool Write;

        public readonly void Execute()
        {
            if (Write)
            {
                Data.CheckWrite();
            }
            else
            {
                Data.CheckRead();
            }
        }
    }

    private struct ThrowJob : IJob
    {
        public readonly void Execute() => throw new InvalidOperationException("the harvest failed");
    }

    private struct NestedJob : IJob
    {
        public ModuleJobs Jobs;
        public JobHandle Other;
        public bool Wait;

        public readonly void Execute()
        {
            if (Wait)
            {
                Other.Complete();
            }
            else
            {
                Jobs.Schedule(new ThrowJob());
            }
        }
    }
}
