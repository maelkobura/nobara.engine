// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Frames;

namespace Nobara.Input;

/// <summary>The devices whose input a layer keeps from the layers below it.</summary>
[Flags]
public enum InputCapture : byte
{
    /// <summary>Everything the layer does not consume passes to the layers below.</summary>
    None = 0,

    /// <summary>Nothing of the keyboard passes to the layers below.</summary>
    Keyboard = 1,

    /// <summary>Nothing of the mouse passes to the layers below.</summary>
    Mouse = 2,

    /// <summary>Nothing passes to the layers below.</summary>
    All = Keyboard | Mouse,
}

/// <summary>What a layer needs of the mouse cursor.</summary>
public enum CursorRequest : byte
{
    /// <summary>Nothing: the layers below decide, unless the layer captures the mouse, which shows the cursor.</summary>
    None,

    /// <summary>The cursor, visible and free: a screen, a debug overlay.</summary>
    Cursor,

    /// <summary>The mouse in relative mode, the cursor hidden and held in place: a first-person camera.</summary>
    Relative,
}

/// <summary>Whether an input stops at a layer or goes on to the layers below.</summary>
public enum InputResult : byte
{
    /// <summary>The input goes on to the layers below.</summary>
    Pass,

    /// <summary>The layer used the input: the layers below do not see it.</summary>
    Consume,
}

/// <summary>An edge of an action, as a layer handler receives it.</summary>
public enum ActionEdge : byte
{
    /// <summary>The action was pressed.</summary>
    Pressed,

    /// <summary>The action was released.</summary>
    Released,
}

/// <summary>The common priorities of layers, from the first to receive input to the last. Any whole number works.</summary>
public static class InputLayerPriority
{
    /// <summary>The debug overlay.</summary>
    public const int Debug = 400;

    /// <summary>Modal screens: inventory, pause menu, chat.</summary>
    public const int Modal = 300;

    /// <summary>The heads-up display.</summary>
    public const int Hud = 200;

    /// <summary>The game: the player controller.</summary>
    public const int Game = 100;
}

/// <summary>
/// What a layer does with the input that reaches it. Every method has a
/// default that passes the input on.
/// </summary>
/// <remarks>The methods run on the loop thread, at the start of the frame; an exception is charged to the module of the layer, and the input passes.</remarks>
public interface IInputLayerHandler
{
    /// <summary>Receives a raw input event that reaches the layer, when the layer asks for raw input.</summary>
    /// <param name="e">The event: a key, a button, a movement of the mouse or of the wheel. A key that repeats arrives with <see cref="RawInputEvent.IsRepeat"/>.</param>
    /// <param name="input">The raw input of the frame, to name keys.</param>
    /// <returns><see cref="InputResult.Consume"/> to keep the event from the actions of the layer and from the layers below.</returns>
    [LoopCallback]
    InputResult OnRawInput(in RawInputEvent e, IRawInput input) => InputResult.Pass;

    /// <summary>Receives an edge of an action of the layer.</summary>
    /// <param name="action">The action.</param>
    /// <param name="edge">Whether it was pressed or released.</param>
    /// <returns>
    /// <see cref="InputResult.Consume"/> on a press caused by a key or a button
    /// keeps that key from the layers below; otherwise the result has no effect.
    /// </returns>
    [LoopCallback]
    InputResult OnAction(InputAction action, ActionEdge edge) => InputResult.Pass;

    /// <summary>Receives typed text or the composition of an input method, when the layer has the text focus.</summary>
    /// <param name="e">A <see cref="RawInputKind.TextInput"/> or <see cref="RawInputKind.TextComposition"/> event.</param>
    /// <param name="text">The characters, valid during the call.</param>
    [LoopCallback]
    void OnText(in RawInputEvent e, ReadOnlySpan<char> text)
    {
    }
}

/// <summary>How a layer is registered.</summary>
public sealed class InputLayerOptions
{
    /// <summary>Gets the name of the layer, for logs and debug tools.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the priority: a higher one receives input first. See <see cref="InputLayerPriority"/>.</summary>
    public int Priority { get; init; } = InputLayerPriority.Game;

    /// <summary>Gets the devices the layer captures at first.</summary>
    public InputCapture Capture { get; init; }

    /// <summary>Gets what the layer needs of the cursor at first.</summary>
    public CursorRequest Cursor { get; init; }

    /// <summary>Gets whether the handler receives the raw input events at first.</summary>
    public bool ReceivesRawInput { get; init; }

