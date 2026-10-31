// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Frames;

namespace Nobara.Input;

/// <summary>
/// The service of nInput: the actions and maps declared by the modules, the
/// stack of input layers, and the bindings of the player. A module resolves
/// it with <c>context.Resolve&lt;IInputSystem&gt;()</c>, after declaring a
/// dependency on <c>nobara.input</c>.
/// </summary>
/// <remarks>
/// <para>
/// The actions and maps are read when nInput initializes: modules that depend
/// on nInput find them from their own initialization on.
/// </para>
/// <para>
/// nInput reads the raw input of nFrames at the start of each frame, before
/// the first phase, and evaluates the actions for each layer. Use the service
/// on the loop thread of the client context.
/// </para>
/// </remarks>
public interface IInputSystem
{
    /// <summary>Gets the actions, sorted by identifier.</summary>
    IReadOnlyList<InputAction> Actions { get; }

    /// <summary>Gets the maps, sorted by identifier.</summary>
    IReadOnlyList<InputMap> Maps { get; }

    /// <summary>Gets the layers that are registered, from the one that receives input first to the last.</summary>
    IReadOnlyList<InputLayer> Layers { get; }

    /// <summary>Gets the bindings of the player: rebinding, conflicts, defaults, and <c>config/input.xml</c>.</summary>
    IInputRebinding Rebinding { get; }

    /// <summary>Gets a version number of the keyboard layout, which grows when the player changes layout: names from <see cref="GetLayoutName"/> cached before are out of date.</summary>
    int LayoutVersion { get; }

    /// <summary>Gets an action.</summary>
    /// <param name="id">The object identifier: <c>inputaction:example.input.jump</c>.</param>
    /// <returns>The action.</returns>
    /// <exception cref="KeyNotFoundException">No module declares the action.</exception>
    InputAction GetAction(string id);

    /// <summary>Gets an action, or reports it absent.</summary>
    /// <param name="id">The object identifier.</param>
    /// <param name="action">The action, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a module declares it.</returns>
    bool TryGetAction(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InputAction? action);

    /// <summary>Gets a map.</summary>
    /// <param name="id">The object identifier: <c>inputmap:example.input.on_foot</c>.</param>
    /// <returns>The map.</returns>
    /// <exception cref="KeyNotFoundException">No module declares the map.</exception>
    InputMap GetMap(string id);

    /// <summary>Gets a map, or reports it absent.</summary>
    /// <param name="id">The object identifier.</param>
    /// <param name="map">The map, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a module declares it.</returns>
    bool TryGetMap(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InputMap? map);

    /// <summary>Registers a layer in the stack.</summary>
    /// <param name="module">The module of the layer: it is charged for the exceptions of the handler, and the layer stops receiving input while it is disabled.</param>
    /// <param name="options">The name, priority, captures, cursor, and handler of the layer.</param>
    /// <returns>The layer; dispose it to remove it from the stack.</returns>
    /// <remarks>
    /// Layers receive input from the highest priority to the lowest; among
    /// layers of the same priority, the one registered last comes first.
    /// </remarks>
    InputLayer AddLayer(ModuleContext module, InputLayerOptions options);

    /// <summary>Gets the name to show to the player for a key, as the keyboard layout labels it, such as <c>Z</c> for <c>Key.W</c> on an AZERTY keyboard.</summary>
    /// <param name="control">A control.</param>
    /// <returns>The name of a key of one side, or an empty string for the other controls, which the game names itself.</returns>
    /// <exception cref="InvalidOperationException">The window is not created yet: it is, from the start step on.</exception>
    string GetLayoutName(InputControl control);
}

/// <summary>
/// The bindings of the player. Changes apply at the next frame; <see cref="Save"/>
/// writes the differences with the defaults to <c>config/input.xml</c>.
/// </summary>
public interface IInputRebinding
{
    /// <summary>Gets the full path of the file of the bindings of the player: <c>input.xml</c> in the configuration folder.</summary>
    string FilePath { get; }

    /// <summary>
    /// Lists the actions that a binding would conflict with: actions of the
    /// same map, or of maps active at the same time, or without a map, that
    /// have a binding triggered by the same input. The user interface
    /// decides whether to refuse or to swap.
    /// </summary>
    /// <param name="action">The action that would receive the binding.</param>
    /// <param name="binding">The binding.</param>
    /// <returns>The conflicts, in the order of the actions.</returns>
    IReadOnlyList<BindingConflict> FindConflicts(InputAction action, InputBinding binding);

