// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Core;
using Nobara.Game;

namespace Nobara.Voxel;

/// <summary>
/// The block ticks of one context: scheduled ticks in a queue per chunk, and
/// random ticks of active chunks. Resolve it with
/// <c>context.Resolve&lt;IBlockTicks&gt;()</c>.
/// </summary>
/// <remarks>
/// Both run at the <c>World</c> stage, after the batches of the tick, in
/// active chunks only, and are delivered by the <c>ScheduledTick</c> and
/// <c>RandomTick</c> hooks of nBlocks to the subscribers of the state the
/// cell holds when the tick is delivered. A dimension whose clock is frozen
/// delivers nothing; a chunk held at <see cref="TicketLevel.ReducedActive"/>
/// is ticked once every <see cref="VoxelSettings.ReducedTickInterval"/> ticks.
/// The batches that subscribers submit apply at the next tick.
/// </remarks>
public interface IBlockTicks
{
    /// <summary>Schedules a tick of a block.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="position">The block.</param>
    /// <param name="delay">The number of ticks of the clock of the dimension before the tick, 1 or more.</param>
    /// <returns><see langword="false"/> when the chunk of the block is not loaded: nothing is scheduled.</returns>
    /// <remarks>A tick already scheduled for the same block at the same clock tick is not added again. The queue of a chunk is dropped when the chunk is unloaded.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The delay is 0.</exception>
    bool Schedule(Grid grid, BlockCoordinate position, ulong delay);

    /// <summary>Gets the number of ticks scheduled in a chunk.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="chunk">The chunk.</param>
    /// <returns>The number of ticks waiting in its queue.</returns>
    int CountScheduled(Grid grid, ChunkCoordinate chunk);
}

/// <summary>A scheduled tick waiting in the queue of its chunk.</summary>
internal readonly record struct ScheduledBlockTick(ulong Target, ulong Sequence, int LocalIndex);

/// <summary>The scheduled and random block ticks.</summary>
internal sealed class BlockTicks : IBlockTicks
{
    private readonly ChunkLoader _loader;
    private readonly ScheduledSystem _scheduled;
    private readonly RandomSystem _random;
    private IBlockHooks _hooks = null!;
    private IGameTime _time = null!;
    private ulong _sequence;
    private ulong _random64 = 0x9E3779B97F4A7C15UL;

    public BlockTicks(ChunkLoader loader)
    {
        _loader = loader;
        _scheduled = new ScheduledSystem(this);
        _random = new RandomSystem(this);
    }

    public void Initialize(ModuleContext module, VoxelWorlds worlds, IBlockHooks hooks, IGameTime time, ITickSchedule schedule)
    {
        _hooks = hooks;
        _time = time;
        schedule.AddSystem(module, "nobara.voxel.scheduled_ticks", TickStages.World, _scheduled, new TickSystemOptions
        {
            After = ["nobara.voxel.batches"],
            Reads = [worlds.ChunkAccess],
        });
        schedule.AddSystem(module, "nobara.voxel.random_ticks", TickStages.World, _random, new TickSystemOptions
        {
            After = ["nobara.voxel.scheduled_ticks"],
            Reads = [worlds.ChunkAccess],
        });
    }

    public bool Schedule(Grid grid, BlockCoordinate position, ulong delay)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (delay == 0) throw new ArgumentOutOfRangeException(nameof(delay), "A scheduled tick comes at least one tick later.");
        ChunkCoordinate chunk = VoxelCoordinates.ToChunk(position);
        if (!_loader.TryGetEntry(grid, chunk, out ChunkEntry? entry) || entry!.State != ChunkLoadState.Loaded) return false;
        ulong target = checked(_time.GetClock(grid.Dimension) + delay);
        int index = VoxelCoordinates.ToIndex(position);
        List<ScheduledBlockTick> queue = entry.Scheduled ??= [];

        // The queue is sorted by decreasing target, then decreasing sequence: the next tick is at the end.
        foreach (ScheduledBlockTick waiting in queue)
        {
            if (waiting.Target == target && waiting.LocalIndex == index) return true;
        }

