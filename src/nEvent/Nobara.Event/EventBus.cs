// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Nobara.Core;
using Nobara.Core.Logging;

namespace Nobara.Event;

/// <summary>How a dispatch reads the cancellation of an event, without boxing it.</summary>
[HotPath]
internal interface ICancelReader<TEvent>
    where TEvent : struct
{
    /// <summary>Gets whether the event type can be cancelled.</summary>
    static abstract bool Reads { get; }

    static abstract bool IsCancelled(in TEvent e);
}

/// <summary>The reader for an event that is not cancellable.</summary>
[HotPath]
internal readonly struct NotCancellable<TEvent> : ICancelReader<TEvent>
    where TEvent : struct
{
    public static bool Reads => false;

    public static bool IsCancelled(in TEvent e) => false;
}

/// <summary>The reader for an <see cref="ICancellable"/> event.</summary>
[HotPath]
internal readonly struct Cancellable<TEvent> : ICancelReader<TEvent>
    where TEvent : struct, ICancellable
{
    public static bool Reads => true;

    public static bool IsCancelled(in TEvent e) => e.Cancelled;
}

/// <summary>
/// The event bus of one context. The tables of handlers and queues are copied
/// when a type first appears, and never changed after being published, so a
/// dispatch reads them without a lock.
/// </summary>
internal sealed class EventBus : IEventBus
{
    /// <summary>The nesting of synchronous emissions that the bus accepts by default.</summary>
    public const int DefaultMaxDepth = 8;

    /// <summary>The number of deferred events of one type that the bus holds by default.</summary>
    public const int DefaultQueueCapacity = 1024;

    private readonly int _maxDepth;
    private readonly int _queueCapacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Subscriber> _subscribers = new(StringComparer.Ordinal);
    private Dictionary<Type, IHandlerList> _lists = [];
    private Dictionary<Type, IPostedQueue> _queues = [];
    private IPostedQueue[] _queueArray = [];
    private int _loopThread = Environment.CurrentManagedThreadId;
    private int _depth;
    private long _postOrder;

    public EventBus(int maxDepth = DefaultMaxDepth, int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        _maxDepth = maxDepth;
        _queueCapacity = queueCapacity;
    }

    /// <summary>Makes the calling thread the loop thread: the only one that emits synchronously.</summary>
    public void BindToCurrentThread() => Volatile.Write(ref _loopThread, Environment.CurrentManagedThreadId);

    /// <summary>Gets the subscriber that stands for a module, creating it on first use.</summary>
    public Subscriber SubscriberFor(ModuleContext module)
    {
        ArgumentNullException.ThrowIfNull(module);
        string id = module.Module.Id;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(id, out Subscriber? subscriber))
            {
                subscriber = new Subscriber(
                    id,
                    _subscribers.Count,
                    () => module.IsModuleActive(id),
                    (message, exception) => module.Logger.Log(LogLevel.Error, message, exception));
                _subscribers.Add(id, subscriber);
            }

