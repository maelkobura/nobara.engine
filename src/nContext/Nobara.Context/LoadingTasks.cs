// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;

namespace Nobara.Context;

/// <summary>
/// The loading tasks of a world session: what the modules wait for before the
/// session is active, and their aggregated progress.
/// </summary>
/// <remarks>
/// Modules add tasks only while nContext calls their <c>SessionStarting</c>
/// handlers. A task belongs to the module whose handler adds it. Use it on the
/// loop thread of the context.
/// </remarks>
public sealed class LoadingTasks
{
    private readonly List<LoadingTask> _tasks = [];
    private readonly CancellationTokenSource _cancellation = new();
    private bool _completed;
    private bool _closed;

    internal LoadingTasks(LoadingScope scope)
    {
        Scope = scope;
        Cancellation = _cancellation.Token;
    }

    /// <summary>Gets what the tasks load: <see cref="LoadingScope.World"/> for the start of a session.</summary>
    public LoadingScope Scope { get; }

    /// <summary>Gets the tasks, in the order they were added.</summary>
    public IReadOnlyList<LoadingTask> Tasks => _tasks;

    /// <summary>Gets a token cancelled when the session is cancelled or ends before the tasks finish.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Gets whether every task finished its work.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Gets the aggregated progress. Reading it allocates nothing.</summary>
    public LoadingProgress Progress
    {
        get
        {
            double weights = 0;
            double done = 0;
            int completed = 0;
            for (int i = 0; i < _tasks.Count; i++)
            {
                LoadingTask task = _tasks[i];
                weights += task.Weight;
                done += task.Weight * task.Progress;
                if (task.State == LoadingTaskState.Completed)
                {
                    completed++;
                }
            }

            double fraction = weights > 0 ? Math.Min(1, done / weights) : (_completed ? 1 : 0);
            return new LoadingProgress(fraction, completed, _tasks.Count);
        }
    }

    // The module whose SessionStarting handler runs; null outside the dispatch.
    internal ModuleContext? Registrar { get; set; }

    /// <summary>
    /// Adds a task that the module drives itself: it reports the progress, and
    /// completes or fails the task, such as when the chunks around the player
    /// are meshed.
    /// </summary>
    /// <param name="name">The name of the task, for the loading screen and the logs.</param>
    /// <param name="weight">The share of the task in the progress, relative to the other tasks.</param>
    /// <returns>The task.</returns>
    /// <exception cref="InvalidOperationException">No <c>SessionStarting</c> handler runs.</exception>
    public LoadingTask Add(string name, double weight = 1) => Create(name, weight);

    /// <summary>
    /// Adds a task that waits for input or output, such as reading a save:
    /// nContext calls <paramref name="load"/> now, and the task completes when
    /// the returned <see cref="Task"/> does. Each <see langword="await"/>
    /// resumes on the loop thread, through the synchronization point of the
    /// module.
    /// </summary>
    /// <param name="name">The name of the task, for the loading screen and the logs.</param>
    /// <param name="load">The work; it receives the task, to report its progress, and <see cref="Cancellation"/>.</param>
    /// <param name="weight">The share of the task in the progress, relative to the other tasks.</param>
    /// <returns>The task.</returns>
    /// <remarks>An exception that <paramref name="load"/> throws fails the task, and the session.</remarks>
    /// <exception cref="InvalidOperationException">No <c>SessionStarting</c> handler runs.</exception>
    public LoadingTask AddAsync(string name, Func<LoadingTask, CancellationToken, Task> load, double weight = 1)
    {
        ArgumentNullException.ThrowIfNull(load);
        LoadingTask task = Create(name, weight);
        try
        {
            task.Async = load(task, Cancellation)
                ?? throw new InvalidOperationException($"The loading task '{name}' returned no task.");
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            task.Fail(exception.Message, exception);
        }

        return task;
    }

    /// <summary>
    /// Adds a task that waits for a job, such as a computation: the task
    /// completes once the job finishes and <paramref name="completed"/>, if
    /// any, applied its result on the loop thread.
    /// </summary>
    /// <param name="name">The name of the task, for the loading screen and the logs.</param>
    /// <param name="job">The job, scheduled by the module.</param>
    /// <param name="completed">Applies the result of the job; it can fail the task. <see langword="null"/> for none.</param>
    /// <param name="weight">The share of the task in the progress, relative to the other tasks.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// A job that throws follows the error policy of its module and does not
    /// fail the task: a job whose failure must cancel the session catches its
    /// exception, writes it in its result, and <paramref name="completed"/>
    /// fails the task. An exception that <paramref name="completed"/> throws
    /// fails the task.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <c>SessionStarting</c> handler runs.</exception>
    public LoadingTask AddJob(string name, JobHandle job, Action<LoadingTask>? completed = null, double weight = 1)
    {
        LoadingTask task = Create(name, weight);
        task.SetJob(job, completed);
        return task;
    }

    internal void Poll()
    {
        for (int i = 0; i < _tasks.Count; i++)
        {
            LoadingTask task = _tasks[i];
            if (task.State == LoadingTaskState.Running)
            {
                task.Poll();
            }
        }
    }

