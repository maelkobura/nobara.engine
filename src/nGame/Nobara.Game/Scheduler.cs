// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Game;

/// <summary>Where a scheduled task is in its life.</summary>
internal enum TaskState
{
    Pending,
    Done,
    Canceled,
}

/// <summary>One scheduled task.</summary>
internal sealed class ScheduledEntry(ModuleContext module, IScheduledTask task, long sequence)
{
    public ModuleContext Module { get; } = module;

    public string ModuleId { get; } = module.Module.Id;

    public IScheduledTask Task { get; } = task;

    public long Sequence { get; } = sequence;

    public TaskState State { get; set; }

    public DimensionId Dimension { get; init; }

    /// <summary>Gets or sets the due tick of the dimension clock, or the due real time in .NET ticks.</summary>
    public ulong Due { get; set; }

    /// <summary>Gets the period in ticks of the dimension clock, or in .NET ticks of real time; 0 for a task that runs once.</summary>
    public ulong Period { get; init; }

    /// <summary>Tells whether a task falls due before another: the earliest due first, then the earliest scheduled.</summary>
    [HotPath]
    public static bool Earlier(ScheduledEntry a, ScheduledEntry b) =>
        a.Due != b.Due ? a.Due < b.Due : a.Sequence < b.Sequence;
}

/// <summary>A binary heap of tasks, the earliest due first, then the earliest scheduled.</summary>
internal sealed class TaskHeap
{
    private ScheduledEntry[] _items = new ScheduledEntry[8];

    public int Count { get; private set; }

    [HotPath]
    public ScheduledEntry Peek() => _items[0];

    /// <summary>Makes room for one more task, when it is scheduled: a task that goes back after it ran finds its place free.</summary>
    public void ReserveOne()
    {
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }
    }

    [HotPath]
    public void Push(ScheduledEntry entry)
    {
        int index = Count++;
        _items[index] = entry;
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (!ScheduledEntry.Earlier(_items[index], _items[parent]))
            {
                break;
            }

            (_items[index], _items[parent]) = (_items[parent], _items[index]);
            index = parent;
        }
    }

    [HotPath]
    public ScheduledEntry Pop()
    {
        ScheduledEntry top = _items[0];
        Count--;
        _items[0] = _items[Count];
        _items[Count] = null!;
        int index = 0;
        while (true)
        {
            int left = (index << 1) + 1;
            if (left >= Count)
            {
                break;
            }

            int child = left + 1 < Count && ScheduledEntry.Earlier(_items[left + 1], _items[left]) ? left + 1 : left;
            if (!ScheduledEntry.Earlier(_items[child], _items[index]))
            {
                break;
            }

            (_items[index], _items[child]) = (_items[child], _items[index]);
            index = child;
        }

        return top;
    }

    /// <summary>Cancels and drops every task.</summary>
    /// <returns>The number of tasks dropped.</returns>
    public int CancelAll()
    {
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            if (_items[i].State == TaskState.Pending)
            {
                _items[i].State = TaskState.Canceled;
            }

            _items[i] = null!;
        }

        Count = 0;
        return count;
    }
}

/// <summary>The scheduler of one context: tasks of game time per dimension, and tasks of real time.</summary>
internal sealed class Scheduler : IScheduler
{
    private readonly GameTime _time;
    private readonly TaskHeap _realTime = new();

    // The tasks due in one run. It holds as many places as tasks are in the
    // heaps, so that a run never grows it.
    private ScheduledEntry?[] _due = new ScheduledEntry?[8];
    private int _queued;
    private long _sequence;

    public Scheduler(GameTime time)
    {
        _time = time;
        _time.Removed = clock => _queued -= clock.Tasks.CancelAll();
    }

    public TaskHandle At(ModuleContext module, DimensionId dimension, ulong tick, IScheduledTask task) =>
        AddGameTask(module, dimension, tick, period: 0, task);

    public TaskHandle After(ModuleContext module, DimensionId dimension, ulong ticks, IScheduledTask task) =>
        AddGameTask(module, dimension, SaturatingAdd(_time.GetClock(dimension), ticks), period: 0, task);

    public TaskHandle Every(ModuleContext module, DimensionId dimension, ulong period, IScheduledTask task)
    {
        ArgumentOutOfRangeException.ThrowIfZero(period);
        return AddGameTask(module, dimension, SaturatingAdd(_time.GetClock(dimension), period), period, task);
    }

