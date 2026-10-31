// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Frames;

/// <summary>What a raw input event reports.</summary>
public enum RawInputKind : byte
{
    /// <summary>A key went down, or repeats while held: <see cref="RawInputEvent.Scancode"/>, <see cref="RawInputEvent.Modifiers"/>, <see cref="RawInputEvent.IsRepeat"/>.</summary>
    KeyDown,

    /// <summary>A key went up: <see cref="RawInputEvent.Scancode"/>, <see cref="RawInputEvent.Modifiers"/>.</summary>
    KeyUp,

    /// <summary>A mouse button went down: <see cref="RawInputEvent.Button"/>, <see cref="RawInputEvent.Clicks"/>, and the position.</summary>
    MouseButtonDown,

    /// <summary>A mouse button went up: <see cref="RawInputEvent.Button"/>, <see cref="RawInputEvent.Clicks"/>, and the position.</summary>
    MouseButtonUp,

    /// <summary>The mouse moved: the position and the movement in <see cref="RawInputEvent.DeltaX"/> and <see cref="RawInputEvent.DeltaY"/>.</summary>
    MouseMotion,

    /// <summary>The wheel turned: the amount in <see cref="RawInputEvent.DeltaX"/> (horizontal) and <see cref="RawInputEvent.DeltaY"/> (vertical, positive away from the player).</summary>
    MouseWheel,

    /// <summary>Text was typed, composition included: the characters, read with <see cref="IRawInput.GetText"/>.</summary>
    TextInput,

    /// <summary>
    /// The text being composed by an input method changed: the characters,
    /// read with <see cref="IRawInput.GetText"/>, and the selected part in
    /// <see cref="RawInputEvent.SelectionStart"/> and <see cref="RawInputEvent.SelectionLength"/>.
    /// An empty text ends the composition.
    /// </summary>
    TextComposition,
}

/// <summary>The modifier keys and lock states held when a key event happened.</summary>
[Flags]
public enum KeyModifiers : ushort
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>The left Shift key.</summary>
    LeftShift = 1 << 0,

    /// <summary>The right Shift key.</summary>
    RightShift = 1 << 1,

    /// <summary>The left Control key.</summary>
    LeftControl = 1 << 2,

    /// <summary>The right Control key.</summary>
    RightControl = 1 << 3,

    /// <summary>The left Alt key.</summary>
    LeftAlt = 1 << 4,

    /// <summary>The right Alt key, AltGr on many layouts.</summary>
    RightAlt = 1 << 5,

    /// <summary>The left meta key: the Windows key, or the Command key.</summary>
    LeftMeta = 1 << 6,

    /// <summary>The right meta key.</summary>
    RightMeta = 1 << 7,

    /// <summary>Num Lock is on.</summary>
    NumLock = 1 << 8,

    /// <summary>Caps Lock is on.</summary>
    CapsLock = 1 << 9,

    /// <summary>Scroll Lock is on.</summary>
    ScrollLock = 1 << 10,

    /// <summary>Either Shift key.</summary>
    Shift = LeftShift | RightShift,

    /// <summary>Either Control key.</summary>
    Control = LeftControl | RightControl,

    /// <summary>Either Alt key.</summary>
    Alt = LeftAlt | RightAlt,

    /// <summary>Either meta key.</summary>
    Meta = LeftMeta | RightMeta,
}

/// <summary>A button of the mouse.</summary>
public enum MouseButton : byte
{
    /// <summary>No button, in an event that is not about a button.</summary>
    None,

    /// <summary>The left button.</summary>
    Left,

    /// <summary>The middle button, often the wheel.</summary>
    Middle,

    /// <summary>The right button.</summary>
    Right,

    /// <summary>The first extra button, often "back".</summary>
    X1,

    /// <summary>The second extra button, often "forward".</summary>
    X2,
}

