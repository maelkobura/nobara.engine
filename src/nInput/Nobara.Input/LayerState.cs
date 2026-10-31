// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Input;

/// <summary>The evaluation state of one action in one layer.</summary>
internal struct ActionSlot
{
    /// <summary>The number of button parts held.</summary>
    public int HeldParts;

    /// <summary>Whether an axis bound to a button or a float is above the threshold.</summary>
    public bool AnalogPressed;

    /// <summary>Whether the bindings are held, before the interaction.</summary>
    public bool InputHeld;

    public double InputSince;

    /// <summary>The slot whose press started the input, or -1.</summary>
    public int PressSlot;

    /// <summary>Whether the action is held, after the interaction.</summary>
    public bool OutHeld;

    public double OutSince;

    public bool ToggleOn;

    public bool HasLastPress;

    public double LastPress;

    public bool DoubleActive;

    public int Presses;

    public int Releases;

    /// <summary>The value of the frame of a 1D action.</summary>
    public float Value;

    /// <summary>The value of the frame of a 2D action.</summary>
    public Vector2 Vector;

    /// <summary>The part of the value that comes from keys and positions, which a sample reads as it is.</summary>
    public float Absolute;

    public Vector2 AbsoluteVector;

    /// <summary>The part of the value that comes from movements, which a sample adds up.</summary>
    public float Relative;

    public Vector2 RelativeVector;
}

/// <summary>
/// What one layer sees of the input: the keys and buttons that reached it,
/// the parts of bindings they hold, the actions and their interactions, and
/// the movements of the frame.
/// </summary>
internal sealed class LayerState
{
    private readonly InputLayer _layer;
    private bool[] _down = new bool[BindingIndex.SlotCount];
    private bool[] _parts = [];
    private ActionSlot[] _actions = [];
    private BindingIndex? _index;
    private IReadOnlyList<InputAction> _list = [];
    private float _dx;
    private float _dy;
    private float _wheelX;
    private float _wheelY;
    private double _now;

    public LayerState(InputLayer layer)
    {
        _layer = layer;
    }

    public List<InputSampler> Samplers { get; } = [];

    public int ActionCount => _actions.Length;

    /// <summary>Gets or sets whether keyboard input reaches the actions of the layer.</summary>
    public bool KeyboardActions { get; set; } = true;

    /// <summary>Starts again from released, with new bindings.</summary>
    public void Reset(BindingIndex index, IReadOnlyList<InputAction> actions)
    {
        _index = index;
        _list = actions;
        _down = new bool[BindingIndex.SlotCount];
        _parts = new bool[index.Parts.Length];
        _actions = new ActionSlot[actions.Count];
        for (int i = 0; i < _actions.Length; i++)
        {
            _actions[i].PressSlot = -1;
        }

        foreach (InputSampler sampler in Samplers)
        {
            sampler.Resize(actions.Count);
        }
    }

    /// <summary>Forgets the edges and the movements of the previous frame.</summary>
    public void BeginFrame(double now)
    {
        _now = now;
        _dx = _dy = _wheelX = _wheelY = 0;
        for (int i = 0; i < _actions.Length; i++)
        {
            _actions[i].Presses = 0;
            _actions[i].Releases = 0;
        }
    }

    public bool IsDown(int slot) => slot >= 0 && _down[slot];

