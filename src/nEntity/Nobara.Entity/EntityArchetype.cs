// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core;

namespace Nobara.Entity;

/// <summary>
/// The entities of a world that have exactly the same components. Entities
/// are stored by archetype, in blocks of unmanaged memory: one column per
/// component, so that iterating a component reads contiguous memory.
/// </summary>
/// <remarks>
/// Get an archetype with <see cref="EntityWorld.GetArchetype(ReadOnlySpan{ComponentType})"/>, then create
/// entities in it. An entity that gains or loses a component moves to
/// another archetype: avoid doing that often (see the entity module sheet,
/// "Transient states").
/// </remarks>
public sealed unsafe class EntityArchetype
{
    /// <summary>The size a block aims at, in bytes. A block holds at least one entity, so a very large entity makes a larger block.</summary>
    internal const int BlockBytes = 64 * 1024;

    private readonly EntityWorld _world;
    private readonly ComponentType[] _components;
    private readonly int[] _slotOf;
    private readonly Dictionary<int, EntityArchetype> _addEdges = [];
    private readonly Dictionary<int, EntityArchetype> _removeEdges = [];
    private NativeList<Block> _blocks = new(4);
    private byte* _spare;

    internal EntityArchetype(EntityWorld world, int index, int[] sortedIndices)
    {
        _world = world;
        Index = index;
        ComponentRegistry registry = world.Registry;
        int count = sortedIndices.Length;
        _components = new ComponentType[count];
        Indices = sortedIndices;
        Sizes = new int[count];
        Tracked = new bool[count];
        ColumnOffsets = new int[count];
        VersionOffsets = new int[count];
        _slotOf = new int[count == 0 ? 0 : sortedIndices[^1] + 1];
        Array.Fill(_slotOf, -1);
        int rowBytes = sizeof(EntityId);
        for (int slot = 0; slot < count; slot++)
        {
            ref readonly ComponentRegistry.Entry entry = ref registry[sortedIndices[slot]];
            _components[slot] = new ComponentType(sortedIndices[slot]);
            Sizes[slot] = entry.Size;
            Tracked[slot] = entry.Track;
            _slotOf[sortedIndices[slot]] = slot;
            rowBytes += entry.Size + (entry.Track ? sizeof(uint) : 0);
        }

        EntityOffset = Align(sizeof(uint) * count);
        int capacity = Math.Max(1, (BlockBytes - EntityOffset) / rowBytes);
        while (capacity > 1 && Layout(capacity, write: false) > BlockBytes)
        {
            capacity--;
        }

        Capacity = capacity;
        BlockSize = Layout(capacity, write: true);
    }

    /// <summary>Gets the world of the archetype.</summary>
    public EntityWorld World
    {
        [HotPath]
        get => _world;
    }

    /// <summary>Gets the components of the entities of the archetype.</summary>
    public IReadOnlyList<ComponentType> Components => _components;

    /// <summary>Gets the number of entities in the archetype.</summary>
    public int EntityCount { get; private set; }

    /// <summary>Gets the number of blocks the entities use.</summary>
    public int BlockCount
    {
        [HotPath]
        get => _blocks.Count;
    }

    /// <summary>Gets the number of entities that one block holds.</summary>
    public int Capacity { get; }

    internal int Index { get; }

    internal int[] Indices { get; }

    internal int[] Sizes { get; }

    internal bool[] Tracked { get; }

    internal int[] ColumnOffsets { get; }

    internal int[] VersionOffsets { get; }

    internal int EntityOffset { get; }

    internal int BlockSize { get; }

    internal NativeList<Block> Blocks
    {
        [HotPath]
        get => _blocks;
    }

    /// <summary>Tells whether the entities of the archetype have a component.</summary>
    /// <param name="component">The component.</param>
    /// <returns><see langword="true"/> when they have it.</returns>
    [HotPath]
    public bool Has(ComponentType component) => SlotOf(component.Index) >= 0;

