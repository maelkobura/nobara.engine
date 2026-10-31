// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Frames;

/// <summary>
/// The physical position of a key on the keyboard, whatever character the
/// layout of the player gives it. The values are the usage identifiers of the
/// keyboard page of the USB HID standard.
/// </summary>
/// <remarks>
/// A binding on <see cref="W"/> stays at the same place on every layout: it is
/// the key labeled Z on an AZERTY keyboard. The name to show to the player
/// comes from <see cref="IRawInput.GetKeyName"/>, which follows the layout.
/// </remarks>
public enum Scancode
{
    /// <summary>A key that has no scancode of its own.</summary>
    Unknown = 0,

    /// <summary>The key at the position of A on a US keyboard.</summary>
    A = 4,

    /// <summary>The key at the position of B on a US keyboard.</summary>
    B = 5,

    /// <summary>The key at the position of C on a US keyboard.</summary>
    C = 6,

    /// <summary>The key at the position of D on a US keyboard.</summary>
    D = 7,

    /// <summary>The key at the position of E on a US keyboard.</summary>
    E = 8,

    /// <summary>The key at the position of F on a US keyboard.</summary>
    F = 9,

    /// <summary>The key at the position of G on a US keyboard.</summary>
    G = 10,

    /// <summary>The key at the position of H on a US keyboard.</summary>
    H = 11,

    /// <summary>The key at the position of I on a US keyboard.</summary>
    I = 12,

    /// <summary>The key at the position of J on a US keyboard.</summary>
    J = 13,

    /// <summary>The key at the position of K on a US keyboard.</summary>
    K = 14,

    /// <summary>The key at the position of L on a US keyboard.</summary>
    L = 15,

    /// <summary>The key at the position of M on a US keyboard.</summary>
    M = 16,

    /// <summary>The key at the position of N on a US keyboard.</summary>
    N = 17,

    /// <summary>The key at the position of O on a US keyboard.</summary>
    O = 18,

    /// <summary>The key at the position of P on a US keyboard.</summary>
    P = 19,

    /// <summary>The key at the position of Q on a US keyboard.</summary>
    Q = 20,

    /// <summary>The key at the position of R on a US keyboard.</summary>
    R = 21,

    /// <summary>The key at the position of S on a US keyboard.</summary>
    S = 22,

    /// <summary>The key at the position of T on a US keyboard.</summary>
    T = 23,

    /// <summary>The key at the position of U on a US keyboard.</summary>
    U = 24,

    /// <summary>The key at the position of V on a US keyboard.</summary>
    V = 25,

    /// <summary>The key at the position of W on a US keyboard.</summary>
    W = 26,

    /// <summary>The key at the position of X on a US keyboard.</summary>
    X = 27,

    /// <summary>The key at the position of Y on a US keyboard.</summary>
    Y = 28,

    /// <summary>The key at the position of Z on a US keyboard.</summary>
    Z = 29,

    /// <summary>The key at the position of 1 on a US keyboard.</summary>
    Digit1 = 30,

    /// <summary>The key at the position of 2 on a US keyboard.</summary>
    Digit2 = 31,

    /// <summary>The key at the position of 3 on a US keyboard.</summary>
    Digit3 = 32,

    /// <summary>The key at the position of 4 on a US keyboard.</summary>
    Digit4 = 33,

    /// <summary>The key at the position of 5 on a US keyboard.</summary>
    Digit5 = 34,

    /// <summary>The key at the position of 6 on a US keyboard.</summary>
    Digit6 = 35,

    /// <summary>The key at the position of 7 on a US keyboard.</summary>
    Digit7 = 36,

    /// <summary>The key at the position of 8 on a US keyboard.</summary>
    Digit8 = 37,

    /// <summary>The key at the position of 9 on a US keyboard.</summary>
    Digit9 = 38,

    /// <summary>The key at the position of 0 on a US keyboard.</summary>
    Digit0 = 39,

    /// <summary>The enter key.</summary>
    Enter = 40,

    /// <summary>The escape key.</summary>
    Escape = 41,

    /// <summary>The backspace key.</summary>
    Backspace = 42,

    /// <summary>The tab key.</summary>
    Tab = 43,

    /// <summary>The space key.</summary>
    Space = 44,

    /// <summary>The minus key.</summary>
    Minus = 45,

    /// <summary>The equals key.</summary>
    Equals = 46,

