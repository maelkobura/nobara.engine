// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Loop;

/// <summary>A phase of the loop, as the distribution declares it.</summary>
public sealed class LoopPhase
{
    internal LoopPhase(string name, bool isFixed, Side side, int index)
    {
        Name = name;
        IsFixed = isFixed;
        Side = side;
        Index = index;
    }

    /// <summary>Gets the name of the phase, such as <c>Simulation</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets whether the phase runs at the fixed tick rate: zero, one, or
    /// several times per frame, as the accumulated time allows. Other phases
    /// run once per frame.
    /// </summary>
    public bool IsFixed { get; }

    /// <summary>Gets the sides where the phase runs: <see cref="Side.Both"/>, or a single side.</summary>
    public Side Side { get; }

    /// <summary>Gets the name of the exclusive resource that reserves the phase: <c>loop.</c> and the name in lower case, such as <c>loop.simulation</c>.</summary>
    public string Reservation => LoopReservations.Phase(Name);

    /// <summary>Gets the position of the phase in the loop of its context.</summary>
    internal int Index { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>Where the loop is when it calls a phase handler.</summary>
/// <param name="Frame">The number of the frame, from 1.</param>
/// <param name="Tick">
/// In a fixed phase, the number of the tick, from 1; elsewhere, the number of
/// the last tick that ran, or 0.
/// </param>
/// <param name="Delta">
/// In a fixed phase, the duration of one tick. Elsewhere, the real time since
/// the previous frame.
/// </param>
/// <param name="Alpha">
/// Outside fixed phases, the fraction of a tick that has accumulated since the
/// last tick, from 0 to 1, to interpolate what is drawn. In a fixed phase, 0.
/// </param>
public readonly record struct PhaseTime(ulong Frame, ulong Tick, TimeSpan Delta, double Alpha);

/// <summary>Code that a module runs in a phase of the loop.</summary>
/// <remarks>
/// The handler runs on the loop thread of its context. It is loop code: it
/// does not use <see langword="async"/>, and in a fixed phase it follows the
/// hot path rules.
/// </remarks>
public interface IPhaseHandler
{
    /// <summary>Runs the handler for one frame, or one tick in a fixed phase.</summary>
    /// <param name="time">Where the loop is.</param>
    [LoopCallback]
    void Run(in PhaseTime time);
}

/// <summary>A handler subscribed to a phase. Disposing it removes the handler at once.</summary>
public sealed class LoopSubscription : IDisposable
{
    private readonly ContextLoop _loop;

    internal LoopSubscription(ContextLoop loop, ModuleRuntime module, LoopPhase? phase, IPhaseHandler handler, Profiling.ProfilerMarker marker)
    {
        _loop = loop;
        Module = module;
        Phase = phase;
        Handler = handler;
        Marker = marker;
    }

    /// <summary>Gets the phase, or <see langword="null"/> for the start of the frame.</summary>
    public LoopPhase? Phase { get; }

    /// <summary>Gets whether the handler still runs.</summary>
    public bool IsActive => !Revoked && !Module.Disabled;

    internal ModuleRuntime Module { get; }

    internal IPhaseHandler Handler { get; }

    internal Profiling.ProfilerMarker Marker { get; }

    internal bool Revoked { get; private set; }

    /// <summary>Removes the handler: it does not run again, even later in the frame that is running.</summary>
    public void Dispose()
    {
        if (!Revoked)
        {
            Revoked = true;
            _loop.Changed();
        }
    }
}

/// <summary>The names of the exclusive resources that grant the levers of the core.</summary>
public static class LoopReservations
{
    /// <summary>The exclusive resource that grants the lever on contexts: <c>core.contexts</c>.</summary>
    public const string Contexts = "core.contexts";

    /// <summary>Gets the exclusive resource that reserves a phase: <c>loop.</c> and the name of the phase in lower case.</summary>
    /// <param name="phase">The name of a phase, such as <c>Simulation</c>.</param>
    /// <returns>The name of the resource, such as <c>loop.simulation</c>.</returns>
    public static string Phase(string phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        return "loop." + phase.ToLowerInvariant();
    }
}