    /// <summary>Gets the handler, or <see langword="null"/> for a layer that only reads actions.</summary>
    public IInputLayerHandler? Handler { get; init; }

    /// <summary>Gets whether the layer receives input at first.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// The state of an action as a layer sees it: for a frame with
/// <see cref="InputLayer.Read"/>, or since the previous tick with
/// <see cref="InputSampler.Read"/>.
/// </summary>
public readonly struct ActionState
{
    internal ActionState(bool held, int presses, int releases, TimeSpan heldFor, float value, Vector2 vector)
    {
        IsHeld = held;
        Presses = presses;
        Releases = releases;
        HeldFor = heldFor;
        Value = value;
        Vector = vector;
    }

    /// <summary>Gets whether the action is held: a button pressed through its interaction, an axis that is not zero.</summary>
    public bool IsHeld { get; }

    /// <summary>Gets whether the action was pressed at least once.</summary>
    public bool WasPressed => Presses > 0;

    /// <summary>Gets whether the action was released at least once.</summary>
    public bool WasReleased => Releases > 0;

    /// <summary>Gets how many times the action was pressed.</summary>
    public int Presses { get; }

    /// <summary>Gets how many times the action was released.</summary>
    public int Releases { get; }

    /// <summary>Gets how long the action has been held, or zero.</summary>
    public TimeSpan HeldFor { get; }

    /// <summary>
    /// Gets the value: 0 or 1 for a button, 0 to 1 for a float, the value of a
    /// 1D axis, the length of <see cref="Vector"/> for a 2D axis. A movement of
    /// the mouse is in logical units multiplied by <c>input.mouseSensitivity</c>,
    /// the wheel in notches.
    /// </summary>
    public float Value { get; }

    /// <summary>Gets the value of a 2D axis, up and right positive; <c>(Value, 0)</c> for the other kinds.</summary>
    public Vector2 Vector { get; }
}

/// <summary>
/// A layer of the input stack, registered with <see cref="IInputSystem.AddLayer"/>.
/// It receives the input that the layers above let through, evaluates the
/// actions from it, and decides what the layers below receive. Disposing it
/// removes it from the stack.
/// </summary>
/// <remarks>Use it on the loop thread of the client context.</remarks>
public sealed class InputLayer : IDisposable
{
    private readonly InputSystem _system;
    private InputCapture _capture;
    private CursorRequest _cursor;
    private bool _enabled;

    internal InputLayer(InputSystem system, ModuleContext module, InputLayerOptions options, long sequence)
    {
        _system = system;
        Module = module;
        Name = options.Name;
        Priority = options.Priority;
        Handler = options.Handler;
        ReceivesRawInput = options.ReceivesRawInput;
        _capture = options.Capture;
        _cursor = options.Cursor;
        _enabled = options.Enabled;
        Sequence = sequence;
        State = new LayerState(this);
    }

    /// <summary>Gets the name of the layer.</summary>
    public string Name { get; }

    /// <summary>Gets the priority of the layer.</summary>
    public int Priority { get; }

    /// <summary>Gets the module of the layer.</summary>
    public ModuleContext Module { get; }

    /// <summary>Gets whether the layer was removed from the stack.</summary>
    public bool IsRemoved { get; private set; }

    /// <summary>
    /// Gets or sets whether the layer receives input. A disabled layer lets
    /// everything through, and its actions read as released from the next
    /// frame on.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                if (!value && HasTextFocus)
                {
                    ReleaseTextFocus();
                }