            return subscriber;
        }
    }

    /// <summary>Revokes every handle of every module.</summary>
    public void RevokeAll()
    {
        Subscriber[] all;
        lock (_gate)
        {
            all = [.. _subscribers.Values];
        }

        foreach (Subscriber subscriber in all)
        {
            subscriber.RevokeAll();
        }
    }

    public EventHandle Subscribe<TEvent>(ModuleContext module, EventCallback<TEvent> callback, EventPriority priority = EventPriority.Normal, bool ignoreCancelled = false)
        where TEvent : struct
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (priority == EventPriority.Monitor)
        {
            throw new ArgumentException("The Monitor priority is for handlers that only read the event: use SubscribeMonitor.", nameof(priority));
        }

        return Add(SubscriberFor(module), callback, null, priority, ignoreCancelled);
    }

    public EventHandle SubscribeMonitor<TEvent>(ModuleContext module, MonitorCallback<TEvent> callback, bool ignoreCancelled = false)
        where TEvent : struct
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Add(SubscriberFor(module), null, callback, EventPriority.Monitor, ignoreCancelled);
    }

    /// <summary>Subscribes a handler for a subscriber that the caller already holds.</summary>
    public EventHandle Add<TEvent>(Subscriber subscriber, EventCallback<TEvent>? callback, MonitorCallback<TEvent>? monitor, EventPriority priority, bool ignoreCancelled)
        where TEvent : struct
    {
        lock (_gate)
        {
            _subscribers.TryAdd(subscriber.ModuleId, subscriber);
        }

        HandlerList<TEvent> list = ListFor<TEvent>();
        var registration = new Registration<TEvent>(list, subscriber, callback, monitor, priority, ignoreCancelled);
        subscriber.Track(registration);
        list.Add(registration);
        return new EventHandle(registration);
    }

    [HotPath]
    public bool HasSubscribers<TEvent>()
        where TEvent : struct
    {
        return Volatile.Read(ref _lists).TryGetValue(typeof(TEvent), out IHandlerList? list)
            && Unsafe.As<HandlerList<TEvent>>(list).Handlers.Length > 0;
    }

    [HotPath]
    public void Emit<TEvent>(ref TEvent e)
        where TEvent : struct
    {
        Dispatch<TEvent, NotCancellable<TEvent>>(ref e);
    }

    [HotPath]
    public void EmitCancellable<TEvent>(ref TEvent e)
        where TEvent : struct, ICancellable
    {
        Dispatch<TEvent, Cancellable<TEvent>>(ref e);
    }

    [HotPath]
    public void Post<TEvent>(in TEvent e)
        where TEvent : struct
    {
        if (default(TEvent) is ICancellable)
        {
            throw new InvalidOperationException($"Event '{typeof(TEvent).FullName}' is cancellable: a handler's answer would come too late. Emit it with EmitCancellable.");
        }

        if (!Volatile.Read(ref _queues).TryGetValue(typeof(TEvent), out IPostedQueue? queue))
        {
            return;
        }

        long order = Interlocked.Increment(ref _postOrder);
        if (!Unsafe.As<PostedQueue<TEvent>>(queue).TryEnqueue(in e, order))
        {
            throw new InvalidOperationException($"The queue of deferred events of type '{typeof(TEvent).FullName}' is full ({_queueCapacity} events).");
        }
    }

    /// <summary>
    /// Hands the deferred events posted so far to their handlers, in the order
    /// they were posted. An event posted while this runs waits for the next call.
    /// </summary>
    [HotPath]
    public void DistributeDeferred()
    {
        RequireLoopThread();
        IPostedQueue[] queues = Volatile.Read(ref _queueArray);
        long limit = Volatile.Read(ref _postOrder);
        while (true)
        {
            IPostedQueue? next = null;
            long oldest = long.MaxValue;
            for (int i = 0; i < queues.Length; i++)
            {
                if (queues[i].TryPeekOrder(out long order) && order <= limit && order < oldest)
                {
                    oldest = order;
                    next = queues[i];
                }
            }

            if (next is null)
            {
                return;
            }

            next.DispatchHead(this);
        }
    }

    [HotPath]
    private void Dispatch<TEvent, TCancel>(ref TEvent e)
        where TEvent : struct
        where TCancel : struct, ICancelReader<TEvent>
    {
        RequireLoopThread();
        if (!TCancel.Reads && default(TEvent) is ICancellable)
        {
            throw new InvalidOperationException($"Event '{typeof(TEvent).FullName}' is cancellable: emit it with EmitCancellable, so that the handlers can cancel it.");
        }

        if (!Volatile.Read(ref _lists).TryGetValue(typeof(TEvent), out IHandlerList? list))
        {
            return;
        }

        var typed = Unsafe.As<HandlerList<TEvent>>(list);

        Registration<TEvent>[] handlers = typed.Handlers;
        if (handlers.Length == 0)
        {
            return;
        }

        if (_depth >= _maxDepth)
        {
            throw new InvalidOperationException($"Event '{typeof(TEvent).FullName}' is emitted from {_depth} nested handlers, the maximum depth. An event loop is likely.");
        }

        _depth++;
        try
        {
            for (int i = 0; i < handlers.Length; i++)
            {
                Registration<TEvent> registration = handlers[i];
                if (registration.Revoked || !registration.Subscriber.IsActive)
                {
                    continue;
                }

                if (registration.IgnoreCancelled && TCancel.IsCancelled(in e))
                {
                    continue;
                }

                try
                {
                    registration.Invoke(ref e);
                }
                catch (Exception exception)
                {
                    registration.Subscriber.ReportFailure(registration.FailureMessage, exception);
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    [HotPath]
    private void RequireLoopThread()
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _loopThread))
        {
            throw new InvalidOperationException(
                $"Synchronous events are emitted from the loop thread of the context only, and thread {Environment.CurrentManagedThreadId} is not it. Post the event instead, or hand the work to the loop thread with ModuleContext.Post.");
        }
    }

    // Publishes a copy of the tables when a type first appears: dispatches never see a table change.
    private HandlerList<TEvent> ListFor<TEvent>()
        where TEvent : struct
    {
        lock (_gate)
        {
            if (_lists.TryGetValue(typeof(TEvent), out IHandlerList? existing))
            {
                return (HandlerList<TEvent>)existing;
            }

            var list = new HandlerList<TEvent>();
            Volatile.Write(ref _lists, new Dictionary<Type, IHandlerList>(_lists) { [typeof(TEvent)] = list });

            // A cancellable event is never deferred, so it needs no queue.
            if (!list.Cancellable)
            {
                var queue = new PostedQueue<TEvent>(_queueCapacity);
                Volatile.Write(ref _queues, new Dictionary<Type, IPostedQueue>(_queues) { [typeof(TEvent)] = queue });
                Volatile.Write(ref _queueArray, [.. _queueArray, queue]);
            }

            return list;
        }
    }
}