/// <summary>
/// One raw input event: a key, a mouse button, a movement of the mouse or of
/// its wheel, or typed text. Only the fields that its <see cref="Kind"/> names
/// are set; the others are zero.
/// </summary>
/// <remarks>
/// Positions are in logical coordinates of the window, from its top-left
/// corner, the coordinates of the user interface: multiply by
/// <see cref="IWindow.Scale"/> to get pixels. In relative mouse mode, the
/// position does not move; the delta does.
/// </remarks>
public readonly struct RawInputEvent
{
    [HotPath]
    private RawInputEvent(RawInputKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets what the event reports.</summary>
    public RawInputKind Kind { get; private init; }

    /// <summary>Gets the physical key of a key event.</summary>
    public Scancode Scancode { get; private init; }

    /// <summary>Gets the modifiers and lock states held during a key event.</summary>
    public KeyModifiers Modifiers { get; private init; }

    /// <summary>Gets whether a <see cref="RawInputKind.KeyDown"/> event repeats a key that is held, rather than a new press.</summary>
    public bool IsRepeat { get; private init; }

    /// <summary>Gets the button of a mouse button event.</summary>
    public MouseButton Button { get; private init; }

    /// <summary>Gets the number of successive clicks of a mouse button event: 1 for a single click, 2 for a double click.</summary>
    public byte Clicks { get; private init; }

    /// <summary>Gets the horizontal position of the mouse, in logical units from the left edge of the window.</summary>
    public float X { get; private init; }

    /// <summary>Gets the vertical position of the mouse, in logical units from the top edge of the window.</summary>
    public float Y { get; private init; }

    /// <summary>Gets the horizontal movement of the mouse in logical units, or of the wheel in notches.</summary>
    public float DeltaX { get; private init; }

    /// <summary>Gets the vertical movement of the mouse in logical units (positive downward), or of the wheel in notches (positive away from the player).</summary>
    public float DeltaY { get; private init; }

    /// <summary>Gets where the selected part of a composition starts, in characters.</summary>
    public int SelectionStart { get; private init; }

    /// <summary>Gets the length of the selected part of a composition, in characters.</summary>
    public int SelectionLength { get; private init; }

    /// <summary>Gets where the characters of a text event start in the text of the frame.</summary>
    internal int TextStart { get; init; }

    /// <summary>Gets the number of characters of a text event.</summary>
    internal int TextLength { get; init; }

    /// <summary>Makes a key event.</summary>
    /// <param name="down"><see langword="true"/> for <see cref="RawInputKind.KeyDown"/>, <see langword="false"/> for <see cref="RawInputKind.KeyUp"/>.</param>
    /// <param name="scancode">The physical key.</param>
    /// <param name="modifiers">The modifiers and lock states held.</param>
    /// <param name="repeat">Whether the event repeats a key that is held.</param>
    /// <returns>The event.</returns>
    [HotPath]
    public static RawInputEvent Key(bool down, Scancode scancode, KeyModifiers modifiers, bool repeat = false) =>
        new(down ? RawInputKind.KeyDown : RawInputKind.KeyUp) { Scancode = scancode, Modifiers = modifiers, IsRepeat = down && repeat };

    /// <summary>Makes a mouse button event.</summary>
    /// <param name="down"><see langword="true"/> when the button went down.</param>
    /// <param name="button">The button.</param>
    /// <param name="clicks">The number of successive clicks.</param>
    /// <param name="x">The horizontal position, in logical units.</param>
    /// <param name="y">The vertical position, in logical units.</param>
    /// <returns>The event.</returns>
    [HotPath]
    public static RawInputEvent MouseButtonEvent(bool down, MouseButton button, byte clicks, float x, float y) =>
        new(down ? RawInputKind.MouseButtonDown : RawInputKind.MouseButtonUp) { Button = button, Clicks = clicks, X = x, Y = y };

    /// <summary>Makes a mouse movement event.</summary>
    /// <param name="x">The new horizontal position, in logical units.</param>
    /// <param name="y">The new vertical position, in logical units.</param>
    /// <param name="deltaX">The horizontal movement, in logical units.</param>
    /// <param name="deltaY">The vertical movement, in logical units.</param>
    /// <returns>The event.</returns>
    [HotPath]
    public static RawInputEvent MouseMotion(float x, float y, float deltaX, float deltaY) =>
        new(RawInputKind.MouseMotion) { X = x, Y = y, DeltaX = deltaX, DeltaY = deltaY };

    /// <summary>Makes a wheel event.</summary>
    /// <param name="x">The horizontal position of the mouse, in logical units.</param>
    /// <param name="y">The vertical position of the mouse, in logical units.</param>
    /// <param name="deltaX">The horizontal amount, in notches, positive to the right.</param>
    /// <param name="deltaY">The vertical amount, in notches, positive away from the player.</param>
    /// <returns>The event.</returns>
    [HotPath]
    public static RawInputEvent MouseWheel(float x, float y, float deltaX, float deltaY) =>
        new(RawInputKind.MouseWheel) { X = x, Y = y, DeltaX = deltaX, DeltaY = deltaY };

    /// <summary>Makes a text event; the backend gives its characters with it to <see cref="IWindowEventSink.OnInput"/>.</summary>
    /// <returns>The event.</returns>
    [HotPath]
    public static RawInputEvent Text() => new(RawInputKind.TextInput);

    /// <summary>Makes a composition event; the backend gives its characters with it to <see cref="IWindowEventSink.OnInput"/>.</summary>
    /// <param name="selectionStart">Where the selected part starts, in characters.</param>
    /// <param name="selectionLength">The length of the selected part, in characters.</param>
    /// <returns>The event.</returns>
    [HotPath]
    public static RawInputEvent Composition(int selectionStart, int selectionLength) =>
        new(RawInputKind.TextComposition) { SelectionStart = selectionStart, SelectionLength = selectionLength };
}

/// <summary>
/// The raw input events of the frame that runs, in the order they happened:
/// keys, mouse, and typed text, as the backend of the window received them.
/// </summary>
/// <remarks>
/// <para>
/// It is the source of the input module, which turns the events into actions
/// for its layers. Other modules read input through the input module, not
/// here.
/// </para>
/// <para>
/// nFrames fills it at the start of each frame, on the loop thread, before
/// the first phase, and empties it at the start of the next frame: read it
/// during the frame, from the loop thread. Reading allocates nothing.
/// </para>
/// </remarks>
public interface IRawInput
{
    /// <summary>Gets the number of the frame whose events these are.</summary>
    ulong Frame { get; }

    /// <summary>Gets the events of the frame, in order.</summary>
    ReadOnlySpan<RawInputEvent> Events { get; }

    /// <summary>Gets the characters of a <see cref="RawInputKind.TextInput"/> or <see cref="RawInputKind.TextComposition"/> event.</summary>
    /// <param name="e">An event of this frame.</param>
    /// <returns>The characters, valid until the end of the frame; empty for other events.</returns>
    [HotPath]
    ReadOnlySpan<char> GetText(in RawInputEvent e);

    /// <summary>
    /// Gets a version number of the keyboard layout, which grows when the
    /// player changes layout: names from <see cref="GetKeyName"/> cached
    /// before are out of date.
    /// </summary>
    int LayoutVersion { get; }

    /// <summary>Gets the name to show to the player for a key, as the current keyboard layout labels it, such as <c>Z</c> for <see cref="Scancode.W"/> on an AZERTY keyboard.</summary>
    /// <param name="scancode">The physical key.</param>
    /// <returns>The name, or an empty string when the key has none.</returns>
    /// <exception cref="InvalidOperationException">The window is not created yet: it is, from the start step on.</exception>
    /// <remarks>The name is cached per key until the layout changes.</remarks>
    string GetKeyName(Scancode scancode);
}
