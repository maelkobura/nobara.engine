// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Event;

/// <summary>
/// The place of a handler among the handlers of an event. Handlers run from
/// <see cref="Lowest"/> to <see cref="Highest"/>, then <see cref="Monitor"/>,
/// so the last word belongs to the highest priority.
/// </summary>
public enum EventPriority
{
    /// <summary>Runs first: its decision can be overridden by every other handler.</summary>
    Lowest,

    /// <summary>Runs after <see cref="Lowest"/>.</summary>
    Low,

    /// <summary>The default priority.</summary>
    Normal,

    /// <summary>Runs after <see cref="Normal"/>.</summary>
    High,

    /// <summary>Runs last of the handlers that may change the event: its decision stands.</summary>
    Highest,

    /// <summary>
    /// Runs after every other handler and sees the final event. It can
    /// neither cancel nor change it: it receives the event by read-only
    /// reference.
    /// </summary>
    Monitor,
}