    /// <inheritdoc/>
    public override string ToString() =>
        "archetype {" + string.Join(", ", _components.Select(c => _world.Registry.Info(c).Name)) + "}";

    /// <summary>Gets the column of a component in a block, or -1 when the archetype does not have it.</summary>
    [HotPath]
    internal int SlotOf(int componentIndex) => (uint)componentIndex < (uint)_slotOf.Length ? _slotOf[componentIndex] : -1;

    [HotPath]
    internal bool TryGetAddEdge(int componentIndex, out EntityArchetype? target) => _addEdges.TryGetValue(componentIndex, out target);

    [HotPath]
    internal bool TryGetRemoveEdge(int componentIndex, out EntityArchetype? target) => _removeEdges.TryGetValue(componentIndex, out target);

    internal void SetEdges(int componentIndex, EntityArchetype withComponent)
    {
        _addEdges[componentIndex] = withComponent;
        withComponent._removeEdges[componentIndex] = this;
    }

    /// <summary>Adds a row for an entity, with the default value of each component, stamped as changed.</summary>
    [HotPath]
    internal int AddRow(EntityId entity, uint version, out int row)
    {
        if (_blocks.Count == 0 || _blocks[_blocks.Count - 1].Count == Capacity)
        {
            byte* data = _spare;
            _spare = null;
            if (data == null)
            {
                data = (byte*)NativeMemory.AlignedAlloc((nuint)BlockSize, 64);
            }

            NativeMemory.Clear(data, (nuint)EntityOffset);
            _blocks.Add(new Block { Data = data, Count = 0 });
        }

        int block = _blocks.Count - 1;
        ref Block last = ref _blocks[block];
        row = last.Count++;
        ((EntityId*)(last.Data + EntityOffset))[row] = entity;
        ComponentRegistry registry = _world.Registry;
        for (int slot = 0; slot < Indices.Length; slot++)
        {
            int size = Sizes[slot];
            Buffer.MemoryCopy(registry[Indices[slot]].Default, last.Data + ColumnOffsets[slot] + (row * size), size, size);
            Stamp(last.Data, slot, row, version);
        }

        EntityCount++;
        return block;
    }

    /// <summary>
    /// Copies the values of a row of another archetype into a row of this one,
    /// for the components both have, with their change versions.
    /// </summary>
    [HotPath]
    internal void CopyRow(EntityArchetype from, int fromBlock, int fromRow, int toBlock, int toRow)
    {
        byte* source = from._blocks[fromBlock].Data;
        byte* target = _blocks[toBlock].Data;
        for (int slot = 0; slot < Indices.Length; slot++)
        {
            int fromSlot = from.SlotOf(Indices[slot]);
            if (fromSlot < 0)
            {
                continue;
            }

            int size = Sizes[slot];
            Buffer.MemoryCopy(source + from.ColumnOffsets[fromSlot] + (fromRow * size), target + ColumnOffsets[slot] + (toRow * size), size, size);
            if (Tracked[slot])
            {
                uint version = ((uint*)(source + from.VersionOffsets[fromSlot]))[fromRow];
                ((uint*)(target + VersionOffsets[slot]))[toRow] = version;
                RaiseBlockVersion(target, slot, version);
            }
        }
    }

    /// <summary>Removes a row: the last entity of the archetype takes its place, and is returned.</summary>
    [HotPath]
    internal EntityId RemoveRow(int block, int row)
    {
        int lastBlock = _blocks.Count - 1;
        ref Block last = ref _blocks[lastBlock];
        int lastRow = last.Count - 1;
        EntityId moved = EntityId.Null;
        if (block != lastBlock || row != lastRow)
        {
            byte* to = _blocks[block].Data;
            byte* from = last.Data;
            moved = ((EntityId*)(from + EntityOffset))[lastRow];
            ((EntityId*)(to + EntityOffset))[row] = moved;
            for (int slot = 0; slot < Indices.Length; slot++)
            {
                int size = Sizes[slot];
                Buffer.MemoryCopy(from + ColumnOffsets[slot] + (lastRow * size), to + ColumnOffsets[slot] + (row * size), size, size);
                if (Tracked[slot])
                {
                    uint version = ((uint*)(from + VersionOffsets[slot]))[lastRow];
                    ((uint*)(to + VersionOffsets[slot]))[row] = version;
                    RaiseBlockVersion(to, slot, version);
                }
            }
        }

        last.Count--;
        EntityCount--;
        if (last.Count == 0)
        {
            ReleaseBlock(last.Data);
            _blocks.Count--;
        }

        return moved;
    }