    public TaskHandle AfterRealTime(ModuleContext module, TimeSpan delay, IScheduledTask task)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        return AddRealTask(module, delay, TimeSpan.Zero, task);
    }

    public TaskHandle EveryRealTime(ModuleContext module, TimeSpan period, IScheduledTask task)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        return AddRealTask(module, period, period, task);
    }

    /// <summary>Runs the tasks of game time that are due, in stage Schedule.</summary>
    [HotPath]
    public void RunGameTasks()
    {
        List<DimensionClock> clocks = _time.Clocks;
        int count = 0;
        for (int c = 0; c < clocks.Count; c++)
        {
            DimensionClock clock = clocks[c];
            if (!clock.Frozen)
            {
                count = TakeDue(clock.Tasks, clock.Tick, count);
            }
        }

        // Each heap gives its tasks in order; the tasks of several dimensions merge here.
        SortDue(count);
        for (int i = 0; i < count; i++)
        {
            ScheduledEntry entry = _due[i]!;
            _due[i] = null;
            int index = _time.Find(entry.Dimension);
            if (index < 0)
            {
                // A task that ran before removed the dimension.
                entry.State = TaskState.Canceled;
                _queued--;
                continue;
            }

            ulong clockTick = _time.Clocks[index].Tick;
            Run(entry, new TaskRun(false, entry.Dimension, entry.Due, clockTick, TimeSpan.Zero, _time.RealTime));
            index = _time.Find(entry.Dimension);
            if (entry.State == TaskState.Pending && entry.Period > 0 && index >= 0)
            {
                // Periods skipped by a jump run once; the next run keeps the period.
                entry.Due = NextDue(entry.Due, entry.Period, clockTick);
                _time.Clocks[index].Tasks.Push(entry);
            }
            else
            {
                Finish(entry);
            }
        }
    }

    /// <summary>Runs the tasks of real time that are due, at the start of a frame.</summary>
    [HotPath]
    public void RunRealTimeTasks()
    {
        ulong now = (ulong)_time.RealTime.Ticks;
        int count = TakeDue(_realTime, now, 0);
        for (int i = 0; i < count; i++)
        {
            ScheduledEntry entry = _due[i]!;
            _due[i] = null;
            Run(entry, new TaskRun(true, DimensionId.Default, 0, 0, TimeSpan.FromTicks((long)entry.Due), _time.RealTime));
            if (entry.State == TaskState.Pending && entry.Period > 0)
            {
                entry.Due = NextDue(entry.Due, entry.Period, now);
                _realTime.Push(entry);
            }
            else
            {
                Finish(entry);
            }
        }
    }

    [HotPath]
    private static ulong NextDue(ulong due, ulong period, ulong now)
    {
        ulong next = SaturatingAdd(due, period);
        return next > now ? next : SaturatingAdd(due, (((now - due) / period) + 1) * period);
    }

    [HotPath]
    private static ulong SaturatingAdd(ulong value, ulong add) => value > ulong.MaxValue - add ? ulong.MaxValue : value + add;

    [HotPath]
    private static void Run(ScheduledEntry entry, in TaskRun run)
    {
        if (entry.State != TaskState.Pending)
        {
            return;
        }

        if (!entry.Module.IsModuleActive(entry.ModuleId))
        {
            entry.State = TaskState.Canceled;
            return;
        }

        Exception? failure = null;
        try
        {
            entry.Task.Run(run);
        }
        catch (Exception thrown) when (thrown is not EngineStopException)
        {
            failure = thrown;
        }

        if (failure is not null)
        {
            entry.Module.ReportFault(failure);
        }
    }

    // Moves the pending tasks of a heap that are due into the run.
    [HotPath]
    private int TakeDue(TaskHeap heap, ulong now, int count)
    {
        while (heap.Count > 0 && heap.Peek().Due <= now)
        {
            ScheduledEntry entry = heap.Pop();
            if (entry.State == TaskState.Pending)
            {
                _due[count++] = entry;
            }
            else
            {
                _queued--;
            }
        }

        return count;
    }

    [HotPath]
    private void Finish(ScheduledEntry entry)
    {
        if (entry.State == TaskState.Pending)
        {
            entry.State = TaskState.Done;
        }

        _queued--;
    }

    // Insertion sort: the tasks due in one tick are few, and mostly in order.
    [HotPath]
    private void SortDue(int count)
    {
        for (int i = 1; i < count; i++)
        {
            ScheduledEntry item = _due[i]!;
            int j = i - 1;
            while (j >= 0 && ScheduledEntry.Earlier(item, _due[j]!))
            {
                _due[j + 1] = _due[j];
                j--;
            }

            _due[j + 1] = item;
        }
    }

    private TaskHandle AddGameTask(ModuleContext module, DimensionId dimension, ulong due, ulong period, IScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(task);
        int index = _time.Find(dimension);
        if (index < 0)
        {
            throw new ArgumentException($"The {dimension} has no clock.", nameof(dimension));
        }

        var entry = new ScheduledEntry(module, task, ++_sequence) { Dimension = dimension, Due = due, Period = period };
        Queue(_time.Clocks[index].Tasks, entry);
        return new TaskHandle(entry);
    }

    private TaskHandle AddRealTask(ModuleContext module, TimeSpan delay, TimeSpan period, IScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(task);
        var entry = new ScheduledEntry(module, task, ++_sequence) { Due = (ulong)(_time.RealTime + delay).Ticks, Period = (ulong)period.Ticks };
        Queue(_realTime, entry);
        return new TaskHandle(entry);
    }

    private void Queue(TaskHeap heap, ScheduledEntry entry)
    {
        heap.ReserveOne();
        if (++_queued > _due.Length)
        {
            Array.Resize(ref _due, _queued * 2);
        }

        heap.Push(entry);
    }
}
