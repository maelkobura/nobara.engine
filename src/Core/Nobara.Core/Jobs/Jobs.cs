// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Jobs;

/// <summary>
/// A job that runs once on a worker thread. Implement it on a
/// <see langword="struct"/> whose fields hold what the job works on.
/// </summary>
/// <remarks>
/// The core copies the struct when the job is scheduled, so scheduling
/// allocates nothing. <see cref="Execute"/> is hot path code: the engine
/// analyzers check every implementation.
/// </remarks>
/// <example>
/// <code>
/// public struct SumJob : IJob
/// {
///     public NativeArray&lt;int&gt; Values;
///     public NativeArray&lt;long&gt; Total;
///
///     public void Execute()
///     {
///         long total = 0;
///         foreach (int value in Values.AsSpan()) total += value;
///         Total[0] = total;
///     }
/// }
/// </code>
/// </example>
public interface IJob
{
    /// <summary>Runs the job.</summary>
    [HotPath]
    void Execute();
}

/// <summary>
/// A job that runs once per index of a range, split into batches that worker
/// threads run in parallel: a <c>parallel for</c>. Implement it on a
/// <see langword="struct"/>.
/// </summary>
/// <remarks>
/// Each batch runs on a copy of the struct, so the job does not keep state
/// between indices in its fields. Indices of one batch run in increasing
/// order; batches run in any order, on any thread. <see cref="Execute"/> is
/// hot path code.
/// </remarks>
/// <example>
/// <code>
/// public struct ScaleJob : IJobFor
/// {
///     public NativeArray&lt;float&gt; Heights;
///     public float Factor;
///
///     public void Execute(int index) => Heights[index] *= Factor;
/// }
/// </code>
/// </example>
public interface IJobFor
{
    /// <summary>Runs the job for one index.</summary>
    /// <param name="index">The index, from 0 to the length given when the job was scheduled, minus 1.</param>
    [HotPath]
    void Execute(int index);
}

/// <summary>When a job runs, compared with the other ready jobs.</summary>
public enum JobPriority
{
    /// <summary>
    /// Work of the frame that runs, such as the systems of a tick or the
    /// preparation of rendering. Tick jobs run before background jobs, and a
    /// loop thread that waits for a job runs ready tick jobs itself.
    /// </summary>
    Tick,

    /// <summary>
    /// Work that may span several frames, such as generating or meshing
    /// chunks. Background jobs never occupy every worker thread when there
    /// are several, so tick jobs always find one.
    /// </summary>
    Background,
}

/// <summary>
/// Identifies a scheduled job: to wait for it, to test whether it is done,
/// or to make another job depend on it. The default value is a job that is
/// already done.
/// </summary>
/// <remarks>
/// A handle is a value: copying it allocates nothing. Once the job is done,
/// the handle stays done.
/// </remarks>
public readonly struct JobHandle : IEquatable<JobHandle>
{
    internal JobHandle(JobScheduler scheduler, int index, int generation)
    {
        Scheduler = scheduler;
        Index = index;
        Generation = generation;
    }

    /// <summary>Gets whether the job is done: it ran, or it failed, or it was canceled because a job it depends on failed.</summary>
    public bool IsCompleted => Scheduler is null || Scheduler.IsDone(this);

    internal JobScheduler? Scheduler { get; }

    internal int Index { get; }

    internal int Generation { get; }

    /// <summary>
    /// Waits until the job is done. While it waits, the calling thread runs
    /// ready tick jobs, starting with this one.
    /// </summary>
    /// <remarks>
    /// Call it from a loop thread, never from a job: a job declares the jobs
    /// it needs as dependencies instead. When the job failed, waiting does not
    /// throw: the exception is charged to its module at the next
    /// synchronization point of its context.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The calling thread runs a job.</exception>
    public void Complete() => Scheduler?.Complete(this);

    /// <inheritdoc/>
    public bool Equals(JobHandle other) => ReferenceEquals(Scheduler, other.Scheduler) && Index == other.Index && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Scheduler, Index, Generation);

    /// <summary>Tells whether two handles identify the same job.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);

    /// <summary>Tells whether two handles identify different jobs.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);
}

/// <summary>
/// The data that a job reads and writes. The scheduler runs a job only once
/// every earlier job that writes what it reads, or touches what it writes,
/// is done.
/// </summary>
/// <remarks>
/// It is a <see langword="ref struct"/> built where the job is scheduled,
/// usually from collection expressions, which do not allocate:
/// <code>
/// context.Jobs.Schedule(job, new JobAccess(reads: [_terrain], writes: [_heights]));
/// </code>
/// </remarks>
public readonly ref struct JobAccess
{
    /// <summary>Declares the data of a job.</summary>
    /// <param name="reads">The data the job only reads.</param>
    /// <param name="writes">The data the job writes, and may read.</param>
    public JobAccess(ReadOnlySpan<JobData> reads, ReadOnlySpan<JobData> writes)
    {
        Reads = reads;
        Writes = writes;
    }

    /// <summary>Gets the data the job only reads.</summary>
    public ReadOnlySpan<JobData> Reads { get; }

    /// <summary>Gets the data the job writes, and may read.</summary>
    public ReadOnlySpan<JobData> Writes { get; }
}

