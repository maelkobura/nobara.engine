// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Entity;

/// <content>The hierarchy of the entities: parents, children, and their depths.</content>
public sealed unsafe partial class EntityWorld
{
    /// <summary>
    /// Makes an entity the child of another. The child gets the components
    /// it lacks among <see cref="IEntityWorlds.WorldTransform"/>,
    /// <see cref="IEntityWorlds.LocalTransform"/>, and
    /// <see cref="IEntityWorlds.ParentLink"/>; from then on, its world
    /// transform follows its parent.
    /// </summary>
    /// <param name="child">The child.</param>
    /// <param name="parent">The parent; it must have a world transform.</param>
    /// <param name="whenParentDestroyed">What happens to the child when the parent is destroyed.</param>
    /// <param name="keepWorldTransform">
    /// <see langword="true"/> to keep the child where it is in the world: its
    /// local transform is computed from both world transforms.
    /// <see langword="false"/> to keep its local transform: it moves to its
    /// place relative to the parent.
    /// </param>
    /// <exception cref="ArgumentException">An entity is not alive in this world, the parent is the child itself or one of its descendants, or the parent has no world transform.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    /// <remarks>Changing the parent of an entity that already has these components moves it to no other archetype.</remarks>
    public void SetParent(EntityId child, EntityId parent, OrphanPolicy whenParentDestroyed = OrphanPolicy.Detach, bool keepWorldTransform = true)
    {
        RequireUnlocked();
        _owner.Structure.CheckWrite();
        SetParentCore(child, parent, whenParentDestroyed, keepWorldTransform);
    }

    /// <summary>
    /// Detaches an entity from its parent: it becomes a root and keeps its
    /// place in the world. Nothing happens for a root.
    /// </summary>
    /// <param name="child">The entity.</param>
    /// <exception cref="ArgumentException">The entity is not alive in this world.</exception>
    /// <exception cref="InvalidOperationException">A system runs on the world: use its command buffer.</exception>
    [HotPath]
    public void ClearParent(EntityId child)
    {
        RequireUnlocked();
        _owner.Structure.CheckWrite();
        if (Record(child).Parent >= 0)
        {
            Detach(child);
        }
    }

    /// <summary>Gets the parent of an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The parent, or <see cref="EntityId.Null"/> for a root.</returns>
    /// <exception cref="ArgumentException">The entity is not alive in this world.</exception>
    [HotPath]
    public EntityId GetParent(EntityId entity)
    {
        int parent = Record(entity).Parent;
        return parent < 0 ? EntityId.Null : new EntityId(parent, _records[parent].Generation);
    }

    /// <summary>Gets the children of an entity, for <c>foreach</c>.</summary>
    /// <param name="parent">The entity.</param>
    /// <returns>The children; do not change the hierarchy while going through them.</returns>
    /// <exception cref="ArgumentException">The entity is not alive in this world.</exception>
    [HotPath]
    public ChildEnumerator GetChildren(EntityId parent) => new(this, Record(parent).FirstChild);

    /// <summary>Gets the depth of an entity in the hierarchy: 0 for a root, 1 for the child of a root.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The depth.</returns>
    /// <exception cref="ArgumentException">The entity is not alive in this world.</exception>
    [HotPath]
    public int GetDepth(EntityId entity) => Record(entity).Parent < 0 ? 0 : DepthOf(entity);

    [HotPath]
    internal void SetParentCore(EntityId child, EntityId parent, OrphanPolicy whenParentDestroyed, bool keepWorldTransform)
    {
        _ = Record(child);
        _ = Record(parent);
        if (child == parent)
        {
            throw new ArgumentException($"The {child} cannot be its own parent.", nameof(parent));
        }

        for (int ancestor = parent.Index; ancestor >= 0; ancestor = _records[ancestor].Parent)
        {
            if (ancestor == child.Index)
            {
                throw new ArgumentException($"The {parent} is a descendant of the {child}: a hierarchy has no cycle.", nameof(parent));
            }
        }

        ComponentType<WorldTransform> world = _owner.WorldTransform;
        ComponentType<LocalTransform> local = _owner.LocalTransform;
        if (!Has(parent, world))
        {
            throw new ArgumentException($"The {parent} has no world transform ('{EntityWorlds.WorldTransformName}'), so it cannot be a parent.", nameof(parent));
        }

        bool hadWorld = Has(child, world);
        if (!hadWorld)
        {
            Move(child, world.Untyped.Index, add: true);
        }

        if (!Has(child, local))
        {
            Move(child, local.Untyped.Index, add: true);
        }

        if (!Has(child, _owner.ParentLink))
        {
            Move(child, EntityWorlds.ParentIndex, add: true);
        }

        WorldTransform parentWorld = *(WorldTransform*)Address(parent, world.Untyped.Index, out _, out _, out _);
        if (keepWorldTransform && hadWorld)
        {
            WorldTransform childWorld = *(WorldTransform*)Address(child, world.Untyped.Index, out _, out _, out _);
            WriteValue(child, local, Relative(parentWorld, childWorld));
        }

        LocalTransform value = *(LocalTransform*)Address(child, local.Untyped.Index, out _, out _, out _);
        WriteValue(child, world, parentWorld.Combine(value.Position, value.Rotation, value.Scale));

        if (_records[child.Index].Parent >= 0)
        {
            Unlink(child.Index);
        }

        Link(child.Index, parent.Index);
        int depth = (_records[parent.Index].Parent < 0 ? 0 : DepthOf(parent)) + 1;
        SetSubtreeDepth(child, depth, parent, whenParentDestroyed);
    }