                _system.LayersChanged();
            }
        }
    }

    /// <summary>Gets or sets the devices the layer keeps from the layers below. It applies from the next event on.</summary>
    public InputCapture Capture
    {
        get => _capture;
        set
        {
            _capture = value;
            _system.LayersChanged();
        }
    }

    /// <summary>
    /// Gets or sets what the layer needs of the cursor. The highest enabled
    /// layer that asks for the cursor or for relative mode, or that captures
    /// the mouse, decides the mode of the mouse at the end of each frame.
    /// </summary>
    public CursorRequest Cursor
    {
        get => _cursor;
        set
        {
            _cursor = value;
            _system.LayersChanged();
        }
    }

    /// <summary>Gets or sets whether the handler receives the raw input events.</summary>
    public bool ReceivesRawInput { get; set; }

    /// <summary>Gets whether the layer has the text focus: typed text goes to its handler, and no keyboard input triggers an action.</summary>
    public bool HasTextFocus => _system.TextFocus == this;

    internal IInputLayerHandler? Handler { get; }

    internal long Sequence { get; }

    internal LayerState State { get; }

    /// <summary>
    /// Takes the text focus, for a text field: typed text and the
    /// compositions of input methods go to the handler of this layer only,
    /// the keyboard reaches no layer below it, and no keyboard input triggers
    /// an action. The window starts text input.
    /// </summary>
    /// <exception cref="InvalidOperationException">The layer is removed or disabled.</exception>
    public void TakeTextFocus()
    {
        if (IsRemoved || !_enabled)
        {
            throw new InvalidOperationException($"The input layer '{Name}' is {(IsRemoved ? "removed" : "disabled")}; it cannot take the text focus.");
        }

        _system.SetTextFocus(this);
    }

    /// <summary>Gives the text focus back, if the layer has it; the window stops text input.</summary>
    public void ReleaseTextFocus()
    {
        if (HasTextFocus)
        {
            _system.SetTextFocus(null);
        }
    }

    /// <summary>Reads the state of an action in the frame that runs, as this layer sees it.</summary>
    /// <param name="action">An action of the service.</param>
    /// <returns>
    /// The state: the edges of this frame, and the movement of the mouse of
    /// this frame, for a camera that turns every frame. An inactive action
    /// reads as released.
    /// </returns>
    public ActionState Read(InputAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return State.Read(action);
    }

    /// <summary>
    /// Creates a sampler of the actions of this layer, for code that runs at
    /// the fixed tick, such as the player controller: no press is lost
    /// between two samples, and the movements of the mouse add up.
    /// </summary>
    /// <returns>The sampler; dispose it when it is no longer used.</returns>
    public InputSampler CreateSampler()
    {
        var sampler = new InputSampler(this);
        State.Samplers.Add(sampler);
        return sampler;
    }

    /// <summary>Removes the layer from the stack. Its samplers stop.</summary>
    public void Dispose()
    {
        if (!IsRemoved)
        {
            ReleaseTextFocus();
            IsRemoved = true;
            _system.RemoveLayer(this);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Priority})";
}

/// <summary>
/// Samples the actions of a layer at the fixed tick. Each <see cref="Sample"/>
/// gathers what happened since the previous one: a key pressed and released
/// between two ticks reads as pressed, and the movements of the mouse add up.
/// When several ticks run in one frame, the first sample receives the input
/// of the frame and the next ones only what is still held.
/// </summary>
/// <remarks>Use it on the loop thread of the client context.</remarks>
public sealed class InputSampler : IDisposable
{
    private readonly InputLayer _layer;
    private SampleSlot[] _pending = [];
    private ActionState[] _sampled = [];

    internal InputSampler(InputLayer layer)
    {
        _layer = layer;
    }

    /// <summary>Gets whether the sampler was disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Gathers what happened to every action since the previous sample.</summary>
    /// <exception cref="ObjectDisposedException">The sampler is disposed.</exception>
    public void Sample()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        LayerState state = _layer.State;
        if (_sampled.Length != state.ActionCount)
        {
            _sampled = new ActionState[state.ActionCount];
        }

        Resize(state.ActionCount);
        for (int i = 0; i < _sampled.Length; i++)
        {
            _sampled[i] = state.Sample(i, ref _pending[i]);
            _pending[i] = default;
        }
    }

    /// <summary>Reads the state of an action at the last sample.</summary>
    /// <param name="action">An action of the service.</param>
    /// <returns>The state; the default state before the first sample.</returns>
    public ActionState Read(InputAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Index < _sampled.Length ? _sampled[action.Index] : default;
    }

    /// <summary>Stops the sampler.</summary>
    public void Dispose()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            _layer.State.Samplers.Remove(this);
        }
    }

    /// <summary>Adds the edges and the movements of a frame.</summary>
    internal void Accumulate(int action, int presses, int releases, float relative, Vector2 relativeVector)
    {
        ref SampleSlot slot = ref _pending[action];
        slot.Presses += presses;
        slot.Releases += releases;
        slot.Relative += relative;
        slot.RelativeVector += relativeVector;
    }

    /// <summary>Forgets what was gathered, after the actions changed.</summary>
    internal void Resize(int actions)
    {
        if (_pending.Length != actions)
        {
            _pending = new SampleSlot[actions];
        }
    }
}

/// <summary>What a sampler gathered for one action since its previous sample.</summary>
internal struct SampleSlot
{
    public int Presses;
    public int Releases;
    public float Relative;
    public Vector2 RelativeVector;
}
