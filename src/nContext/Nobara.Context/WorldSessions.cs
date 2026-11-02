// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Context;

/// <summary>
/// The world sessions of one context. The state stack runs it at the start of
/// each frame, after the transitions.
/// </summary>
internal sealed class WorldSessions : IWorldSessions
{
    private readonly ModuleContext _context;
    private readonly List<Handler> _starting = [];
    private readonly List<Handler> _started = [];
    private readonly List<Handler> _ending = [];
    private readonly List<Handler> _ended = [];
    private readonly List<Counter> _counters = [];
    private long _sequence;
    private int _number;
    private WorldSession? _current;
    private bool _endRequested;

    public WorldSessions(ModuleContext context)
    {
        _context = context;
    }

    public SessionState State => _current?.State ?? SessionState.None;

    public WorldSession? Current => _current;

    public bool HasPendingEnd => _endRequested;

    // Whether a session is active once the requests already made apply: the
    // states that require a session can enter.
    internal bool IsActiveOnceRequestsApply => _current is { State: SessionState.Active } && !_endRequested;

    public WorldSession Start()
    {
        if (_current is { } current)
        {
            throw new InvalidOperationException(
                $"A world session cannot start: session {current.Number} is {Describe(current.State)}. End it, and start the next one once State is None.");
        }

        _current = new WorldSession(++_number);
        _context.Logger.Info($"World session {_number} is requested.");
        return _current;
    }

    public void End()
    {
        if (_current is null)
        {
            throw new InvalidOperationException("There is no world session to end.");
        }

        _endRequested = true;
    }

    public SessionSubscription SubscribeStarting(ModuleContext module, Action<WorldSession> handler) => Subscribe(_starting, module, handler);

    public SessionSubscription SubscribeStarted(ModuleContext module, Action<WorldSession> handler) => Subscribe(_started, module, handler);

    public SessionSubscription SubscribeEnding(ModuleContext module, Action<WorldSession> handler) => Subscribe(_ending, module, handler);

    public SessionSubscription SubscribeEnded(ModuleContext module, Action<WorldSession> handler) => Subscribe(_ended, module, handler);