    /// <summary>Records a tracked write of one entity: its version and the version of its block.</summary>
    [HotPath]
    internal void Stamp(byte* block, int slot, int row, uint version)
    {
        if (Tracked[slot])
        {
            ((uint*)block)[slot] = version;
            ((uint*)(block + VersionOffsets[slot]))[row] = version;
        }
    }

    /// <summary>Brings every change version older than <paramref name="oldest"/> up to it, so that versions stay comparable after the counter wraps.</summary>
    [HotPath]
    internal void ClampVersions(uint current, uint oldest)
    {
        for (int b = 0; b < _blocks.Count; b++)
        {
            Block block = _blocks[b];
            for (int slot = 0; slot < Indices.Length; slot++)
            {
                if (!Tracked[slot])
                {
                    continue;
                }

                uint* blockVersion = (uint*)block.Data + slot;
                if (ChangeVersions.IsOlder(*blockVersion, oldest, current))
                {
                    *blockVersion = oldest;
                }

                uint* versions = (uint*)(block.Data + VersionOffsets[slot]);
                for (int row = 0; row < block.Count; row++)
                {
                    if (ChangeVersions.IsOlder(versions[row], oldest, current))
                    {
                        versions[row] = oldest;
                    }
                }
            }
        }
    }

    internal void Clear()
    {
        for (int b = 0; b < _blocks.Count; b++)
        {
            ReleaseBlock(_blocks[b].Data);
        }

        _blocks.Clear();
        EntityCount = 0;
    }

    /// <summary>Frees the memory of the archetype. Its world disposes it.</summary>
    internal void Dispose()
    {
        Clear();
        _blocks.Dispose();
        if (_spare != null)
        {
            NativeMemory.AlignedFree(_spare);
            _spare = null;
        }
    }

    [HotPath]
    private static void RaiseBlockVersion(byte* block, int slot, uint version)
    {
        uint* blockVersion = (uint*)block + slot;
        if (ChangeVersions.IsNewer(version, *blockVersion))
        {
            *blockVersion = version;
        }
    }

    private static int Align(int value) => (value + 15) & ~15;

    // One spare block avoids freeing and allocating again when entities come and go at the edge of a block.
    [HotPath]
    private void ReleaseBlock(byte* data)
    {
        if (_spare == null)
        {
            _spare = data;
        }
        else
        {
            NativeMemory.AlignedFree(data);
        }
    }

    private int Layout(int capacity, bool write)
    {
        int offset = EntityOffset + (sizeof(EntityId) * capacity);
        for (int slot = 0; slot < Indices.Length; slot++)
        {
            offset = Align(offset);
            if (write)
            {
                ColumnOffsets[slot] = offset;
            }

            offset += Sizes[slot] * capacity;
        }

        for (int slot = 0; slot < Indices.Length; slot++)
        {
            if (Tracked[slot])
            {
                offset = Align(offset);
                if (write)
                {
                    VersionOffsets[slot] = offset;
                }

                offset += sizeof(uint) * capacity;
            }
            else if (write)
            {
                VersionOffsets[slot] = -1;
            }
        }

        return offset;
    }

    /// <summary>A block of entities: its memory and the number of entities in it.</summary>
    internal struct Block
    {
        public byte* Data;
        public int Count;
    }
}
