// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>
/// The propagation of the block channel by queues: a removal phase, then an
/// addition phase, each in rounds of jobs with one chunk per index. A front
/// that crosses a face of its chunk waits for the next round, in the work of
/// the chunk on that side; a front toward a chunk that is not loaded is
/// dropped, and the border it came from is spread again when that chunk loads.
/// </summary>
/// <remarks>
/// The result is the only fixed point of "each block holds the brightest of
/// its emission and of its neighbors' light minus one level and its filter",
/// whatever the order of the rounds and of the cells: two contexts that apply
/// the same changes hold the same light. Only integers are used.
/// </remarks>
internal sealed class Propagator
{
    private readonly ModuleContext _context;
    private readonly LightSourceRegistry _sources;
    private readonly IShapeRegistry _shapes;
    private readonly JobData _blocks;
    private readonly JobData _light;
    private readonly List<LightChunk> _active = [];
    private readonly List<LightChunk> _work = [];
    private readonly Stack<WorkState> _pool = new();
    private readonly Scratch?[] _scratch;

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

    /// <summary>Gets the chunks that have work, or that changed in the current pass.</summary>
    public List<LightChunk> Active => _active;

    /// <summary>Gets the rounds of jobs of the last pass.</summary>
    public int LastRounds { get; private set; }

    /// <summary>Gets the work of a chunk, taking it from the pool when it has none.</summary>
    public WorkState Work(LightChunk chunk)
    {
        if (chunk.Work is { } work) return work;
        work = _pool.Count > 0 ? _pool.Pop() : new WorkState();
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

    /// <summary>Runs the rounds until no front remains. Call it on the loop thread, inside the accesses of the tick system.</summary>
    public void Run()
    {
        int rounds = 0;
        while (Collect(removal: true))
        {
            Execute(removal: true);
            Merge(removal: true);
            rounds++;
        }

        while (Collect(removal: false))
        {
            Execute(removal: false);
            Merge(removal: false);
            rounds++;
        }

        LastRounds = rounds;
    }

    /// <summary>Returns the works to the pool once the changes of the pass are read.</summary>
    public void Finish()
    {
        foreach (LightChunk chunk in _active)
        {
            if (chunk.Work is { } work)
            {
                work.Clear();
                _pool.Push(work);
                chunk.Work = null;
            }
        }

        _active.Clear();
    }

    [HotPath]
    internal void RunLane(int lane, int lanes, bool removal)
    {
        Scratch scratch = _scratch[lane]!;
        for (int i = lane; i < _work.Count; i += lanes)
        {
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
            WorkState work = chunk.Work!;
            if (removal ? work.HasRemovalWork : work.HasAdditionWork) _work.Add(chunk);
        }

        return _work.Count > 0;
    }

    private void Execute(bool removal)
    {
        int lanes = Math.Min(_work.Count, _scratch.Length);
        for (int i = 0; i < lanes; i++) _scratch[i] ??= new Scratch();
        var job = new PropagationJob(this, lanes, removal);
        _context.Jobs.ScheduleFor(job, lanes, 1, new JobAccess([_blocks], [_light])).Complete();
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

    // The removal phase of one chunk: its seeds, the removals that entered it, then its queue.
    [HotPath]
    private void Remove(LightChunk chunk, WorkState work, Scratch scratch)
    {
        _light.CheckWrite();
        List<uint> seeds = work.Seeds;
        for (int i = 0; i < seeds.Count; i++)
        {
            int index = (int)(seeds[i] & 0xFFFF);
            ushort removed = (ushort)(seeds[i] >> 16);

            // A changed cell may now close a face that its light crossed: its removal leaves through every face.
            for (int face = 0; face < 6; face++) RemoveToward(chunk, work, scratch, index, face, removed);
        }

        seeds.Clear();
        if (work.HasIncomingRemoval)
        {
            ushort[] incoming = work.IncomingRemoval;
            for (int face = 0; face < 6; face++)
            {
                int start = face * Faces.Cells;
                for (int slot = 0; slot < Faces.Cells; slot++)
                {
                    ushort removed = incoming[start + slot];
                    if (removed == 0) continue;
                    incoming[start + slot] = 0;
                    int index = Faces.Cell(face, slot);
                    if (!IsOpen(chunk, index, face)) continue;
                    RemoveAt(chunk, work, scratch, index, removed);
                }
            }

            work.HasIncomingRemoval = false;
        }

        while (scratch.HasRemovals)
        {
            int index = scratch.PopRemoval(out ushort removed);
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
        ushort current = chunk.GetBlock(index);
        if (current == 0) return;
        ushort gone = 0;
        bool spread = false;
        for (int shift = 0; shift < 12; shift += 4)
        {
            int level = (removed >> shift) & 15;
            int here = (current >> shift) & 15;
            if (level == 0 || here == 0) continue;
            if (here < level) gone |= (ushort)(here << shift);
            else spread = true;
        }

        if (gone != 0)
        {
            ushort kept = (ushort)(current & ~Mask(gone));
            ushort emission = (ushort)(_sources.States[chunk.Blocks.GetState(index)].Emission & Mask(gone));
            if (emission != 0)
            {
                kept |= emission;
                spread = true;
            }

            chunk.SetBlock(index, kept);
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
            int index = scratch.PopAddition();
            ushort light = chunk.GetBlock(index);
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
        ushort current = chunk.GetBlock(index);
        ushort brighter = Faces.Max(current, light);
        if (brighter == current) return;
        chunk.SetBlock(index, brighter);
        scratch.PushAddition(index);
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
        (ushort)(((value & 0xF00) != 0 ? 0xF00 : 0) | ((value & 0x0F0) != 0 ? 0x0F0 : 0) | ((value & 0x00F) != 0 ? 0x00F : 0));

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
