// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Context;

/// <summary>Where a world session is in its life.</summary>
public enum SessionState
{
    /// <summary>No session: the context is in its menus. <see cref="IWorldSessions.State"/> only.</summary>
    None,

    /// <summary>The session was started: its loading tasks run, from the frame after <see cref="IWorldSessions.Start"/>.</summary>
    Starting,

    /// <summary>Every loading task finished: the context is in the world.</summary>
    Active,

    /// <summary>The handlers of <c>SessionEnding</c> and <c>SessionEnded</c> run.</summary>
    Ending,

    /// <summary>The session is over. <see cref="WorldSession.State"/> only.</summary>
    Ended,
}

/// <summary>Why a world session ended.</summary>
public enum SessionEndReason
{
    /// <summary>A module called <see cref="IWorldSessions.End"/>, such as on the way back to the main menu.</summary>
    Requested,

    /// <summary>The context stops: a stop request reached it, or its state stack became empty.</summary>
    Stopping,

    /// <summary>A loading task failed: <see cref="WorldSession.Failure"/> tells which and why.</summary>
    Failed,
}

/// <summary>What a set of loading tasks loads.</summary>
public enum LoadingScope
{
    /// <summary>The start of a world session.</summary>
    World,

    /// <summary>A change of dimension inside a session. Not used by this version.</summary>
    Dimension,
}

/// <summary>Where a loading task is.</summary>
public enum LoadingTaskState
{
    /// <summary>The task runs.</summary>
    Running,

    /// <summary>The task finished its work.</summary>
    Completed,

    /// <summary>The task failed, which cancels the session.</summary>
    Failed,

    /// <summary>The session was cancelled or ended before the task finished.</summary>
    Canceled,
}

/// <summary>The progress of a set of loading tasks, as a loading screen shows it.</summary>
/// <param name="Fraction">The progress from 0 to 1: the progress of each task, weighted by its weight.</param>
/// <param name="CompletedTasks">The number of tasks that finished their work.</param>
/// <param name="TaskCount">The number of tasks.</param>
public readonly record struct LoadingProgress(double Fraction, int CompletedTasks, int TaskCount);

/// <summary>Why a world session was cancelled while it started.</summary>
/// <param name="Task">The name of the loading task that failed.</param>
/// <param name="Module">The identifier of the module that registered the task.</param>
/// <param name="Reason">The reason, as the task gave it, to show to the player.</param>
/// <param name="Exception">The exception that failed the task, or <see langword="null"/>.</param>
public sealed record SessionFailure(string Task, string Module, string Reason, Exception? Exception);

/// <summary>
/// A handler subscribed to the events of world sessions, or a world state
/// counter. Disposing it removes it at once, even during a dispatch.
/// </summary>
public sealed class SessionSubscription : IDisposable
{
    private readonly Action<SessionSubscription> _remove;

    internal SessionSubscription(Action<SessionSubscription> remove)
    {
        _remove = remove;
    }

    /// <summary>Gets whether the handler or counter is still registered.</summary>
    public bool IsActive => !Revoked;

    internal bool Revoked { get; private set; }

    /// <summary>Removes the handler or counter. Calling it again does nothing.</summary>
    public void Dispose()
    {
        if (!Revoked)
        {
            Revoked = true;
            _remove(this);
        }
    }
}

/// <summary>
/// Reported in development mode when a world state counter of a module is not
/// zero after a world session ended: the module keeps state of a world that
/// no longer exists.
/// </summary>
public sealed class WorldStateLeakException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="module">The identifier of the module that registered the counter.</param>
    /// <param name="counter">The name of the counter.</param>
    /// <param name="count">The value of the counter.</param>
    public WorldStateLeakException(string module, string counter, long count)
        : base($"Module '{module}' keeps world state after the session ended: its counter '{counter}' is {count}, and should be 0.")
    {
        Module = module;
        Counter = counter;
        Count = count;
    }

    /// <summary>Gets the identifier of the module that registered the counter.</summary>
    public string Module { get; }

    /// <summary>Gets the name of the counter.</summary>
    public string Counter { get; }

    /// <summary>Gets the value of the counter.</summary>
    public long Count { get; }
}