    /// <summary>The left bracket key.</summary>
    LeftBracket = 47,

    /// <summary>The right bracket key.</summary>
    RightBracket = 48,

    /// <summary>The backslash key.</summary>
    Backslash = 49,

    /// <summary>The key next to Enter on an ISO keyboard, which a US keyboard does not have.</summary>
    NonUSHash = 50,

    /// <summary>The semicolon key.</summary>
    Semicolon = 51,

    /// <summary>The apostrophe key.</summary>
    Apostrophe = 52,

    /// <summary>The grave key.</summary>
    Grave = 53,

    /// <summary>The comma key.</summary>
    Comma = 54,

    /// <summary>The period key.</summary>
    Period = 55,

    /// <summary>The slash key.</summary>
    Slash = 56,

    /// <summary>The caps lock key.</summary>
    CapsLock = 57,

    /// <summary>The F1 key.</summary>
    F1 = 58,

    /// <summary>The F2 key.</summary>
    F2 = 59,

    /// <summary>The F3 key.</summary>
    F3 = 60,

    /// <summary>The F4 key.</summary>
    F4 = 61,

    /// <summary>The F5 key.</summary>
    F5 = 62,

    /// <summary>The F6 key.</summary>
    F6 = 63,

    /// <summary>The F7 key.</summary>
    F7 = 64,

    /// <summary>The F8 key.</summary>
    F8 = 65,

    /// <summary>The F9 key.</summary>
    F9 = 66,

    /// <summary>The F10 key.</summary>
    F10 = 67,

    /// <summary>The F11 key.</summary>
    F11 = 68,

    /// <summary>The F12 key.</summary>
    F12 = 69,

    /// <summary>The print screen key.</summary>
    PrintScreen = 70,

    /// <summary>The scroll lock key.</summary>
    ScrollLock = 71,

    /// <summary>The pause key.</summary>
    Pause = 72,

    /// <summary>The insert key.</summary>
    Insert = 73,

    /// <summary>The home key.</summary>
    Home = 74,

    /// <summary>The page up key.</summary>
    PageUp = 75,

    /// <summary>The delete key.</summary>
    Delete = 76,

    /// <summary>The end key.</summary>
    End = 77,

    /// <summary>The page down key.</summary>
    PageDown = 78,

    /// <summary>The right arrow key.</summary>
    ArrowRight = 79,

    /// <summary>The left arrow key.</summary>
    ArrowLeft = 80,

    /// <summary>The down arrow key.</summary>
    ArrowDown = 81,

    /// <summary>The up arrow key.</summary>
    ArrowUp = 82,

    /// <summary>The num lock key.</summary>
    NumLock = 83,

    /// <summary>The keypad divide key.</summary>
    KeypadDivide = 84,

    /// <summary>The keypad multiply key.</summary>
    KeypadMultiply = 85,

    /// <summary>The keypad minus key.</summary>
    KeypadMinus = 86,

    /// <summary>The keypad plus key.</summary>
    KeypadPlus = 87,

    /// <summary>The keypad enter key.</summary>
    KeypadEnter = 88,

    /// <summary>The keypad 1 key.</summary>
    Keypad1 = 89,

    /// <summary>The keypad 2 key.</summary>
    Keypad2 = 90,

    /// <summary>The keypad 3 key.</summary>
    Keypad3 = 91,

    /// <summary>The keypad 4 key.</summary>
    Keypad4 = 92,

    /// <summary>The keypad 5 key.</summary>
    Keypad5 = 93,

    /// <summary>The keypad 6 key.</summary>
    Keypad6 = 94,

    /// <summary>The keypad 7 key.</summary>
    Keypad7 = 95,

    /// <summary>The keypad 8 key.</summary>
    Keypad8 = 96,

    /// <summary>The keypad 9 key.</summary>
    Keypad9 = 97,

    /// <summary>The keypad 0 key.</summary>
    Keypad0 = 98,

    /// <summary>The keypad period key.</summary>
    KeypadPeriod = 99,

    /// <summary>The key next to the left Shift on an ISO keyboard, which a US keyboard does not have.</summary>
    NonUSBackslash = 100,

    /// <summary>The application key.</summary>
    Application = 101,

    /// <summary>The power key.</summary>
    Power = 102,

    /// <summary>The keypad equals key.</summary>
    KeypadEquals = 103,

    /// <summary>The F13 key.</summary>
    F13 = 104,

