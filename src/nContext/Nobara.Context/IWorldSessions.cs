// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Context;

/// <summary>
/// The world sessions of a context: the presence in a world, from the entry
/// to the exit. A context runs one session at a time, and chains as many as it
/// needs without restarting its modules. There is one per context.
/// </summary>
/// <remarks>
/// <para>
/// A state starts a session with <see cref="Start"/>, such as a loading
/// screen, and ends it with <see cref="End"/>. Both are requests, applied at
/// the start of the next frame, after the transitions of the state stack.
/// </para>
/// <para>
/// Modules keep their state of a world between the events: they create it in
/// <c>SessionStarting</c>, where they also add the loading tasks the session
/// waits for, and free it in <c>SessionEnded</c>. The events of a session come
/// in this order: <c>SessionStarting</c>, <c>SessionStarted</c> once every
/// task completed, <c>SessionEnding</c>, <c>SessionEnded</c>. A session
/// canceled while it starts skips <c>SessionStarted</c>. Handlers run in the
/// load order of their modules for the start, and in the reverse order for
/// the end.
/// </para>
/// <para>Use it on the loop thread of the context.</para>
/// </remarks>
public interface IWorldSessions
{
    /// <summary>Gets where the current session is, or <see cref="SessionState.None"/> without one.</summary>
    SessionState State { get; }

    /// <summary>Gets the current session, from <see cref="Start"/> until its <c>SessionEnded</c> handlers ran, or <see langword="null"/>.</summary>
    WorldSession? Current { get; }

    /// <summary>Gets whether <see cref="End"/> was called and the session ends at the start of the next frame.</summary>
    bool HasPendingEnd { get; }

    /// <summary>
    /// Requests a world session: at the start of the next frame, the
    /// <c>SessionStarting</c> handlers run and add their loading tasks; the
    /// session is active once every task completed.
    /// </summary>
    /// <returns>
    /// The session, in the <see cref="SessionState.Starting"/> state: the
    /// caller reads its progress, and its <see cref="WorldSession.Failure"/>
    /// when a task fails.
    /// </returns>
    /// <exception cref="InvalidOperationException">A session exists: end it, and start the next one once <see cref="State"/> is <see cref="SessionState.None"/>.</exception>
    WorldSession Start();

    /// <summary>
    /// Requests the end of the current session, active or starting: at the
    /// start of the next frame, the <c>SessionEnding</c> then the
    /// <c>SessionEnded</c> handlers run. Calling it again does nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no session.</exception>
    void End();

    /// <summary>
    /// Subscribes a handler to <c>SessionStarting</c>: create the state of the
    /// module tied to the world, and add loading tasks to
    /// <see cref="WorldSession.Loading"/>.
    /// </summary>
    /// <param name="module">The context of the module that subscribes; an exception of the handler is charged to it.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The subscription; dispose it to remove the handler.</returns>
    SessionSubscription SubscribeStarting(ModuleContext module, Action<WorldSession> handler);

    /// <summary>Subscribes a handler to <c>SessionStarted</c>: every loading task completed, and the session is active.</summary>
    /// <param name="module">The context of the module that subscribes; an exception of the handler is charged to it.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The subscription; dispose it to remove the handler.</returns>
    SessionSubscription SubscribeStarted(ModuleContext module, Action<WorldSession> handler);

    /// <summary>
    /// Subscribes a handler to <c>SessionEnding</c>: save, disconnect cleanly.
    /// <see cref="WorldSession.EndReason"/> tells why the session ends.
    /// </summary>
    /// <param name="module">The context of the module that subscribes; an exception of the handler is charged to it.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The subscription; dispose it to remove the handler.</returns>
    SessionSubscription SubscribeEnding(ModuleContext module, Action<WorldSession> handler);

    /// <summary>
    /// Subscribes a handler to <c>SessionEnded</c>: free the state of the
    /// module tied to the world, and wait for the background jobs that use it.
    /// </summary>
    /// <param name="module">The context of the module that subscribes; an exception of the handler is charged to it.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The subscription; dispose it to remove the handler.</returns>
    SessionSubscription SubscribeEnded(ModuleContext module, Action<WorldSession> handler);

    /// <summary>
    /// Registers a world state counter: in development mode, once the
    /// <c>SessionEnded</c> handlers ran, nContext reads it, and a value other
    /// than 0 is an error charged to the module, under its error policy, as a
    /// <see cref="WorldStateLeakException"/>. Outside development mode, the
    /// counter is never read.
    /// </summary>
    /// <param name="module">The context of the module that keeps the state.</param>
    /// <param name="name">What the counter counts, such as <c>chunks</c>.</param>
    /// <param name="count">Reads the number of objects tied to a world that the module holds.</param>
    /// <returns>The registration; dispose it to remove the counter.</returns>
    SessionSubscription RegisterWorldStateCounter(ModuleContext module, string name, Func<long> count);
}

/// <summary>A world session of a context, from <see cref="IWorldSessions.Start"/> to its end.</summary>
public sealed class WorldSession
{
    internal WorldSession(int number)
    {
        Number = number;
        Loading = new LoadingTasks(LoadingScope.World);
    }

    /// <summary>Gets the number of the session in its context, from 1.</summary>
    public int Number { get; }

    /// <summary>Gets where the session is: from <see cref="SessionState.Starting"/> to <see cref="SessionState.Ended"/>.</summary>
    public SessionState State { get; internal set; } = SessionState.Starting;

    /// <summary>Gets the loading tasks that the session waits for, and their progress.</summary>
    public LoadingTasks Loading { get; }

    /// <summary>Gets why the session ends, or <see langword="null"/> until it does.</summary>
    public SessionEndReason? EndReason { get; internal set; }

    /// <summary>Gets why the session was canceled while it started, or <see langword="null"/>.</summary>
    public SessionFailure? Failure { get; internal set; }

    // Whether the SessionStarting handlers ran.
    internal bool Raised { get; set; }
}
