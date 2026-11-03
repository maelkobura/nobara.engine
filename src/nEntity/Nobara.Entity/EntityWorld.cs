// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Entity;

/// <summary>
/// The entities of one dimension: an ECS world. Each dimension has its own
/// world, created and destroyed with <see cref="IEntityWorlds"/>.
/// </summary>
/// <remarks>
/// <para>
/// Change the structure of a world (create and destroy entities, add and
/// remove components, change parents) on the loop thread of its context.
/// While a system of the entity module runs on the world, its structure is
/// locked: record the changes in <see cref="EntitySystemRun.Commands"/>,
/// which the system applies once it returns.
/// </para>
/// <para>
/// In development mode, reading or writing a component checks the accesses
/// of the running system or job, as <see cref="ChunkView"/> does.
/// </para>
/// </remarks>
public sealed unsafe partial class EntityWorld
{
    private readonly EntityWorlds _owner;
    private readonly List<EntityArchetype> _archetypes = [];
    private readonly Dictionary<ArchetypeKey, EntityArchetype> _byKey = [];
    private readonly EntityArchetype _empty;

    // Creating an archetype allocates once per set of components. Structural
    // changes reach that rare path through this delegate.
    private readonly Func<EntityArchetype, int, bool, EntityArchetype> _edge;
    private readonly Func<EntityDefinition, EntityArchetype> _definitionArchetype;
    private NativeList<EntityRecord> _records = new(1024);
    private NativeList<int> _free = new(256);
    private NativeList<int> _depths = new(8);
    private int _lock;
    private bool _disposed;

    internal EntityWorld(EntityWorlds owner, DimensionId dimension, string ownerModuleId)
    {
        _owner = owner;
        Dimension = dimension;
        OwnerModuleId = ownerModuleId;
        _edge = CreateEdge;
        _definitionArchetype = CreateDefinitionArchetype;
        _empty = GetArchetype(ReadOnlySpan<ComponentType>.Empty);
    }

    /// <summary>Gets the dimension of the world.</summary>
    public DimensionId Dimension { get; }

    /// <summary>Gets the module that created the world, and destroys it.</summary>
    public string OwnerModuleId { get; }

    /// <summary>Gets the number of living entities.</summary>
    public int EntityCount { get; private set; }

    /// <summary>Gets the archetypes of the world, in the order they appeared.</summary>
    public IReadOnlyList<EntityArchetype> Archetypes => _archetypes;

    /// <summary>
    /// Gets the change version that writes record now. It goes up after each
    /// run of a system on the world, and with <see cref="AdvanceChangeVersion"/>.
    /// </summary>
    public uint ChangeVersion { [HotPath] get; internal set; } = 1;

    /// <summary>Gets whether a system runs on the world: its structure is locked.</summary>
    public bool IsLocked
    {
        [HotPath]
        get => _lock != 0;
    }

    /// <summary>Gets whether the world was destroyed.</summary>
    public bool IsDestroyed => _disposed;

    internal ComponentRegistry Registry
    {
        [HotPath]
        get => _owner.Registry;
    }

    internal List<EntityArchetype> ArchetypeList
    {
        [HotPath]
        get => _archetypes;
    }

    internal EntityWorlds Owner
    {
        [HotPath]
        get => _owner;
    }

    /// <summary>Gets the state of each entity system in this world, by the slot of the system.</summary>
    internal EntitySystemRunner.WorldState[] SystemStates { get; set; } = [];