/// <summary>
/// Names a piece of data that jobs read and write, such as the storage of a
/// component or the cells of a chunk, so that jobs can declare their
/// accesses to it with <see cref="JobAccess"/>.
/// </summary>
/// <remarks>
/// <para>
/// Create it with <see cref="ModuleJobs.CreateData"/>, once per piece of
/// data. The code that owns the data calls <see cref="CheckRead"/> and
/// <see cref="CheckWrite"/> where it reads and writes it. In development mode
/// (<see cref="EngineOptions.DevelopmentMode"/>), they throw
/// <see cref="JobAccessException"/> when a job touches the data without
/// declaring it, or when a loop thread touches data that a job in progress
/// writes. Otherwise they return at once.
/// </para>
/// </remarks>
public sealed class JobData
{
    internal JobData(JobScheduler scheduler, string name)
    {
        Scheduler = scheduler;
        Name = name;
    }

    /// <summary>Gets the name of the data in error messages.</summary>
    public string Name { get; }

    internal JobScheduler Scheduler { get; }

    // The last job that writes the data, and the jobs that read it since.
    // Only the scheduler changes them, under its lock.
    internal JobHandle LastWriter { get; set; }

    internal List<JobHandle> Readers { get; } = [];

    /// <summary>Checks, in development mode, that the calling code may read the data.</summary>
    /// <exception cref="JobAccessException">
    /// In development mode: the calling job does not declare the data, or the
    /// caller is not a job and a job in progress writes the data.
    /// </exception>
    [HotPath]
    public void CheckRead()
    {
        if (Scheduler.ChecksAccess)
        {
            Scheduler.CheckAccess(this, write: false);
        }
    }

    /// <summary>Checks, in development mode, that the calling code may write the data.</summary>
    /// <exception cref="JobAccessException">
    /// In development mode: the calling job does not declare that it writes
    /// the data, or the caller is not a job and a job in progress reads or
    /// writes the data.
    /// </exception>
    [HotPath]
    public void CheckWrite()
    {
        if (Scheduler.ChecksAccess)
        {
            Scheduler.CheckAccess(this, write: true);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// The data that code on a loop thread declares it reads and writes, such as
/// a system of the simulation tick. In development mode, while the code runs
/// inside <see cref="Enter"/>, touching other data throws.
/// </summary>
/// <remarks>
/// <para>
/// Create it once with <see cref="ModuleJobs.DeclareLoopAccess"/>. Inside a
/// scope, in development mode, <see cref="JobData.CheckRead"/> and
/// <see cref="JobData.CheckWrite"/> on the same thread throw
/// <see cref="JobAccessException"/> for data that is not declared, or that is
/// declared as read only and written. Scheduling a job that declares data the
/// scope does not declare throws too: the job works on behalf of the code.
/// </para>
/// <para>
/// Outside development mode, <see cref="Enter"/> does nothing. Scopes nest:
/// disposing one restores the previous one.
/// </para>
/// </remarks>
public sealed class LoopAccess
{
    private readonly JobData[] _reads;
    private readonly JobData[] _writes;

    internal LoopAccess(JobScheduler scheduler, string name, string moduleId, JobData[] reads, JobData[] writes)
    {
        Scheduler = scheduler;
        Name = name;
        ModuleId = moduleId;
        _reads = reads;
        _writes = writes;
    }

    /// <summary>Gets the name of the code in error messages, such as <c>tick system 'example.farm.growth'</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the module that declared the access.</summary>
    public string ModuleId { get; }

    /// <summary>Gets the data the code only reads.</summary>
    public IReadOnlyList<JobData> Reads => _reads;

    /// <summary>Gets the data the code writes, and may read.</summary>
    public IReadOnlyList<JobData> Writes => _writes;

    internal JobScheduler Scheduler { get; }

    /// <summary>Starts a scope where the calling thread may touch only the declared data. Dispose it on the same thread.</summary>
    /// <returns>The scope; it does nothing outside development mode.</returns>
    /// <remarks>It allocates nothing, except the first time a thread enters a scope.</remarks>
    [HotPath]
    public LoopAccessScope Enter() => Scheduler.ChecksAccess ? Scheduler.EnterLoopAccess(this) : default;

    [HotPath]
    internal bool Allows(JobData data, bool write)
    {
        foreach (JobData candidate in _writes)
        {
            if (ReferenceEquals(candidate, data))
            {
                return true;
            }
        }

        if (!write)
        {
            foreach (JobData candidate in _reads)
            {
                if (ReferenceEquals(candidate, data))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>A scope opened by <see cref="LoopAccess.Enter"/>. Disposing it restores the previous scope of the thread.</summary>
public readonly struct LoopAccessScope : IDisposable
{
    private readonly JobScheduler? _scheduler;
    private readonly LoopAccess? _previous;

    [HotPath]
    internal LoopAccessScope(JobScheduler scheduler, LoopAccess? previous)
    {
        _scheduler = scheduler;
        _previous = previous;
    }

    /// <summary>Ends the scope.</summary>
    [HotPath]
    public void Dispose() => _scheduler?.ExitLoopAccess(_previous);
}

/// <summary>Thrown, in development mode, when code touches data that it did not declare, or that a job in progress uses.</summary>
public sealed class JobAccessException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What was touched, and by whom.</param>
    public JobAccessException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reports a job that threw. The core throws it at the synchronization point
/// of the context of the job, where the error policy applies; the exception
/// the job threw is the inner exception.
/// </summary>
public sealed class JobFailedException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="job">The type of the job.</param>
    /// <param name="moduleId">The module that scheduled the job.</param>
    /// <param name="innerException">The exception the job threw.</param>
    public JobFailedException(string job, string moduleId, Exception innerException)
        : base($"Job '{job}' of module '{moduleId}' threw: {innerException.Message}", innerException)
    {
        Job = job;
        ModuleId = moduleId;
    }

    /// <summary>Gets the type of the job.</summary>
    public string Job { get; }

    /// <summary>Gets the module that scheduled the job.</summary>
    public string ModuleId { get; }
}
