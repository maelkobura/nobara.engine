// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Game;

/// <summary>
/// The service that runs tasks later: at a tick of a dimension clock, after
/// a number of ticks, periodically, or after real time. Resolve it with
/// <c>context.Resolve&lt;IScheduler&gt;()</c>; there is one per context.
/// </summary>
/// <remarks>
/// <para>
/// Tasks of game time run in stage <see cref="TickStages.Schedule"/>, in the
/// order of their due tick, then of their scheduling. They follow the pause,
/// the time scale, and the frozen dimensions: a task of a frozen dimension
/// waits. A task whose due tick has passed, after a jump of its clock, runs
/// at the next tick.
/// </para>
/// <para>
/// Tasks of real time run at the start of each frame, before the phases, in
/// the order of their due time, then of their scheduling. They go on during a
/// pause and ignore the time scale: they suit network delays and timeouts.
/// </para>
/// <para>
/// A task that runs schedules others for a later run, never for the same
/// one. The scheduler is not saved: a module that needs lasting tasks saves
/// them itself. Schedule from the loop thread of the context. Scheduling
/// allocates; running due tasks does not.
/// </para>
/// </remarks>
public interface IScheduler
{
    /// <summary>Runs a task once, when the clock of a dimension reaches a tick.</summary>
    /// <param name="module">The module of the task; an exception in the task follows its error policy.</param>
    /// <param name="dimension">The dimension whose clock counts.</param>
    /// <param name="tick">The tick of that clock; a tick already reached runs at the next tick.</param>
    /// <param name="task">The code to run.</param>
    /// <returns>The handle; dispose it to cancel the task.</returns>
    /// <exception cref="ArgumentException">The dimension has no clock.</exception>
    TaskHandle At(ModuleContext module, DimensionId dimension, ulong tick, IScheduledTask task);

    /// <summary>Runs a task once, a number of ticks of a dimension clock from now.</summary>
    /// <param name="module">The module of the task.</param>
    /// <param name="dimension">The dimension whose clock counts.</param>
    /// <param name="ticks">The delay, in ticks of that clock; 0 runs it at the next tick.</param>
    /// <param name="task">The code to run.</param>
    /// <returns>The handle; dispose it to cancel the task.</returns>
    /// <exception cref="ArgumentException">The dimension has no clock.</exception>
    TaskHandle After(ModuleContext module, DimensionId dimension, ulong ticks, IScheduledTask task);

    /// <summary>
    /// Runs a task every given number of ticks of a dimension clock, the first
    /// time one period from now. Periods that a jump of the clock skips run
    /// once, then the task goes on at its period.
    /// </summary>
    /// <param name="module">The module of the task.</param>
    /// <param name="dimension">The dimension whose clock counts.</param>
    /// <param name="period">The period, in ticks of that clock, at least 1.</param>
    /// <param name="task">The code to run.</param>
    /// <returns>The handle; dispose it to stop the task.</returns>
    /// <exception cref="ArgumentException">The dimension has no clock.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> is 0.</exception>
    TaskHandle Every(ModuleContext module, DimensionId dimension, ulong period, IScheduledTask task);

    /// <summary>Runs a task once, after a delay of real time.</summary>
    /// <param name="module">The module of the task.</param>
    /// <param name="delay">The delay, 0 or more; 0 runs it at the start of the next frame.</param>
    /// <param name="task">The code to run.</param>
    /// <returns>The handle; dispose it to cancel the task.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    TaskHandle AfterRealTime(ModuleContext module, TimeSpan delay, IScheduledTask task);

    /// <summary>
    /// Runs a task every period of real time, the first time one period from
    /// now. Periods missed by a slow frame run once.
    /// </summary>
    /// <param name="module">The module of the task.</param>
    /// <param name="period">The period, greater than 0.</param>
    /// <param name="task">The code to run.</param>
    /// <returns>The handle; dispose it to stop the task.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> is not greater than 0.</exception>
    TaskHandle EveryRealTime(ModuleContext module, TimeSpan period, IScheduledTask task);
}

/// <summary>Code that the scheduler runs.</summary>
/// <remarks>
/// <see cref="Run"/> is hot path code, checked by the engine analyzers in
/// every implementation. It runs on the loop thread of the context; it
/// declares no data accesses.
/// </remarks>
public interface IScheduledTask
{
    /// <summary>Runs the task.</summary>
    /// <param name="run">Why the task runs now.</param>
    [HotPath]
    void Run(in TaskRun run);
}

/// <summary>Why a task runs now.</summary>
public readonly struct TaskRun
{
    /// <summary>Initializes the description of a run.</summary>
    /// <param name="isRealTime">Whether the task counts real time.</param>
    /// <param name="dimension">The dimension of a task of game time.</param>
    /// <param name="dueTick">The tick the task was due at.</param>
    /// <param name="clockTick">The dimension clock now.</param>
    /// <param name="dueTime">The real time the task was due at.</param>
    /// <param name="realTime">The real time now.</param>
    [HotPath]
    public TaskRun(bool isRealTime, DimensionId dimension, ulong dueTick, ulong clockTick, TimeSpan dueTime, TimeSpan realTime)
    {
        IsRealTime = isRealTime;
        Dimension = dimension;
        DueTick = dueTick;
        ClockTick = clockTick;
        DueTime = dueTime;
        RealTime = realTime;
    }

    /// <summary>Gets whether the task counts real time instead of a dimension clock.</summary>
    public bool IsRealTime { get; }

    /// <summary>Gets the dimension of a task of game time; <see cref="DimensionId.Default"/> for real time.</summary>
    public DimensionId Dimension { get; }

    /// <summary>Gets the tick of the dimension clock the task was due at; 0 for real time.</summary>
    public ulong DueTick { get; }

    /// <summary>Gets the dimension clock now, at least <see cref="DueTick"/>; 0 for real time.</summary>
    public ulong ClockTick { get; }

    /// <summary>Gets the real time the task was due at; zero for game time.</summary>
    public TimeSpan DueTime { get; }

    /// <summary>Gets the real time now, as <see cref="IGameTime.RealTime"/> gives it.</summary>
    public TimeSpan RealTime { get; }
}

/// <summary>A scheduled task. Disposing it cancels the task: it does not run again, even in the run that is going on.</summary>
public sealed class TaskHandle : IDisposable
{
    internal TaskHandle(ScheduledEntry entry)
    {
        Entry = entry;
    }

    /// <summary>Gets whether the task will run again: it is not canceled, not done, and its module is active.</summary>
    public bool IsActive => Entry.State == TaskState.Pending && Entry.Module.IsModuleActive(Entry.ModuleId);

    internal ScheduledEntry Entry { get; }

    /// <summary>Cancels the task.</summary>
    public void Dispose()
    {
        if (Entry.State == TaskState.Pending)
        {
            Entry.State = TaskState.Canceled;
        }
    }
}