    internal LoadingTask? FirstFailed()
    {
        for (int i = 0; i < _tasks.Count; i++)
        {
            if (_tasks[i].State == LoadingTaskState.Failed)
            {
                return _tasks[i];
            }
        }

        return null;
    }

    internal bool AllCompleted()
    {
        for (int i = 0; i < _tasks.Count; i++)
        {
            if (_tasks[i].State != LoadingTaskState.Completed)
            {
                return false;
            }
        }

        return true;
    }

    internal void MarkCompleted() => _completed = true;

    // Once the tasks completed, frees the token; otherwise cancels it and the
    // tasks that still run. Returns the exception of the cancellation
    // callbacks, if any threw.
    internal AggregateException? Close()
    {
        if (_closed)
        {
            return null;
        }

        _closed = true;
        AggregateException? failure = null;
        if (!_completed)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (AggregateException exception)
            {
                failure = exception;
            }
        }

        _cancellation.Dispose();
        foreach (LoadingTask task in _tasks)
        {
            task.Cancel();
        }

        return failure;
    }

    private LoadingTask Create(string name, double weight)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!double.IsFinite(weight) || weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "The weight of a loading task is a finite number above 0.");
        }

        ModuleContext owner = Registrar
            ?? throw new InvalidOperationException($"Loading task '{name}' cannot be added: tasks are added by the SessionStarting handlers, while they run.");
        var task = new LoadingTask(owner, name, weight);
        _tasks.Add(task);
        return task;
    }
}

/// <summary>A loading task: a share of the work that a world session waits for before it is active.</summary>
public sealed class LoadingTask
{
    private double _progress;
    private JobHandle _job;
    private bool _hasJob;
    private Action<LoadingTask>? _completed;

    internal LoadingTask(ModuleContext owner, string name, double weight)
    {
        Owner = owner;
        Name = name;
        Weight = weight;
    }

    /// <summary>Gets the name of the task.</summary>
    public string Name { get; }

    /// <summary>Gets the identifier of the module that added the task.</summary>
    public string Module => Owner.Module.Id;

    /// <summary>Gets the share of the task in the progress, relative to the other tasks.</summary>
    public double Weight { get; }

    /// <summary>Gets where the task is.</summary>
    public LoadingTaskState State { get; private set; }

    /// <summary>Gets the progress of the task, from 0 to 1; 1 once it completed.</summary>
    public double Progress => State == LoadingTaskState.Completed ? 1 : Volatile.Read(ref _progress);

    /// <summary>Gets why the task failed, or <see langword="null"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Gets the exception that failed the task, or <see langword="null"/>.</summary>
    public Exception? Exception { get; private set; }

    internal ModuleContext Owner { get; }

    internal Task? Async { get; set; }

    /// <summary>Reports the progress of the task. It can be called from any thread.</summary>
    /// <param name="progress">The progress, from 0 to 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="progress"/> is not between 0 and 1.</exception>
    public void Report(double progress)
    {
        if (!(progress >= 0 && progress <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), progress, "The progress of a loading task is between 0 and 1.");
        }

        Volatile.Write(ref _progress, progress);
    }

    /// <summary>Completes the task. Does nothing once the task finished. Call it on the loop thread.</summary>
    public void Complete()
    {
        if (State == LoadingTaskState.Running)
        {
            State = LoadingTaskState.Completed;
        }
    }

    /// <summary>
    /// Fails the task, which cancels the session at the start of the next
    /// frame. Does nothing once the task finished. Call it on the loop thread.
    /// </summary>
    /// <param name="reason">Why the task failed, to show to the player.</param>
    /// <param name="exception">The exception that failed the task, or <see langword="null"/>.</param>
    public void Fail(string reason, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (State == LoadingTaskState.Running)
        {
            State = LoadingTaskState.Failed;
            FailureReason = reason;
            Exception = exception;
        }
    }

    internal void SetJob(JobHandle job, Action<LoadingTask>? completed)
    {
        _job = job;
        _hasJob = true;
        _completed = completed;
    }

    internal void Cancel()
    {
        if (State == LoadingTaskState.Running)
        {
            State = LoadingTaskState.Canceled;
        }
    }

    // Runs at the start of each frame while the task runs; allocates nothing
    // until the task finishes.
    internal void Poll()
    {
        if (!Owner.IsModuleActive(Owner.Module.Id))
        {
            Fail($"Module '{Module}' was disabled.");
            return;
        }

        if (Async is { IsCompleted: true } running)
        {
            if (running.IsCompletedSuccessfully)
            {
                Complete();
            }
            else if (running.Exception?.GetBaseException() is { } exception)
            {
                Fail(exception.Message, exception);
            }
            else
            {
                Fail("The task was canceled.");
            }
        }
        else if (_hasJob && _job.IsCompleted)
        {
            _hasJob = false;
            if (_completed is not null)
            {
                SynchronizationContext? previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(Owner.SynchronizationContext);
                try
                {
                    _completed(this);
                }
                catch (Exception exception) when (exception is not EngineStopException)
                {
                    Fail(exception.Message, exception);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }

            Complete();
        }
    }
}
