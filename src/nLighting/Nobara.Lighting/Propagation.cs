// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>
/// The propagation of the light by queues, on four components (sky, red,
/// green, blue): removal rounds while any removal remains, then addition
/// rounds, each a job with one lane per index. A front that crosses a face of
/// its chunk waits for the next round, in the work of the chunk on that side;
/// a front toward a chunk that is not loaded is dropped, and the border it
/// came from is spread again when that chunk loads.
/// </summary>
/// <remarks>
/// <para>
/// The result is the only fixed point of "each block holds the brightest of
/// its sources and of its neighbors' light minus one level and its filter",
/// whatever the order of the rounds and of the cells: two contexts that apply
/// the same changes hold the same light. Only integers are used. The sources
/// are the emission of the block and, for the sky component, 15 above the
/// highest opaque block of the column.
/// </para>
/// <para>
/// A pass may stop when its budget of operations runs out and go on at the
/// next tick; changes that arrive in between join it. A removal that crosses
/// a face also drops the light that waits on that face and may have come from
/// the cells it removes, so no stale front lights a block later.
/// </para>
/// </remarks>
internal sealed class Propagator
{
    /// <summary>The fewest operations a lane may do in a round, so that a small remaining budget still makes progress.</summary>
    public const int MinimumQuota = 1024;

    private readonly ModuleContext _context;
    private readonly LightSourceRegistry _sources;
    private readonly IShapeRegistry _shapes;
    private readonly JobData _blocks;
    private readonly JobData _light;
    private readonly List<LightChunk> _active = [];
    private readonly List<LightChunk> _work = [];
    private readonly Stack<WorkState> _pool = new();
    private readonly Scratch?[] _scratch;

    // A cached delegate: sorting with an IComparer would create one at each call.
    private static readonly Comparison<LightChunk> ByPriority = static (x, y) => x.Priority.CompareTo(y.Priority);

    public Propagator(ModuleContext context, LightSourceRegistry sources, IShapeRegistry shapes, JobData blocks, JobData light)
    {
        _context = context;
        _sources = sources;
        _shapes = shapes;
        _blocks = blocks;
        _light = light;
        // One lane per worker, and one for the loop thread, which runs jobs while it waits; each lane takes its queues at its first use.
        _scratch = new Scratch[context.Jobs.WorkerCount + 1];
    }

    /// <summary>Gets the chunks that have work, or that changed in the current tick.</summary>
    public List<LightChunk> Active => _active;

    /// <summary>Gets the rounds of jobs of the last run.</summary>
    public int LastRounds { get; private set; }

    /// <summary>Gets or sets the number of the current tick, stamped on the work that chunks get.</summary>
    public long Tick { get; set; }

    /// <summary>Gets the work of a chunk, taking it from the pool when it has none.</summary>
    public WorkState Work(LightChunk chunk)
    {
        if (chunk.Work is { } work) return work;
        work = _pool.Count > 0 ? _pool.Pop() : new WorkState();
        work.Since = Tick;
        chunk.Work = work;
        _active.Add(chunk);
        return work;
    }

    /// <summary>Drops the work of a chunk that leaves memory.</summary>
    public void Forget(LightChunk chunk)
    {
        if (chunk.Work is not { } work) return;
        work.Clear();
        _pool.Push(work);
        chunk.Work = null;
        _active.Remove(chunk);
    }

    /// <summary>Drops every work, at the end of a session.</summary>
    public void Clear()
    {
        foreach (LightChunk chunk in _active)
        {
            if (chunk.Work is not { } work) continue;
            work.Clear();
            _pool.Push(work);
            chunk.Work = null;
        }

        _active.Clear();
    }

    /// <summary>
    /// Runs rounds until no front remains or the budget is spent. Call it on
    /// the loop thread, inside the accesses of the tick system.
    /// </summary>
    /// <param name="budget">The operations the run may do; <see cref="long.MaxValue"/> for no limit. A round that starts may go past it by a little.</param>
    /// <returns>The operations done.</returns>
    public long Run(long budget)
    {
        int rounds = 0;
        long used = 0;
        while (used < budget)
        {
            bool removal = Collect(removal: true);
            if (!removal && !Collect(removal: false)) break;
            used += Execute(removal, budget - used);
            Merge(removal);
            rounds++;
        }

        LastRounds = rounds;
        return used;
    }

