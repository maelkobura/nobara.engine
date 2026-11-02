// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core;

namespace Nobara.Entity;

/// <summary>
/// A growable list of unmanaged values in unmanaged memory. It grows without
/// shrinking and never touches the managed heap. The owner disposes it.
/// </summary>
[HotPath]
internal unsafe struct NativeList<T> : IDisposable
    where T : unmanaged
{
    private T* _items;
    private int _capacity;

    public NativeList(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _items = (T*)NativeMemory.Alloc((nuint)_capacity, (nuint)sizeof(T));
        Count = 0;
    }

    public int Count { get; set; }

    public readonly T* Pointer => _items;

    public readonly ref T this[int index] => ref _items[index];

    public readonly Span<T> AsSpan() => new(_items, Count);

    public void Add(in T item)
    {
        if (Count == _capacity)
        {
            Reserve(_capacity * 2);
        }

        _items[Count++] = item;
    }

    public void Reserve(int capacity)
    {
        if (capacity > _capacity)
        {
            _items = (T*)NativeMemory.Realloc(_items, (nuint)capacity * (nuint)sizeof(T));
            _capacity = capacity;
        }
    }

    public void Clear() => Count = 0;

    public void Dispose()
    {
        if (_items != null)
        {
            NativeMemory.Free(_items);
            _items = null;
            _capacity = 0;
            Count = 0;
        }
    }
}
