// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Graphics;

/// <summary>
/// One copy of a resource per frame in flight, so the CPU rewrites the copy
/// of the current frame while the GPU still reads the others.
/// </summary>
/// <typeparam name="T">The type of the copies, such as <see cref="GpuBuffer"/>.</typeparam>
/// <example>
/// <code>
/// var constants = new PerFrame&lt;GpuBuffer&gt;(device, _ => device.CreateBuffer(new BufferDescription(256, BufferUsage.Uniform)));
/// device.UpdateBuffer(constants.Current, 0, bytes);
/// </code>
/// </example>
public sealed class PerFrame<T> : IDisposable
    where T : class
{
    private readonly GraphicsDevice _device;
    private readonly T[] _items;

    /// <summary>Creates one copy per frame in flight.</summary>
    /// <param name="device">The device.</param>
    /// <param name="create">Creates the copy of a slot, from 0 to <see cref="GraphicsDevice.FramesInFlight"/> - 1.</param>
    public PerFrame(GraphicsDevice device, Func<int, T> create)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(create);
        _device = device;
        _items = new T[device.FramesInFlight];
        for (int slot = 0; slot < _items.Length; slot++)
        {
            _items[slot] = create(slot);
        }
    }

    /// <summary>Gets the copy of the current frame.</summary>
    public T Current
    {
        [HotPath]
        get => _items[_device.FrameIndex];
    }

    /// <summary>Gets the number of copies: <see cref="GraphicsDevice.FramesInFlight"/>.</summary>
    public int Count => _items.Length;

    /// <summary>Gets the copy of a slot.</summary>
    /// <param name="slot">The slot.</param>
    public T this[int slot] => _items[slot];

    /// <summary>Disposes the copies that are disposable.</summary>
    public void Dispose()
    {
        foreach (T item in _items)
        {
            (item as IDisposable)?.Dispose();
        }
    }
}
