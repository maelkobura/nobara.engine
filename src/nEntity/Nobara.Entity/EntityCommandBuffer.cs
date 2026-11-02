// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Nobara.Core;

namespace Nobara.Entity;

/// <summary>
/// Changes to the structure of a world recorded while a system runs on it,
/// when the structure is locked. The system applies them, in the order they
/// were recorded, once it returns.
/// </summary>
/// <remarks>
/// <para>
/// A system gets its buffer from <see cref="EntitySystemRun.Commands"/>, and
/// declares <see cref="EntitySystemOptions.ChangesStructure"/>. Recording is
/// safe from the jobs of the system, on any thread; recording from several
/// threads at once gives an order between them that is not fixed.
/// </para>
/// <para>
/// A command on an entity that is no longer alive when it is applied, such
/// as one destroyed by an earlier command, is skipped. Recording allocates no
/// managed memory once the buffer has grown to its size.
/// </para>
/// </remarks>
public sealed unsafe class EntityCommandBuffer
{
    private readonly object _gate = new();
    private readonly EntityWorld _world;
    private NativeList<byte> _bytes = new(1024);

    internal EntityCommandBuffer(EntityWorld world)
    {
        _world = world;
    }

    /// <summary>Gets the number of commands recorded and not applied yet.</summary>
    public int Count { get; private set; }

    /// <summary>Records the creation of an entity in an archetype, with the default value of each component.</summary>
    /// <param name="archetype">The archetype, of the world of the buffer.</param>
    /// <exception cref="ArgumentException">The archetype belongs to another world.</exception>
    [HotPath]
    public void Create(EntityArchetype archetype)
    {
        if (archetype.World != _world)
        {
            throw new ArgumentException("The archetype belongs to another world.", nameof(archetype));
        }

        Append(new Command { Kind = CommandKind.Create, Component = archetype.Index }, null, 0);
    }

    /// <summary>Records the destruction of an entity, with its children as their links say.</summary>
    /// <param name="entity">The entity.</param>
    [HotPath]
    public void Destroy(EntityId entity) => Append(new Command { Kind = CommandKind.Destroy, Entity = entity }, null, 0);

    /// <summary>Records the addition of a component to an entity, with a value.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <param name="value">The value.</param>
    /// <remarks>When the entity has the component by then, the value is set instead.</remarks>
    [HotPath]
    public void Add<T>(EntityId entity, ComponentType<T> component, in T value)
        where T : unmanaged
    {
        _world.Registry.Require(component);
        if (component.Untyped.Index == EntityWorlds.ParentIndex)
        {
            throw new InvalidOperationException("The parent link of an entity is added by SetParent only.");
        }

        T copy = value;
        Append(new Command { Kind = CommandKind.Add, Entity = entity, Component = component.Untyped.Index }, (byte*)&copy, sizeof(T));
    }

    /// <summary>Records the removal of a component from an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component; never <see cref="ParentLink"/>.</param>
    /// <remarks>
    /// When the entity does not have the component by then, nothing happens.
    /// Removing a transform of an entity that has a parent or children throws
    /// when the command is applied.
    /// </remarks>
    [HotPath]
    public void Remove(EntityId entity, ComponentType component)
    {
        _world.Registry.Require(component);
        if (component.Index == EntityWorlds.ParentIndex)
        {
            throw new ArgumentException("The parent link is never removed: record ClearParent instead.", nameof(component));
        }

        Append(new Command { Kind = CommandKind.Remove, Entity = entity, Component = component.Index }, null, 0);
    }

    /// <summary>Records a new value of a component of an entity.</summary>
    /// <typeparam name="T">The type of the component.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component.</param>
    /// <param name="value">The value.</param>
    /// <remarks>When the entity does not have the component by then, nothing happens.</remarks>
    [HotPath]
    public void Set<T>(EntityId entity, ComponentType<T> component, in T value)
        where T : unmanaged
    {
        _world.Registry.Require(component);
        if (component.Untyped.Index == EntityWorlds.ParentIndex)
        {
            throw new InvalidOperationException("The parent link of an entity changes through SetParent and ClearParent only.");
        }

        T copy = value;
        Append(new Command { Kind = CommandKind.Set, Entity = entity, Component = component.Untyped.Index }, (byte*)&copy, sizeof(T));
    }

