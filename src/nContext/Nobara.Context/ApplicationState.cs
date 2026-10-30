// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;

namespace Nobara.Context;

/// <summary>
/// A state of the application, such as a main menu, a loading screen, or the
/// game itself. A module provides it, declares it in its manifest, and
/// registers it with <see cref="IStateStack.Register"/>.
/// </summary>
/// <remarks>
/// <para>
/// Override only the methods the state needs; they do nothing by default.
/// nContext calls them on the loop thread, at the start of a frame, before
/// the first phase of the loop: never in the middle of a simulation tick.
/// </para>
/// <para>
/// nContext creates a new instance each time the state enters the stack, and
/// drops it once the state has exited.
/// </para>
/// </remarks>
public abstract class ApplicationState
{
    /// <summary>Gets the identifier of the state, such as <c>state:example.menu</c>.</summary>
    public string Id { get; internal set; } = string.Empty;

    /// <summary>Called when the state enters the stack. The state then runs.</summary>
    [LoopCallback]
    public virtual void Enter()
    {
    }

    /// <summary>Called when the state leaves the stack.</summary>
    [LoopCallback]
    public virtual void Exit()
    {
    }

    /// <summary>
    /// Called when the state stops running while it stays in the stack: a
    /// state that does not keep the states below it running was pushed on top
    /// of it.
    /// </summary>
    [LoopCallback]
    public virtual void Suspend()
    {
    }

    /// <summary>Called when the state runs again after <see cref="Suspend"/>.</summary>
    [LoopCallback]
    public virtual void Resume()
    {
    }

    /// <summary>
    /// Called at each frame while the state runs, after the transitions of the
    /// frame. The running states are updated from the bottom of the stack to
    /// its top.
    /// </summary>
    /// <param name="time">The frame: its number and the real time since the previous frame.</param>
    [LoopCallback]
    public virtual void Update(in PhaseTime time)
    {
    }

    /// <summary>
    /// Called when a global event reaches the application, such as a stop
    /// request, while this state is the active state.
    /// </summary>
    /// <param name="e">The event.</param>
    /// <returns>
    /// <see langword="true"/> when the state handled the event; the default
    /// action of the event, such as stopping, then does not apply.
    /// </returns>
    [LoopCallback]
    public virtual bool OnGlobalEvent(GlobalEvent e) => false;
}
