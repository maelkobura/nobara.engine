// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>What the cells of a grid body test against the voxels of another grid.</summary>
public enum VoxelContactMode
{
    /// <summary>The collision boxes of each surface cell: exact to the block, the cost grows with the boxes tested.</summary>
    Blocks,

    /// <summary>The corners of the box of each surface cell: much cheaper, but an edge can go into the voxels between two corners.</summary>
    Corners,
}

/// <summary>The work of the voxel contacts of a grid over the steps since it was last read.</summary>
/// <param name="Pairs">The pairs tested: the grid and a chunk of another grid, or the grid and another grid body.</param>
/// <param name="Tests">The tests made: a box against a box, or a corner against a box.</param>
/// <param name="Deferred">The surface cells whose tests were left for a later step, as their pair had used its budget.</param>
[HotPath]
public readonly record struct VoxelContactCounts(long Pairs, long Tests, long Deferred);

/// <summary>
/// How the body of a grid collides with the voxels of other grids: the
/// terrain, the grids that are not bodies, and the other grid bodies. The
/// module that moves grids keeps it and gives it to nPhysics with
/// <see cref="IPhysicsSystem.SetVoxelCollision"/>; the backend reads it while
/// a world steps, from the threads of its solver, and tests the surface
/// cells of the grid against the voxels of the other grid.
/// </summary>
/// <remarks>
/// <para>
/// A surface cell is a cell of the grid, in the coordinates of the grid,
/// that the other voxels can touch: a block with an open side. Only the cells
/// marked here are tested, as the module that keeps them chose them; a cell
/// without collision box is never tested.
/// </para>
/// <para>
/// Between two grid bodies, the grid with the fewer surface cells is tested
/// against the voxels of the other, in <see cref="VoxelContactMode.Corners"/>
/// when either asks for it, with the smaller budget of the two.
/// </para>
/// <para>
/// Change it on the loop thread only, outside the step: between two ticks, or
/// from a tick system that runs before the step of nPhysics. The counts are
/// written by the backend from the threads of its solver.
/// </para>
/// </remarks>
public sealed class GridVoxelCollision
{
    /// <summary>The number of 64-bit words that hold the surface of a chunk.</summary>
    public const int WordsPerChunk = Chunk.Volume / 64;

    private readonly Dictionary<ChunkCoordinate, ulong[]> _chunks = [];
    private readonly Stack<ulong[]> _spare = [];
    private int _budget;
    private long _pairs;
    private long _tests;
    private long _deferred;
    private int _cursor;

    /// <summary>Gets or sets what the surface cells test.</summary>
    public VoxelContactMode Mode { get; set; }

    /// <summary>Gets or sets the most tests that one pair makes in a step; past it, the remaining cells wait for a later step; 0 for no limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int PairBudget
    {
        get => _budget;
        set => _budget = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The budget of a pair is 0 or more.");
    }

    /// <summary>Gets the number of surface cells.</summary>
    public int SurfaceCount { get; private set; }

    /// <summary>Gets or sets where the next pair that runs out of budget starts, so that the cells it leaves are tested first at a later step; the backend moves it.</summary>
    public int Cursor
    {
        get => Volatile.Read(ref _cursor);
        set => Volatile.Write(ref _cursor, value);
    }

    /// <summary>Marks a cell as a surface cell, or not.</summary>
    /// <param name="cell">The cell, in the coordinates of the grid.</param>
    /// <param name="surface">Whether it is a surface cell.</param>
    /// <remarks>The first surface cell of a chunk takes the words of a chunk that has none left, or allocates them.</remarks>
    public void SetSurface(BlockCoordinate cell, bool surface)
    {
        ChunkCoordinate chunk = VoxelCoordinates.ToChunk(cell);
        int index = VoxelCoordinates.ToIndex(cell);
        ulong bit = 1UL << (index & 63);
        if (!_chunks.TryGetValue(chunk, out ulong[]? words))
        {
            if (!surface)
            {
                return;
            }

            words = _spare.Count > 0 ? _spare.Pop() : new ulong[WordsPerChunk + 1];
            _chunks.Add(chunk, words);
        }

        ref ulong word = ref words[index >> 6];
        if (((word & bit) != 0) == surface)
        {
            return;
        }

        word ^= bit;
        int change = surface ? 1 : -1;
        SurfaceCount += change;
        words[WordsPerChunk] = (ulong)((long)words[WordsPerChunk] + change);   // the count of the chunk
        if (words[WordsPerChunk] == 0)
        {
            _chunks.Remove(chunk);
            _spare.Push(words);
        }
    }

    /// <summary>Gets whether a cell is a surface cell.</summary>
    /// <param name="cell">The cell, in the coordinates of the grid.</param>
    /// <returns><see langword="true"/> for a surface cell.</returns>
    [HotPath]
    public bool IsSurface(BlockCoordinate cell)
    {
        int index = VoxelCoordinates.ToIndex(cell);
        return _chunks.TryGetValue(VoxelCoordinates.ToChunk(cell), out ulong[]? words) && (words[index >> 6] & (1UL << (index & 63))) != 0;
    }

    /// <summary>Gets the surface of a chunk: bit <c>i % 64</c> of word <c>i / 64</c> for the cell of local index <c>i</c> (<see cref="VoxelCoordinates.ToIndex(BlockCoordinate)"/>).</summary>
    /// <param name="chunk">The coordinate of the chunk in the grid.</param>
    /// <returns><see cref="WordsPerChunk"/> words, or none when no cell of the chunk is a surface cell.</returns>
    [HotPath]
    public ReadOnlySpan<ulong> GetSurface(ChunkCoordinate chunk) =>
        _chunks.TryGetValue(chunk, out ulong[]? words) ? new ReadOnlySpan<ulong>(words, 0, WordsPerChunk) : default;

    /// <summary>Unmarks every cell of a chunk.</summary>
    /// <param name="chunk">The coordinate of the chunk in the grid.</param>
    public void ClearSurface(ChunkCoordinate chunk)
    {
        if (_chunks.Remove(chunk, out ulong[]? words))
        {
            SurfaceCount -= (int)words[WordsPerChunk];
            Array.Clear(words);
            _spare.Push(words);
        }
    }

    /// <summary>Unmarks every cell.</summary>
    public void ClearSurface()
    {
        foreach (ulong[] words in _chunks.Values)
        {
            Array.Clear(words);
            _spare.Push(words);
        }

        _chunks.Clear();
        SurfaceCount = 0;
    }

    /// <summary>Adds the work of one pair in a step, from a thread of the solver of the backend.</summary>
    /// <param name="tests">The tests made.</param>
    /// <param name="deferred">The surface cells left for a later step.</param>
    [HotPath]
    public void Count(int tests, int deferred)
    {
        Interlocked.Increment(ref _pairs);
        Interlocked.Add(ref _tests, tests);
        if (deferred > 0)
        {
            Interlocked.Add(ref _deferred, deferred);
        }
    }

    /// <summary>Reads the work since the last read, and starts counting again; on the loop thread, outside the step.</summary>
    /// <returns>The work since the last read.</returns>
    [HotPath]
    public VoxelContactCounts TakeCounts() =>
        new(Interlocked.Exchange(ref _pairs, 0), Interlocked.Exchange(ref _tests, 0), Interlocked.Exchange(ref _deferred, 0));
}