    /// <summary>Records a new parent for an entity, as <see cref="EntityWorld.SetParent"/> does.</summary>
    /// <param name="child">The child.</param>
    /// <param name="parent">The parent.</param>
    /// <param name="whenParentDestroyed">What happens to the child when the parent is destroyed.</param>
    /// <param name="keepWorldTransform">Whether the child keeps its place in the world.</param>
    /// <remarks>A command that would make a cycle throws when it is applied, under the error policy of the module of the system.</remarks>
    [HotPath]
    public void SetParent(EntityId child, EntityId parent, OrphanPolicy whenParentDestroyed = OrphanPolicy.Detach, bool keepWorldTransform = true) =>
        Append(new Command { Kind = CommandKind.SetParent, Entity = child, Other = parent, Policy = whenParentDestroyed, Keep = keepWorldTransform }, null, 0);

    /// <summary>Records the detachment of an entity from its parent, as <see cref="EntityWorld.ClearParent"/> does.</summary>
    /// <param name="child">The entity.</param>
    [HotPath]
    public void ClearParent(EntityId child) => Append(new Command { Kind = CommandKind.ClearParent, Entity = child }, null, 0);

    /// <summary>Frees the memory of the buffer. Its world disposes it.</summary>
    internal void Dispose() => _bytes.Dispose();

    /// <summary>Applies the commands in order, then empties the buffer. The structure of the world is unlocked.</summary>
    [HotPath]
    internal void Playback()
    {
        if (Count == 0)
        {
            return;
        }

        _world.Owner.Structure.CheckWrite();
        int offset = 0;
        int end = _bytes.Count;
        try
        {
            while (offset < end)
            {
                Command command = *(Command*)(_bytes.Pointer + offset);
                byte* value = _bytes.Pointer + offset + sizeof(Command);
                offset += sizeof(Command) + Align(command.Size);
                Apply(command, value);
            }
        }
        finally
        {
            Clear();
        }
    }

    /// <summary>Drops the commands without applying them.</summary>
    [HotPath]
    internal void Clear()
    {
        _bytes.Clear();
        Count = 0;
    }

    [HotPath]
    private static int Align(int size) => (size + 7) & ~7;

    [HotPath]
    private void Apply(in Command command, byte* value)
    {
        EntityWorld world = _world;
        if (command.Kind == CommandKind.Create)
        {
            world.Create(world.ArchetypeList[command.Component]);
            return;
        }

        if (!world.IsAlive(command.Entity))
        {
            return;
        }

        switch (command.Kind)
        {
            case CommandKind.Destroy:
                world.Destroy(command.Entity);
                break;
            case CommandKind.Add:
                if (!world.Has(command.Entity, new ComponentType(command.Component)))
                {
                    world.Move(command.Entity, command.Component, add: true);
                }

                world.WriteBytes(command.Entity, command.Component, value);
                break;
            case CommandKind.Remove:
                if (world.Has(command.Entity, new ComponentType(command.Component)))
                {
                    world.RequireRemovable(command.Entity, command.Component);
                    world.Move(command.Entity, command.Component, add: false);
                }

                break;
            case CommandKind.Set:
                if (world.Has(command.Entity, new ComponentType(command.Component)))
                {
                    world.WriteBytes(command.Entity, command.Component, value);
                }

                break;
            case CommandKind.SetParent:
                if (world.IsAlive(command.Other))
                {
                    world.SetParentCore(command.Entity, command.Other, command.Policy, command.Keep);
                }

                break;
            case CommandKind.ClearParent:
                world.ClearParent(command.Entity);
                break;
        }
    }

    [HotPath]
    private void Append(in Command command, byte* value, int size)
    {
        lock (_gate)
        {
            int start = _bytes.Count;
            int length = sizeof(Command) + Align(size);
            _bytes.Reserve(Math.Max(start + length, _bytes.Count * 2));
            _bytes.Count = start + length;
            Command copy = command;
            copy.Size = size;
            *(Command*)(_bytes.Pointer + start) = copy;
            if (size > 0)
            {
                Buffer.MemoryCopy(value, _bytes.Pointer + start + sizeof(Command), size, size);
            }

            Count++;
        }
    }

    private enum CommandKind
    {
        Create,
        Destroy,
        Add,
        Remove,
        Set,
        SetParent,
        ClearParent,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Command
    {
        public CommandKind Kind;
        public EntityId Entity;
        public EntityId Other;
        public int Component;
        public int Size;
        public OrphanPolicy Policy;
        public bool Keep;
    }
}
