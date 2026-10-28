// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Event;

/// <summary>
/// The event bus of a context: typed notifications between modules. A module
/// resolves it with <c>context.Resolve&lt;IEventBus&gt;()</c>, after declaring a
/// dependency on <c>nobara.event</c>.
/// </summary>
/// <remarks>
/// <para>
/// An event is a <see langword="struct"/> that the module that emits it
/// declares. A handler receives exactly the type it subscribed to, never a
/// related type. The bus is local to a context: nothing crosses the network
/// through it, and it is not for hot paths such as voxel changes or physics
/// contacts.
/// </para>
/// <para>
/// Handlers of one type run by priority, then in load order of their modules,
/// then in the order they subscribed. A handler that subscribes during a
/// dispatch is called from the next dispatch.
/// </para>
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// Subscribes a handler that may read and change events of one type.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="module">The subscribing module: it is charged for the exceptions of the handler and its handles are revoked when it stops.</param>
    /// <param name="callback">The handler.</param>
    /// <param name="priority">Where the handler runs; anything but <see cref="EventPriority.Monitor"/>.</param>
    /// <param name="ignoreCancelled">
    /// <see langword="true"/> to skip the handler when a handler that ran
    /// before it cancelled the event. It has no effect on events that are not
    /// <see cref="ICancellable"/>.
    /// </param>
    /// <returns>The handle that removes the handler.</returns>
    /// <exception cref="ArgumentException"><paramref name="priority"/> is <see cref="EventPriority.Monitor"/>: use <see cref="SubscribeMonitor{TEvent}"/>.</exception>
    /// <remarks>Allocates the registration; subscribe during initialization rather than in a loop.</remarks>
    EventHandle Subscribe<TEvent>(ModuleContext module, EventCallback<TEvent> callback, EventPriority priority = EventPriority.Normal, bool ignoreCancelled = false)
        where TEvent : struct;

    /// <summary>
    /// Subscribes a handler that sees the final event after every other
    /// handler, and can neither cancel nor change it.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="module">The subscribing module.</param>
    /// <param name="callback">The handler.</param>
    /// <param name="ignoreCancelled"><see langword="true"/> to skip the handler when the event ended cancelled.</param>
    /// <returns>The handle that removes the handler.</returns>
    EventHandle SubscribeMonitor<TEvent>(ModuleContext module, MonitorCallback<TEvent> callback, bool ignoreCancelled = false)
        where TEvent : struct;

    /// <summary>Tells whether an event type has handlers, to skip building a costly event.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <returns><see langword="true"/> when at least one handler is subscribed.</returns>
    /// <remarks>Allocation-free.</remarks>
    [HotPath]
    bool HasSubscribers<TEvent>()
        where TEvent : struct;

    /// <summary>
    /// Emits an event that is not <see cref="ICancellable"/> to its handlers,
    /// which run before this method returns.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="e">The event. Handlers may change it; read it after the call.</param>
    /// <exception cref="InvalidOperationException">
    /// The caller is not on the loop thread of the context, the dispatch is nested deeper than
    /// the maximum depth, or <typeparamref name="TEvent"/> is <see cref="ICancellable"/>.
    /// </exception>
    /// <remarks>
    /// Allocation-free. An exception in a handler is logged with its module
    /// and the other handlers still run. From a job, post the event instead.
    /// </remarks>
    [HotPath]
    void Emit<TEvent>(ref TEvent e)
        where TEvent : struct;

    /// <summary>
    /// Emits an <see cref="ICancellable"/> event to its handlers, which run
    /// before this method returns.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="e">The event. Read <see cref="ICancellable.Cancelled"/> and the changed fields after the call.</param>
    /// <exception cref="InvalidOperationException">
    /// The caller is not on the loop thread of the context, or the dispatch is nested deeper than the maximum depth.
    /// </exception>
    /// <remarks>Allocation-free. See <see cref="Emit{TEvent}(ref TEvent)"/> for errors in handlers.</remarks>
    [HotPath]
    void EmitCancellable<TEvent>(ref TEvent e)
        where TEvent : struct, ICancellable;

    /// <summary>
    /// Queues an event that is not <see cref="ICancellable"/> for the end of
    /// the tick: the loop thread hands it to the handlers after the
    /// simulation. It can be called from any thread, jobs included.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="e">The event, copied into the queue.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TEvent"/> is <see cref="ICancellable"/>, or the queue of this type is full.
    /// </exception>
    /// <remarks>
    /// Lock-free and allocation-free. An event posted while the queue is
    /// handed out waits for the next tick. An event of a type nobody
    /// subscribed to is dropped.
    /// </remarks>
    [HotPath]
    void Post<TEvent>(in TEvent e)
        where TEvent : struct;
}
