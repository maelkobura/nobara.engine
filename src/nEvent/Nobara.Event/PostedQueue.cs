// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Event;

/// <summary>The deferred events of one type, as the bus sees them without knowing the type.</summary>
internal interface IPostedQueue
{
    /// <summary>Reads the post order of the oldest event, when it is ready to be handed out.</summary>
    [HotPath]
    bool TryPeekOrder(out long order);

    /// <summary>Takes the oldest event out of the queue and emits it.</summary>
    [HotPath]
    void DispatchHead(EventBus bus);
}

/// <summary>
/// A fixed-capacity queue that any number of threads post to and the loop
/// thread reads, without a lock or an allocation. Each slot carries a
/// sequence number that says whose turn it is: a producer claims a slot by
/// advancing the tail, writes it, then publishes it with the sequence number.
/// </summary>
internal sealed class PostedQueue<TEvent> : IPostedQueue
    where TEvent : struct
{
    private readonly Slot[] _slots;
    private readonly long _mask;
    private long _tail;
    private long _head;

    /// <param name="capacity">The number of events the queue holds; a power of two.</param>
    public PostedQueue(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity of a queue is a power of two, at least 2.");
        }

        _slots = new Slot[capacity];
        _mask = capacity - 1;
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Sequence = i;
        }
    }

    /// <summary>Appends an event. Any thread.</summary>
    /// <returns><see langword="false"/> when the queue is full.</returns>
    [HotPath]
    public bool TryEnqueue(in TEvent value, long order)
    {
        long position = Volatile.Read(ref _tail);
        while (true)
        {
            ref Slot slot = ref _slots[(int)(position & _mask)];
            long difference = Volatile.Read(ref slot.Sequence) - position;
            if (difference == 0)
            {
                if (Interlocked.CompareExchange(ref _tail, position + 1, position) == position)
                {
                    slot.Value = value;
                    slot.Order = order;
                    Volatile.Write(ref slot.Sequence, position + 1);
                    return true;
                }

                position = Volatile.Read(ref _tail);
            }
            else if (difference < 0)
            {
                return false;
            }
            else
            {
                position = Volatile.Read(ref _tail);
            }
        }
    }

    [HotPath]
    public bool TryPeekOrder(out long order)
    {
        ref Slot slot = ref _slots[(int)(_head & _mask)];
        if (Volatile.Read(ref slot.Sequence) == _head + 1)
        {
            order = slot.Order;
            return true;
        }

        order = 0;
        return false;
    }

    [HotPath]
    public void DispatchHead(EventBus bus)
    {
        long position = _head;
        ref Slot slot = ref _slots[(int)(position & _mask)];
        TEvent value = slot.Value;
        slot.Value = default;
        Volatile.Write(ref slot.Sequence, position + _mask + 1);
        _head = position + 1;
        bus.Emit(ref value);
    }

    private struct Slot
    {
        public long Sequence;
        public long Order;
        public TEvent Value;
    }
}
