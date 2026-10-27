// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using F = Nobara.Formats;

namespace Nobara.Core.Loop;

/// <summary>
/// The loop of one context: its phases, the handlers subscribed to them, the
/// fixed-tick accumulator, and the synchronization point.
/// </summary>
internal sealed class ContextLoop
{
    /// <summary>The tick rate when the distribution declares none.</summary>
    public const int DefaultTickRate = 30;

    private readonly EngineContext _context;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxFrameTime;
    private readonly LoopPhase[] _phases;
    private readonly Dictionary<string, LoopPhase> _byName;
    private readonly List<LoopPhase> _declaredElsewhere;
    private readonly Dictionary<LoopPhase, ModuleRuntime> _reservedBy = [];
    private readonly List<LoopSubscription> _subscriptions = [];
    private readonly ConcurrentQueue<Posted> _posted = new();
    private readonly bool _hasFixedPhase;

    // Handlers of each phase, then of the start of the frame, rebuilt when a
    // subscription changes.
    private LoopSubscription[][] _handlers;
    private LoopSubscription[] _frameStart = [];
    private bool _changed;

    private long _lastTimestamp;
    private TimeSpan _accumulated;
    private TimeSpan _tickDuration;

    public ContextLoop(EngineContext context, F.Loop? declared, TimeProvider time, TimeSpan maxFrameTime)
    {
        _context = context;
        _time = time;
        _maxFrameTime = maxFrameTime;

        var phases = new List<LoopPhase>();
        _declaredElsewhere = [];
        foreach ((string name, bool isFixed, Side side) in DeclaredPhases(declared))
        {
            if ((side & context.Side) != Side.None)
            {
                phases.Add(new LoopPhase(name, isFixed, side, phases.Count));
            }
            else
            {
                _declaredElsewhere.Add(new LoopPhase(name, isFixed, side, -1));
            }
        }

        _phases = [.. phases];
        _byName = _phases.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _hasFixedPhase = _phases.Any(p => p.IsFixed);
        _handlers = new LoopSubscription[_phases.Length][];
        Array.Fill(_handlers, []);
        TickRate = declared is { TickRate: > 0 } ? declared.TickRate : DefaultTickRate;
        _tickDuration = TimeSpan.FromSeconds(1 / TickRate);
    }

    public IReadOnlyList<LoopPhase> Phases => _phases;

    public ulong Frame { get; private set; }

    public ulong Tick { get; private set; }

    public TickControl? Control { get; private set; }

    public double TickRate { get; private set; }

    public TimeSpan TickDuration => _tickDuration;

    public bool Paused { get; set; }

    public double TimeScale { get; set; } = 1;

    /// <summary>Gets the phases declared in order, the ones of the other side included.</summary>
    public static IEnumerable<(string Name, bool IsFixed, Side Side)> DeclaredPhases(F.Loop? declared)
    {
        if (declared is null || declared.Phases.Count == 0)
        {
            // The loop that a distribution without <Loop> runs.
            return
            [
                ("Input", false, Side.Both),
                ("Simulation", true, Side.Both),
                ("PostSimulation", false, Side.Both),
                ("Render", false, Side.Client),
            ];
        }

        return declared.Phases.Select(p => (p.Name, p.Fixed, p.Side switch
        {
            F.Side.Client => Side.Client,
            F.Side.Server => Side.Server,
            _ => Side.Both,
        }));
    }

    /// <summary>Finds which active modules reserve the phases. Called when the context starts.</summary>
    public void FindReservations(IEnumerable<ModuleRuntime> modules)
    {
        _reservedBy.Clear();
        foreach (ModuleRuntime module in modules)
        {
            foreach (LoopPhase phase in _phases)
            {
                if (module.Info.Reservations.Contains(phase.Reservation, StringComparer.Ordinal))
                {
                    _reservedBy[phase] = module;
                }
            }
        }
    }

