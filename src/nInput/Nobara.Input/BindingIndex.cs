// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Frames;

namespace Nobara.Input;

/// <summary>What a part of a binding contributes to its action.</summary>
internal enum PartRole : byte
{
    /// <summary>A key or a combination: the action is held while the part is.</summary>
    Button,

    /// <summary>The negative key of a <see cref="BindingKind.Composite1D"/>.</summary>
    Negative,

    /// <summary>The positive key of a <see cref="BindingKind.Composite1D"/>.</summary>
    Positive,

    /// <summary>The up key of a <see cref="BindingKind.Composite2D"/>.</summary>
    Up,

    /// <summary>The down key of a <see cref="BindingKind.Composite2D"/>.</summary>
    Down,

    /// <summary>The left key of a <see cref="BindingKind.Composite2D"/>.</summary>
    Left,

    /// <summary>The right key of a <see cref="BindingKind.Composite2D"/>.</summary>
    Right,
}

/// <summary>
/// Keys or buttons that are pressed together for one effect: a key, a
/// combination, or one key of a composite. The last control triggers the part.
/// </summary>
/// <param name="Action">The index of the action.</param>
/// <param name="Role">What the part contributes.</param>
/// <param name="Controls">The controls, the trigger last.</param>
internal sealed record Part(int Action, PartRole Role, InputControl[] Controls)
{
    /// <summary>Gets the physical slots of each control, in the order of <see cref="Controls"/>.</summary>
    public int[][] Slots { get; } = Controls.Select(c => BindingIndex.Slots(c).ToArray()).ToArray();

    /// <summary>Gets whether a control is a step of the wheel, in the order of <see cref="Controls"/>.</summary>
    public bool[] WheelSteps { get; } = Controls.Select(c => c.IsWheelStep).ToArray();
}

/// <summary>The bindings of one action, split for evaluation.</summary>
/// <param name="Parts">The indices of its parts.</param>
/// <param name="Axes">The axes it reads.</param>
internal sealed record ActionBindings(int[] Parts, InputControl[] Axes);

/// <summary>
/// The bindings of every action, indexed by physical key or button. It is
/// rebuilt when bindings change; the layers start again from released.
/// </summary>
internal sealed class BindingIndex
{
    /// <summary>The number of keyboard slots: one per scancode.</summary>
    public const int KeyboardSlots = 256;

    /// <summary>The number of slots: the keys, then the buttons and wheel steps of the mouse.</summary>
    public const int SlotCount = KeyboardSlots + 16;

    private BindingIndex(Part[] parts, ActionBindings[] actions, int[][] triggers, int[][] members)
    {
        Parts = parts;
        Actions = actions;
        Triggers = triggers;
        Members = members;
    }

    /// <summary>Gets every part of every binding.</summary>
    public Part[] Parts { get; }

    /// <summary>Gets the bindings of each action, by index.</summary>
    public ActionBindings[] Actions { get; }

    /// <summary>Gets, for each slot, the parts that a press of it triggers.</summary>
    public int[][] Triggers { get; }

    /// <summary>Gets, for each slot, the parts that a release of it can end.</summary>
    public int[][] Members { get; }

