// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Frozen;
using Nobara.Frames;

namespace Nobara.Input;

/// <summary>The device an input control belongs to.</summary>
public enum InputDevice : byte
{
    /// <summary>No device: the default value of <see cref="InputControl"/>.</summary>
    None,

    /// <summary>The keyboard, by physical key.</summary>
    Keyboard,

    /// <summary>The mouse: buttons, wheel, and movement.</summary>
    Mouse,

    /// <summary>A gamepad. Its controls are accepted in bindings, but no gamepad is read yet.</summary>
    Gamepad,
}

/// <summary>What an input control gives.</summary>
public enum InputControlKind : byte
{
    /// <summary>Down or up: a key, a button, a step of the wheel.</summary>
    Button,

    /// <summary>One number: a movement of the mouse along one axis, the wheel, a trigger.</summary>
    Axis1D,

    /// <summary>Two numbers: the movement of the mouse, a stick.</summary>
    Axis2D,
}

/// <summary>The controls of the mouse that a binding can name.</summary>
public enum MouseControl : byte
{
    /// <summary>The left button: <c>Mouse.Left</c>.</summary>
    Left,

    /// <summary>The right button: <c>Mouse.Right</c>.</summary>
    Right,

    /// <summary>The middle button, often the wheel: <c>Mouse.Middle</c>.</summary>
    Middle,

    /// <summary>The first extra button, often "back": <c>Mouse.Back</c>.</summary>
    Back,

    /// <summary>The second extra button, often "forward": <c>Mouse.Forward</c>.</summary>
    Forward,

    /// <summary>One step of the wheel away from the player, as a button pressed and released at once: <c>Mouse.WheelUp</c>.</summary>
    WheelUp,

    /// <summary>One step of the wheel toward the player: <c>Mouse.WheelDown</c>.</summary>
    WheelDown,

    /// <summary>One step of the horizontal wheel to the left: <c>Mouse.WheelLeft</c>.</summary>
    WheelLeft,

    /// <summary>One step of the horizontal wheel to the right: <c>Mouse.WheelRight</c>.</summary>
    WheelRight,

    /// <summary>The movement of the mouse, a 2D axis in logical units, up positive: <c>Mouse.Delta</c>.</summary>
    Delta,

    /// <summary>The horizontal movement of the mouse, right positive: <c>Mouse.DeltaX</c>.</summary>
    DeltaX,

    /// <summary>The vertical movement of the mouse, up positive: <c>Mouse.DeltaY</c>.</summary>
    DeltaY,

    /// <summary>The vertical wheel in notches, away from the player positive: <c>Mouse.Wheel</c>.</summary>
    Wheel,

    /// <summary>The horizontal wheel in notches, right positive: <c>Mouse.WheelX</c>.</summary>
    WheelX,
}

/// <summary>
/// The controls of a gamepad that a binding can name, by position: the face
/// buttons are named after the cardinal points.
/// </summary>
public enum GamepadControl : byte
{
    /// <summary>The bottom face button: <c>Gamepad.South</c>.</summary>
    South,

    /// <summary>The right face button: <c>Gamepad.East</c>.</summary>
    East,

    /// <summary>The left face button: <c>Gamepad.West</c>.</summary>
    West,

    /// <summary>The top face button: <c>Gamepad.North</c>.</summary>
    North,

    /// <summary>The back or select button: <c>Gamepad.Back</c>.</summary>
    Back,

    /// <summary>The guide or home button: <c>Gamepad.Guide</c>.</summary>
    Guide,

    /// <summary>The start button: <c>Gamepad.Start</c>.</summary>
    Start,

    /// <summary>The left stick pressed: <c>Gamepad.LeftStickPress</c>.</summary>
    LeftStickPress,

    /// <summary>The right stick pressed: <c>Gamepad.RightStickPress</c>.</summary>
    RightStickPress,

    /// <summary>The left shoulder button: <c>Gamepad.LeftShoulder</c>.</summary>
    LeftShoulder,

    /// <summary>The right shoulder button: <c>Gamepad.RightShoulder</c>.</summary>
    RightShoulder,

    /// <summary>The directional pad up: <c>Gamepad.DPadUp</c>.</summary>
    DPadUp,

    /// <summary>The directional pad down: <c>Gamepad.DPadDown</c>.</summary>
    DPadDown,

    /// <summary>The directional pad left: <c>Gamepad.DPadLeft</c>.</summary>
    DPadLeft,

    /// <summary>The directional pad right: <c>Gamepad.DPadRight</c>.</summary>
    DPadRight,

    /// <summary>The left trigger, from 0 to 1: <c>Gamepad.LeftTrigger</c>.</summary>
    LeftTrigger,

    /// <summary>The right trigger, from 0 to 1: <c>Gamepad.RightTrigger</c>.</summary>
    RightTrigger,

