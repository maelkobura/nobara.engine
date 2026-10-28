// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Event;

/// <summary>What a handle revokes.</summary>
internal interface IRegistration
{
    bool Revoked { get; }

    void Revoke();
}

/// <summary>
/// A module as the bus sees it: who is charged for a failing handler, where
/// the module ranks in the order of handlers, and which registrations it holds.
/// </summary>
/// <remarks>
/// The rank is the order in which the bus first saw the module subscribe,
/// which is the load order for the modules that subscribe while they
/// initialize.
/// </remarks>
internal sealed class Subscriber
{
    private readonly Func<bool> _isActive;
    private readonly Action<string, Exception> _report;
    private readonly Lock _gate = new();
    private readonly List<IRegistration> _registrations = [];

    public Subscriber(string moduleId, int rank, Func<bool> isActive, Action<string, Exception> report)
    {
        ModuleId = moduleId;
        Rank = rank;
        _isActive = isActive;
        _report = report;
    }

    public string ModuleId { get; }

    public int Rank { get; }

    /// <summary>Gets whether the module still runs: a module that the core disabled gets no more events.</summary>
    public bool IsActive
    {
        [HotPath]
        get => _isActive();
    }

    [HotPath]
    public void ReportFailure(string message, Exception exception) => _report(message, exception);

    public void Track(IRegistration registration)
    {
        lock (_gate)
        {
            _registrations.Add(registration);
        }
    }

    public void Forget(IRegistration registration)
    {
        lock (_gate)
        {
            _registrations.Remove(registration);
        }
    }

    public void RevokeAll()
    {
        IRegistration[] all;
        lock (_gate)
        {
            all = [.. _registrations];
        }

        foreach (IRegistration registration in all)
        {
            registration.Revoke();
        }
    }
}

/// <summary>One handler subscribed to one event type.</summary>
internal sealed class Registration<TEvent> : IRegistration
    where TEvent : struct
{
    private readonly HandlerList<TEvent> _list;
    private readonly EventCallback<TEvent>? _callback;
    private readonly MonitorCallback<TEvent>? _monitor;
    private int _revoked;

    public Registration(
        HandlerList<TEvent> list,
        Subscriber subscriber,
        EventCallback<TEvent>? callback,
        MonitorCallback<TEvent>? monitor,
        EventPriority priority,
        bool ignoreCancelled)
    {
        _list = list;
        _callback = callback;
        _monitor = monitor;
        Subscriber = subscriber;
        Priority = priority;
        IgnoreCancelled = ignoreCancelled;
        FailureMessage = $"Module '{subscriber.ModuleId}' threw in a handler of event '{typeof(TEvent).FullName}'; the other handlers still run.";
    }

    public Subscriber Subscriber { get; }

    public EventPriority Priority { get; }

    public bool IgnoreCancelled { get; }

    /// <summary>Gets the message logged, with the exception, when the handler throws. Built once, so that a failure allocates nothing here.</summary>
    public string FailureMessage { get; }

    public bool Revoked
    {
        [HotPath]
        get => Volatile.Read(ref _revoked) != 0;
    }

    [HotPath]
    public void Invoke(ref TEvent e)
    {
        if (_callback is not null)
        {
            _callback(ref e);
        }
        else
        {
            _monitor!(in e);
        }
    }

    public void Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) == 0)
        {
            _list.Remove(this);
            Subscriber.Forget(this);
        }
    }
}

/// <summary>The handlers of one event type of one bus.</summary>
internal interface IHandlerList
{
    /// <summary>Gets whether the event type implements <see cref="ICancellable"/>.</summary>
    bool Cancellable { get; }
}

/// <summary>
/// The handlers of one event type, sorted by priority, then by rank of
/// module, then by order of subscription. The array is copied on each change,
/// so a dispatch walks the array it read without a lock.
/// </summary>
internal sealed class HandlerList<TEvent> : IHandlerList
    where TEvent : struct
{
    private readonly Lock _gate = new();
    private Registration<TEvent>[] _handlers = [];

    public bool Cancellable { get; } = default(TEvent) is ICancellable;

    public Registration<TEvent>[] Handlers
    {
        [HotPath]
        get => Volatile.Read(ref _handlers);
    }

    public void Add(Registration<TEvent> registration)
    {
        lock (_gate)
        {
            Registration<TEvent>[] old = _handlers;
            int index = old.Length;
            for (int i = 0; i < old.Length; i++)
            {
                if (Compare(old[i], registration) > 0)
                {
                    index = i;
                    break;
                }
            }

            var copy = new Registration<TEvent>[old.Length + 1];
            Array.Copy(old, 0, copy, 0, index);
            copy[index] = registration;
            Array.Copy(old, index, copy, index + 1, old.Length - index);
            Volatile.Write(ref _handlers, copy);
        }
    }

    public void Remove(Registration<TEvent> registration)
    {
        lock (_gate)
        {
            Registration<TEvent>[] old = _handlers;
            int index = Array.IndexOf(old, registration);
            if (index < 0)
            {
                return;
            }

            var copy = new Registration<TEvent>[old.Length - 1];
            Array.Copy(old, 0, copy, 0, index);
            Array.Copy(old, index + 1, copy, index, old.Length - index - 1);
            Volatile.Write(ref _handlers, copy);
        }
    }

    // A new registration goes after the ones that rank equal: the order of subscription breaks ties.
    private static int Compare(Registration<TEvent> existing, Registration<TEvent> added)
    {
        int byPriority = existing.Priority.CompareTo(added.Priority);
        return byPriority != 0 ? byPriority : existing.Subscriber.Rank.CompareTo(added.Subscriber.Rank);
    }
}
