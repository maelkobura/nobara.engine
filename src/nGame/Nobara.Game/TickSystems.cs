// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;

namespace Nobara.Game;

/// <summary>The names of the standard stages of the simulation tick, in the order they run.</summary>
public static class TickStages
{
    /// <summary>Stage 1: inputs and intents received, their validation, the controller of the player.</summary>
    public const string Intents = "Intents";

    /// <summary>Stage 2: the tasks of the scheduler that are due.</summary>
    public const string Schedule = "Schedule";

    /// <summary>Stage 3: the world itself, such as scheduled and random block ticks.</summary>
    public const string World = "World";

    /// <summary>Stage 4: gameplay systems of entities, and their artificial intelligence.</summary>
    public const string Entities = "Entities";

    /// <summary>Stage 5: physics and motion.</summary>
    public const string Physics = "Physics";

    /// <summary>Stage 6: reactions to the changes of the tick, and last corrections.</summary>
    public const string Late = "Late";

    /// <summary>Gets the standard stages, in the order they run.</summary>
    public static IReadOnlyList<string> Standard { get; } = Array.AsReadOnly(new[] { Intents, Schedule, World, Entities, Physics, Late });

    /// <summary>
    /// The name of the system that runs the tasks of the scheduler, in stage
    /// <see cref="Schedule"/>. Other systems can be ordered before or after it.
    /// </summary>
    public const string SchedulerSystem = "nobara.game.scheduler";
}

/// <summary>Where the simulation is when a tick system runs.</summary>
public readonly struct TickTime
{
    /// <summary>Initializes the time of a tick.</summary>
    /// <param name="tick">The number of the tick.</param>
    /// <param name="delta">The duration of one tick.</param>
    [HotPath]
    public TickTime(ulong tick, TimeSpan delta)
    {
        Tick = tick;
        Delta = delta;
    }

    /// <summary>Gets the number of the tick of the context, from 1. It goes up by one for each tick that runs, whatever the dimension clocks do.</summary>
    public ulong Tick { get; }

    /// <summary>Gets the duration of one tick of simulated time: the inverse of the tick rate.</summary>
    public TimeSpan Delta { get; }
}

/// <summary>
/// Code that runs once per simulation tick, in a stage. Every piece of code
/// that runs during the tick is a tick system registered with
/// <see cref="ITickSchedule"/>.
/// </summary>
/// <remarks>
/// <see cref="Run"/> is hot path code, checked by the engine analyzers in
/// every implementation. It runs on the loop thread of the context; work
/// spread over processors goes to jobs that it schedules and completes.
/// </remarks>
public interface ITickSystem
{
    /// <summary>Runs the system for one tick.</summary>
    /// <param name="time">The tick that runs.</param>
    [HotPath]
    void Run(in TickTime time);
}

/// <summary>How a tick system is ordered, what it touches, and where it runs.</summary>
public sealed class TickSystemOptions
{
    /// <summary>Gets the systems that run before this one, by qualified name. A name that no system has in this context is ignored.</summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>Gets the systems that run after this one, by qualified name. A name that no system has in this context is ignored.</summary>
    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>Gets the data the system only reads, such as the storage of a component.</summary>
    public IReadOnlyList<JobData> Reads { get; init; } = [];

    /// <summary>Gets the data the system writes, and may read.</summary>
    public IReadOnlyList<JobData> Writes { get; init; } = [];

    /// <summary>
    /// Gets the sides where the system runs: <see cref="Side.Both"/> by
    /// default. In a context of another side, the system keeps its place in
    /// the order but does not run.
    /// </summary>
    public Side Side { get; init; } = Side.Both;
}

/// <summary>A registered tick system. Disposing it removes the system from the next tick on.</summary>
public sealed class TickSystemHandle : IDisposable
{
    private readonly TickSchedule _schedule;

    internal TickSystemHandle(TickSchedule schedule, string name)
    {
        _schedule = schedule;
        Name = name;
    }

    /// <summary>Gets the qualified name of the system.</summary>
    public string Name { get; }

    /// <summary>Gets whether the handle was disposed.</summary>
    public bool IsRevoked { get; private set; }

    /// <summary>
    /// Removes the system. A system removed while a tick runs still runs in
    /// that tick when its turn comes; it does not run from the next tick on.
    /// </summary>
    public void Dispose()
    {
        if (!IsRevoked)
        {
            IsRevoked = true;
            _schedule.Revoke(Name);
        }
    }
}

/// <summary>Whether a system of the resolved order runs in this context, or why not.</summary>
public enum TickSystemState
{
    /// <summary>The system runs every tick.</summary>
    Runs,

    /// <summary>The system runs on the other side only.</summary>
    OtherSide,

    /// <summary>The distribution disables the system in <c>engine.nobara</c>.</summary>
    Disabled,

    /// <summary>The handle of the system was disposed.</summary>
    Revoked,

    /// <summary>The module of the system was disabled after an error.</summary>
    ModuleDisabled,
}

/// <summary>A tick system in the resolved order.</summary>
/// <param name="Name">The qualified name of the system.</param>
/// <param name="Stage">The stage of the system.</param>
/// <param name="ModuleId">The module that registered it.</param>
/// <param name="State">Whether it runs.</param>
/// <param name="Reads">The names of the data it declares it reads.</param>
/// <param name="Writes">The names of the data it declares it writes.</param>
public sealed record TickSystemInfo(string Name, string Stage, string ModuleId, TickSystemState State, IReadOnlyList<string> Reads, IReadOnlyList<string> Writes);

/// <summary>A stage in the resolved order, with its systems in the order they run.</summary>
/// <param name="Name">The name of the stage.</param>
/// <param name="ModuleId">The module that inserted it, <c>nobara.game</c> for a standard stage, or the identifier of the distribution for a stage of <c>engine.nobara</c>.</param>
/// <param name="Systems">Its systems, in order.</param>
public sealed record TickStageInfo(string Name, string ModuleId, IReadOnlyList<TickSystemInfo> Systems);
