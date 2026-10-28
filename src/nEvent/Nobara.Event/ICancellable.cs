// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Event;

/// <summary>
/// Marks an event that handlers can cancel. The emitter reads
/// <see cref="Cancelled"/> after the dispatch and gives up the action.
/// </summary>
/// <remarks>
/// A cancellable event is emitted synchronously with
/// <see cref="IEventBus.EmitCancellable{TEvent}(ref TEvent)"/>: it cannot be
/// posted for later, since nobody would be waiting for the answer.
/// </remarks>
/// <example>
/// <code>
/// public struct PlayerJoinRequested : ICancellable
/// {
///     public string Name;
///     public bool Cancelled { get; set; }
/// }
/// </code>
/// </example>
public interface ICancellable
{
    /// <summary>Gets or sets whether a handler cancelled the event.</summary>
    [HotPath]
    bool Cancelled { get; set; }
}