    /// <summary>The horizontal axis of the left stick: <c>Gamepad.LeftStickX</c>.</summary>
    LeftStickX,

    /// <summary>The vertical axis of the left stick, up positive: <c>Gamepad.LeftStickY</c>.</summary>
    LeftStickY,

    /// <summary>The horizontal axis of the right stick: <c>Gamepad.RightStickX</c>.</summary>
    RightStickX,

    /// <summary>The vertical axis of the right stick, up positive: <c>Gamepad.RightStickY</c>.</summary>
    RightStickY,

    /// <summary>The left stick, a 2D axis: <c>Gamepad.LeftStick</c>.</summary>
    LeftStick,

    /// <summary>The right stick, a 2D axis: <c>Gamepad.RightStick</c>.</summary>
    RightStick,
}

/// <summary>
/// A key, button, or axis that a binding names, such as <c>Key.W</c>,
/// <c>Key.Control</c>, <c>Mouse.Left</c>, or <c>Mouse.Delta</c>. The names
/// form a closed list: <see cref="TryParse"/> accepts exactly the names of
/// <see cref="AllNames"/>.
/// </summary>
/// <remarks>
/// Keys are physical positions (<see cref="Scancode"/>): <c>Key.W</c> is the key
/// labeled Z on an AZERTY keyboard. <c>Key.Control</c>, <c>Key.Shift</c>,
/// <c>Key.Alt</c>, and <c>Key.Meta</c> stand for the left or the right key of
/// the pair; <c>Key.LeftControl</c> names one side only.
/// </remarks>
public readonly struct InputControl : IEquatable<InputControl>
{
    private const int EitherSide = 0x1000;

    private static readonly FrozenDictionary<string, InputControl> ByName = BuildNames();

    private static readonly FrozenDictionary<InputControl, string> NameOf = ByName.ToFrozenDictionary(p => p.Value, p => p.Key);

    private InputControl(InputDevice device, int code)
    {
        Device = device;
        Code = code;
    }

    /// <summary>Gets the side-agnostic Control key: the left or the right one.</summary>
    public static InputControl Control => new(InputDevice.Keyboard, EitherSide | (int)Scancode.LeftControl);

    /// <summary>Gets the side-agnostic Shift key.</summary>
    public static InputControl Shift => new(InputDevice.Keyboard, EitherSide | (int)Scancode.LeftShift);

    /// <summary>Gets the side-agnostic Alt key.</summary>
    public static InputControl Alt => new(InputDevice.Keyboard, EitherSide | (int)Scancode.LeftAlt);

    /// <summary>Gets the side-agnostic meta key: the Windows key, or the Command key.</summary>
    public static InputControl Meta => new(InputDevice.Keyboard, EitherSide | (int)Scancode.LeftMeta);

    /// <summary>Gets every name a binding can use, sorted.</summary>
    public static IReadOnlyList<string> AllNames { get; } = ByName.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Gets the device of the control; <see cref="InputDevice.None"/> for the default value.</summary>
    public InputDevice Device { get; }

    /// <summary>Gets what the control gives.</summary>
    public InputControlKind Kind => Device switch
    {
        InputDevice.Mouse => (MouseControl)Code switch
        {
            MouseControl.Delta => InputControlKind.Axis2D,
            >= MouseControl.DeltaX => InputControlKind.Axis1D,
            _ => InputControlKind.Button,
        },
        InputDevice.Gamepad => (GamepadControl)Code switch
        {
            GamepadControl.LeftStick or GamepadControl.RightStick => InputControlKind.Axis2D,
            >= GamepadControl.LeftTrigger => InputControlKind.Axis1D,
            _ => InputControlKind.Button,
        },
        _ => InputControlKind.Button,
    };

    /// <summary>Gets whether the control reports movements rather than a position: the movement of the mouse and the wheel.</summary>
    public bool IsRelative => Device == InputDevice.Mouse && (MouseControl)Code >= MouseControl.Delta;

    /// <summary>Gets whether the control is a step of the wheel, a button pressed and released at once.</summary>
    public bool IsWheelStep => Device == InputDevice.Mouse && (MouseControl)Code is >= MouseControl.WheelUp and <= MouseControl.WheelRight;

    /// <summary>Gets whether the control is a side-agnostic modifier: <see cref="Control"/>, <see cref="Shift"/>, <see cref="Alt"/>, or <see cref="Meta"/>.</summary>
    public bool IsEitherSide => Device == InputDevice.Keyboard && (Code & EitherSide) != 0;

    /// <summary>Gets the name of the control, such as <c>Key.W</c>; empty for the default value.</summary>
    public string Name => NameOf.GetValueOrDefault(this, string.Empty);

    internal int Code { get; }

    /// <summary>Gets the control of a physical key.</summary>
    /// <param name="scancode">The key; not <see cref="Scancode.Unknown"/>.</param>
    /// <returns>The control, <c>Key.</c> followed by the name of the scancode.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scancode"/> is not a named key.</exception>
    public static InputControl Key(Scancode scancode)
    {
        if (scancode == Scancode.Unknown || !Enum.IsDefined(scancode))
        {
            throw new ArgumentOutOfRangeException(nameof(scancode), scancode, "The scancode is not a named key.");
        }

        return new InputControl(InputDevice.Keyboard, (int)scancode);
    }

    /// <summary>Gets a control of the mouse.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The control, <c>Mouse.</c> followed by its name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="control"/> is not a value of the enumeration.</exception>
    public static InputControl Mouse(MouseControl control)
    {
        if (!Enum.IsDefined(control))
        {
            throw new ArgumentOutOfRangeException(nameof(control), control, "Not a control of the mouse.");
        }

        return new InputControl(InputDevice.Mouse, (int)control);
    }

    /// <summary>Gets a control of a gamepad.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The control, <c>Gamepad.</c> followed by its name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="control"/> is not a value of the enumeration.</exception>
    public static InputControl Gamepad(GamepadControl control)
    {
        if (!Enum.IsDefined(control))
        {
            throw new ArgumentOutOfRangeException(nameof(control), control, "Not a control of a gamepad.");
        }

        return new InputControl(InputDevice.Gamepad, (int)control);
    }

    /// <summary>Reads the name of a control.</summary>
    /// <param name="name">A name of <see cref="AllNames"/>, such as <c>Key.LeftControl</c>; case-sensitive.</param>
    /// <param name="control">The control, or the default value.</param>
    /// <returns><see langword="true"/> when the name is known.</returns>
    public static bool TryParse(string? name, out InputControl control)
    {
        if (name is not null && ByName.TryGetValue(name, out control))
        {
            return true;
        }

        control = default;
        return false;
    }

    /// <summary>Reads the name of a control.</summary>
    /// <param name="name">A name of <see cref="AllNames"/>.</param>
    /// <returns>The control.</returns>
    /// <exception cref="FormatException">The name is not known.</exception>
    public static InputControl Parse(string name) =>
        TryParse(name, out InputControl control) ? control : throw new FormatException($"'{name}' is not the name of an input control, such as 'Key.W' or 'Mouse.Left'.");

    /// <summary>Tells whether this control stands for another one: the same control, or a side-agnostic modifier and one of its sides.</summary>
    /// <param name="other">A control.</param>
    /// <returns><see langword="true"/> when pressing <paramref name="other"/> presses this control.</returns>
    public bool Covers(InputControl other)
    {
        if (Equals(other))
        {
            return true;
        }

        return IsEitherSide && other.Device == InputDevice.Keyboard && !other.IsEitherSide
            && (other.Code == (Code & ~EitherSide) || other.Code == (Code & ~EitherSide) + 4);
    }

    /// <summary>Tells whether two controls can be pressed by the same key: equal, or a side-agnostic modifier and one of its sides.</summary>
    /// <param name="other">A control.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool Overlaps(InputControl other) => Covers(other) || other.Covers(this);

    /// <inheritdoc/>
    public bool Equals(InputControl other) => Device == other.Device && Code == other.Code;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is InputControl other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ((int)Device << 16) | Code;

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Compares two controls.</summary>
    /// <param name="left">A control.</param>
    /// <param name="right">A control.</param>
    /// <returns><see langword="true"/> when they are the same control.</returns>
    public static bool operator ==(InputControl left, InputControl right) => left.Equals(right);

    /// <summary>Compares two controls.</summary>
    /// <param name="left">A control.</param>
    /// <param name="right">A control.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(InputControl left, InputControl right) => !left.Equals(right);

    private static FrozenDictionary<string, InputControl> BuildNames()
    {
        var names = new Dictionary<string, InputControl>(StringComparer.Ordinal);
        foreach (Scancode scancode in Enum.GetValues<Scancode>())
        {
            if (scancode != Scancode.Unknown)
            {
                names.Add("Key." + scancode, new InputControl(InputDevice.Keyboard, (int)scancode));
            }
        }

        names.Add("Key.Control", Control);
        names.Add("Key.Shift", Shift);
        names.Add("Key.Alt", Alt);
        names.Add("Key.Meta", Meta);
        foreach (MouseControl control in Enum.GetValues<MouseControl>())
        {
            names.Add("Mouse." + control, new InputControl(InputDevice.Mouse, (int)control));
        }

        foreach (GamepadControl control in Enum.GetValues<GamepadControl>())
        {
            names.Add("Gamepad." + control, new InputControl(InputDevice.Gamepad, (int)control));
        }

        return names.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
