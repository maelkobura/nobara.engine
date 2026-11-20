// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using BepuUtilities;
using BepuUtilities.Memory;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;

namespace Nobara.Physics.Bepu;

/// <summary>
/// The thread dispatcher that BEPUphysics spreads a step over, on the job
/// system of the core: it creates no thread. Worker 0 runs on the calling
/// thread, the loop thread; workers 1 to n - 1 are the indices of one
/// <see cref="IJobFor"/> scheduled in batches of one, which the calling
/// thread waits for.
/// </summary>
/// <remarks>
/// <para>
/// Each worker has its own pool of unmanaged memory, owned by the dispatcher
/// and released with it. One dispatch runs at a time: a world steps on one
/// thread at a time, and owns its dispatcher.
/// </para>
/// <para>
/// A dispatch to zero workers runs nothing: BEPUphysics then runs the work of
/// worker 0 itself. A worker that throws is logged at once, since the job
/// system reports it only at its next synchronization point.
/// </para>
/// </remarks>
internal sealed unsafe class JobThreadDispatcher : IThreadDispatcher, IDisposable
{
    private readonly ModuleJobs _jobs;
    private readonly Logger _logger;
    private readonly int _threadCount;
    private readonly WorkerBufferPools _pools;
    private delegate*<int, IThreadDispatcher, void> _unmanagedBody;
    private Action<int>? _managedBody;
    private void* _unmanagedContext;
    private object? _managedContext;
    private bool _disposed;

    /// <summary>Creates a dispatcher over the workers of the job system and the calling thread.</summary>
    /// <param name="jobs">The job system, as the module of the backend sees it.</param>
    /// <param name="logger">Where a worker that throws is reported.</param>
    /// <param name="threadCount">The number of workers, calling thread included; 0 for every worker of the job system plus the calling thread.</param>
    public JobThreadDispatcher(ModuleJobs jobs, Logger logger, int threadCount = 0)
    {
        _jobs = jobs;
        _logger = logger;
        _threadCount = threadCount > 0 ? threadCount : jobs.WorkerCount + 1;
        _pools = new WorkerBufferPools(_threadCount);
    }

    /// <inheritdoc/>
    public int ThreadCount => _threadCount;

    /// <inheritdoc/>
    public WorkerBufferPools WorkerPools => _pools;

    /// <inheritdoc/>
    public void* UnmanagedContext => _unmanagedContext;

    /// <inheritdoc/>
    public object? ManagedContext => _managedContext;

    /// <summary>Gets the number of dispatches made so far.</summary>
    public long Dispatches { get; private set; }

    /// <summary>Gets or sets a hook called by each worker before its work, with its index: the tests record the threads that run them.</summary>
    internal Action<int>? OnWorker { get; set; }

    /// <summary>Gets the unmanaged memory the pools of the workers hold, in bytes.</summary>
    public ulong AllocatedBytes => _pools.GetTotalAllocatedByteCount();

    /// <inheritdoc/>
    [HotPath]
    public void DispatchWorkers(delegate*<int, IThreadDispatcher, void> workerBody, int maximumWorkerCount = int.MaxValue, void* unmanagedContext = null, object? managedContext = null)
    {
        _unmanagedBody = workerBody;
        _unmanagedContext = unmanagedContext;
        _managedContext = managedContext;
        try
        {
            Run(maximumWorkerCount);
        }
        finally
        {
            _unmanagedBody = null;
            _unmanagedContext = null;
            _managedContext = null;
        }
    }

    /// <inheritdoc/>
    [HotPath]
    public void DispatchWorkers(Action<int> workerBody, int maximumWorkerCount = int.MaxValue, void* unmanagedContext = null, object? managedContext = null)
    {
        _managedBody = workerBody;
        _unmanagedContext = unmanagedContext;
        _managedContext = managedContext;
        try
        {
            Run(maximumWorkerCount);
        }
        finally
        {
            _managedBody = null;
            _unmanagedContext = null;
            _managedContext = null;
        }
    }

    /// <summary>Releases the pools of the workers.</summary>
    [HotPath]
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _pools.Dispose();
        }
    }

    [HotPath]
    private void Run(int maximumWorkerCount)
    {
        Dispatches++;
        int count = Math.Min(maximumWorkerCount, _threadCount);
        if (count <= 0)
        {
            return;   // BEPUphysics runs the work of worker 0 itself then
        }

        if (count == 1)
        {
            Invoke(0);
            return;
        }

        JobHandle workers = _jobs.ScheduleFor(new WorkerJob(this), count - 1, 1);
        try
        {
            Invoke(0);
        }
        catch
        {
            WaitQuietly(workers);   // the failure of worker 0 is the one reported
            throw;
        }

        workers.Complete();
    }

    /// <summary>Waits for the other workers, whose failure was logged already.</summary>
    [HotPath]
    private static void WaitQuietly(JobHandle workers)
    {
        try
        {
            workers.Complete();
        }
        catch (Exception)
        {
            // Logged by the worker that threw.
        }
    }

    [HotPath]
    private void Invoke(int worker)
    {
        OnWorker?.Invoke(worker);
        if (_unmanagedBody != null)
        {
            _unmanagedBody(worker, this);
        }
        else
        {
            _managedBody!(worker);
        }
    }

    [HotPath]
    private void Report(Exception exception) => _logger.Error("A worker of the physics step failed.", exception);

    /// <summary>Runs worker <c>index + 1</c> on a thread of the job system.</summary>
    [HotPath]
    private readonly struct WorkerJob(JobThreadDispatcher dispatcher) : IJobFor
    {
        [HotPath]
        public void Execute(int index)
        {
            try
            {
                dispatcher.Invoke(index + 1);
            }
            catch (Exception exception)
            {
                dispatcher.Report(exception);
                throw;
            }
        }
    }
}