    public LoopPhase Find(ModuleRuntime caller, string name)
    {
        if (_byName.TryGetValue(name, out LoopPhase? phase))
        {
            return phase;
        }

        LoopPhase? elsewhere = _declaredElsewhere.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        string reason = elsewhere is not null
            ? $"phase '{name}' runs only on the {EngineStopException.Describe(elsewhere.Side)} side, and this is the {_context.Name} context"
            : $"the loop has no phase '{name}'; its phases are {string.Join(", ", _phases.Select(p => p.Name))}";
        throw new InvalidOperationException($"Module '{caller.Info.Id}' cannot use phase '{name}': {reason}.");
    }

    public LoopPhase? Find(string name) => _byName.GetValueOrDefault(name);

    public ModuleRuntime? ReservedBy(LoopPhase phase) => _reservedBy.GetValueOrDefault(phase);

    public LoopSubscription Subscribe(ModuleRuntime module, LoopPhase? phase, IPhaseHandler handler)
    {
        if (phase is not null && _reservedBy.TryGetValue(phase, out ModuleRuntime? owner) && owner != module)
        {
            throw new InvalidOperationException(
                $"Module '{module.Info.Id}' cannot subscribe to phase '{phase.Name}': module '{owner.Info.Id}' reserves it ({phase.Reservation}). Register with that module instead.");
        }

        var subscription = new LoopSubscription(this, module, phase, handler);
        _subscriptions.Add(subscription);
        Changed();
        return subscription;
    }

    public TickControl GetControl(ModuleRuntime module)
    {
        foreach (KeyValuePair<LoopPhase, ModuleRuntime> reservation in _reservedBy)
        {
            if (reservation.Key.IsFixed && reservation.Value == module)
            {
                return Control ??= new TickControl(this);
            }
        }

        throw new InvalidOperationException(
            $"Module '{module.Info.Id}' cannot control the tick: only the module that reserves a fixed phase can"
            + (_phases.FirstOrDefault(p => p.IsFixed) is LoopPhase fixedPhase ? $" (here {fixedPhase.Reservation})." : "; this loop has no fixed phase."));
    }

    public void SetTickRate(double ticksPerSecond)
    {
        TickRate = ticksPerSecond;
        _tickDuration = TimeSpan.FromSeconds(1 / ticksPerSecond);
    }

    public void Changed() => _changed = true;

    public void Post(ModuleRuntime? module, SendOrPostCallback callback, object? state) => _posted.Enqueue(new Posted(module, callback, state));

    /// <summary>Gets how long until the next tick is due, at the current scale.</summary>
    public TimeSpan UntilNextTick()
    {
        if (!_hasFixedPhase)
        {
            return TimeSpan.Zero;
        }

        if (Paused || TimeScale <= 0)
        {
            return _tickDuration;
        }

        TimeSpan left = _tickDuration - _accumulated - _time.GetElapsedTime(_lastTimestamp);
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left / TimeScale;
    }

    /// <summary>Starts measuring time: the first frame has no elapsed time.</summary>
    public void Begin()
    {
        _lastTimestamp = _time.GetTimestamp();
        _accumulated = TimeSpan.Zero;
    }

    /// <summary>Runs one frame. Faults are handed to the context, which may throw <see cref="EngineStopException"/>.</summary>
    public void RunFrame()
    {
        Frame++;
        long now = _time.GetTimestamp();
        TimeSpan elapsed = _time.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;
        if (elapsed > _maxFrameTime)
        {
            _context.Logger.Warning($"Frame {Frame} came {elapsed.TotalMilliseconds:F0} ms after the previous one; the loop catches up {_maxFrameTime.TotalMilliseconds:F0} ms of it.");
            elapsed = _maxFrameTime;
        }

        if (_changed)
        {
            Rebuild();
        }

        DrainPosted();

        int ticks = Accumulate(elapsed);
        double alpha = _hasFixedPhase ? _accumulated / _tickDuration : 0;
        ulong firstTick = Tick + 1;
        var frameTime = new PhaseTime(Frame, Tick, elapsed, alpha);

        RunHandlers(_frameStart, null, frameTime);
        foreach (LoopPhase phase in _phases)
        {
            if (!phase.IsFixed)
            {
                RunHandlers(_handlers[phase.Index], phase, frameTime with { Tick = Tick });
                continue;
            }

            for (int i = 0; i < ticks; i++)
            {
                ulong tick = firstTick + (ulong)i;
                RunHandlers(_handlers[phase.Index], phase, new PhaseTime(Frame, tick, _tickDuration, 0));
                if (tick > Tick)
                {
                    Tick = tick;
                }
            }
        }

        _context.EnterPhase(string.Empty);
    }

