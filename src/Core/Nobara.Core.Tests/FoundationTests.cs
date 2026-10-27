// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Collections;
using Nobara.Core.Mathematics;
using Nobara.Core.Memory;

namespace Nobara.Core.Tests;

/// <summary>The low-level foundations: unmanaged arrays, pools, bit sets, and integer math.</summary>
public sealed class FoundationTests
{
    [Fact]
    public void A_native_array_starts_at_zero_and_reads_and_writes_its_elements()
    {
        using var array = new NativeArray<long>(100);

        Assert.Equal(100, array.Length);
        Assert.All(array.AsSpan().ToArray(), value => Assert.Equal(0, value));
        array[3] = 42;
        array[99] += 1;
        Assert.Equal(42, array.AsSpan()[3]);
        Assert.Equal(1, array.AsSpan(98, 2)[1]);

        array.Clear();
        Assert.Equal(0, array[3]);
    }

    [Fact]
    public void A_native_array_refuses_indices_outside_it_and_use_after_dispose()
    {
        var array = new NativeArray<int>(4);

        Assert.Throws<IndexOutOfRangeException>(() => array[4]);
        Assert.Throws<IndexOutOfRangeException>(() => array[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => array.AsSpan(3, 2));

        array.Dispose();
        array.Dispose();
        Assert.True(array.IsDisposed);
        Assert.Equal(0, array.Length);
        Assert.Throws<ObjectDisposedException>(() => array[0]);
        Assert.Throws<ObjectDisposedException>(() => array.AsSpan());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeArray<int>(-1));
    }

    [Fact]
    public void An_empty_native_array_has_an_empty_span()
    {
        using var array = new NativeArray<int>(0);
        Assert.True(array.AsSpan().IsEmpty);
        Assert.False(array.IsDisposed);
    }

    [Fact]
    public void A_pool_reuses_returned_objects_resets_them_and_drops_those_beyond_its_capacity()
    {
        int created = 0;
        var pool = new ObjectPool<List<int>>(() => { created++; return []; }, list => list.Clear(), capacity: 2);

        List<int> first = pool.Rent();
        first.Add(1);
        pool.Return(first);
        Assert.Same(first, pool.Rent());
        Assert.Empty(first);

        pool.Return(first);
        pool.Return(new List<int>());
        pool.Return(new List<int>());
        Assert.Equal(2, pool.Count);
        Assert.Equal(1, created);
    }

    [Fact]
    public void A_bit_set_adds_removes_and_enumerates_values_in_order()
    {
        var set = new BitSet(130);

        Assert.True(set.IsEmpty);
        Assert.True(set.Add(0));
        Assert.True(set.Add(64));
        Assert.True(set.Add(129));
        Assert.False(set.Add(64));
        Assert.True(set.Contains(129));
        Assert.False(set.Contains(130));
        Assert.False(set.Contains(-1));
        Assert.Equal(3, set.Count);

        var values = new List<int>();
        foreach (int value in set)
        {
            values.Add(value);
        }

        Assert.Equal([0, 64, 129], values);
        Assert.True(set.Remove(64));
        Assert.False(set.Remove(64));
        Assert.False(set.Remove(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Add(130));

        set.Clear();
        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void Bit_sets_combine()
    {
        var wood = new BitSet(100);
        var fuel = new BitSet(70);
        wood.Add(1);
        wood.Add(2);
        fuel.Add(2);
        fuel.Add(69);

        Assert.True(wood.Overlaps(fuel));
        Assert.False(wood.IsSupersetOf(fuel));

        var union = new BitSet(100);
        union.UnionWith(wood);
        union.UnionWith(fuel);
        Assert.Equal(3, union.Count);
        Assert.True(union.IsSupersetOf(fuel));
        Assert.Throws<ArgumentException>(() => fuel.UnionWith(union));

        var both = new BitSet(100);
        both.UnionWith(wood);
        both.IntersectWith(fuel);
        Assert.Equal([2], Values(both));

        wood.ExceptWith(fuel);
        Assert.Equal([1], Values(wood));

        static List<int> Values(BitSet set)
        {
            var values = new List<int>();
            foreach (int value in set)
            {
                values.Add(value);
            }

            return values;
        }
    }

    [Theory]
    [InlineData(-1, 16, -1, 15)]
    [InlineData(-16, 16, -1, 0)]
    [InlineData(-17, 16, -2, 15)]
    [InlineData(0, 16, 0, 0)]
    [InlineData(17, 16, 1, 1)]
    public void Floor_division_and_modulo_round_toward_negative_infinity(int value, int divisor, int quotient, int remainder)
    {
        Assert.Equal(quotient, IntMath.DivFloor(value, divisor));
        Assert.Equal(remainder, IntMath.ModFloor(value, divisor));
    }

    [Fact]
    public void Ceiling_division_counts_batches()
    {
        Assert.Equal(0, IntMath.DivCeil(0, 64));
        Assert.Equal(1, IntMath.DivCeil(1, 64));
        Assert.Equal(2, IntMath.DivCeil(65, 64));
        Assert.Equal(int.MaxValue, IntMath.DivCeil(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.DivCeil(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.DivFloor(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.ModFloor(1, -2));
    }

    [Fact]
    public void Hot_path_foundations_allocate_nothing()
    {
        using var array = new NativeArray<int>(256);
        var set = new BitSet(256);
        var other = new BitSet(256);
        var pool = new ObjectPool<object>(() => new object(), capacity: 4);
        pool.Return(pool.Rent());
        other.Add(5);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 1000; i++)
        {
            array[i & 255] = i;
            total += array.AsSpan(0, 16)[0] + array.Length;
            array.Clear();

            set.Add(i & 255);
            set.UnionWith(other);
            set.IntersectWith(other);
            set.ExceptWith(other);
            total += set.Count + (set.Contains(i & 255) ? 1 : 0) + (set.Overlaps(other) ? 1 : 0) + (set.IsSupersetOf(other) ? 1 : 0) + (set.IsEmpty ? 1 : 0);
            foreach (int value in other)
            {
                total += value;
            }

            set.Remove(5);
            set.Clear();

            pool.Return(pool.Rent());
            total += IntMath.DivFloor(-i, 16) + IntMath.ModFloor(-i, 16) + IntMath.DivCeil(i, 16);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0, total);
    }
}