    /// <summary>Returns the works of the chunks that have none left to the pool, once the changes of the tick are read.</summary>
    public void Release()
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            LightChunk chunk = _active[i];
            if (chunk.Work is { } work)
            {
                if (work.HasRemovalWork || work.HasAdditionWork) continue;
                work.Clear();
                _pool.Push(work);
                chunk.Work = null;
            }

            _active.RemoveAt(i);
        }
    }

    [HotPath]
    internal void RunLane(int lane, int lanes, bool removal)
    {
        Scratch scratch = _scratch[lane]!;
        for (int i = lane; i < _work.Count; i += lanes)
        {
            if (scratch.IsSpent) return;
            LightChunk chunk = _work[i];
            if (removal) Remove(chunk, chunk.Work!, scratch);
            else Add(chunk, chunk.Work!, scratch);
        }
    }

    private bool Collect(bool removal)
    {
        _work.Clear();
        foreach (LightChunk chunk in _active)
        {
            WorkState? work = chunk.Work;
            if (work is not null && (removal ? work.HasRemovalWork : work.HasAdditionWork)) _work.Add(chunk);
        }

        if (_work.Count == 0) return false;

        // The nearest chunks first: lane i takes chunks i, i + lanes, and so on, and stops when its share of the budget is spent.
        _work.Sort(ByPriority);
        return true;
    }

    private long Execute(bool removal, long remaining)
    {
        int lanes = Math.Min(_work.Count, _scratch.Length);
        int quota = remaining >= int.MaxValue ? int.MaxValue : (int)Math.Max(MinimumQuota, (remaining + lanes - 1) / lanes);
        for (int i = 0; i < lanes; i++)
        {
            Scratch scratch = _scratch[i] ??= new Scratch();
            scratch.Operations = 0;
            scratch.Quota = quota;
        }

        var job = new PropagationJob(this, lanes, removal);
        _context.Jobs.ScheduleFor(job, lanes, 1, new JobAccess([_blocks], [_light])).Complete();
        long used = 0;
        for (int i = 0; i < lanes; i++) used += _scratch[i]!.Operations;
        return used;
    }

    // Hands the fronts that left each chunk to the chunk on that side.
    private void Merge(bool removal)
    {
        foreach (LightChunk chunk in _work)
        {
            WorkState work = chunk.Work!;
            if (!(removal ? work.HasOutgoingRemoval : work.HasOutgoingAdd)) continue;
            ushort[] outgoing = removal ? work.OutgoingRemoval : work.OutgoingAdd;
            for (int face = 0; face < 6; face++)
            {
                Span<ushort> leaving = outgoing.AsSpan(face * Faces.Cells, Faces.Cells);
                LightChunk? neighbor = chunk.Neighbors[face];
                if (neighbor is not null && leaving.IndexOfAnyExcept((ushort)0) >= 0)
                {
                    WorkState target = Work(neighbor);
                    Span<ushort> entering = (removal ? target.IncomingRemoval : target.IncomingAdd).AsSpan(Faces.Opposite(face) * Faces.Cells, Faces.Cells);
                    for (int slot = 0; slot < Faces.Cells; slot++)
                    {
                        if (leaving[slot] != 0) entering[slot] = Faces.Max(entering[slot], leaving[slot]);
                    }

                    if (removal) target.HasIncomingRemoval = true;
                    else target.HasIncomingAdd = true;
                }

                leaving.Clear();
            }

            if (removal) work.HasOutgoingRemoval = false;
            else work.HasOutgoingAdd = false;
        }
    }

    // The removal phase of one chunk: what its lane left last time, its seeds, the removals that entered it, then its queue.
    [HotPath]
    private void Remove(LightChunk chunk, WorkState work, Scratch scratch)
    {
        _light.CheckWrite();
        List<uint> queued = work.RemovalQueue;
        for (int i = 0; i < queued.Count; i++) scratch.PushRemoval((int)(queued[i] & 0xFFFF), (ushort)(queued[i] >> 16));
        queued.Clear();

        List<uint> seeds = work.Seeds;
        int seed = 0;
        for (; seed < seeds.Count && !scratch.IsSpent; seed++)
        {
            int index = (int)(seeds[seed] & 0xFFFF);
            ushort removed = (ushort)(seeds[seed] >> 16);
            scratch.Operations++;

            // A changed cell may now close a face that its light crossed: its removal leaves through every face.
            for (int face = 0; face < 6; face++) RemoveToward(chunk, work, scratch, index, face, removed);
        }

        if (seed == seeds.Count) seeds.Clear();
        else seeds.RemoveRange(0, seed);

        if (work.HasIncomingRemoval)
        {
            ushort[] incoming = work.IncomingRemoval;
            ushort[] waiting = work.IncomingAdd;
            for (int face = 0; face < 6; face++)
            {
                int start = face * Faces.Cells;
                for (int slot = 0; slot < Faces.Cells; slot++)
                {
                    ushort removed = incoming[start + slot];
                    if (removed == 0) continue;
                    incoming[start + slot] = 0;

                    // Light that waits on this face and is dimmer than the removed value may have come from the removed cell.
                    ref ushort front = ref waiting[start + slot];
                    if (front != 0) front = Faces.KeepAtLeast(front, removed);
                    int index = Faces.Cell(face, slot);
                    if (!IsOpen(chunk, index, face)) continue;
                    RemoveAt(chunk, work, scratch, index, removed);
                }
            }

            work.HasIncomingRemoval = false;
        }

        while (scratch.HasRemovals)
        {
            if (scratch.IsSpent)
            {
                // Out of budget: the rest of the queue waits in the chunk for the next round or tick.
                while (scratch.HasRemovals)
                {
                    int left = scratch.PopRemoval(out ushort value);
                    queued.Add((uint)left | ((uint)value << 16));
                }

                return;
            }

            int index = scratch.PopRemoval(out ushort removed);
            scratch.Operations++;
            int faces = ClosedFaces(chunk, index);
            for (int face = 0; face < 6; face++)
            {
                if (faces != 0x3F && (faces & (1 << face)) != 0) continue;
                RemoveToward(chunk, work, scratch, index, face, removed);
            }
        }
    }

    [HotPath]
    private void RemoveToward(LightChunk chunk, WorkState work, Scratch scratch, int index, int face, ushort removed)
    {
        if (Faces.Step(index, face, out int neighbor))
        {
            if (IsOpen(chunk, neighbor, Faces.Opposite(face))) RemoveAt(chunk, work, scratch, neighbor, removed);
            return;
        }

        ref ushort leaving = ref work.OutgoingRemoval[(face * Faces.Cells) + Faces.Slot(face, index)];
        leaving = Faces.Max(leaving, removed);
        work.HasOutgoingRemoval = true;
    }

    // A cell reached by a removal: each component dimmer than the removed one came from it and goes, the others spread again.
    [HotPath]
    private void RemoveAt(LightChunk chunk, WorkState work, Scratch scratch, int index, ushort removed)
    {
        ushort current = chunk.Get(index);
        if (current == 0) return;
        ushort gone = 0;
        bool spread = false;
        for (int shift = 0; shift < 16; shift += 4)
        {
            int level = (removed >> shift) & 15;
            int here = (current >> shift) & 15;
            if (level == 0 || here == 0) continue;
            if (here < level) gone |= (ushort)(here << shift);
            else spread = true;
        }

        if (gone != 0)
        {
            ushort mask = Mask(gone);
            ushort kept = (ushort)(current & ~mask);
            ushort source = (ushort)(SourceOf(chunk, index) & mask);
            if (source != 0)
            {
                kept |= source;
                spread = true;
            }

            chunk.Set(index, kept);
            scratch.PushRemoval(index, gone);
        }

        if (spread) work.MarkPending(index);
    }

    // The addition phase of one chunk: the light that entered it, its pending cells, then its queue.
    [HotPath]
    private void Add(LightChunk chunk, WorkState work, Scratch scratch)
    {
        _light.CheckWrite();
        if (work.HasIncomingAdd)
        {
            ushort[] incoming = work.IncomingAdd;
            for (int face = 0; face < 6; face++)
            {
                int start = face * Faces.Cells;
                for (int slot = 0; slot < Faces.Cells; slot++)
                {
                    ushort light = incoming[start + slot];
                    if (light == 0) continue;
                    incoming[start + slot] = 0;
                    int index = Faces.Cell(face, slot);
                    uint state = chunk.Blocks.GetState(index);
                    ref readonly StateLight entered = ref _sources.States[state];
                    if ((Closed(chunk, index, state, entered) & (1 << face)) != 0) continue;
                    Raise(chunk, scratch, index, Faces.Subtract(light, entered.Filter, 0));
                }
            }

            work.HasIncomingAdd = false;
        }

        if (work.HasPending)
        {
            ulong[] pending = work.Pending;
            for (int word = 0; word < pending.Length; word++)
            {
                ulong bits = pending[word];
                if (bits == 0) continue;
                pending[word] = 0;
                while (bits != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    scratch.PushAddition((word << 6) + bit);
                }
            }

            work.HasPending = false;
        }

        while (scratch.HasAdditions)
        {
            if (scratch.IsSpent)
            {
                // Out of budget: the cells left in the queue spread their light at the next round or tick.
                while (scratch.HasAdditions) work.MarkPending(scratch.PopAddition());
                return;
            }

            int index = scratch.PopAddition();
            scratch.Operations++;
            ushort light = chunk.Get(index);
            if (light == 0) continue;
            int faces = ClosedFaces(chunk, index);
            for (int face = 0; face < 6; face++)
            {
                // A block closed on six faces lets its own light out, never light in.
                if (faces != 0x3F && (faces & (1 << face)) != 0) continue;
                if (Faces.Step(index, face, out int neighbor))
                {
                    uint state = chunk.Blocks.GetState(neighbor);
                    ref readonly StateLight entered = ref _sources.States[state];
                    if ((Closed(chunk, neighbor, state, entered) & (1 << Faces.Opposite(face))) != 0) continue;
                    Raise(chunk, scratch, neighbor, Faces.Subtract(light, entered.Filter, 1));
                    continue;
                }

                ushort leaving = Faces.Subtract(light, 0, 1);
                if (leaving == 0) continue;
                ref ushort slot = ref work.OutgoingAdd[(face * Faces.Cells) + Faces.Slot(face, index)];
                slot = Faces.Max(slot, leaving);
                work.HasOutgoingAdd = true;
            }
        }
    }

    [HotPath]
    private static void Raise(LightChunk chunk, Scratch scratch, int index, ushort light)
    {
        if (light == 0) return;
        ushort current = chunk.Get(index);
        ushort brighter = Faces.Max(current, light);
        if (brighter == current) return;
        chunk.Set(index, brighter);
        scratch.PushAddition(index);
    }

    /// <summary>Gets the light that a cell makes by itself: the emission of its state, and the full sky when the cell is exposed.</summary>
    [HotPath]
    internal ushort SourceOf(LightChunk chunk, int index)
    {
        ushort emission = _sources.States[chunk.Blocks.GetState(index)].Emission;
        return chunk.IsExposed(index) ? (ushort)(emission | 0xF000) : emission;
    }

    [HotPath]
    private bool IsOpen(LightChunk chunk, int index, int face) => (ClosedFaces(chunk, index) & (1 << face)) == 0;

    // The faces of a cell that light does not cross; a shape that depends on block data reads them from its data.
    [HotPath]
    internal int ClosedFaces(LightChunk chunk, int index)
    {
        uint state = chunk.Blocks.GetState(index);
        return Closed(chunk, index, state, _sources.States[state]);
    }

    [HotPath]
    private int Closed(LightChunk chunk, int index, uint state, in StateLight light) =>
        (light.Flags & StateLight.Instance) == 0 ? light.Faces : _shapes.Get(state, chunk.Blocks.GetBlockDataView(index)).Opacity.Bits;

    // The bits of the components that are not zero.
    [HotPath]
    private static ushort Mask(ushort value) =>
        (ushort)(((value & 0xF000) != 0 ? 0xF000 : 0) | ((value & 0xF00) != 0 ? 0xF00 : 0) | ((value & 0x0F0) != 0 ? 0x0F0 : 0) | ((value & 0x00F) != 0 ? 0x00F : 0));

    private readonly struct PropagationJob : IJobFor
    {
        private readonly Propagator _owner;
        private readonly int _lanes;
        private readonly bool _removal;

        public PropagationJob(Propagator owner, int lanes, bool removal) => (_owner, _lanes, _removal) = (owner, lanes, removal);

        [HotPath]
        public void Execute(int index) => _owner.RunLane(index, _lanes, _removal);
    }
}
