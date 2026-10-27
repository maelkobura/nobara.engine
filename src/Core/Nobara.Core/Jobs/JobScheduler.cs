// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Mathematics;
using Nobara.Core.Profiling;

namespace Nobara.Core.Jobs;

/// <summary>
/// The job system of a process: a fixed pool of worker threads, shared by
/// every context, and the jobs scheduled on it.
/// </summary>
/// <remarks>
/// <para>
/// Every structure is reused: records of jobs, edges between a job and the
/// jobs that depend on it, queues, and the copies of job structs. They grow
/// when more jobs are in flight than ever before, and never shrink, so a
/// steady workload allocates nothing.
/// </para>
/// <para>
/// One lock protects the bookkeeping. Worker threads and waiting loop threads
/// wait on it, and every completion wakes them.
/// </para>
/// </remarks>
internal sealed class JobScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Thread[] _workers;
    private readonly ThreadLocal<int> _running = new();
    private readonly Dictionary<Type, JobRunner> _runners = [];
    private readonly Dictionary<Type, JobRunner> _forRunners = [];
    private readonly Profiler _profiler;
    private readonly int _backgroundLimit;
    private readonly EntryQueue _tick = new();
    private readonly EntryQueue _background = new();

    private Record[] _records = new Record[64];
    private int _recordCount;
    private int[] _freeRecords = new int[64];
    private int _freeRecordCount;
    private int[] _edgeJob = new int[128];
    private int[] _edgeNext = new int[128];
    private int _edgeCount;
    private int _freeEdge = -1;
    private int[] _finishing = new int[16];
    private int _backgroundRunning;
    private bool _disposed;

    public JobScheduler(int workerCount, bool checksAccess, Profiler profiler)
    {
        WorkerCount = Math.Max(1, workerCount);
        ChecksAccess = checksAccess;
        _profiler = profiler;
        _backgroundLimit = Math.Max(1, WorkerCount - 1);
        _workers = new Thread[WorkerCount];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(Work) { IsBackground = true, Name = $"Nobara worker {i + 1}" };
            _workers[i].Start();
        }
    }

    public int WorkerCount { get; }

    public bool ChecksAccess { get; }

    public SingleRunner<T> RunnerOf<T>()
        where T : struct, IJob
    {
        lock (_gate)
        {
            if (!_runners.TryGetValue(typeof(T), out JobRunner? runner))
            {
                runner = new SingleRunner<T>();
                _runners.Add(typeof(T), runner);
            }

            return (SingleRunner<T>)runner;
        }
    }

    public ForRunner<T> ForRunnerOf<T>()
        where T : struct, IJobFor
    {
        lock (_gate)
        {
            if (!_forRunners.TryGetValue(typeof(T), out JobRunner? runner))
            {
                runner = new ForRunner<T>();
                _forRunners.Add(typeof(T), runner);
            }

            return (ForRunner<T>)runner;
        }
    }

    public JobHandle Schedule<T>(JobOwner owner, JobRunner<T> runner, in T job, int length, int batchSize, JobHandle dependsOn, in JobAccess access, JobPriority priority)
        where T : struct
    {
        RequireNotInJob("schedule a job");
        lock (_gate)
        {
            owner.RequireOpen();
            CheckHandle(dependsOn);
            CheckData(access);
            int index = NewRecord();
            ref Record record = ref _records[index];
            record.Runner = runner;
            record.Slot = runner.Store(job);
            record.Length = length;
            record.BatchSize = batchSize;
            record.BatchCount = runner.IsParallel ? IntMath.DivCeil(length, batchSize) : 1;
            record.Priority = priority;
            record.Owner = owner;
            record.Marker = _profiler.Enabled ? owner.MarkerOf(_profiler, runner) : null;
            var handle = new JobHandle(this, index, record.Generation);

            AddDependency(index, dependsOn);
            Track(index, handle, access);
            owner.InFlight++;
            if (_records[index].Pending == 0)
            {
                Ready(index);
            }

            return handle;
        }
    }

    public JobHandle Combine(JobOwner owner, ReadOnlySpan<JobHandle> handles)
    {
        RequireNotInJob("combine jobs");
        lock (_gate)
        {
            owner.RequireOpen();
            foreach (JobHandle dependency in handles)
            {
                CheckHandle(dependency);
            }

            int index = NewRecord();
            _records[index].Owner = owner;
            var handle = new JobHandle(this, index, _records[index].Generation);
            foreach (JobHandle dependency in handles)
            {
                AddDependency(index, dependency);
            }

            owner.InFlight++;
            if (_records[index].Pending == 0)
            {
                Ready(index);
            }

            return handle;
        }
    }

    public bool IsDone(JobHandle handle)
    {
        lock (_gate)
        {
            return IsDoneLocked(handle);
        }
    }

    public void Complete(JobHandle handle)
    {
        RequireNotInJob("wait for a job");
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                if (IsDoneLocked(handle))
                {
                    return;
                }

                if (!TryTake(allowBackground: false, handle, out batch))
                {
                    Monitor.Wait(_gate);
                    continue;
                }
            }

            Run(batch, countsAsBackground: false);
        }
    }

    // Waits until every job of a module is done, running ready jobs meanwhile.
    public void WaitFor(JobOwner owner)
    {
        RequireNotInJob("wait for jobs");
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                if (owner.InFlight == 0)
                {
                    return;
                }

                if (!TryTake(allowBackground: true, default, out batch))
                {
                    Monitor.Wait(_gate);
                    continue;
                }
            }

            Run(batch, countsAsBackground: false);
        }
    }

    [HotPath]
    public void CheckAccess(JobData data, bool write)
    {
        int running = _running.Value;
        if (running != 0)
        {
            // A job: the data must be declared. The record does not change
            // while its job runs, so it can be read without the lock.
            Record[] records = Volatile.Read(ref _records);
            ref Record record = ref records[running - 1];
            JobData[]? declared = record.Access;
            int start = write ? record.ReadCount : 0;
            int end = record.ReadCount + record.WriteCount;
            for (int i = start; i < end; i++)
            {
                if (ReferenceEquals(declared![i], data))
                {
                    return;
                }
            }

            throw new JobAccessException(
                $"Job '{record.Runner?.Name}' of module '{record.Owner?.ModuleId}' {(write ? "writes" : "reads")} '{data.Name}', which it does not declare{(write ? " as written" : string.Empty)}. Declare it in the JobAccess of the job.");
        }

        lock (_gate)
        {
            if (!IsDoneLocked(data.LastWriter))
            {
                throw new JobAccessException(
                    $"The loop thread {(write ? "writes" : "reads")} '{data.Name}' while job '{_records[data.LastWriter.Index].Runner?.Name}' of module '{_records[data.LastWriter.Index].Owner?.ModuleId}' writes it. Complete that job first.");
            }

            if (write)
            {
                List<JobHandle> readers = data.Readers;
                for (int i = 0; i < readers.Count; i++)
                {
                    JobHandle reader = readers[i];
                    if (!IsDoneLocked(reader))
                    {
                        throw new JobAccessException(
                            $"The loop thread writes '{data.Name}' while job '{_records[reader.Index].Runner?.Name}' of module '{_records[reader.Index].Owner?.ModuleId}' reads it. Complete that job first.");
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        foreach (Thread worker in _workers)
        {
            worker.Join();
        }

        _running.Dispose();
    }

    private void Work()
    {
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                while (!TryTake(_backgroundRunning < _backgroundLimit, default, out batch))
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Monitor.Wait(_gate);
                }

                if (batch.Background)
                {
                    _backgroundRunning++;
                }
            }

            Run(batch, batch.Background);
        }
    }

    private void Run(in Batch batch, bool countsAsBackground)
    {
        int previous = _running.Value;
        _running.Value = batch.Index + 1;
        ProfilerScope scope = batch.Marker is null ? default : batch.Marker.Begin();
        Exception? fault = null;
        try
        {
            batch.Runner.Run(batch.Slot, batch.Start, batch.End);
        }
        catch (Exception exception)
        {
            fault = exception;
        }

        scope.Dispose();
        _running.Value = previous;
        lock (_gate)
        {
            if (countsAsBackground)
            {
                _backgroundRunning--;
            }

            ref Record record = ref _records[batch.Index];
            if (fault is not null && record.Fault is null)
            {
                // The batches that no thread has taken yet do not run.
                record.Fault = new JobFault(record.Runner!.Name, record.Owner!.ModuleId, fault);
                record.OwnsFault = true;
                record.DoneBatches += record.BatchCount - record.NextBatch;
                record.NextBatch = record.BatchCount;
            }

            if (++record.DoneBatches == record.BatchCount)
            {
                Finish(batch.Index);
            }
        }
    }

    // Takes a batch: of the preferred job when it is ready, else of the first
    // ready tick job, else of the first ready background job if allowed.
    private bool TryTake(bool allowBackground, JobHandle preferred, out Batch batch)
    {
        if (preferred.Scheduler == this && TryClaim(preferred.Index, preferred.Generation, out batch))
        {
            return true;
        }

        return TryTake(_tick, out batch) || (allowBackground && TryTake(_background, out batch));
    }

    private bool TryTake(EntryQueue queue, out Batch batch)
    {
        while (queue.TryPeek(out int index, out int generation))
        {
            if (TryClaim(index, generation, out batch))
            {
                if (_records[index].NextBatch == _records[index].BatchCount)
                {
                    queue.Dequeue();
                }

                return true;
            }

            queue.Dequeue();
        }

        batch = default;
        return false;
    }

    private bool TryClaim(int index, int generation, out Batch batch)
    {
        ref Record record = ref _records[index];
        if (record.Generation != generation || !record.Queued || record.NextBatch >= record.BatchCount)
        {
            batch = default;
            return false;
        }

        int start = record.NextBatch * record.BatchSize;
        record.NextBatch++;
        batch = new Batch(
            index,
            record.Runner!,
            record.Slot,
            start,
            Math.Min(record.Length, start + record.BatchSize),
            record.Marker,
            record.Priority == JobPriority.Background);
        return true;
    }

    [HotPath]
    private bool IsDoneLocked(JobHandle handle) =>
        handle.Scheduler != this || handle.Index >= _recordCount || _records[handle.Index].Generation != handle.Generation;

    private int NewRecord()
    {
        int index;
        if (_freeRecordCount > 0)
        {
            index = _freeRecords[--_freeRecordCount];
        }
        else
        {
            if (_recordCount == _records.Length)
            {
                Array.Resize(ref _records, _records.Length * 2);
            }

            index = _recordCount++;
            _records[index].Generation = 1;
        }

        ref Record record = ref _records[index];
        record.FirstEdge = -1;
        record.Pending = 0;
        record.NextBatch = 0;
        record.DoneBatches = 0;
        record.BatchCount = 0;
        record.Queued = false;
        return index;
    }

    // Makes a job wait for another one, unless it is done or is the job itself.
    private void AddDependency(int index, JobHandle dependency)
    {
        if (dependency.Scheduler is null)
        {
            return;
        }

        if (dependency.Scheduler != this)
        {
            throw new ArgumentException("The job depends on a job of another engine.", nameof(dependency));
        }

        if (IsDoneLocked(dependency) || dependency.Index == index)
        {
            return;
        }

        int edge;
        if (_freeEdge >= 0)
        {
            edge = _freeEdge;
            _freeEdge = _edgeNext[edge];
        }
        else
        {
            if (_edgeCount == _edgeJob.Length)
            {
                Array.Resize(ref _edgeJob, _edgeJob.Length * 2);
                Array.Resize(ref _edgeNext, _edgeNext.Length * 2);
            }

            edge = _edgeCount++;
        }

        _edgeJob[edge] = index;
        _edgeNext[edge] = _records[dependency.Index].FirstEdge;
        _records[dependency.Index].FirstEdge = edge;
        _records[index].Pending++;
    }

    // Checked before a record is taken, so that a refused job leaves nothing behind.
    private void CheckHandle(JobHandle handle)
    {
        if (handle.Scheduler is not null && handle.Scheduler != this)
        {
            throw new ArgumentException("The job depends on a job of another engine.", nameof(handle));
        }
    }

    private void CheckData(in JobAccess access)
    {
        foreach (JobData data in access.Reads)
        {
            if (data.Scheduler != this)
            {
                throw new ArgumentException($"Data '{data.Name}' belongs to another engine.", nameof(access));
            }
        }

        foreach (JobData data in access.Writes)
        {
            if (data.Scheduler != this)
            {
                throw new ArgumentException($"Data '{data.Name}' belongs to another engine.", nameof(access));
            }
        }
    }

    // Orders the job after the earlier jobs that conflict with its accesses,
    // then records it as the last writer or a reader of each piece of data.
    private void Track(int index, JobHandle handle, in JobAccess access)
    {
        ReadOnlySpan<JobData> reads = access.Reads;
        ReadOnlySpan<JobData> writes = access.Writes;
        foreach (JobData data in reads)
        {
            AddDependency(index, data.LastWriter);
        }

        foreach (JobData data in writes)
        {
            AddDependency(index, data.LastWriter);
            foreach (JobHandle reader in data.Readers)
            {
                AddDependency(index, reader);
            }
        }

        foreach (JobData data in reads)
        {
            if (!Contains(writes, data))
            {
                // Readers that are done no longer order anything.
                List<JobHandle> readers = data.Readers;
                int kept = 0;
                for (int i = 0; i < readers.Count; i++)
                {
                    if (!IsDoneLocked(readers[i]))
                    {
                        readers[kept++] = readers[i];
                    }
                }

                readers.RemoveRange(kept, readers.Count - kept);
                readers.Add(handle);
            }
        }

        foreach (JobData data in writes)
        {
            data.LastWriter = handle;
            data.Readers.Clear();
        }

        ref Record record = ref _records[index];
        record.ReadCount = 0;
        record.WriteCount = 0;
        if (ChecksAccess && reads.Length + writes.Length > 0)
        {
            if (record.Access is null || record.Access.Length < reads.Length + writes.Length)
            {
                record.Access = new JobData[Math.Max(4, reads.Length + writes.Length)];
            }

            reads.CopyTo(record.Access);
            writes.CopyTo(record.Access.AsSpan(reads.Length));
            record.ReadCount = reads.Length;
            record.WriteCount = writes.Length;
        }
    }

    private static bool Contains(ReadOnlySpan<JobData> data, JobData item)
    {
        foreach (JobData candidate in data)
        {
            if (ReferenceEquals(candidate, item))
            {
                return true;
            }
        }

        return false;
    }

    private void Ready(int index)
    {
        ref Record record = ref _records[index];
        if (record.Fault is not null || record.BatchCount == 0)
        {
            Finish(index);
            return;
        }

        record.Queued = true;
        (record.Priority == JobPriority.Tick ? _tick : _background).Enqueue(index, record.Generation);
        Monitor.PulseAll(_gate);
    }

    // Ends a job, then the jobs that were waiting only for it: they become
    // ready, or end at once when they are canceled or have nothing to run.
    private void Finish(int index)
    {
        int count = 0;
        _finishing[count++] = index;
        while (count > 0)
        {
            int current = _finishing[--count];
            ref Record record = ref _records[current];
            JobFault? fault = record.Fault;
            JobOwner owner = record.Owner!;
            if (fault is not null && record.OwnsFault)
            {
                owner.ReportFault(fault);
            }

            record.Runner?.Free(record.Slot);
            int edge = record.FirstEdge;
            record.Generation++;
            record.Runner = null;
            record.Owner = null;
            record.Fault = null;
            record.OwnsFault = false;
            record.Marker = null;
            record.Queued = false;
            if (record.Access is not null)
            {
                Array.Clear(record.Access);
            }

            owner.InFlight--;
            if (_freeRecordCount == _freeRecords.Length)
            {
                Array.Resize(ref _freeRecords, _freeRecords.Length * 2);
            }

            _freeRecords[_freeRecordCount++] = current;

            while (edge >= 0)
            {
                int dependent = _edgeJob[edge];
                int next = _edgeNext[edge];
                _edgeNext[edge] = _freeEdge;
                _freeEdge = edge;
                edge = next;

                ref Record waiting = ref _records[dependent];
                if (fault is not null && waiting.Fault is null)
                {
                    // Canceled: the job will not run, and the failure is
                    // reported once, by the job that threw.
                    waiting.Fault = fault;
                }

                if (--waiting.Pending == 0)
                {
                    if (waiting.Fault is not null || waiting.BatchCount == 0)
                    {
                        if (count == _finishing.Length)
                        {
                            Array.Resize(ref _finishing, _finishing.Length * 2);
                        }

                        _finishing[count++] = dependent;
                    }
                    else
                    {
                        waiting.Queued = true;
                        (waiting.Priority == JobPriority.Tick ? _tick : _background).Enqueue(dependent, waiting.Generation);
                    }
                }
            }
        }

        Monitor.PulseAll(_gate);
    }

    private void RequireNotInJob(string action)
    {
        if (_running.Value != 0)
        {
            throw new InvalidOperationException($"A job cannot {action}. Declare the jobs it needs as dependencies, and schedule from the loop thread.");
        }
    }

    // The bookkeeping of a job. The generation changes when the job ends, so
    // the handles of a reused record are done.
    private struct Record
    {
        public int Generation;
        public JobRunner? Runner;
        public int Slot;
        public int Length;
        public int BatchSize;
        public int BatchCount;
        public int NextBatch;
        public int DoneBatches;
        public int Pending;
        public int FirstEdge;
        public bool Queued;
        public JobPriority Priority;
        public JobOwner? Owner;
        public JobFault? Fault;
        public bool OwnsFault;
        public ProfilerMarker? Marker;
        public JobData[]? Access;
        public int ReadCount;
        public int WriteCount;
    }

    private readonly record struct Batch(int Index, JobRunner Runner, int Slot, int Start, int End, ProfilerMarker? Marker, bool Background);

    // A queue of ready jobs, as index and generation, that grows and never shrinks.
    private sealed class EntryQueue
    {
        private long[] _items = new long[64];
        private int _head;
        private int _count;

        public void Enqueue(int index, int generation)
        {
            if (_count == _items.Length)
            {
                long[] larger = new long[_items.Length * 2];
                for (int i = 0; i < _count; i++)
                {
                    larger[i] = _items[(_head + i) % _items.Length];
                }

                _items = larger;
                _head = 0;
            }

            _items[(_head + _count) % _items.Length] = ((long)index << 32) | (uint)generation;
            _count++;
        }

        public bool TryPeek(out int index, out int generation)
        {
            if (_count == 0)
            {
                index = 0;
                generation = 0;
                return false;
            }

            long item = _items[_head];
            index = (int)(item >> 32);
            generation = (int)item;
            return true;
        }

        public void Dequeue()
        {
            _head = (_head + 1) % _items.Length;
            _count--;
        }
    }
}