    /// <summary>Detaches a child that has a parent: it keeps its place in the world.</summary>
    [HotPath]
    internal void Detach(EntityId child)
    {
        ComponentType<WorldTransform> world = _owner.WorldTransform;
        ComponentType<LocalTransform> local = _owner.LocalTransform;
        EntityId parent = GetParent(child);

        // Its world transform may be a tick old: compute it from the parent as it is now.
        if (Has(parent, world))
        {
            WorldTransform parentWorld = *(WorldTransform*)Address(parent, world.Untyped.Index, out _, out _, out _);
            LocalTransform value = *(LocalTransform*)Address(child, local.Untyped.Index, out _, out _, out _);
            WriteValue(child, world, parentWorld.Combine(value.Position, value.Rotation, value.Scale));
        }

        OrphanPolicy policy = ((ParentLink*)Address(child, EntityWorlds.ParentIndex, out _, out _, out _))->WhenParentDestroyed;
        Unlink(child.Index);
        SetSubtreeDepth(child, 0, EntityId.Null, policy);
    }

    /// <summary>Gets the depth written in the link of an entity that has a parent.</summary>
    [HotPath]
    internal int DepthOf(EntityId entity) => ((ParentLink*)Address(entity, EntityWorlds.ParentIndex, out _, out _, out _))->Depth;

    /// <summary>Removes an entity from the children of its parent.</summary>
    [HotPath]
    internal void Unlink(int index)
    {
        ref EntityRecord record = ref _records[index];
        if (record.PreviousSibling >= 0)
        {
            _records[record.PreviousSibling].NextSibling = record.NextSibling;
        }
        else
        {
            _records[record.Parent].FirstChild = record.NextSibling;
        }

        if (record.NextSibling >= 0)
        {
            _records[record.NextSibling].PreviousSibling = record.PreviousSibling;
        }

        record.Parent = -1;
        record.NextSibling = -1;
        record.PreviousSibling = -1;
    }

    [HotPath]
    private static LocalTransform Relative(in WorldTransform parent, in WorldTransform child)
    {
        System.Numerics.Quaternion rotation = System.Numerics.Quaternion.Normalize(
            System.Numerics.Quaternion.Concatenate(child.Rotation, System.Numerics.Quaternion.Conjugate(parent.Rotation)));
        System.Numerics.Vector3 scale = new(
            parent.Scale.X == 0 ? 0 : child.Scale.X / parent.Scale.X,
            parent.Scale.Y == 0 ? 0 : child.Scale.Y / parent.Scale.Y,
            parent.Scale.Z == 0 ? 0 : child.Scale.Z / parent.Scale.Z);
        return new LocalTransform(parent.InverseTransformPoint(child.Position), rotation, scale);
    }

    [HotPath]
    private void Link(int child, int parent)
    {
        ref EntityRecord record = ref _records[child];
        record.Parent = parent;
        record.PreviousSibling = -1;
        record.NextSibling = _records[parent].FirstChild;
        if (record.NextSibling >= 0)
        {
            _records[record.NextSibling].PreviousSibling = child;
        }

        _records[parent].FirstChild = child;
    }

    /// <summary>
    /// Writes the link of an entity, then the depths of its descendants, and
    /// keeps the count of entities per depth. The walk follows the links of
    /// the records, so it allocates nothing.
    /// </summary>
    [HotPath]
    private void SetSubtreeDepth(EntityId root, int depth, EntityId parent, OrphanPolicy policy)
    {
        ParentLink* link = (ParentLink*)Address(root, EntityWorlds.ParentIndex, out _, out _, out _);
        int previous = link->Parent.IsNull ? 0 : link->Depth;
        *link = new ParentLink(parent, depth, policy);
        CountDepth(previous, depth);

        int node = _records[root.Index].FirstChild;
        int nodeDepth = depth + 1;
        while (node >= 0)
        {
            var entity = new EntityId(node, _records[node].Generation);
            link = (ParentLink*)Address(entity, EntityWorlds.ParentIndex, out _, out _, out _);
            CountDepth(link->Depth, nodeDepth);
            *link = new ParentLink(link->Parent, nodeDepth, link->WhenParentDestroyed);

            if (_records[node].FirstChild >= 0)
            {
                node = _records[node].FirstChild;
                nodeDepth++;
                continue;
            }

            while (node != root.Index && _records[node].NextSibling < 0)
            {
                node = _records[node].Parent;
                nodeDepth--;
            }

            node = node == root.Index ? -1 : _records[node].NextSibling;
        }
    }

    [HotPath]
    private void CountDepth(int previous, int depth)
    {
        if (previous > 0)
        {
            _depths[previous]--;
        }

        if (depth > 0)
        {
            while (_depths.Count <= depth)
            {
                _depths.Add(0);
            }

            _depths[depth]++;
        }
    }

    /// <summary>Goes through the children of an entity.</summary>
    public ref struct ChildEnumerator
    {
        private readonly EntityWorld _world;
        private int _next;

        [HotPath]
        internal ChildEnumerator(EntityWorld world, int first)
        {
            _world = world;
            _next = first;
            Current = EntityId.Null;
        }

        /// <summary>Gets the current child.</summary>
        public EntityId Current { readonly get; private set; }

        /// <summary>Returns the enumerator itself, for <c>foreach</c>.</summary>
        /// <returns>The enumerator.</returns>
        [HotPath]
        public readonly ChildEnumerator GetEnumerator() => this;

        /// <summary>Moves to the next child.</summary>
        /// <returns><see langword="false"/> after the last child.</returns>
        [HotPath]
        public bool MoveNext()
        {
            if (_next < 0)
            {
                return false;
            }

            Current = new EntityId(_next, _world._records[_next].Generation);
            _next = _world._records[_next].NextSibling;
            return true;
        }
    }
}
