// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Context;

/// <summary>
/// Something that happens to the whole application and that the active state
/// decides about first, such as a stop request. A module raises one with
/// <see cref="IStateStack.Raise"/>.
/// </summary>
/// <remarks>
/// The active state receives the event in <see cref="ApplicationState.OnGlobalEvent"/>.
/// When it does not handle it, or when the stack is empty, the default action
/// of the event applies.
/// </remarks>
public abstract class GlobalEvent
{
    /// <summary>
    /// Applies the default action of the event, when no state handles it.
    /// Does nothing unless a derived event overrides it.
    /// </summary>
    /// <param name="states">The state stack of the context.</param>
    protected virtual void ApplyDefault(IStateStack states)
    {
    }

    internal void ApplyDefaultAction(IStateStack states) => ApplyDefault(states);
}

/// <summary>
/// A request to stop the application: Ctrl+C in the console, or a stop that
/// the host of the engine requests. Its default action pops every state, which
/// stops the context.
/// </summary>
/// <remarks>
/// A state that handles it decides what the request does: a dedicated server
/// replaces its running state with a state that saves before it stops, a
/// client asks for a confirmation.
/// </remarks>
public sealed class StopRequested : GlobalEvent
{
    /// <summary>Initializes the event.</summary>
    public StopRequested()
    {
    }

    /// <inheritdoc/>
    protected override void ApplyDefault(IStateStack states)
    {
        ArgumentNullException.ThrowIfNull(states);
        states.PopAll();
    }
}
