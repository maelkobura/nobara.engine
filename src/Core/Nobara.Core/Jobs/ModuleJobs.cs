// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Jobs;

/// <summary>
/// The job system as one module sees it, in one context: it schedules the
/// jobs of the module, which are charged to it when they fail.
/// </summary>
/// <remarks>
/// <para>
/// The worker threads are shared by every context of the process. Schedule
/// jobs and wait for them from the loop thread of the context, never from a
/// job. Once the subscriptions and the jobs of a frame are stable, scheduling
/// and waiting allocate nothing.
/// </para>
/// <para>
/// A job that throws fails: the jobs that depend on it do not run, and the
/// exception is charged, at the next synchronization point of the context,
/// to the module whose code threw, under its error policy. Before a module is
/// released, by a stop of its context or by its error policy, the core waits
/// for its jobs.
/// </para>
/// </remarks>
public sealed class ModuleJobs
{
    private readonly JobScheduler _scheduler;
    private readonly JobOwner _owner;

    internal ModuleJobs(JobScheduler scheduler, JobOwner owner)
    {
        _scheduler = scheduler;
        _owner = owner;
    }

    /// <summary>Gets the number of worker threads of the process.</summary>
    public int WorkerCount => _scheduler.WorkerCount;

    /// <summary>Gets whether the accesses that jobs declare are checked when they run (development mode).</summary>
    public bool ChecksAccess => _scheduler.ChecksAccess;

    /// <summary>Creates the name of a piece of data that jobs declare in their <see cref="JobAccess"/>.</summary>
    /// <param name="name">The name of the data in error messages, such as <c>heights of chunk 3,-1</c>.</param>
    /// <returns>The data.</returns>
    public JobData CreateData(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new JobData(_scheduler, name);
    }

    /// <summary>Schedules a job that runs once.</summary>
    /// <typeparam name="T">The type of the job.</typeparam>
    /// <param name="job">The job; it is copied.</param>
    /// <param name="dependsOn">A job that must be done before this one runs.</param>
    /// <param name="priority">When the job runs, compared with other ready jobs.</param>
    /// <returns>The handle of the job.</returns>
    /// <exception cref="InvalidOperationException">The calling thread runs a job, or the module is disabled or released.</exception>
    public JobHandle Schedule<T>(in T job, JobHandle dependsOn = default, JobPriority priority = JobPriority.Tick)
        where T : struct, IJob =>
        _scheduler.Schedule(_owner, _scheduler.RunnerOf<T>(), job, 1, 1, dependsOn, default, priority);

    /// <summary>Schedules a job that runs once and declares the data it reads and writes.</summary>
    /// <typeparam name="T">The type of the job.</typeparam>
    /// <param name="job">The job; it is copied.</param>
    /// <param name="access">
    /// The data the job reads and writes. The job runs once every earlier job
    /// that writes what it reads, or reads or writes what it writes, is done.
    /// </param>
    /// <param name="dependsOn">A job that must be done before this one runs.</param>
    /// <param name="priority">When the job runs, compared with other ready jobs.</param>
    /// <returns>The handle of the job.</returns>
    /// <exception cref="InvalidOperationException">The calling thread runs a job, or the module is disabled or released.</exception>
    public JobHandle Schedule<T>(in T job, in JobAccess access, JobHandle dependsOn = default, JobPriority priority = JobPriority.Tick)
        where T : struct, IJob =>
        _scheduler.Schedule(_owner, _scheduler.RunnerOf<T>(), job, 1, 1, dependsOn, access, priority);

    /// <summary>Schedules a job that runs once per index of a range, in batches that run in parallel.</summary>
    /// <typeparam name="T">The type of the job.</typeparam>
    /// <param name="job">The job; each batch runs on a copy.</param>
    /// <param name="length">The number of indices, from 0.</param>
    /// <param name="batchSize">The number of indices per batch: large enough that a batch takes longer than handing it to a thread.</param>
    /// <param name="dependsOn">A job that must be done before this one runs.</param>
    /// <param name="priority">When the job runs, compared with other ready jobs.</param>
    /// <returns>The handle of the job, done when every batch is.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, or <paramref name="batchSize"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The calling thread runs a job, or the module is disabled or released.</exception>
    public JobHandle ScheduleFor<T>(in T job, int length, int batchSize, JobHandle dependsOn = default, JobPriority priority = JobPriority.Tick)
        where T : struct, IJobFor
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return _scheduler.Schedule(_owner, _scheduler.ForRunnerOf<T>(), job, length, batchSize, dependsOn, default, priority);
    }

    /// <summary>Schedules a job that runs once per index of a range, in parallel batches, and declares the data it reads and writes.</summary>
    /// <typeparam name="T">The type of the job.</typeparam>
    /// <param name="job">The job; each batch runs on a copy.</param>
    /// <param name="length">The number of indices, from 0.</param>
    /// <param name="batchSize">The number of indices per batch.</param>
    /// <param name="access">The data the job reads and writes.</param>
    /// <param name="dependsOn">A job that must be done before this one runs.</param>
    /// <param name="priority">When the job runs, compared with other ready jobs.</param>
    /// <returns>The handle of the job, done when every batch is.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, or <paramref name="batchSize"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The calling thread runs a job, or the module is disabled or released.</exception>
    public JobHandle ScheduleFor<T>(in T job, int length, int batchSize, in JobAccess access, JobHandle dependsOn = default, JobPriority priority = JobPriority.Tick)
        where T : struct, IJobFor
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return _scheduler.Schedule(_owner, _scheduler.ForRunnerOf<T>(), job, length, batchSize, dependsOn, access, priority);
    }

    /// <summary>Combines several jobs into one handle, done when all of them are.</summary>
    /// <param name="handles">The jobs.</param>
    /// <returns>A handle to depend on or to wait for.</returns>
    /// <exception cref="InvalidOperationException">The calling thread runs a job, or the module is disabled or released.</exception>
    public JobHandle Combine(ReadOnlySpan<JobHandle> handles) => _scheduler.Combine(_owner, handles);
}