    /// <summary>Replaces every binding of an action.</summary>
    /// <param name="action">The action.</param>
    /// <param name="bindings">The new bindings; empty to unbind the action.</param>
    /// <exception cref="ArgumentException">A binding cannot give the value of the action (<see cref="InputBinding.Accepts"/>).</exception>
    void SetBindings(InputAction action, IReadOnlyList<InputBinding> bindings);

    /// <summary>Replaces one binding of an action, or adds one.</summary>
    /// <param name="action">The action.</param>
    /// <param name="index">The position of the binding in <see cref="InputAction.Bindings"/>, or its count to add one.</param>
    /// <param name="binding">The new binding.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or above the count.</exception>
    /// <exception cref="ArgumentException">The binding cannot give the value of the action.</exception>
    void Rebind(InputAction action, int index, InputBinding binding);

    /// <summary>Removes one binding of an action.</summary>
    /// <param name="action">The action.</param>
    /// <param name="index">The position of the binding in <see cref="InputAction.Bindings"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a position of a binding.</exception>
    void RemoveBinding(InputAction action, int index);

    /// <summary>Replaces the interaction of an action, such as a hold that the player turns into a toggle.</summary>
    /// <param name="action">A button or a float.</param>
    /// <param name="interaction">The interaction.</param>
    /// <exception cref="ArgumentException">The action is an axis.</exception>
    void SetInteraction(InputAction action, InputInteraction interaction);

    /// <summary>Goes back to the bindings and the interaction that the resource declares.</summary>
    /// <param name="action">The action.</param>
    void ResetToDefault(InputAction action);

    /// <summary>Goes back to the defaults for every action.</summary>
    void ResetAllToDefault();

    /// <summary>
    /// Listens for the next input of the player, for a rebinding screen. While
    /// it listens, the input of the player triggers no action and reaches no
    /// layer.
    /// </summary>
    /// <param name="kind">
    /// The kind of value of the action to bind: a button or a float takes the
    /// next key or button, with the modifiers held as a combination, or a step
    /// of the wheel; a 1D axis takes the wheel, a movement of the mouse along
    /// one axis, or a key; a 2D axis takes a movement of the mouse.
    /// </param>
    /// <param name="captured">Receives the binding, or <see langword="null"/> when the player presses <paramref name="cancel"/>.</param>
    /// <param name="cancel">The key that cancels, pressed without modifier.</param>
    /// <returns>The listening; dispose it to stop listening without a result.</returns>
    /// <remarks>
    /// A modifier key, such as Shift, is captured alone when it is released
    /// before another key is pressed. Only one listening runs at a time: a new
    /// one ends the previous one without a result.
    /// </remarks>
    InputListening Listen(InputValueKind kind, Action<InputBinding?> captured, Scancode cancel = Scancode.Escape);

    /// <summary>Writes the bindings and interactions that differ from the defaults to <see cref="FilePath"/>.</summary>
    /// <remarks>
    /// Entries for actions that no module declares, such as those of a mod
    /// that is not loaded, are written back as they were read.
    /// </remarks>
    /// <exception cref="IOException">The file cannot be written.</exception>
    void Save();
}

/// <summary>A binding of another action that would conflict.</summary>
/// <param name="Action">The other action.</param>
/// <param name="Index">The position of the conflicting binding in its <see cref="InputAction.Bindings"/>.</param>
public readonly record struct BindingConflict(InputAction Action, int Index);

/// <summary>A listening for the next input of the player. Disposing it stops it without a result.</summary>
public sealed class InputListening : IDisposable
{
    internal InputListening(InputValueKind kind, Action<InputBinding?> captured, Scancode cancel)
    {
        Kind = kind;
        Captured = captured;
        Cancel = cancel;
    }

    /// <summary>Gets whether it still listens.</summary>
    public bool IsListening { get; private set; } = true;

    internal InputValueKind Kind { get; }

    internal Action<InputBinding?> Captured { get; }

    internal Scancode Cancel { get; }

    internal List<InputControl> Modifiers { get; } = [];

    internal float MotionX { get; set; }

    internal float MotionY { get; set; }

    /// <summary>Stops listening, without a result.</summary>
    public void Dispose() => IsListening = false;
}