    /// <summary>The F14 key.</summary>
    F14 = 105,

    /// <summary>The F15 key.</summary>
    F15 = 106,

    /// <summary>The F16 key.</summary>
    F16 = 107,

    /// <summary>The F17 key.</summary>
    F17 = 108,

    /// <summary>The F18 key.</summary>
    F18 = 109,

    /// <summary>The F19 key.</summary>
    F19 = 110,

    /// <summary>The F20 key.</summary>
    F20 = 111,

    /// <summary>The F21 key.</summary>
    F21 = 112,

    /// <summary>The F22 key.</summary>
    F22 = 113,

    /// <summary>The F23 key.</summary>
    F23 = 114,

    /// <summary>The F24 key.</summary>
    F24 = 115,

    /// <summary>The execute key.</summary>
    Execute = 116,

    /// <summary>The help key.</summary>
    Help = 117,

    /// <summary>The menu key.</summary>
    Menu = 118,

    /// <summary>The select key.</summary>
    Select = 119,

    /// <summary>The stop key.</summary>
    Stop = 120,

    /// <summary>The again key.</summary>
    Again = 121,

    /// <summary>The undo key.</summary>
    Undo = 122,

    /// <summary>The cut key.</summary>
    Cut = 123,

    /// <summary>The copy key.</summary>
    Copy = 124,

    /// <summary>The paste key.</summary>
    Paste = 125,

    /// <summary>The find key.</summary>
    Find = 126,

    /// <summary>The mute key.</summary>
    Mute = 127,

    /// <summary>The volume up key.</summary>
    VolumeUp = 128,

    /// <summary>The volume down key.</summary>
    VolumeDown = 129,

    /// <summary>The keypad comma key.</summary>
    KeypadComma = 133,

    /// <summary>The keypad equals as400 key.</summary>
    KeypadEqualsAs400 = 134,

    /// <summary>The international 1 key.</summary>
    International1 = 135,

    /// <summary>The international 2 key.</summary>
    International2 = 136,

    /// <summary>The international 3 key.</summary>
    International3 = 137,

    /// <summary>The international 4 key.</summary>
    International4 = 138,

    /// <summary>The international 5 key.</summary>
    International5 = 139,

    /// <summary>The international 6 key.</summary>
    International6 = 140,

    /// <summary>The international 7 key.</summary>
    International7 = 141,

    /// <summary>The international 8 key.</summary>
    International8 = 142,

    /// <summary>The international 9 key.</summary>
    International9 = 143,

    /// <summary>The language 1 key.</summary>
    Language1 = 144,

    /// <summary>The language 2 key.</summary>
    Language2 = 145,

    /// <summary>The language 3 key.</summary>
    Language3 = 146,

    /// <summary>The language 4 key.</summary>
    Language4 = 147,

    /// <summary>The language 5 key.</summary>
    Language5 = 148,

    /// <summary>The language 6 key.</summary>
    Language6 = 149,

    /// <summary>The language 7 key.</summary>
    Language7 = 150,

    /// <summary>The language 8 key.</summary>
    Language8 = 151,

    /// <summary>The language 9 key.</summary>
    Language9 = 152,

    /// <summary>The alt erase key.</summary>
    AltErase = 153,

    /// <summary>The sys req key.</summary>
    SysReq = 154,

    /// <summary>The cancel key.</summary>
    Cancel = 155,

    /// <summary>The clear key.</summary>
    Clear = 156,

    /// <summary>The prior key.</summary>
    Prior = 157,

    /// <summary>The return 2 key.</summary>
    Return2 = 158,

    /// <summary>The separator key.</summary>
    Separator = 159,

    /// <summary>The out key.</summary>
    Out = 160,

    /// <summary>The oper key.</summary>
    Oper = 161,

    /// <summary>The clear again key.</summary>
    ClearAgain = 162,

    /// <summary>The cr sel key.</summary>
    CrSel = 163,

    /// <summary>The ex sel key.</summary>
    ExSel = 164,

    /// <summary>The front key.</summary>
    Front = 165,

    /// <summary>The keypad 00 key.</summary>
    Keypad00 = 176,

    /// <summary>The keypad 000 key.</summary>
    Keypad000 = 177,

    /// <summary>The thousands separator key.</summary>
    ThousandsSeparator = 178,

    /// <summary>The decimal separator key.</summary>
    DecimalSeparator = 179,