    /// <summary>Gets the highest depth of the hierarchy: 0 when no entity has a parent.</summary>
    internal int MaxDepth
    {
        [HotPath]
        get
        {
            for (int depth = _depths.Count - 1; depth > 0; depth--)
            {
                if (_depths[depth] > 0)
                {
                    return depth;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Ends the current change version and starts the next one. A reader of
    /// changes calls it before it reads: it reads the changes up to the
    /// returned version, and the writes that follow carry a newer version.
    /// </summary>
    /// <returns>The version that ended.</returns>
    [HotPath]
    public uint AdvanceChangeVersion()
    {
        uint ended = ChangeVersion;
        ChangeVersion = ended + 1;

        // Every 2^29 versions, bring the oldest versions up, so that any two versions stay comparable.
        if ((ChangeVersion & ((ChangeVersions.MaxAge >> 1) - 1)) == 0)
        {
            uint oldest = ChangeVersion - ChangeVersions.MaxAge;
            for (int i = 0; i < _archetypes.Count; i++)
            {
                _archetypes[i].ClampVersions(ChangeVersion, oldest);
            }
        }

        return ended;
    }

    /// <summary>Gets the archetype of the entities that have exactly these components.</summary>
    /// <param name="components">The components, in any order; duplicates count once.</param>
    /// <returns>The archetype, created the first time.</returns>
    /// <remarks>It allocates, even for an archetype that exists: get archetypes once, not in hot path code.</remarks>
    /// <exception cref="ArgumentException">A handle is not valid in this context.</exception>
    public EntityArchetype GetArchetype(params ReadOnlySpan<ComponentType> components)
    {
        RequireNotDisposed();
        int[] indices = new int[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            Registry.Require(components[i]);
            indices[i] = components[i].Index;
        }

        Array.Sort(indices);
        int count = indices.Length == 0 ? 0 : 1;
        for (int i = 1; i < indices.Length; i++)
        {
            if (indices[i] != indices[count - 1])
            {
                indices[count++] = indices[i];
            }
        }

        Array.Resize(ref indices, count);
        return GetArchetype(indices);
    }

    /// <summary>Creates a query of the entities of this world.</summary>
    /// <param name="description">The components the entities have, and do not have.</param>
    /// <returns>The query; keep it, it follows the archetypes created later.</returns>
    public EntityQuery Query(QueryDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        RequireNotDisposed();
        int[] all = new int[description.All.Count];
        for (int i = 0; i < all.Length; i++)
        {
            Registry.Require(description.All[i]);
            all[i] = description.All[i].Index;
        }

        int[] none = new int[description.None.Count];
        for (int i = 0; i < none.Length; i++)
        {
            Registry.Require(description.None[i]);
            none[i] = description.None[i].Index;
        }

        return new EntityQuery(this, all, none);
    }

    /// <summary>Tells whether an entity lives in this world.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns><see langword="true"/> when it was created and not destroyed.</returns>
    [HotPath]
    public bool IsAlive(EntityId entity) =>
        !_disposed && (uint)entity.Index < (uint)_records.Count && entity.Generation != 0 && _records[entity.Index].Generation == entity.Generation && _records[entity.Index].Archetype >= 0;

    /// <summary>Gets the archetype of an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Its archetype.</returns>
    /// <exception cref="ArgumentException">The entity is not alive in this world.</exception>
    [HotPath]
    public EntityArchetype GetArchetypeOf(EntityId entity) => _archetypes[Record(entity).Archetype];

    /// <summary>Tells whether an entity has a component.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <returns><see langword="true"/> when it has it.</returns>
    /// <exception cref="ArgumentException">The entity is not alive in this world.</exception>
    [HotPath]
    public bool Has(EntityId entity, ComponentType component) => _archetypes[Record(entity).Archetype].SlotOf(component.Index) >= 0;

    /// <summary>Creates an entity without components.</summary>
    /// <returns>The entity.</returns>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public EntityId Create() => Create(_empty);

    /// <summary>Creates an entity in an archetype, with the default value of each component.</summary>
    /// <param name="archetype">The archetype, of this world.</param>
    /// <returns>The entity.</returns>
    /// <exception cref="ArgumentException">The archetype belongs to another world.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public EntityId Create(EntityArchetype archetype)
    {
        RequireUnlocked();
        RequireOwn(archetype);
        _owner.Structure.CheckWrite();
        EntityId entity = NewEntity();
        int block = archetype.AddRow(entity, ChangeVersion, out int row);
        Place(entity.Index, archetype.Index, block, row);
        return entity;
    }

    /// <summary>Creates entities in an archetype, with the default value of each component.</summary>
    /// <param name="archetype">The archetype, of this world.</param>
    /// <param name="entities">Receives the new entities; its length is the number to create.</param>
    /// <exception cref="ArgumentException">The archetype belongs to another world.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public void Create(EntityArchetype archetype, Span<EntityId> entities)
    {
        RequireUnlocked();
        RequireOwn(archetype);
        _owner.Structure.CheckWrite();
        _records.Reserve(_records.Count + entities.Length);
        for (int i = 0; i < entities.Length; i++)
        {
            EntityId entity = NewEntity();
            int block = archetype.AddRow(entity, ChangeVersion, out int row);
            Place(entity.Index, archetype.Index, block, row);
            entities[i] = entity;
        }
    }

    /// <summary>
    /// Destroys an entity. Its children are detached or destroyed, as their
    /// link declares (<see cref="ParentLink.WhenParentDestroyed"/>).
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <exception cref="ArgumentException">The entity is not alive in this world.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public void Destroy(EntityId entity)
    {
        RequireUnlocked();
        _ = Record(entity);
        _owner.Structure.CheckWrite();
        DestroyCore(entity);
    }

    /// <summary>Destroys entities, as <see cref="Destroy(EntityId)"/> does for each.</summary>
    /// <param name="entities">The entities; those already destroyed, by this call or before, are skipped.</param>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public void Destroy(ReadOnlySpan<EntityId> entities)
    {
        RequireUnlocked();
        _owner.Structure.CheckWrite();
        foreach (EntityId entity in entities)
        {
            if (IsAlive(entity))
            {
                DestroyCore(entity);
            }
        }
    }

    /// <summary>Destroys every entity a query matches, as <see cref="Destroy(EntityId)"/> does for each.</summary>
    /// <param name="query">The query, of this world.</param>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public void Destroy(EntityQuery query)
    {
        RequireUnlocked();
        if (query.World != this)
        {
            throw new ArgumentException("The query belongs to another world.", nameof(query));
        }

        _owner.Structure.CheckWrite();
        query.Update();
        List<EntityArchetype> matches = query.Matches;
        for (int i = 0; i < matches.Count; i++)
        {
            EntityArchetype archetype = matches[i];

            // Destroying a parent may destroy entities of other archetypes; take the last entity each time.
            while (archetype.EntityCount > 0)
            {
                EntityArchetype.Block block = archetype.Blocks[archetype.BlockCount - 1];
                DestroyCore(((EntityId*)(block.Data + archetype.EntityOffset))[block.Count - 1]);
            }
        }
    }

    /// <summary>Adds a component to an entity, with a value. The entity moves to another archetype.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException">The entity is not alive in this world, or already has the component.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer; or the component is <see cref="ParentLink"/>, which <see cref="SetParent"/> adds.</exception>
    [HotPath]
    public void Add<T>(EntityId entity, ComponentType<T> component, in T value)
        where T : unmanaged
    {
        RequireUnlocked();
        Registry.Require(component);
        RequireWritable(component.Untyped);
        _owner.Structure.CheckWrite();
        if (Has(entity, component))
        {
            throw new ArgumentException($"The {entity} already has the component.", nameof(component));
        }

        Move(entity, component.Untyped.Index, add: true);
        WriteValue(entity, component, value);
    }

    /// <summary>Removes a component from an entity. The entity moves to another archetype.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <exception cref="ArgumentException">
    /// The entity is not alive in this world, or does not have the component;
    /// or the component is <see cref="ParentLink"/>, or a transform of an
    /// entity that has a parent or children: detach them first.
    /// </exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public void Remove(EntityId entity, ComponentType component)
    {
        RequireUnlocked();
        Registry.Require(component);
        _owner.Structure.CheckWrite();
        if (!Has(entity, component))
        {
            throw new ArgumentException($"The {entity} does not have the component.", nameof(component));
        }

        RequireRemovable(entity, component.Index);
        Move(entity, component.Index, add: false);
    }

    /// <summary>Gets the value of a component of an entity, to read it.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <returns>A reference to the value, valid until the structure of the world changes.</returns>
    /// <exception cref="ArgumentException">The entity is not alive in this world, or does not have the component.</exception>
    [HotPath]
    public ref readonly T Read<T>(EntityId entity, ComponentType<T> component)
        where T : unmanaged
    {
        Registry.Require(component);
        Registry[component.Untyped.Index].Data.CheckRead();
        return ref *(T*)Address(entity, component.Untyped.Index, out _, out _, out _);
    }

    /// <summary>Gets the value of a component of an entity, to write it. The value counts as changed.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <returns>A reference to the value, valid until the structure of the world changes.</returns>
    /// <exception cref="ArgumentException">The entity is not alive in this world, or does not have the component.</exception>
    /// <exception cref="InvalidOperationException">The component is <see cref="ParentLink"/>.</exception>
    [HotPath]
    public ref T Write<T>(EntityId entity, ComponentType<T> component)
        where T : unmanaged
    {
        RequireWritable(component.Untyped);
        Registry.Require(component);
        Registry[component.Untyped.Index].Data.CheckWrite();
        byte* address = Address(entity, component.Untyped.Index, out EntityArchetype archetype, out int slot, out int row);
        archetype.Stamp(BlockOf(entity), slot, row, ChangeVersion);
        return ref *(T*)address;
    }

    /// <summary>Sets the value of a component of an entity. The value counts as changed.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException">The entity is not alive in this world, or does not have the component.</exception>
    /// <exception cref="InvalidOperationException">The component is <see cref="ParentLink"/>.</exception>
    [HotPath]
    public void Set<T>(EntityId entity, ComponentType<T> component, in T value)
        where T : unmanaged =>
        Write(entity, component) = value;

    /// <inheritdoc/>
    public override string ToString() => $"entity world of {Dimension}";

    /// <summary>Locks the structure while a system runs on the world.</summary>
    [HotPath]
    internal void Lock() => _lock++;

    [HotPath]
    internal void Unlock() => _lock--;

    /// <summary>Gets the block that holds an entity.</summary>
    [HotPath]
    internal byte* BlockOf(EntityId entity)
    {
        ref EntityRecord record = ref Record(entity);
        return _archetypes[record.Archetype].Blocks[record.Block].Data;
    }

    /// <summary>Frees every entity and the memory of the world.</summary>
    internal void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (EntitySystemRunner.WorldState state in SystemStates)
        {
            state.Dispose();
        }

        foreach (EntityArchetype archetype in _archetypes)
        {
            archetype.Dispose();
        }

        EntityCount = 0;
        _records.Dispose();
        _free.Dispose();
        _depths.Dispose();
    }

    /// <summary>Gets the record of a living entity, or throws.</summary>
    [HotPath]
    internal ref EntityRecord Record(EntityId entity)
    {
        if (!IsAlive(entity))
        {
            throw new ArgumentException($"The {entity} is not alive in the {this}.", nameof(entity));
        }

        return ref _records[entity.Index];
    }

    /// <summary>Gets the address of the value of a component of a living entity.</summary>
    [HotPath]
    internal byte* Address(EntityId entity, int componentIndex, out EntityArchetype archetype, out int slot, out int row)
    {
        ref EntityRecord record = ref Record(entity);
        archetype = _archetypes[record.Archetype];
        slot = archetype.SlotOf(componentIndex);
        if (slot < 0)
        {
            throw new ArgumentException($"The {entity} does not have the component '{Registry.Info(new ComponentType(componentIndex)).Name}'.", nameof(entity));
        }

        row = record.Row;
        return archetype.Blocks[record.Block].Data + archetype.ColumnOffsets[slot] + (row * archetype.Sizes[slot]);
    }

    [HotPath]
    internal void RequireUnlocked()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EntityWorld), $"The {this} was destroyed.");
        }