        var tick = new ScheduledBlockTick(target, _sequence++, index);
        int at = queue.Count;
        while (at > 0 && Before(tick, queue[at - 1])) at--;
        queue.Insert(at, tick);
        return true;
    }

    public int CountScheduled(Grid grid, ChunkCoordinate chunk) =>
        _loader.TryGetEntry(grid, chunk, out ChunkEntry? entry) && entry!.Scheduled is { } queue ? queue.Count : 0;

    // Whether a comes later than b, and so before it in the queue.
    [HotPath]
    private static bool Before(ScheduledBlockTick a, ScheduledBlockTick b) => a.Target > b.Target || (a.Target == b.Target && a.Sequence > b.Sequence);

    [HotPath]
    private bool Skips(ChunkEntry entry, ulong tick) =>
        _time.IsFrozen(entry.Grid.Dimension) || (entry.Active == 0 && tick % (ulong)_loader.ReducedTickInterval != 0);

    [HotPath]
    private void RunScheduled(ulong tick)
    {
        List<ChunkEntry> active = _loader.ActiveChunks;
        for (int i = 0; i < active.Count; i++)
        {
            ChunkEntry entry = active[i];
            if (entry.Scheduled is not { Count: > 0 } queue || entry.Chunk is not { } chunk || Skips(entry, tick)) continue;
            ulong clock = _time.GetClock(entry.Grid.Dimension);
            BlockCoordinate corner = VoxelCoordinates.ToBlock(entry.Coordinate, new LocalBlockCoordinate(0, 0, 0));
            while (queue.Count > 0 && queue[^1].Target <= clock && entry.Chunk is not null)
            {
                ScheduledBlockTick due = queue[^1];
                queue.RemoveAt(queue.Count - 1);
                uint state = chunk.GetState(due.LocalIndex);
                if (state == 0 || !_hooks.HasSubscribers(_hooks.ScheduledTick, state)) continue;
                _hooks.Fire(_hooks.ScheduledTick, state, new BlockTick(Position(entry.Grid, corner, due.LocalIndex), state));
            }
        }
    }

    [HotPath]
    internal void RunRandom(ulong tick)
    {
        int draws = _loader.RandomTicksPerChunk;
        if (draws == 0) return;
        List<ChunkEntry> active = _loader.ActiveChunks;
        for (int i = 0; i < active.Count; i++)
        {
            ChunkEntry entry = active[i];
            if (entry.Chunk is not { } chunk || Skips(entry, tick) || !HasRandomSubscribers(chunk.Palette)) continue;
            BlockCoordinate corner = VoxelCoordinates.ToBlock(entry.Coordinate, new LocalBlockCoordinate(0, 0, 0));
            for (int draw = 0; draw < draws && entry.Chunk is not null; draw++)
            {
                int index = (int)(Next() & (Chunk.Volume - 1));
                uint state = chunk.GetState(index);
                if (state == 0 || !_hooks.HasSubscribers(_hooks.RandomTick, state)) continue;
                _hooks.Fire(_hooks.RandomTick, state, new BlockTick(Position(entry.Grid, corner, index), state));
            }
        }
    }

    [HotPath]
    private bool HasRandomSubscribers(Palette palette)
    {
        for (int i = 0; i < palette.Count; i++)
        {
            uint state = palette.GetEntry(i);
            if (state != 0 && _hooks.HasSubscribers(_hooks.RandomTick, state)) return true;
        }

        return false;
    }

    [HotPath]
    private static BlockPosition Position(Grid grid, BlockCoordinate corner, int index) =>
        new(grid.Identifier, corner.X + (index & 31), corner.Y + (index >> 10), corner.Z + ((index >> 5) & 31));

    // xorshift64*: deterministic for a context, the same from one run to the next.
    [HotPath]
    private ulong Next()
    {
        ulong x = _random64;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _random64 = x;
        return (x * 0x2545F4914F6CDD1DUL) >> 32;
    }

    private sealed class ScheduledSystem(BlockTicks ticks) : ITickSystem
    {
        [HotPath]
        public void Run(in TickTime time) => ticks.RunScheduled(time.Tick);
    }

    private sealed class RandomSystem(BlockTicks ticks) : ITickSystem
    {
        [HotPath]
        public void Run(in TickTime time) => ticks.RunRandom(time.Tick);
    }
}
