// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using ImGuiNET;
using Nobara.Frames;

namespace Nobara.Debug;

/// <summary>
/// The keys of Dear ImGui for the physical keys of nFrames. Letters follow the
/// keyboard layout of the player, so that the shortcuts of text fields
/// (Ctrl+A, Ctrl+Z) are the keys labeled so; the other keys keep their
/// position.
/// </summary>
internal sealed class ImGuiKeys
{
    private readonly ImGuiKey[] _letters = new ImGuiKey[26];
    private int _layoutVersion = -1;

    /// <summary>Gets the key of Dear ImGui for a physical key, or <see cref="ImGuiKey.None"/>.</summary>
    public ImGuiKey Map(Scancode scancode, IRawInput input)
    {
        if (scancode is >= Scancode.A and <= Scancode.Z)
        {
            if (_layoutVersion != input.LayoutVersion)
            {
                ReadLayout(input);
            }

            return _letters[scancode - Scancode.A];
        }

        return Position(scancode);
    }

    /// <summary>Gets the key of Dear ImGui at the position of a physical key, or <see cref="ImGuiKey.None"/>.</summary>
    internal static ImGuiKey Position(Scancode scancode) => scancode switch
    {
        >= Scancode.A and <= Scancode.Z => ImGuiKey.A + (scancode - Scancode.A),
        >= Scancode.Digit1 and <= Scancode.Digit9 => ImGuiKey._1 + (scancode - Scancode.Digit1),
        Scancode.Digit0 => ImGuiKey._0,
        >= Scancode.F1 and <= Scancode.F12 => ImGuiKey.F1 + (scancode - Scancode.F1),
        >= Scancode.F13 and <= Scancode.F24 => ImGuiKey.F13 + (scancode - Scancode.F13),
        >= Scancode.Keypad1 and <= Scancode.Keypad9 => ImGuiKey.Keypad1 + (scancode - Scancode.Keypad1),
        Scancode.Keypad0 => ImGuiKey.Keypad0,
        Scancode.Enter => ImGuiKey.Enter,
        Scancode.Escape => ImGuiKey.Escape,
        Scancode.Backspace => ImGuiKey.Backspace,
        Scancode.Tab => ImGuiKey.Tab,
        Scancode.Space => ImGuiKey.Space,
        Scancode.Minus => ImGuiKey.Minus,
        Scancode.Equals => ImGuiKey.Equal,
        Scancode.LeftBracket => ImGuiKey.LeftBracket,
        Scancode.RightBracket => ImGuiKey.RightBracket,
        Scancode.Backslash or Scancode.NonUSHash => ImGuiKey.Backslash,
        Scancode.Semicolon => ImGuiKey.Semicolon,
        Scancode.Apostrophe => ImGuiKey.Apostrophe,
        Scancode.Grave => ImGuiKey.GraveAccent,
        Scancode.Comma => ImGuiKey.Comma,
        Scancode.Period => ImGuiKey.Period,
        Scancode.Slash => ImGuiKey.Slash,
        Scancode.CapsLock => ImGuiKey.CapsLock,
        Scancode.PrintScreen => ImGuiKey.PrintScreen,
        Scancode.ScrollLock => ImGuiKey.ScrollLock,
        Scancode.Pause => ImGuiKey.Pause,
        Scancode.Insert => ImGuiKey.Insert,
        Scancode.Home => ImGuiKey.Home,
        Scancode.PageUp => ImGuiKey.PageUp,
        Scancode.Delete => ImGuiKey.Delete,
        Scancode.End => ImGuiKey.End,
        Scancode.PageDown => ImGuiKey.PageDown,
        Scancode.ArrowRight => ImGuiKey.RightArrow,
        Scancode.ArrowLeft => ImGuiKey.LeftArrow,
        Scancode.ArrowDown => ImGuiKey.DownArrow,
        Scancode.ArrowUp => ImGuiKey.UpArrow,
        Scancode.NumLock => ImGuiKey.NumLock,
        Scancode.KeypadDivide => ImGuiKey.KeypadDivide,
        Scancode.KeypadMultiply => ImGuiKey.KeypadMultiply,
        Scancode.KeypadMinus => ImGuiKey.KeypadSubtract,
        Scancode.KeypadPlus => ImGuiKey.KeypadAdd,
        Scancode.KeypadEnter => ImGuiKey.KeypadEnter,
        Scancode.KeypadPeriod => ImGuiKey.KeypadDecimal,
        Scancode.KeypadEquals => ImGuiKey.KeypadEqual,
        Scancode.Application or Scancode.Menu => ImGuiKey.Menu,
        Scancode.LeftControl => ImGuiKey.LeftCtrl,
        Scancode.LeftShift => ImGuiKey.LeftShift,
        Scancode.LeftAlt => ImGuiKey.LeftAlt,
        Scancode.LeftMeta => ImGuiKey.LeftSuper,
        Scancode.RightControl => ImGuiKey.RightCtrl,
        Scancode.RightShift => ImGuiKey.RightShift,
        Scancode.RightAlt => ImGuiKey.RightAlt,
        Scancode.RightMeta => ImGuiKey.RightSuper,
        _ => ImGuiKey.None,
    };

    // A letter key takes the letter its layout prints, or keeps its position
    // when the layout prints something else there.
    private void ReadLayout(IRawInput input)
    {
        for (Scancode key = Scancode.A; key <= Scancode.Z; key++)
        {
            string name = input.GetKeyName(key);
            _letters[key - Scancode.A] = name.Length == 1 && char.ToUpperInvariant(name[0]) is >= 'A' and <= 'Z' and char letter
                ? ImGuiKey.A + (letter - 'A')
                : Position(key);
        }

        _layoutVersion = input.LayoutVersion;
    }
}