        if (_lock != 0)
        {
            throw new InvalidOperationException($"A system runs on the {this}: its structure is locked. Record the change in the command buffer of the system.");
        }
    }

    /// <summary>Adds or removes a component of a living entity: moves it to the archetype with or without it.</summary>
    [HotPath]
    internal void Move(EntityId entity, int componentIndex, bool add)
    {
        ref EntityRecord record = ref Record(entity);
        EntityArchetype source = _archetypes[record.Archetype];
        EntityArchetype? target;
        bool found = add ? source.TryGetAddEdge(componentIndex, out target) : source.TryGetRemoveEdge(componentIndex, out target);
        if (!found)
        {
            target = _edge(source, componentIndex, add);
        }

        int block = target!.AddRow(entity, ChangeVersion, out int row);
        target.CopyRow(source, record.Block, record.Row, block, row);
        EntityId moved = source.RemoveRow(record.Block, record.Row);
        if (!moved.IsNull)
        {
            _records[moved.Index].Block = record.Block;
            _records[moved.Index].Row = record.Row;
        }

        Place(entity.Index, target.Index, block, row);
    }

    /// <summary>Writes the value of a component of a living entity and records the change.</summary>
    [HotPath]
    internal void WriteValue<T>(EntityId entity, ComponentType<T> component, in T value)
        where T : unmanaged
    {
        byte* address = Address(entity, component.Untyped.Index, out EntityArchetype archetype, out int slot, out int row);
        *(T*)address = value;
        archetype.Stamp(BlockOf(entity), slot, row, ChangeVersion);
    }

    /// <summary>Writes the value of a component given as bytes, and records the change.</summary>
    [HotPath]
    internal void WriteBytes(EntityId entity, int componentIndex, byte* value)
    {
        byte* address = Address(entity, componentIndex, out EntityArchetype archetype, out int slot, out int row);
        int size = archetype.Sizes[slot];
        Buffer.MemoryCopy(value, address, size, size);
        archetype.Stamp(BlockOf(entity), slot, row, ChangeVersion);
    }

    /// <summary>Refuses to remove the parent link, or a transform the hierarchy needs.</summary>
    [HotPath]
    internal void RequireRemovable(EntityId entity, int componentIndex)
    {
        if (componentIndex == EntityWorlds.ParentIndex)
        {
            throw new ArgumentException("The parent link is never removed: detach the entity with ClearParent.", nameof(componentIndex));
        }

        ref EntityRecord record = ref Record(entity);
        bool transform = componentIndex == _owner.WorldTransform.Untyped.Index || componentIndex == _owner.LocalTransform.Untyped.Index;
        if (transform && (record.Parent >= 0 || record.FirstChild >= 0))
        {
            throw new ArgumentException(
                $"The {entity} has a parent or children, which need its transforms: detach them before removing its transform.",
                nameof(componentIndex));
        }
    }

    [HotPath]
    private static void RequireWritable(ComponentType component)
    {
        if (component.Index == EntityWorlds.ParentIndex)
        {
            throw new InvalidOperationException("The parent link of an entity changes through SetParent and ClearParent only.");
        }
    }

    [HotPath]
    private void RequireOwn(EntityArchetype archetype)
    {
        if (archetype.World != this)
        {
            throw new ArgumentException("The archetype belongs to another world.", nameof(archetype));
        }
    }

    private void RequireNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private EntityArchetype GetArchetype(int[] sortedIndices)
    {
        var key = new ArchetypeKey(sortedIndices);
        if (!_byKey.TryGetValue(key, out EntityArchetype? archetype))
        {
            archetype = new EntityArchetype(this, _archetypes.Count, sortedIndices);
            _archetypes.Add(archetype);
            _byKey.Add(key, archetype);
        }

        return archetype;
    }

    // The rare path of a structural change: the first time an entity of an archetype gains or loses a component.
    private EntityArchetype CreateEdge(EntityArchetype source, int componentIndex, bool add)
    {
        int[] indices = add
            ? [.. source.Indices, componentIndex]
            : Array.FindAll(source.Indices, i => i != componentIndex);
        Array.Sort(indices);
        EntityArchetype target = GetArchetype(indices);
        if (add)
        {
            source.SetEdges(componentIndex, target);
        }
        else
        {
            target.SetEdges(componentIndex, source);
        }

        return target;
    }

    [HotPath]
    private EntityId NewEntity()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free[--_free.Count];
        }
        else
        {
            index = _records.Count;
            _records.Add(new EntityRecord { Generation = 0, Archetype = -1, Parent = -1, FirstChild = -1, NextSibling = -1, PreviousSibling = -1 });
        }

        ref EntityRecord record = ref _records[index];
        record.Generation = record.Generation == int.MaxValue ? 1 : record.Generation + 1;
        record.Parent = -1;
        record.FirstChild = -1;
        record.NextSibling = -1;
        record.PreviousSibling = -1;
        EntityCount++;
        return new EntityId(index, record.Generation);
    }

    [HotPath]
    private void Place(int index, int archetype, int block, int row)
    {
        ref EntityRecord record = ref _records[index];
        record.Archetype = archetype;
        record.Block = block;
        record.Row = row;
    }

    /// <summary>Destroys a living entity, after handling its children and its parent.</summary>
    [HotPath]
    private void DestroyCore(EntityId entity)
    {
        ref EntityRecord record = ref _records[entity.Index];

        // Children first: each is detached or destroyed as its link says.
        while (record.FirstChild >= 0)
        {
            int child = record.FirstChild;
            var childEntity = new EntityId(child, _records[child].Generation);
            ref readonly ParentLink link = ref *(ParentLink*)Address(childEntity, EntityWorlds.ParentIndex, out _, out _, out _);
            if (link.WhenParentDestroyed == OrphanPolicy.Destroy)
            {
                DestroyCore(childEntity);
            }
            else
            {
                Detach(childEntity);
            }

            record = ref _records[entity.Index];
        }

        if (record.Parent >= 0)
        {
            Unlink(entity.Index);
            _depths[DepthOf(entity)]--;
        }

        EntityArchetype archetype = _archetypes[record.Archetype];
        EntityId moved = archetype.RemoveRow(record.Block, record.Row);
        if (!moved.IsNull)
        {
            _records[moved.Index].Block = record.Block;
            _records[moved.Index].Row = record.Row;
        }

        record.Archetype = -1;
        _free.Add(entity.Index);
        EntityCount--;
    }

    /// <summary>Where an entity is stored, and its links in the hierarchy.</summary>
    internal struct EntityRecord
    {
        public int Archetype;
        public int Block;
        public int Row;
        public int Generation;
        public int Parent;
        public int FirstChild;
        public int NextSibling;
        public int PreviousSibling;
    }

    /// <summary>A set of components, as sorted indices, compared by value.</summary>
    private readonly struct ArchetypeKey(int[] indices) : IEquatable<ArchetypeKey>
    {
        private readonly int[] _indices = indices;

        public bool Equals(ArchetypeKey other) => _indices.AsSpan().SequenceEqual(other._indices);

        public override bool Equals(object? obj) => obj is ArchetypeKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            foreach (int index in _indices)
            {
                hash.Add(index);
            }

            return hash.ToHashCode();
        }
    }
}