    /// <summary>Tells whether the layer holds anything of a device.</summary>
    public bool Holds(bool keyboard)
    {
        int start = keyboard ? 0 : BindingIndex.KeyboardSlots;
        int end = keyboard ? BindingIndex.KeyboardSlots : BindingIndex.SlotCount;
        for (int slot = start; slot < end; slot++)
        {
            if (_down[slot])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A key or a button reached the layer, before its actions see it.</summary>
    public void MarkDown(int slot) => _down[slot] = true;

    /// <summary>A key or a button was pressed: triggers the most specific parts.</summary>
    /// <returns><see langword="true"/> when the handler consumed a press it caused.</returns>
    public bool ButtonDown(int slot)
    {
        _down[slot] = true;
        if (_index is null || (BindingIndex.IsKeyboard(slot) && !KeyboardActions))
        {
            return false;
        }

        // Priority to the most specific: only the matching parts with the most keys trigger.
        int longest = 0;
        foreach (int p in _index.Triggers[slot])
        {
            if (Matches(p))
            {
                longest = Math.Max(longest, _index.Parts[p].Controls.Length);
            }
        }

        bool consumed = false;
        if (longest == 0)
        {
            return false;
        }

        foreach (int p in _index.Triggers[slot])
        {
            Part part = _index.Parts[p];
            if (part.Controls.Length != longest || !Matches(p))
            {
                continue;
            }

            _parts[p] = true;
            if (part.Role == PartRole.Button)
            {
                ref ActionSlot action = ref _actions[part.Action];
                action.HeldParts++;
                if (action.HeldParts == 1)
                {
                    consumed |= InputChanged(part.Action, held: true, slot, synthetic: false);
                }
            }
        }

        return consumed;
    }

    /// <summary>A key or a button was released: ends the parts it held.</summary>
    public void ButtonUp(int slot)
    {
        _down[slot] = false;
        if (_index is null)
        {
            return;
        }

        foreach (int p in _index.Members[slot])
        {
            if (_parts[p] && !AllDown(_index.Parts[p]))
            {
                ReleasePart(p, synthetic: false);
            }
        }
    }

    public void Motion(float dx, float dy)
    {
        _dx += dx;
        _dy += dy;
    }

    public void Wheel(float x, float y)
    {
        _wheelX += x;
        _wheelY += y;
    }

    /// <summary>Releases what a device holds, when it no longer reaches the layer or its actions; a toggle stays on.</summary>
    /// <param name="keyboard">The keyboard, or the mouse.</param>
    /// <param name="forget">Whether the keys are forgotten too, so that their release does not reach the layer.</param>
    public void ReleaseDevice(bool keyboard, bool forget)
    {
        if (_index is null)
        {
            return;
        }

        for (int p = 0; p < _parts.Length; p++)
        {
            if (_parts[p] && UsesDevice(_index.Parts[p].Slots, keyboard))
            {
                ReleasePart(p, synthetic: true);
            }
        }

        if (forget)
        {
            int start = keyboard ? 0 : BindingIndex.KeyboardSlots;
            int end = keyboard ? BindingIndex.KeyboardSlots : BindingIndex.SlotCount;
            Array.Clear(_down, start, end - start);
        }
    }

    /// <summary>Starts an action again from released, toggle included, after its map turned off.</summary>
    public void ResetAction(int action)
    {
        if (_index is null)
        {
            return;
        }

        foreach (int p in _index.Actions[action].Parts)
        {
            _parts[p] = false;
        }

        ref ActionSlot slot = ref _actions[action];
        bool held = slot.OutHeld;
        int presses = slot.Presses;
        int releases = slot.Releases;
        slot = default;
        slot.PressSlot = -1;
        slot.Presses = presses;
        slot.Releases = releases;
        if (held)
        {
            slot.Releases++;
            Notify(action, ActionEdge.Released);
        }
    }

    /// <summary>Computes the values of the frame, runs the interactions that depend on time, and hands the frame to the samplers.</summary>
    public void EndFrame(float sensitivity, bool invertY)
    {
        if (_index is null)
        {
            return;
        }

        float mouseY = (invertY ? _dy : -_dy) * sensitivity;
        var mouse = new Vector2(_dx * sensitivity, mouseY);
        for (int a = 0; a < _actions.Length; a++)
        {
            InputAction action = _list[a];
            if (!action.IsActive)
            {
                continue;
            }

            switch (action.ValueKind)
            {
                case InputValueKind.Button or InputValueKind.Float:
                    EndButton(a, action, mouse);
                    break;
                case InputValueKind.Axis1D:
                    EndAxis1D(a, mouse);
                    break;
                default:
                    EndAxis2D(a, mouse);
                    break;
            }

            ref ActionSlot slot = ref _actions[a];
            foreach (InputSampler sampler in Samplers)
            {
                sampler.Accumulate(a, slot.Presses, slot.Releases, slot.Relative, slot.RelativeVector);
            }
        }
    }

    /// <summary>Reads an action in the frame.</summary>
    public ActionState Read(InputAction action)
    {
        if (action.Index >= _actions.Length || !action.IsActive || _layer.IsRemoved)
        {
            return default;
        }

        ref ActionSlot slot = ref _actions[action.Index];
        TimeSpan heldFor = slot.OutHeld ? TimeSpan.FromSeconds(_now - slot.OutSince) : TimeSpan.Zero;
        return action.ValueKind switch
        {
            InputValueKind.Button => new ActionState(slot.OutHeld, slot.Presses, slot.Releases, heldFor, slot.OutHeld ? 1 : 0, new Vector2(slot.OutHeld ? 1 : 0, 0)),
            InputValueKind.Axis2D => new ActionState(slot.OutHeld, slot.Presses, slot.Releases, heldFor, slot.Vector.Length(), slot.Vector),
            _ => new ActionState(slot.OutHeld, slot.Presses, slot.Releases, heldFor, slot.Value, new Vector2(slot.Value, 0)),
        };
    }

    /// <summary>Builds the state that a sampler reads: what it gathered, and what is held now.</summary>
    public ActionState Sample(int index, ref SampleSlot pending)
    {
        InputAction action = _list[index];
        if (!action.IsActive || _layer.IsRemoved)
        {
            return default;
        }

        ref ActionSlot slot = ref _actions[index];
        TimeSpan heldFor = slot.OutHeld ? TimeSpan.FromSeconds(_now - slot.OutSince) : TimeSpan.Zero;
        switch (action.ValueKind)
        {
            case InputValueKind.Button:
                return new ActionState(slot.OutHeld, pending.Presses, pending.Releases, heldFor, slot.OutHeld ? 1 : 0, new Vector2(slot.OutHeld ? 1 : 0, 0));
            case InputValueKind.Float:
                float value = Math.Max(slot.Absolute, Math.Min(1, pending.Relative));
                return new ActionState(slot.OutHeld, pending.Presses, pending.Releases, heldFor, value, new Vector2(value, 0));
            case InputValueKind.Axis1D:
                float axis = Largest(slot.Absolute, pending.Relative);
                return new ActionState(slot.OutHeld || axis != 0, pending.Presses, pending.Releases, heldFor, axis, new Vector2(axis, 0));
            default:
                Vector2 vector = Largest(slot.AbsoluteVector, pending.RelativeVector);
                return new ActionState(slot.OutHeld || vector != Vector2.Zero, pending.Presses, pending.Releases, heldFor, vector.Length(), vector);
        }
    }

    private static float Largest(float a, float b) => Math.Abs(b) > Math.Abs(a) ? b : a;

    private static Vector2 Largest(Vector2 a, Vector2 b) => b.LengthSquared() > a.LengthSquared() ? b : a;

    private static bool UsesDevice(int[][] slots, bool keyboard)
    {
        foreach (int[] control in slots)
        {
            foreach (int slot in control)
            {
                if (BindingIndex.IsKeyboard(slot) == keyboard)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool Matches(int p)
    {
        Part part = _index!.Parts[p];
        if (_parts[p] || !_list[part.Action].IsActive)
        {
            return false;
        }

        // The other keys are held already: the trigger is the last one.
        for (int i = 0; i < part.Slots.Length - 1; i++)
        {
            if (!IsHeld(part.Slots[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool AllDown(Part part)
    {
        for (int i = 0; i < part.Slots.Length; i++)
        {
            // A step of the wheel is down for an instant only.
            if (part.WheelSteps[i] || !IsHeld(part.Slots[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsHeld(int[] slots)
    {
        foreach (int slot in slots)
        {
            if (_down[slot])
            {
                return true;
            }
        }

        return false;
    }

    private void ReleasePart(int p, bool synthetic)
    {
        _parts[p] = false;
        Part part = _index!.Parts[p];
        if (part.Role == PartRole.Button)
        {
            ref ActionSlot action = ref _actions[part.Action];
            action.HeldParts--;
            if (action.HeldParts == 0 && !action.AnalogPressed)
            {
                InputChanged(part.Action, held: false, -1, synthetic);
            }
        }
    }

    /// <summary>Runs the interaction of a button or a float when its bindings become held or released.</summary>
    /// <returns><see langword="true"/> when the handler consumed a press it caused.</returns>
    private bool InputChanged(int a, bool held, int pressSlot, bool synthetic)
    {
        InputAction action = _list[a];
        ref ActionSlot slot = ref _actions[a];
        if (slot.InputHeld == held)
        {
            return false;
        }

        double pressed = slot.InputSince;
        slot.InputHeld = held;
        if (action.ValueKind is InputValueKind.Axis1D or InputValueKind.Axis2D)
        {
            // Axes have no interaction; their value is computed at the end of the frame.
            return false;
        }

        InputInteraction interaction = action.Interaction;
        if (held)
        {
            slot.InputSince = _now;
            slot.PressSlot = pressSlot;
            switch (interaction.Kind)
            {
                case InteractionKind.Press:
                    return Press(a);
                case InteractionKind.DoubleTap:
                    if (slot.HasLastPress && _now - slot.LastPress <= interaction.Seconds)
                    {
                        slot.HasLastPress = false;
                        slot.DoubleActive = true;
                        return Press(a);
                    }

                    slot.HasLastPress = true;
                    slot.LastPress = _now;
                    return false;
                case InteractionKind.Toggle:
                    slot.ToggleOn = !slot.ToggleOn;
                    if (slot.ToggleOn)
                    {
                        return Press(a);
                    }

                    Release(a);
                    return false;
                default:
                    return false;
            }
        }

        double duration = _now - pressed;
        switch (interaction.Kind)
        {
            case InteractionKind.Press:
                Release(a);
                break;
            case InteractionKind.Release when !synthetic:
                Pulse(a);
                break;
            case InteractionKind.Hold:
                if (slot.OutHeld)
                {
                    Release(a);
                }
                else if (!synthetic && duration >= interaction.Seconds)
                {
                    // The press lasted long enough, even if no frame ended during it.
                    Pulse(a);
                }

                break;
            case InteractionKind.Tap when !synthetic:
                if (duration < interaction.Seconds && !HoldWins(slot.PressSlot, duration))
                {
                    Pulse(a);
                }

                break;
            case InteractionKind.DoubleTap:
                if (slot.DoubleActive)
                {
                    slot.DoubleActive = false;
                    Release(a);
                }

                break;
        }

        return false;
    }

    /// <summary>Tells whether an action with a hold on the same key passed its duration: a hold wins over a tap.</summary>
    private bool HoldWins(int pressSlot, double duration)
    {
        if (pressSlot < 0)
        {
            return false;
        }

        foreach (int p in _index!.Triggers[pressSlot])
        {
            InputAction other = _list[_index.Parts[p].Action];
            if (other.IsActive && other.Interaction.Kind == InteractionKind.Hold && other.Interaction.Seconds <= duration)
            {
                return true;
            }
        }

        return false;
    }

    private void EndButton(int a, InputAction action, Vector2 mouse)
    {
        ref ActionSlot slot = ref _actions[a];
        float absolute = slot.HeldParts > 0 ? 1 : 0;
        float relative = 0;
        float analog = 0;
        foreach (InputControl axis in _index!.Actions[a].Axes)
        {
            float value = Math.Clamp(AxisValue(axis, mouse.X, mouse.Y), 0, 1);
            analog = Math.Max(analog, value);
            if (axis.IsRelative)
            {
                relative = Math.Max(relative, value);
            }
            else
            {
                absolute = Math.Max(absolute, value);
            }
        }

        slot.Absolute = absolute;
        slot.Relative = relative;
        slot.Value = Math.Max(absolute, relative);
        bool analogPressed = analog > 0 && analog >= action.Threshold;
        if (analogPressed != slot.AnalogPressed)
        {
            slot.AnalogPressed = analogPressed;
            InputChanged(a, slot.HeldParts > 0 || analogPressed, -1, synthetic: false);
        }

        if (action.Interaction.Kind == InteractionKind.Hold && slot.InputHeld && !slot.OutHeld && _now - slot.InputSince >= action.Interaction.Seconds)
        {
            Press(a);
        }
    }

    private void EndAxis1D(int a, Vector2 mouse)
    {
        ref ActionSlot slot = ref _actions[a];
        ActionBindings bindings = _index!.Actions[a];
        float absolute = 0;
        float negative = 0;
        float positive = 0;
        foreach (int p in bindings.Parts)
        {
            if (!_parts[p])
            {
                continue;
            }

            switch (_index.Parts[p].Role)
            {
                case PartRole.Negative:
                    negative = 1;
                    break;
                case PartRole.Positive:
                    positive = 1;
                    break;
                default:
                    absolute = 1;
                    break;
            }
        }

        absolute = Largest(absolute, positive - negative);
        float relative = 0;
        foreach (InputControl axis in bindings.Axes)
        {
            float value = AxisValue(axis, mouse.X, mouse.Y);
            if (axis.IsRelative)
            {
                relative = Largest(relative, value);
            }
            else
            {
                absolute = Largest(absolute, value);
            }
        }

        slot.Absolute = absolute;
        slot.Relative = relative;
        slot.Value = Largest(absolute, relative);
        SetAxisHeld(a, slot.Value != 0);
    }

    private void EndAxis2D(int a, Vector2 mouse)
    {
        ref ActionSlot slot = ref _actions[a];
        ActionBindings bindings = _index!.Actions[a];
        Vector2 absolute = Vector2.Zero;
        for (int i = 0; i + 3 < bindings.Parts.Length; i += 4)
        {
            // The four parts of a composite are contiguous: up, down, left, right.
            float x = Held(bindings.Parts[i + 3]) - Held(bindings.Parts[i + 2]);
            float y = Held(bindings.Parts[i]) - Held(bindings.Parts[i + 1]);
            absolute = Largest(absolute, new Vector2(x, y));
        }

        Vector2 relative = Vector2.Zero;
        foreach (InputControl axis in bindings.Axes)
        {
            if (axis.IsRelative)
            {
                relative = Largest(relative, mouse);
            }
        }

        slot.AbsoluteVector = absolute;
        slot.RelativeVector = relative;
        slot.Vector = Largest(absolute, relative);
        SetAxisHeld(a, slot.Vector != Vector2.Zero);
    }

    private float Held(int p) => _parts[p] ? 1 : 0;

    private void SetAxisHeld(int a, bool held)
    {
        ref ActionSlot slot = ref _actions[a];
        if (held && !slot.OutHeld)
        {
            Press(a);
        }
        else if (!held && slot.OutHeld)
        {
            Release(a);
        }
    }

    private float AxisValue(InputControl axis, float mouseX, float mouseY)
    {
        if (axis.Device != InputDevice.Mouse)
        {
            return 0;
        }

        return (MouseControl)axis.Code switch
        {
            MouseControl.DeltaX => mouseX,
            MouseControl.DeltaY => mouseY,
            MouseControl.Wheel => _wheelY,
            MouseControl.WheelX => _wheelX,
            _ => 0,
        };
    }

    private bool Press(int a)
    {
        ref ActionSlot slot = ref _actions[a];
        slot.OutHeld = true;
        slot.OutSince = _now;
        slot.Presses++;
        return Notify(a, ActionEdge.Pressed);
    }

    private void Release(int a)
    {
        ref ActionSlot slot = ref _actions[a];
        if (slot.OutHeld)
        {
            slot.OutHeld = false;
            slot.Releases++;
            Notify(a, ActionEdge.Released);
        }
    }

    private void Pulse(int a)
    {
        Press(a);
        Release(a);
    }

    private bool Notify(int a, ActionEdge edge)
    {
        IInputLayerHandler? handler = _layer.Handler;
        if (handler is null)
        {
            return false;
        }

        try
        {
            return handler.OnAction(_list[a], edge) == InputResult.Consume;
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            _layer.Module.ReportFault(exception);
            return false;
        }
    }
}
