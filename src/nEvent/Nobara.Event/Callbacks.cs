// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Event;

/// <summary>A handler that may read and change an event.</summary>
/// <typeparam name="TEvent">The event type, exactly: a handler of a type does not receive other types.</typeparam>
/// <param name="e">The event, by reference. The emitter reads the changes after the dispatch.</param>
public delegate void EventCallback<TEvent>(ref TEvent e)
    where TEvent : struct;

/// <summary>A handler that only reads an event, at the <see cref="EventPriority.Monitor"/> priority.</summary>
/// <typeparam name="TEvent">The event type, exactly.</typeparam>
/// <param name="e">The final event, by read-only reference.</param>
public delegate void MonitorCallback<TEvent>(in TEvent e)
    where TEvent : struct;
