// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Memory;

/// <summary>
/// Keeps objects that are no longer used so that they can be used again,
/// instead of allocating new ones on a hot path.
/// </summary>
/// <typeparam name="T">The type of the objects.</typeparam>
/// <remarks>
/// <para>
/// <see cref="Rent"/> takes a kept object, or creates one when the pool is
/// empty; <see cref="Return"/> resets an object and keeps it. The pool keeps
/// at most <see cref="Capacity"/> objects and drops the others, so it never
/// grows. Once the pool holds as many objects as the code uses at once,
/// renting and returning do not allocate.
/// </para>
/// <para>The pool is not thread-safe: use it from one thread, usually the loop thread of a context.</para>
/// </remarks>
/// <example>
/// <code>
/// var pool = new ObjectPool&lt;List&lt;int&gt;&gt;(() =&gt; new List&lt;int&gt;(64), list =&gt; list.Clear());
/// List&lt;int&gt; changed = pool.Rent();
/// // ...
/// pool.Return(changed);
/// </code>
/// </example>
public sealed class ObjectPool<T>
    where T : class
{
    private readonly Func<T> _create;
    private readonly Action<T>? _reset;
    private readonly T?[] _items;
    private int _count;

    /// <summary>Creates an empty pool.</summary>
    /// <param name="create">Creates an object when the pool is empty.</param>
    /// <param name="reset">Prepares a returned object for its next use, or <see langword="null"/>.</param>
    /// <param name="capacity">The most objects the pool keeps.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public ObjectPool(Func<T> create, Action<T>? reset = null, int capacity = 32)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _create = create;
        _reset = reset;
        _items = new T?[capacity];
    }

    /// <summary>Gets the most objects the pool keeps.</summary>
    public int Capacity => _items.Length;

    /// <summary>Gets the number of objects the pool keeps now.</summary>
    public int Count
    {
        [HotPath]
        get => _count;
    }

    /// <summary>Takes an object from the pool, or creates one when the pool is empty.</summary>
    /// <returns>An object that the caller owns until it returns it.</returns>
    [HotPath]
    public T Rent()
    {
        if (_count == 0)
        {
            return _create();
        }

        T item = _items[--_count]!;
        _items[_count] = null;
        return item;
    }

    /// <summary>Resets an object and keeps it, or drops it when the pool is full.</summary>
    /// <param name="item">An object rented from this pool, which the caller no longer uses.</param>
    [HotPath]
    public void Return(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _reset?.Invoke(item);
        if (_count < _items.Length)
        {
            _items[_count++] = item;
        }
    }
}
