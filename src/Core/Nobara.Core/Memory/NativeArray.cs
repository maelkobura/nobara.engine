// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;

namespace Nobara.Core.Memory;

/// <summary>
/// A fixed-length array of unmanaged values that lives outside the managed
/// heap, so the garbage collector neither scans nor moves it.
/// </summary>
/// <typeparam name="T">The type of the elements: a type without references.</typeparam>
/// <remarks>
/// <para>
/// Use it for large data that lives long, such as the cells of a grid or a
/// buffer that jobs fill. Creating the array allocates the small managed
/// object that owns the memory; reading and writing it do not allocate.
/// </para>
/// <para>
/// Dispose the array when it is no longer used. An array that is not disposed
/// frees its memory when the garbage collector finalizes it. The array is not
/// thread-safe: jobs that share one declare their accesses (see
/// <see cref="Jobs.JobAccess"/>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var heights = new NativeArray&lt;float&gt;(256 * 256);
/// heights[0] = 64f;
/// Span&lt;float&gt; row = heights.AsSpan(0, 256);
/// </code>
/// </example>
public sealed unsafe class NativeArray<T> : IDisposable
    where T : unmanaged
{
    private T* _items;
    private int _length;

    /// <summary>Allocates an array.</summary>
    /// <param name="length">The number of elements.</param>
    /// <param name="clear">Whether the elements start at zero. Without it, their content is undefined until written.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public NativeArray(int length, bool clear = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
        nuint size = (nuint)sizeof(T);
        _items = (T*)(clear ? NativeMemory.AllocZeroed((nuint)Math.Max(length, 1), size) : NativeMemory.Alloc((nuint)Math.Max(length, 1), size));
    }

    /// <summary>Frees the memory of an array that was not disposed.</summary>
    ~NativeArray()
    {
        Free();
    }

    /// <summary>Gets the number of elements, or 0 once the array is disposed.</summary>
    public int Length
    {
        [HotPath]
        get => _length;
    }

    /// <summary>Gets whether the array is disposed.</summary>
    public bool IsDisposed
    {
        [HotPath]
        get => _items == null;
    }

    /// <summary>Gets a reference to an element.</summary>
    /// <param name="index">The position of the element.</param>
    /// <returns>A reference to the element, valid until the array is disposed.</returns>
    /// <exception cref="ObjectDisposedException">The array is disposed.</exception>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the array.</exception>
    public ref T this[int index]
    {
        [HotPath]
        get
        {
            if ((uint)index >= (uint)_length)
            {
                throw _items == null
                    ? new ObjectDisposedException(nameof(NativeArray<>))
                    : new IndexOutOfRangeException($"Index {index} is outside a native array of {_length} elements.");
            }

            return ref _items[index];
        }
    }

    /// <summary>Gets the elements as a span.</summary>
    /// <returns>A span over every element, valid until the array is disposed.</returns>
    /// <exception cref="ObjectDisposedException">The array is disposed.</exception>
    [HotPath]
    public Span<T> AsSpan() => _items != null ? new Span<T>(_items, _length) : throw new ObjectDisposedException(nameof(NativeArray<>));

    /// <summary>Gets part of the elements as a span.</summary>
    /// <param name="start">The position of the first element.</param>
    /// <param name="length">The number of elements.</param>
    /// <returns>A span over the elements, valid until the array is disposed.</returns>
    /// <exception cref="ObjectDisposedException">The array is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the array.</exception>
    [HotPath]
    public Span<T> AsSpan(int start, int length)
    {
        if (_items == null)
        {
            throw new ObjectDisposedException(nameof(NativeArray<>));
        }

        if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"The range {start}..{start + length} is outside a native array of {_length} elements.");
        }

        return new Span<T>(_items + start, length);
    }

    /// <summary>Sets every element to zero.</summary>
    /// <exception cref="ObjectDisposedException">The array is disposed.</exception>
    [HotPath]
    public void Clear() => AsSpan().Clear();

    /// <summary>Frees the memory. Disposing the array again does nothing.</summary>
    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        T* items = _items;
        _items = null;
        _length = 0;
        if (items != null)
        {
            NativeMemory.Free(items);
        }
    }
}
