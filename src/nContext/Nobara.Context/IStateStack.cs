// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Context;

/// <summary>
/// The state stack of a context: the states of the application that modules
/// register, the stack whose top is the active state, and the transitions.
/// There is one per context.
/// </summary>
/// <remarks>
/// <para>
/// Transitions are requested at any time on the loop thread, and applied at
/// the start of the next frame, before the first phase of the loop, in the
/// order they were requested. A transition requested while transitions are
/// applied waits for the frame after.
/// </para>
/// <para>
/// When the stack becomes empty, the context stops after the frame: for the
/// main context, the game ends.
/// </para>
/// <para>Use it on the loop thread of the context.</para>
/// </remarks>
public interface IStateStack
{
    /// <summary>
    /// Gets the identifier of the initial state of this side, which enters the
    /// stack at the first frame, or <see langword="null"/> before it is read.
    /// </summary>
    string? InitialState { get; }

    /// <summary>Gets the identifiers of the states in the stack, from the bottom to the top.</summary>
    IReadOnlyList<string> States { get; }

    /// <summary>Gets the identifier of the active state, at the top of the stack, or <see langword="null"/> when the stack is empty.</summary>
    string? Active { get; }

    /// <summary>Gets whether transitions are waiting for the next frame.</summary>
    bool HasPendingTransitions { get; }

    /// <summary>
    /// Registers a state of the module. A module registers each state that its
    /// manifest declares on the side of the context, while it initializes.
    /// </summary>
    /// <param name="module">The context of the module that provides the state.</param>
    /// <param name="id">The identifier of the state, as the manifest declares it, such as <c>state:example.menu</c>.</param>
    /// <param name="create">Creates an instance of the state, each time it enters the stack.</param>
    /// <param name="options">How the state behaves in the stack; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> does not start with <c>state:</c>, or is already registered.</exception>
    /// <exception cref="InvalidOperationException">The step of the context is not <see cref="LifecycleStep.Initialization"/>.</exception>
    void Register(ModuleContext module, string id, Func<ApplicationState> create, StateOptions? options = null);

    /// <summary>Tells whether a state is registered in this context.</summary>
    /// <param name="id">The identifier of a state.</param>
    /// <returns><see langword="true"/> when a module registered it.</returns>
    bool IsRegistered(string id);

    /// <summary>Tells whether a state of the stack runs: it is at the top, or every state above it keeps the states below it running.</summary>
    /// <param name="id">The identifier of a state.</param>
    /// <returns><see langword="true"/> when the state is in the stack and runs.</returns>
    bool IsRunning(string id);

    /// <summary>Requests a state to be pushed on top of the stack, such as a pause menu over the game.</summary>
    /// <param name="id">The identifier of a registered state.</param>
    /// <exception cref="ArgumentException">No module registers the state in this context.</exception>
    /// <exception cref="InvalidOperationException">The state is in the stack once the transitions already requested apply.</exception>
    void Push(string id);

    /// <summary>Requests the active state to be popped.</summary>
    /// <exception cref="InvalidOperationException">The stack is empty once the transitions already requested apply.</exception>
    void Pop();

    /// <summary>Requests the active state to be replaced, such as a menu by a loading screen.</summary>
    /// <param name="id">The identifier of a registered state.</param>
    /// <exception cref="ArgumentException">No module registers the state in this context.</exception>
    /// <exception cref="InvalidOperationException">
    /// The stack is empty once the transitions already requested apply, or
    /// the state is in it below the active state.
    /// </exception>
    void Replace(string id);

    /// <summary>
    /// Requests every state to be popped, from the top to the bottom. Unless a
    /// later transition pushes a state, the stack is then empty and the
    /// context stops.
    /// </summary>
    void PopAll();

    /// <summary>
    /// Raises a global event: at the start of the next frame, before the
    /// transitions, the active state receives it, and its default action
    /// applies when the state does not handle it.
    /// </summary>
    /// <param name="e">The event.</param>
    void Raise(GlobalEvent e);
}

/// <summary>How a state behaves in the stack.</summary>
public sealed class StateOptions
{
    /// <summary>
    /// Gets whether the states below this state keep running while it is in
    /// the stack, such as the game under a pause menu in multiplayer.
    /// Otherwise they are suspended.
    /// </summary>
    public bool KeepStatesBelowRunning { get; init; }
}