/// <summary>Runs the jobs of one type, and keeps their copies between scheduling and execution.</summary>
internal abstract class JobRunner
{
    protected JobRunner(Type type)
    {
        Name = type.FullName ?? type.Name;
    }

    public string Name { get; }

    public abstract bool IsParallel { get; }

    public abstract void Run(int slot, int start, int end);

    public abstract void Free(int slot);
}

/// <summary>Keeps copies of the jobs of type <typeparamref name="T"/> in slots that are reused.</summary>
/// <remarks>Store and Free run under the lock of the scheduler. Run reads a slot that does not change while its job runs.</remarks>
internal abstract class JobRunner<T> : JobRunner
    where T : struct
{
    private T[] _jobs = new T[8];
    private int[] _free = new int[8];
    private int _freeCount;
    private int _used;

    protected JobRunner()
        : base(typeof(T))
    {
    }

    public int Store(in T job)
    {
        int slot;
        if (_freeCount > 0)
        {
            slot = _free[--_freeCount];
        }
        else
        {
            if (_used == _jobs.Length)
            {
                Array.Resize(ref _jobs, _jobs.Length * 2);
                Array.Resize(ref _free, _free.Length * 2);
            }

            slot = _used++;
        }

        _jobs[slot] = job;
        return slot;
    }

    public override void Free(int slot)
    {
        _jobs[slot] = default;
        _free[_freeCount++] = slot;
    }

    protected T Copy(int slot) => Volatile.Read(ref _jobs)[slot];
}

