// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;

namespace Nobara.Core.Collections;

/// <summary>
/// A set of small non-negative integers, such as the runtime indices of the
/// members of a tag, stored as one bit per possible value.
/// </summary>
/// <remarks>
/// <para>
/// The capacity is fixed when the set is created: values go from 0 to
/// <see cref="Capacity"/> minus 1. Creating a set allocates its storage;
/// every other operation runs without allocating, including enumeration with
/// <see langword="foreach"/>.
/// </para>
/// <para>A set is not thread-safe for writes. Several threads may read a set that nobody writes.</para>
/// </remarks>
/// <example>
/// <code>
/// var wooden = new BitSet(itemCount);
/// wooden.Add(oakPlanks);
/// if (wooden.Contains(item)) { /* ... */ }
/// foreach (int index in wooden) { /* ... */ }
/// </code>
/// </example>
public sealed class BitSet
{
    private readonly ulong[] _words;

    /// <summary>Creates an empty set.</summary>
    /// <param name="capacity">The number of possible values, from 0 to <paramref name="capacity"/> minus 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public BitSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Capacity = capacity;
        _words = new ulong[(capacity + 63) >> 6];
    }

    /// <summary>Gets the number of possible values.</summary>
    public int Capacity { get; }

    /// <summary>Gets the number of values in the set.</summary>
    public int Count
    {
        [HotPath]
        get
        {
            int count = 0;
            foreach (ulong word in _words)
            {
                count += BitOperations.PopCount(word);
            }

            return count;
        }
    }

    /// <summary>Gets whether the set has no value.</summary>
    public bool IsEmpty
    {
        [HotPath]
        get
        {
            foreach (ulong word in _words)
            {
                if (word != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Tells whether a value is in the set.</summary>
    /// <param name="value">The value. A value outside the capacity is never in the set.</param>
    /// <returns><see langword="true"/> when the set has the value.</returns>
    [HotPath]
    public bool Contains(int value) => (uint)value < (uint)Capacity && (_words[value >> 6] & (1UL << value)) != 0;

    /// <summary>Adds a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the value was not in the set.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside the capacity.</exception>
    [HotPath]
    public bool Add(int value)
    {
        if ((uint)value >= (uint)Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"The set holds values from 0 to {Capacity - 1}.");
        }

        ref ulong word = ref _words[value >> 6];
        ulong bit = 1UL << value;
        bool added = (word & bit) == 0;
        word |= bit;
        return added;
    }

    /// <summary>Removes a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the value was in the set.</returns>
    [HotPath]
    public bool Remove(int value)
    {
        if ((uint)value >= (uint)Capacity)
        {
            return false;
        }

        ref ulong word = ref _words[value >> 6];
        ulong bit = 1UL << value;
        bool removed = (word & bit) != 0;
        word &= ~bit;
        return removed;
    }

    /// <summary>Removes every value.</summary>
    [HotPath]
    public void Clear() => Array.Clear(_words);

    /// <summary>Adds every value of another set.</summary>
    /// <param name="other">A set whose capacity is at most the capacity of this one.</param>
    /// <exception cref="ArgumentException"><paramref name="other"/> has a larger capacity.</exception>
    [HotPath]
    public void UnionWith(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Capacity > Capacity)
        {
            throw new ArgumentException($"A set of capacity {Capacity} cannot hold the values of a set of capacity {other.Capacity}.", nameof(other));
        }

        ulong[] words = other._words;
        for (int i = 0; i < words.Length; i++)
        {
            _words[i] |= words[i];
        }
    }

    /// <summary>Keeps only the values that are also in another set.</summary>
    /// <param name="other">Any set.</param>
    [HotPath]
    public void IntersectWith(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ulong[] words = other._words;
        for (int i = 0; i < _words.Length; i++)
        {
            _words[i] &= i < words.Length ? words[i] : 0;
        }
    }

    /// <summary>Removes the values that are in another set.</summary>
    /// <param name="other">Any set.</param>
    [HotPath]
    public void ExceptWith(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ulong[] words = other._words;
        int count = Math.Min(_words.Length, words.Length);
        for (int i = 0; i < count; i++)
        {
            _words[i] &= ~words[i];
        }
    }

    /// <summary>Tells whether the two sets have a value in common.</summary>
    /// <param name="other">Any set.</param>
    /// <returns><see langword="true"/> when one value is in both sets.</returns>
    [HotPath]
    public bool Overlaps(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ulong[] words = other._words;
        int count = Math.Min(_words.Length, words.Length);
        for (int i = 0; i < count; i++)
        {
            if ((_words[i] & words[i]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tells whether every value of another set is in this one.</summary>
    /// <param name="other">Any set.</param>
    /// <returns><see langword="true"/> when this set contains <paramref name="other"/>.</returns>
    [HotPath]
    public bool IsSupersetOf(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ulong[] words = other._words;
        for (int i = 0; i < words.Length; i++)
        {
            ulong mine = i < _words.Length ? _words[i] : 0;
            if ((words[i] & ~mine) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets an enumerator over the values, in increasing order.</summary>
    /// <returns>An enumerator that does not allocate.</returns>
    [HotPath]
    public Enumerator GetEnumerator() => new(_words);

    /// <summary>Enumerates the values of a set in increasing order, without allocating.</summary>
    /// <remarks>Do not change the set while it is enumerated.</remarks>
    public struct Enumerator
    {
        private readonly ulong[] _words;
        private int _index;
        private ulong _word;

        [HotPath]
        internal Enumerator(ulong[] words)
        {
            _words = words;
            _index = -1;
            _word = 0;
            Current = -1;
        }

        /// <summary>Gets the current value.</summary>
        public int Current { readonly get; private set; }

        /// <summary>Moves to the next value.</summary>
        /// <returns><see langword="false"/> when there is no other value.</returns>
        [HotPath]
        public bool MoveNext()
        {
            while (_word == 0)
            {
                if (++_index >= _words.Length)
                {
                    return false;
                }

                _word = _words[_index];
            }

            int bit = BitOperations.TrailingZeroCount(_word);
            _word &= _word - 1;
            Current = (_index << 6) + bit;
            return true;
        }
    }
}
