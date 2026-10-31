// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Frames;

/// <summary>
/// The raw input events of one frame. The arrays grow when a frame has more
/// events or text than any frame before, and are reused otherwise.
/// </summary>
internal sealed class RawInputBuffer : IRawInput
{
    private readonly Dictionary<Scancode, string> _names = [];
    private RawInputEvent[] _events = new RawInputEvent[64];
    private char[] _text = new char[256];
    private int _count;
    private int _textLength;

    public ulong Frame { get; private set; }

    public ReadOnlySpan<RawInputEvent> Events => _events.AsSpan(0, _count);

    public int LayoutVersion { get; private set; }

    /// <summary>Gets or sets the backend that names keys, once the window exists.</summary>
    public IWindowBackend? Backend { get; set; }

    [HotPath]
    public ReadOnlySpan<char> GetText(in RawInputEvent e) =>
        e.Kind is RawInputKind.TextInput or RawInputKind.TextComposition ? _text.AsSpan(e.TextStart, e.TextLength) : [];

    public string GetKeyName(Scancode scancode)
    {
        if (Backend is null)
        {
            throw new InvalidOperationException("Key names come from the window backend, which nFrames chooses when it starts.");
        }

        if (!_names.TryGetValue(scancode, out string? name))
        {
            name = Backend.GetKeyName(scancode);
            _names[scancode] = name;
        }

        return name;
    }

    /// <summary>Empties the buffer for a new frame.</summary>
    [HotPath]
    public void Begin(ulong frame)
    {
        Frame = frame;
        _count = 0;
        _textLength = 0;
    }

    [HotPath]
    public void Add(in RawInputEvent e, ReadOnlySpan<char> text)
    {
        if (_count == _events.Length)
        {
            Array.Resize(ref _events, _events.Length * 2);
        }

        RawInputEvent stored = e;
        if (e.Kind is RawInputKind.TextInput or RawInputKind.TextComposition)
        {
            if (_textLength + text.Length > _text.Length)
            {
                Array.Resize(ref _text, Math.Max(_text.Length * 2, _textLength + text.Length));
            }

            text.CopyTo(_text.AsSpan(_textLength));
            stored = e with { TextStart = _textLength, TextLength = text.Length };
            _textLength += text.Length;
        }

        _events[_count++] = stored;
    }

    /// <summary>Forgets the cached names after a change of layout.</summary>
    public void LayoutChanged()
    {
        _names.Clear();
        LayoutVersion++;
    }
}