/// <summary>Runs jobs that run once.</summary>
internal sealed class SingleRunner<T> : JobRunner<T>
    where T : struct, IJob
{
    public override bool IsParallel => false;

    public override void Run(int slot, int start, int end)
    {
        T job = Copy(slot);
        job.Execute();
    }
}

/// <summary>Runs one batch of a parallel job.</summary>
internal sealed class ForRunner<T> : JobRunner<T>
    where T : struct, IJobFor
{
    public override bool IsParallel => true;

    public override void Run(int slot, int start, int end)
    {
        T job = Copy(slot);
        for (int i = start; i < end; i++)
        {
            job.Execute(i);
        }
    }
}

/// <summary>A failure of a job, reported once to the context of its module.</summary>
internal sealed class JobFault(string job, string moduleId, Exception exception)
{
    public string Job { get; } = job;

    public string ModuleId { get; } = moduleId;

    public Exception Exception { get; } = exception;

    public JobFailedException ToException() => new(Job, ModuleId, Exception);
}

/// <summary>The jobs of one module in one context.</summary>
internal sealed class JobOwner
{
    private readonly Dictionary<JobRunner, ProfilerMarker> _markers = [];

    public JobOwner(EngineContext context, ModuleRuntime module)
    {
        Context = context;
        Module = module;
    }

