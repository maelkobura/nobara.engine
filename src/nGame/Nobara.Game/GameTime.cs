// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;

namespace Nobara.Game;

/// <summary>The clock of one dimension, and the tasks scheduled on it.</summary>
internal sealed class DimensionClock(DimensionId id, ulong tick)
{
    public DimensionId Id { get; } = id;

    public ulong Tick { get; set; } = tick;

    public bool Frozen { get; set; }

    public TaskHeap Tasks { get; } = new();
}

/// <summary>Game time of one context, over the levers of the core on the tick.</summary>
internal sealed class GameTime : IGameTime
{
    private readonly TickControl _control;
    private readonly List<DimensionClock> _clocks = [];
    private readonly List<DimensionId> _ids = [];

    public GameTime(TickControl control)
    {
        _control = control;
        AddDimension(DimensionId.Default);
    }

    /// <summary>Gets or sets where the levers send their notifications; <see langword="null"/> without the event bus.</summary>
    public IGameNotifications? Notifications { get; set; }

    /// <summary>Gets what to do with the tasks of a removed dimension.</summary>
    public Action<DimensionClock>? Removed { get; set; }

    public ulong Tick { get; private set; }

    public TimeSpan RealTime { get; private set; }

    public bool Paused
    {
        get => _control.Paused;
        set
        {
            if (value == _control.Paused)
            {
                return;
            }

            if (value && !CanPause)
            {
                throw new InvalidOperationException("The simulation cannot pause while the game is open to other players.");
            }

            _control.Paused = value;
            Notifications?.PauseChanged(value);
        }
    }

    // Pausing will depend on whether the game is open to the network; no
    // network exists in this version.
    public bool CanPause => true;

    public double TimeScale
    {
        get => _control.TimeScale;
        set
        {
            double previous = _control.TimeScale;
            _control.TimeScale = value;
            if (previous != value)
            {
                Notifications?.TimeScaleChanged(previous, value);
            }
        }
    }

    public double TickRate
    {
        get => _control.TickRate;
        set
        {
            double previous = _control.TickRate;
            _control.TickRate = value;
            if (previous != value)
            {
                Notifications?.TickRateChanged(previous, value);
            }
        }
    }

    public TimeSpan TickDuration => _control.TickDuration;

    public IReadOnlyList<DimensionId> Dimensions => _ids;

    /// <summary>Gets the clocks, in the order of their dimensions.</summary>
    public List<DimensionClock> Clocks
    {
        [HotPath]
        get => _clocks;
    }

    public bool HasDimension(DimensionId dimension) => Find(dimension) >= 0;

    public void AddDimension(DimensionId dimension, ulong clock = 0)
    {
        int index = Find(dimension);
        if (index >= 0)
        {
            throw new ArgumentException($"The {dimension} already has a clock.", nameof(dimension));
        }

        _clocks.Insert(~index, new DimensionClock(dimension, clock));
        _ids.Insert(~index, dimension);
    }

    public void RemoveDimension(DimensionId dimension)
    {
        if (dimension == DimensionId.Default)
        {
            throw new ArgumentException("The clock of the default dimension is never removed.", nameof(dimension));
        }

        int index = Require(dimension);
        DimensionClock clock = _clocks[index];
        _clocks.RemoveAt(index);
        _ids.RemoveAt(index);
        Removed?.Invoke(clock);
    }

    public ulong GetClock(DimensionId dimension) => _clocks[Require(dimension)].Tick;

    public void Jump(DimensionId dimension, ulong ticks)
    {
        DimensionClock clock = _clocks[Require(dimension)];
        if (ticks == 0)
        {
            return;
        }

        ulong from = clock.Tick;
        clock.Tick = checked(from + ticks);
        Notifications?.ClockJumped(dimension, from, clock.Tick);
    }

    public bool IsFrozen(DimensionId dimension) => _clocks[Require(dimension)].Frozen;

    public void SetFrozen(DimensionId dimension, bool frozen)
    {
        DimensionClock clock = _clocks[Require(dimension)];
        if (clock.Frozen != frozen)
        {
            clock.Frozen = frozen;
            Notifications?.FreezeChanged(dimension, frozen);
        }
    }

    /// <summary>Starts a tick: records its number and moves the clocks that are not frozen.</summary>
    [HotPath]
    public void BeginTick(ulong tick)
    {
        Tick = tick;
        foreach (DimensionClock clock in _clocks)
        {
            if (!clock.Frozen)
            {
                clock.Tick++;
            }
        }
    }

    /// <summary>Adds the real time of a frame.</summary>
    [HotPath]
    public void AddRealTime(TimeSpan elapsed) => RealTime += elapsed;

    /// <summary>Finds the clock of a dimension.</summary>
    /// <returns>The index of the clock, or the complement of where it would go.</returns>
    [HotPath]
    public int Find(DimensionId dimension)
    {
        int low = 0;
        int high = _clocks.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            DimensionId id = _clocks[middle].Id;
            if (id == dimension)
            {
                return middle;
            }

            if (id < dimension)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    [HotPath]
    private int Require(DimensionId dimension)
    {
        int index = Find(dimension);
        return index >= 0 ? index : throw new ArgumentException($"The {dimension} has no clock.", nameof(dimension));
    }
}