    /// <summary>The currency unit key.</summary>
    CurrencyUnit = 180,

    /// <summary>The currency subunit key.</summary>
    CurrencySubunit = 181,

    /// <summary>The keypad left parenthesis key.</summary>
    KeypadLeftParenthesis = 182,

    /// <summary>The keypad right parenthesis key.</summary>
    KeypadRightParenthesis = 183,

    /// <summary>The keypad left brace key.</summary>
    KeypadLeftBrace = 184,

    /// <summary>The keypad right brace key.</summary>
    KeypadRightBrace = 185,

    /// <summary>The keypad tab key.</summary>
    KeypadTab = 186,

    /// <summary>The keypad backspace key.</summary>
    KeypadBackspace = 187,

    /// <summary>The keypad a key.</summary>
    KeypadA = 188,

    /// <summary>The keypad b key.</summary>
    KeypadB = 189,

    /// <summary>The keypad c key.</summary>
    KeypadC = 190,

    /// <summary>The keypad d key.</summary>
    KeypadD = 191,

    /// <summary>The keypad e key.</summary>
    KeypadE = 192,

    /// <summary>The keypad f key.</summary>
    KeypadF = 193,

    /// <summary>The keypad xor key.</summary>
    KeypadXor = 194,

    /// <summary>The keypad power key.</summary>
    KeypadPower = 195,

    /// <summary>The keypad percent key.</summary>
    KeypadPercent = 196,

    /// <summary>The keypad less key.</summary>
    KeypadLess = 197,

    /// <summary>The keypad greater key.</summary>
    KeypadGreater = 198,

    /// <summary>The keypad ampersand key.</summary>
    KeypadAmpersand = 199,

    /// <summary>The keypad double ampersand key.</summary>
    KeypadDoubleAmpersand = 200,

    /// <summary>The keypad vertical bar key.</summary>
    KeypadVerticalBar = 201,

    /// <summary>The keypad double vertical bar key.</summary>
    KeypadDoubleVerticalBar = 202,

    /// <summary>The keypad colon key.</summary>
    KeypadColon = 203,

    /// <summary>The keypad hash key.</summary>
    KeypadHash = 204,

    /// <summary>The keypad space key.</summary>
    KeypadSpace = 205,

    /// <summary>The keypad at key.</summary>
    KeypadAt = 206,

    /// <summary>The keypad exclamation key.</summary>
    KeypadExclamation = 207,

    /// <summary>The keypad memory store key.</summary>
    KeypadMemoryStore = 208,

    /// <summary>The keypad memory recall key.</summary>
    KeypadMemoryRecall = 209,

    /// <summary>The keypad memory clear key.</summary>
    KeypadMemoryClear = 210,

    /// <summary>The keypad memory add key.</summary>
    KeypadMemoryAdd = 211,

    /// <summary>The keypad memory subtract key.</summary>
    KeypadMemorySubtract = 212,

    /// <summary>The keypad memory multiply key.</summary>
    KeypadMemoryMultiply = 213,

    /// <summary>The keypad memory divide key.</summary>
    KeypadMemoryDivide = 214,

    /// <summary>The keypad plus minus key.</summary>
    KeypadPlusMinus = 215,

    /// <summary>The keypad clear key.</summary>
    KeypadClear = 216,

    /// <summary>The keypad clear entry key.</summary>
    KeypadClearEntry = 217,

    /// <summary>The keypad binary key.</summary>
    KeypadBinary = 218,

    /// <summary>The keypad octal key.</summary>
    KeypadOctal = 219,

    /// <summary>The keypad decimal key.</summary>
    KeypadDecimal = 220,

    /// <summary>The keypad hexadecimal key.</summary>
    KeypadHexadecimal = 221,

    /// <summary>The left Control key.</summary>
    LeftControl = 224,

    /// <summary>The left Shift key.</summary>
    LeftShift = 225,

    /// <summary>The left Alt key.</summary>
    LeftAlt = 226,

    /// <summary>The left meta key: the Windows key, or the Command key on a Mac keyboard.</summary>
    LeftMeta = 227,

    /// <summary>The right Control key.</summary>
    RightControl = 228,

    /// <summary>The right Shift key.</summary>
    RightShift = 229,

    /// <summary>The right Alt key.</summary>
    RightAlt = 230,

    /// <summary>The right meta key: the Windows key, or the Command key on a Mac keyboard.</summary>
    RightMeta = 231,
}