    /// <summary>Builds the index of the bindings in use.</summary>
    /// <param name="actions">The actions, by index.</param>
    /// <returns>The index.</returns>
    public static BindingIndex Build(IReadOnlyList<InputAction> actions)
    {
        var parts = new List<Part>();
        var perAction = new ActionBindings[actions.Count];
        var triggers = new List<int>[SlotCount];
        var members = new List<int>[SlotCount];
        for (int a = 0; a < actions.Count; a++)
        {
            var own = new List<int>();
            var axes = new List<InputControl>();
            foreach (InputBinding binding in actions[a].Bindings)
            {
                switch (binding.Kind)
                {
                    case BindingKind.Axis:
                        axes.Add(binding.Controls[0]);
                        break;
                    case BindingKind.Key or BindingKind.Combo:
                        own.Add(Add(parts, new Part(a, PartRole.Button, [.. binding.Controls])));
                        break;
                    case BindingKind.Composite1D:
                        own.Add(Add(parts, new Part(a, PartRole.Negative, [binding.Controls[0]])));
                        own.Add(Add(parts, new Part(a, PartRole.Positive, [binding.Controls[1]])));
                        break;
                    case BindingKind.Composite2D:
                        own.Add(Add(parts, new Part(a, PartRole.Up, [binding.Controls[0]])));
                        own.Add(Add(parts, new Part(a, PartRole.Down, [binding.Controls[1]])));
                        own.Add(Add(parts, new Part(a, PartRole.Left, [binding.Controls[2]])));
                        own.Add(Add(parts, new Part(a, PartRole.Right, [binding.Controls[3]])));
                        break;
                }
            }

            perAction[a] = new ActionBindings([.. own], [.. axes]);
        }

        for (int p = 0; p < parts.Count; p++)
        {
            int[][] slots = parts[p].Slots;
            foreach (int slot in slots[^1])
            {
                (triggers[slot] ??= []).Add(p);
            }

            foreach (int[] control in slots)
            {
                foreach (int slot in control)
                {
                    List<int> list = members[slot] ??= [];
                    if (!list.Contains(p))
                    {
                        list.Add(p);
                    }
                }
            }
        }

        return new BindingIndex(
            [.. parts],
            perAction,
            triggers.Select(l => l?.ToArray() ?? []).ToArray(),
            members.Select(l => l?.ToArray() ?? []).ToArray());
    }

    /// <summary>Gets the slot of a physical key.</summary>
    public static int KeySlot(Scancode scancode) => (int)scancode is >= 0 and < KeyboardSlots ? (int)scancode : 0;

    /// <summary>Gets the slot of a button or a step of the wheel of the mouse.</summary>
    public static int MouseSlot(MouseControl control) => KeyboardSlots + (int)control;

    /// <summary>Gets the slot of a mouse button of a raw event, or -1.</summary>
    public static int MouseSlot(MouseButton button) => button switch
    {
        MouseButton.Left => MouseSlot(MouseControl.Left),
        MouseButton.Right => MouseSlot(MouseControl.Right),
        MouseButton.Middle => MouseSlot(MouseControl.Middle),
        MouseButton.X1 => MouseSlot(MouseControl.Back),
        MouseButton.X2 => MouseSlot(MouseControl.Forward),
        _ => -1,
    };

    /// <summary>Tells whether a slot is a key of the keyboard.</summary>
    public static bool IsKeyboard(int slot) => slot < KeyboardSlots;

    /// <summary>Gets the physical slots that press a control: one, two for a side-agnostic modifier, none for an axis or a gamepad.</summary>
    public static IEnumerable<int> Slots(InputControl control)
    {
        if (control.Kind != InputControlKind.Button)
        {
            yield break;
        }

        if (control.Device == InputDevice.Keyboard)
        {
            for (int slot = 1; slot < KeyboardSlots; slot++)
            {
                if (Enum.IsDefined((Scancode)slot) && control.Covers(InputControl.Key((Scancode)slot)))
                {
                    yield return slot;
                }
            }
        }
        else if (control.Device == InputDevice.Mouse)
        {
            yield return MouseSlot((MouseControl)control.Code);
        }
    }

    /// <summary>Gets the slot of a control of one side, or -1.</summary>
    public static int SlotOf(InputControl control) => control switch
    {
        { Device: InputDevice.Keyboard, IsEitherSide: false } => control.Code,
        { Device: InputDevice.Mouse, Kind: InputControlKind.Button } => MouseSlot((MouseControl)control.Code),
        _ => -1,
    };

    private static int Add(List<Part> parts, Part part)
    {
        parts.Add(part);
        return parts.Count - 1;
    }
}