    public SessionSubscription RegisterWorldStateCounter(ModuleContext module, string name, Func<long> count)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(count);
        Counter counter = null!;
        var subscription = new SessionSubscription(_ => _counters.Remove(counter));
        counter = new Counter(module, name, count, subscription, _sequence++);
        Insert(_counters, counter, c => (c.Module.LoadOrder, c.Sequence));
        return subscription;
    }

    /// <summary>Applies the requests, starts the session, and follows its loading tasks. Allocates nothing while a session runs.</summary>
    internal void RunFrameStart()
    {
        WorldSession? session = _current;
        if (session is null)
        {
            return;
        }

        // A request made while this runs waits for the next frame.
        if (_endRequested)
        {
            EndNow(SessionEndReason.Requested, null);
            return;
        }

        if (session.State != SessionState.Starting)
        {
            return;
        }

        if (!session.Raised)
        {
            RaiseStarting(session);
        }

        if (_current == session && session.State == SessionState.Starting)
        {
            Follow(session);
        }
    }

    /// <summary>Ends the session at once, because the context stops.</summary>
    internal void EndForStop()
    {
        if (_current is not null)
        {
            EndNow(SessionEndReason.Stopping, null);
        }
    }

    /// <summary>Logs a session that the context leaves without ending it.</summary>
    internal void Stop()
    {
        if (_current is { } session)
        {
            _context.Logger.Info($"The context stops during world session {session.Number}, which does not end.");
        }
    }

    private void RaiseStarting(WorldSession session)
    {
        session.Raised = true;
        _context.Logger.Info($"World session {session.Number} starts.");
        Dispatch(_starting, session, reverse: false, loading: session.Loading);
    }

    private void Follow(WorldSession session)
    {
        LoadingTasks loading = session.Loading;
        loading.Poll();
        if (loading.FirstFailed() is { } failed)
        {
            _context.Logger.Error(
                $"Loading task '{failed.Name}' of module '{failed.Module}' failed: {failed.FailureReason} World session {session.Number} is cancelled.",
                failed.Exception);
            EndNow(SessionEndReason.Failed, new SessionFailure(failed.Name, failed.Module, failed.FailureReason!, failed.Exception));
            return;
        }

        if (!loading.AllCompleted())
        {
            return;
        }

        loading.MarkCompleted();
        Close(loading);
        session.State = SessionState.Active;
        _context.Logger.Info($"World session {session.Number} is active: {loading.Tasks.Count} loading tasks completed.");
        Dispatch(_started, session, reverse: false, loading: null);
    }

    private void EndNow(SessionEndReason reason, SessionFailure? failure)
    {
        WorldSession session = _current!;
        _endRequested = false;
        Close(session.Loading);
        session.EndReason = reason;
        session.Failure = failure;
        if (!session.Raised)
        {
            // Nothing was created for a session whose start was never applied.
            session.State = SessionState.Ended;
            _current = null;
            _context.Logger.Info($"World session {session.Number} ends before it started.");
            return;
        }

        session.State = SessionState.Ending;
        Dispatch(_ending, session, reverse: true, loading: null);
        Dispatch(_ended, session, reverse: true, loading: null);
        session.State = SessionState.Ended;
        _current = null;
        _context.Logger.Info($"World session {session.Number} ended ({Describe(reason)}).");
        if (_context.DevelopmentMode)
        {
            CheckCounters();
        }
    }

    private void Close(LoadingTasks loading)
    {
        if (loading.Close() is { } exception)
        {
            _context.Logger.Error("A callback of the cancellation token of the loading tasks threw.", exception);
        }
    }

    // In load order for the start, in the reverse order for the end. The
    // copy lets a handler subscribe or revoke while the dispatch runs.
    private void Dispatch(List<Handler> handlers, WorldSession session, bool reverse, LoadingTasks? loading)
    {
        Handler[] snapshot = [.. handlers];
        for (int n = 0; n < snapshot.Length; n++)
        {
            Handler handler = snapshot[reverse ? snapshot.Length - 1 - n : n];
            ModuleContext module = handler.Module;
            if (handler.Subscription.Revoked || !module.IsModuleActive(module.Module.Id))
            {
                continue;
            }

            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(module.SynchronizationContext);
            if (loading is not null)
            {
                loading.Registrar = module;
            }

            try
            {
                handler.Action(session);
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                module.ReportFault(exception);
            }
            finally
            {
                if (loading is not null)
                {
                    loading.Registrar = null;
                }

                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    private void CheckCounters()
    {
        Counter[] snapshot = [.. _counters];
        foreach (Counter counter in snapshot)
        {
            ModuleContext module = counter.Module;
            if (counter.Subscription.Revoked || !module.IsModuleActive(module.Module.Id))
            {
                continue;
            }

            long count;
            try
            {
                count = counter.Count();
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                module.ReportFault(exception);
                continue;
            }

            if (count != 0)
            {
                module.ReportFault(new WorldStateLeakException(module.Module.Id, counter.Name, count));
            }
        }
    }

    private SessionSubscription Subscribe(List<Handler> handlers, ModuleContext module, Action<WorldSession> action)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(action);
        Handler handler = null!;
        var subscription = new SessionSubscription(_ => handlers.Remove(handler));
        handler = new Handler(module, action, subscription, _sequence++);
        Insert(handlers, handler, h => (h.Module.LoadOrder, h.Sequence));
        return subscription;
    }

    // Keeps the list sorted by load order, then by registration.
    private static void Insert<T>(List<T> list, T item, Func<T, (int Order, long Sequence)> key)
    {
        (int Order, long Sequence) added = key(item);
        int index = list.Count;
        while (index > 0 && key(list[index - 1]).CompareTo(added) > 0)
        {
            index--;
        }

        list.Insert(index, item);
    }

    private static string Describe(SessionState state) => state switch
    {
        SessionState.Starting => "starting",
        SessionState.Active => "active",
        SessionState.Ending => "ending",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string Describe(SessionEndReason reason) => reason switch
    {
        SessionEndReason.Requested => "requested",
        SessionEndReason.Stopping => "the context stops",
        _ => "a loading task failed",
    };

    private sealed record Handler(ModuleContext Module, Action<WorldSession> Action, SessionSubscription Subscription, long Sequence);

    private sealed record Counter(ModuleContext Module, string Name, Func<long> Count, SessionSubscription Subscription, long Sequence);
}