    // Adds the elapsed time to the accumulator and returns the number of ticks due.
    private int Accumulate(TimeSpan elapsed)
    {
        if (!_hasFixedPhase || Paused)
        {
            return 0;
        }

        _accumulated += TimeScale == 1 ? elapsed : elapsed * TimeScale;
        int ticks = (int)(_accumulated.Ticks / _tickDuration.Ticks);
        _accumulated -= _tickDuration * ticks;
        return ticks;
    }

    private void RunHandlers(LoopSubscription[] handlers, LoopPhase? phase, in PhaseTime time)
    {
        _context.EnterPhase(phase?.Name ?? "FrameStart");
        int next = 0;
        while (next < handlers.Length)
        {
            int failed = LoopDispatch.Run(handlers, next, time, out Exception? exception);
            if (failed < 0)
            {
                return;
            }

            _context.HandleLoopFault(handlers[failed].Module, exception!);
            next = failed + 1;
        }
    }

    private void DrainPosted()
    {
        _context.EnterPhase("Synchronization");
        int count = _posted.Count;
        for (int i = 0; i < count && _posted.TryDequeue(out Posted posted); i++)
        {
            if (posted.Module is { Disabled: true })
            {
                continue;
            }

            Exception? exception = LoopDispatch.Run(posted.Callback, posted.State);
            if (exception is not null)
            {
                _context.HandleLoopFault(posted.Module, exception);
            }
        }
    }

    private void Rebuild()
    {
        _changed = false;
        _subscriptions.RemoveAll(s => !s.IsActive);
        _frameStart = [.. _subscriptions.Where(s => s.Phase is null)];
        for (int i = 0; i < _phases.Length; i++)
        {
            _handlers[i] = [.. _subscriptions.Where(s => s.Phase == _phases[i])];
        }
    }

    private readonly record struct Posted(ModuleRuntime? Module, SendOrPostCallback Callback, object? State);
}

/// <summary>The calls into module code that run every frame.</summary>
/// <remarks>
/// They are not marked as hot path: a handler of a phase that is not fixed,
/// such as a change of state, does not have to follow the hot path rules. The
/// tests measure that a frame allocates nothing.
/// </remarks>
internal static class LoopDispatch
{
    /// <summary>Runs handlers from <paramref name="start"/> until one throws.</summary>
    /// <returns>The index of the handler that threw, or -1.</returns>
    public static int Run(LoopSubscription[] handlers, int start, in PhaseTime time, out Exception? exception)
    {
        for (int i = start; i < handlers.Length; i++)
        {
            LoopSubscription subscription = handlers[i];
            if (subscription.Revoked || subscription.Module.Disabled)
            {
                continue;
            }

            try
            {
                subscription.Handler.Run(time);
            }
            catch (Exception thrown) when (thrown is not EngineStopException)
            {
                exception = thrown;
                return i;
            }
        }

        exception = null;
        return -1;
    }

    /// <summary>Runs a callback posted to the synchronization point.</summary>
    /// <returns>The exception it threw, or <see langword="null"/>.</returns>
    public static Exception? Run(SendOrPostCallback callback, object? state)
    {
        try
        {
            callback(state);
            return null;
        }
        catch (Exception thrown) when (thrown is not EngineStopException)
        {
            return thrown;
        }
    }
}