    public EngineContext Context { get; }

    public ModuleRuntime Module { get; }

    public string ModuleId => Module.Info.Id;

    // The jobs of the module that are not done. Changed under the lock of the scheduler.
    public int InFlight { get; set; }

    // Set once the module or its context is released: no job is scheduled
    // any more, and failures are logged instead of handed to the loop.
    public bool Closed { get; set; }

    public void RequireOpen()
    {
        if (Closed || Module.Disabled)
        {
            throw new InvalidOperationException(
                $"Module '{ModuleId}' cannot schedule jobs in the {Context.Name} context: it is {(Module.Disabled ? "disabled" : "released")}.");
        }
    }

    public ProfilerMarker MarkerOf(Profiler profiler, JobRunner runner)
    {
        if (!_markers.TryGetValue(runner, out ProfilerMarker? marker))
        {
            marker = profiler.CreateMarker(Context.Name, ModuleId, ProfileKind.Job, runner.Name);
            _markers.Add(runner, marker);
        }

        return marker;
    }

    public void ReportFault(JobFault fault)
    {
        if (Closed || Module.Disabled)
        {
            Module.Context.Logger.Error($"Job '{fault.Job}' of module '{ModuleId}' threw after its module stopped; the exception is ignored.", fault.Exception);
            return;
        }

        // The policy applies on the loop thread, at the next synchronization point.
        Context.Loop.Post(Module, static state => throw ((JobFault)state!).ToException(), fault);
    }
}
