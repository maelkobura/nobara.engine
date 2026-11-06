// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Renderer;

/// <summary>
/// Hands out ranges of elements of a buffer: first fit, with adjacent free
/// ranges merged. It allocates nothing once its list of free ranges has grown
/// to the largest number of holes.
/// </summary>
internal sealed class RangeAllocator
{
    // Free ranges, sorted by start, never adjacent.
    private int[] _starts = new int[16];
    private int[] _lengths = new int[16];
    private int _count;

    public RangeAllocator(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _starts[0] = 0;
        _lengths[0] = capacity;
        _count = 1;
    }

    /// <summary>Gets the number of elements.</summary>
    public int Capacity { get; private set; }

    /// <summary>Gets the number of elements in use.</summary>
    public int Used { get; private set; }

    /// <summary>Gets the number of free ranges.</summary>
    public int FreeRanges => _count;

    /// <summary>Gets the length of the largest free range.</summary>
    public int LargestFree
    {
        [HotPath]
        get
        {
            int largest = 0;
            for (int i = 0; i < _count; i++)
            {
                largest = Math.Max(largest, _lengths[i]);
            }

            return largest;
        }
    }

    /// <summary>Gets the part of the free space outside the largest free range, from 0 to 1.</summary>
    public float Fragmentation
    {
        [HotPath]
        get
        {
            int free = Capacity - Used;
            return free == 0 ? 0 : 1 - ((float)LargestFree / free);
        }
    }

    /// <summary>Gets the first element after the last range in use: 0 when nothing is used.</summary>
    public int End
    {
        [HotPath]
        get => _count > 0 && _starts[_count - 1] + _lengths[_count - 1] == Capacity ? _starts[_count - 1] : Capacity;
    }

    /// <summary>Takes the first free range that holds <paramref name="length"/> elements.</summary>
    /// <param name="length">The number of elements; more than 0.</param>
    /// <param name="start">The first element of the range.</param>
    /// <returns><see langword="false"/> when no free range is long enough.</returns>
    [HotPath]
    public bool TryAllocate(int length, out int start) => TryAllocateBelow(length, int.MaxValue, out start);

    /// <summary>Takes the first free range that holds <paramref name="length"/> elements and ends at or before <paramref name="limit"/>.</summary>
    /// <param name="length">The number of elements; more than 0.</param>
    /// <param name="limit">The element the range may not go past.</param>
    /// <param name="start">The first element of the range.</param>
    /// <returns><see langword="false"/> when no such range exists.</returns>
    [HotPath]
    public bool TryAllocateBelow(int length, int limit, out int start)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A range has at least one element.");
        }

        for (int i = 0; i < _count; i++)
        {
            if (_starts[i] + length > limit)
            {
                break;
            }

            if (_lengths[i] >= length)
            {
                start = _starts[i];
                _starts[i] += length;
                _lengths[i] -= length;
                if (_lengths[i] == 0)
                {
                    RemoveAt(i);
                }

                Used += length;
                return true;
            }
        }

        start = -1;
        return false;
    }

    /// <summary>Gives a range back.</summary>
    /// <param name="start">The first element.</param>
    /// <param name="length">The number of elements.</param>
    /// <exception cref="InvalidOperationException">The range is outside the buffer or overlaps a free range.</exception>
    [HotPath]
    public void Free(int start, int length)
    {
        if (length <= 0 || start < 0 || start + length > Capacity)
        {
            throw new InvalidOperationException($"The range of {length} elements at {start} is not in a buffer of {Capacity} elements.");
        }

        // The first free range after the freed one.
        int next = 0;
        while (next < _count && _starts[next] < start)
        {
            next++;
        }

        bool joinsPrevious = next > 0 && _starts[next - 1] + _lengths[next - 1] == start;
        bool joinsNext = next < _count && start + length == _starts[next];
        if ((next > 0 && _starts[next - 1] + _lengths[next - 1] > start) || (next < _count && start + length > _starts[next]))
        {
            throw new InvalidOperationException($"The range of {length} elements at {start} is already free, in part.");
        }

        if (joinsPrevious && joinsNext)
        {
            _lengths[next - 1] += length + _lengths[next];
            RemoveAt(next);
        }
        else if (joinsPrevious)
        {
            _lengths[next - 1] += length;
        }
        else if (joinsNext)
        {
            _starts[next] = start;
            _lengths[next] += length;
        }
        else
        {
            InsertAt(next, start, length);
        }

        Used -= length;
    }

    /// <summary>Makes the buffer larger: the new elements are free.</summary>
    /// <param name="capacity">The new number of elements.</param>
    public void Grow(int capacity)
    {
        if (capacity <= Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"A buffer of {Capacity} elements grows to more.");
        }

        int added = capacity - Capacity;
        int old = Capacity;
        Capacity = capacity;
        Used += added;
        Free(old, added);
    }

    [HotPath]
    private void RemoveAt(int index)
    {
        Array.Copy(_starts, index + 1, _starts, index, _count - index - 1);
        Array.Copy(_lengths, index + 1, _lengths, index, _count - index - 1);
        _count--;
    }

    [HotPath]
    private void InsertAt(int index, int start, int length)
    {
        if (_count == _starts.Length)
        {
            Array.Resize(ref _starts, _starts.Length * 2);
            Array.Resize(ref _lengths, _lengths.Length * 2);
        }

        Array.Copy(_starts, index, _starts, index + 1, _count - index);
        Array.Copy(_lengths, index, _lengths, index + 1, _count - index);
        _starts[index] = start;
        _lengths[index] = length;
        _count++;
    }
}
