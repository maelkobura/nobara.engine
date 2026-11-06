// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Renderer.Tests;

public sealed class RangeAllocatorTests
{
    [Fact]
    public void RangesAreTakenFirstFitAndMergedWhenFreed()
    {
        var allocator = new RangeAllocator(100);
        Assert.True(allocator.TryAllocate(10, out int a));
        Assert.True(allocator.TryAllocate(20, out int b));
        Assert.True(allocator.TryAllocate(30, out int c));
        Assert.Equal((0, 10, 30), (a, b, c));
        Assert.Equal(60, allocator.Used);
        Assert.Equal(60, allocator.End);

        allocator.Free(b, 20);
        Assert.Equal(2, allocator.FreeRanges);
        Assert.True(allocator.TryAllocate(5, out int d));
        Assert.Equal(10, d);

        allocator.Free(a, 10);
        allocator.Free(d, 5);
        allocator.Free(c, 30);
        Assert.Equal(0, allocator.Used);
        Assert.Equal(1, allocator.FreeRanges);
        Assert.Equal(100, allocator.LargestFree);
        Assert.Equal(0, allocator.End);
    }

    [Fact]
    public void ARangeBelowALimitEndsBeforeIt()
    {
        var allocator = new RangeAllocator(100);
        allocator.TryAllocate(50, out _);
        allocator.TryAllocate(50, out int high);
        allocator.Free(10, 20);
        Assert.False(allocator.TryAllocateBelow(21, high, out _));
        Assert.True(allocator.TryAllocateBelow(20, 30, out int start));
        Assert.Equal(10, start);
    }

    [Fact]
    public void FragmentationIsTheFreeSpaceOutsideTheLargestRange()
    {
        var allocator = new RangeAllocator(100);
        allocator.TryAllocate(100, out _);
        Assert.Equal(0, allocator.Fragmentation);
        allocator.Free(0, 10);
        allocator.Free(50, 30);
        Assert.Equal(1 - (30f / 40), allocator.Fragmentation, 5);
    }

    [Fact]
    public void GrowingAddsFreeElementsAtTheEnd()
    {
        var allocator = new RangeAllocator(10);
        allocator.TryAllocate(8, out _);
        Assert.False(allocator.TryAllocate(5, out _));
        allocator.Grow(20);
        Assert.True(allocator.TryAllocate(5, out int start));
        Assert.Equal(8, start);
        Assert.Equal(20, allocator.Capacity);
    }

    [Fact]
    public void FreeingWhatIsFreeOrOutsideIsRefused()
    {
        var allocator = new RangeAllocator(10);
        allocator.TryAllocate(4, out _);
        Assert.Throws<InvalidOperationException>(() => allocator.Free(2, 4));
        Assert.Throws<InvalidOperationException>(() => allocator.Free(8, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.TryAllocate(0, out _));
    }

    [Fact]
    public void RandomAllocationsNeverOverlap()
    {
        var random = new Random(7);
        var allocator = new RangeAllocator(10_000);
        var live = new List<(int Start, int Length)>();
        for (int step = 0; step < 20_000; step++)
        {
            if (live.Count > 0 && random.Next(3) == 0)
            {
                int i = random.Next(live.Count);
                allocator.Free(live[i].Start, live[i].Length);
                live.RemoveAt(i);
            }
            else
            {
                int length = random.Next(1, 200);
                if (allocator.TryAllocate(length, out int start))
                {
                    live.Add((start, length));
                }
            }

            if (step % 1000 == 0)
            {
                var sorted = live.OrderBy(r => r.Start).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    Assert.True(sorted[i - 1].Start + sorted[i - 1].Length <= sorted[i].Start, $"Ranges at {sorted[i - 1].Start} and {sorted[i].Start} overlap.");
                }

                Assert.Equal(live.Sum(r => r.Length), allocator.Used);
            }
        }
    }
}
