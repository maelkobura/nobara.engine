// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Entity;

/// <summary>
/// One block of entities of the same archetype, during an iteration: the
/// columns of their components, as spans indexed from 0 to
/// <see cref="Count"/> - 1.
/// </summary>
/// <remarks>
/// <para>
/// In development mode, getting a column checks the accesses of the running
/// system or job: reading a component it does not declare, or writing one it
/// declares as read only, throws a <c>JobAccessException</c>.
/// </para>
/// <para>
/// Writing a tracked component (<see cref="ComponentOptions.TrackChanges"/>)
/// through <see cref="Write{T}"/> records every entity of the block as
/// changed. A system that changes only some of them takes the column with
/// <see cref="WriteUntracked{T}"/> and calls <see cref="MarkChanged"/> for
/// each entity it changes.
/// </para>
/// </remarks>
[HotPath]
public readonly unsafe ref struct ChunkView
{
    private readonly EntityArchetype _archetype;
    private readonly byte* _data;

    internal ChunkView(EntityArchetype archetype, byte* data, int count)
    {
        _archetype = archetype;
        _data = data;
        Count = count;
    }

    /// <summary>Gets the number of entities in the block.</summary>
    public int Count { get; }

    /// <summary>Gets the archetype of the entities of the block.</summary>
    public EntityArchetype Archetype => _archetype;

    /// <summary>Gets the entities of the block.</summary>
    public ReadOnlySpan<EntityId> Entities => new(_data + _archetype.EntityOffset, Count);

    /// <summary>Tells whether the entities of the block have a component.</summary>
    /// <param name="component">The component.</param>
    /// <returns><see langword="true"/> when they have it.</returns>
    public bool Has(ComponentType component) => _archetype.SlotOf(component.Index) >= 0;

    /// <summary>Gets the values of a component, to read them.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="component">The component.</param>
    /// <returns>One value per entity of the block.</returns>
    /// <exception cref="ArgumentException">The entities of the block do not have the component.</exception>
    public ReadOnlySpan<T> Read<T>(ComponentType<T> component)
        where T : unmanaged
    {
        int slot = Slot(component.Untyped);
        _archetype.World.Registry[component.Untyped.Index].Data.CheckRead();
        return new ReadOnlySpan<T>(_data + _archetype.ColumnOffsets[slot], Count);
    }

    /// <summary>Gets the values of a component, to write them. Every entity of the block counts as changed.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="component">The component.</param>
    /// <returns>One value per entity of the block.</returns>
    /// <exception cref="ArgumentException">The entities of the block do not have the component.</exception>
    /// <exception cref="InvalidOperationException">The component is <see cref="ParentLink"/>, which changes through the hierarchy methods only.</exception>
    public Span<T> Write<T>(ComponentType<T> component)
        where T : unmanaged
    {
        int slot = WritableSlot(component.Untyped);
        if (_archetype.Tracked[slot])
        {
            uint version = _archetype.World.ChangeVersion;
            ((uint*)_data)[slot] = version;
            new Span<uint>(_data + _archetype.VersionOffsets[slot], Count).Fill(version);
        }

        return new Span<T>(_data + _archetype.ColumnOffsets[slot], Count);
    }

    /// <summary>
    /// Gets the values of a component, to write them, without recording any
    /// change: call <see cref="MarkChanged"/> for each entity whose value
    /// changes, when the component is tracked.
    /// </summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="component">The component.</param>
    /// <returns>One value per entity of the block.</returns>
    /// <exception cref="ArgumentException">The entities of the block do not have the component.</exception>
    /// <exception cref="InvalidOperationException">The component is <see cref="ParentLink"/>.</exception>
    public Span<T> WriteUntracked<T>(ComponentType<T> component)
        where T : unmanaged =>
        new(_data + _archetype.ColumnOffsets[WritableSlot(component.Untyped)], Count);

    /// <summary>Records that the value of a component changed for one entity of the block. Nothing happens when the component is not tracked.</summary>
    /// <param name="component">The component.</param>
    /// <param name="index">The index of the entity in the block.</param>
    /// <exception cref="ArgumentException">The entities of the block do not have the component.</exception>
    public void MarkChanged(ComponentType component, int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "The index is outside the block.");
        }

        int slot = Slot(component);
        _archetype.World.Registry[component.Index].Data.CheckWrite();
        _archetype.Stamp(_data, slot, index, _archetype.World.ChangeVersion);
    }

    /// <summary>
    /// Tells whether a tracked component changed for at least one entity of
    /// the block since a version: a reader skips the blocks where it did not.
    /// </summary>
    /// <param name="component">The component; it must be tracked.</param>
    /// <param name="since">The version of the last read.</param>
    /// <returns><see langword="true"/> when an entity of the block changed after <paramref name="since"/>.</returns>
    /// <exception cref="ArgumentException">The entities of the block do not have the component, or it is not tracked.</exception>
    public bool HasChangedSince(ComponentType component, uint since) =>
        ChangeVersions.ChangedSince(((uint*)_data)[TrackedSlot(component)], since, _archetype.World.ChangeVersion);

    /// <summary>Tells whether a tracked component changed for one entity of the block since a version.</summary>
    /// <param name="component">The component; it must be tracked.</param>
    /// <param name="index">The index of the entity in the block.</param>
    /// <param name="since">The version of the last read.</param>
    /// <returns><see langword="true"/> when the value changed after <paramref name="since"/>.</returns>
    /// <exception cref="ArgumentException">The entities of the block do not have the component, or it is not tracked.</exception>
    public bool HasChangedSince(ComponentType component, int index, uint since) =>
        ChangeVersions.ChangedSince(GetChangeVersions(component)[index], since, _archetype.World.ChangeVersion);

    /// <summary>Gets the change version of a tracked component for each entity of the block: the version of the last write that was recorded.</summary>
    /// <param name="component">The component; it must be tracked.</param>
    /// <returns>One version per entity of the block.</returns>
    /// <exception cref="ArgumentException">The entities of the block do not have the component, or it is not tracked.</exception>
    public ReadOnlySpan<uint> GetChangeVersions(ComponentType component) =>
        new(_data + _archetype.VersionOffsets[TrackedSlot(component)], Count);

    private int Slot(ComponentType component)
    {
        int slot = _archetype.SlotOf(component.Index);
        if (slot < 0)
        {
            throw new ArgumentException("The entities of this block do not have the component.", nameof(component));
        }

        return slot;
    }

    private int WritableSlot(ComponentType component)
    {
        if (component.Index == EntityWorlds.ParentIndex)
        {
            throw new InvalidOperationException("The parent link of an entity changes through SetParent and ClearParent only.");
        }

        int slot = Slot(component);
        _archetype.World.Registry[component.Index].Data.CheckWrite();
        return slot;
    }

    private int TrackedSlot(ComponentType component)
    {
        int slot = Slot(component);
        _archetype.World.Registry[component.Index].Data.CheckRead();
        if (!_archetype.Tracked[slot])
        {
            throw new ArgumentException("The component is not tracked: register it with ComponentOptions.TrackChanges.", nameof(component));
        }

        return slot;
    }
}
