// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Entity;

/// <summary>What happens to a child when its parent is destroyed.</summary>
public enum OrphanPolicy
{
    /// <summary>The child is detached: it becomes a root and keeps its place in the world.</summary>
    Detach,

    /// <summary>The child is destroyed with its parent, and so are its own children by their policies.</summary>
    Destroy,
}

/// <summary>
/// The transform of an entity relative to its parent: component
/// <c>nobara.entity.local_transform</c>. The entity module computes the
/// world transform of the child from it.
/// </summary>
/// <param name="Position">The position in the frame of the parent, in blocks of the parent scale.</param>
/// <param name="Rotation">The rotation in the frame of the parent; a unit quaternion.</param>
/// <param name="Scale">The scale relative to the parent.</param>
[HotPath]
public readonly record struct LocalTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>Gets the transform that places a child exactly on its parent.</summary>
    public static LocalTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Gets a transform at a position in the frame of the parent, without rotation or scale.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The transform.</returns>
    public static LocalTransform At(Vector3 position) => new(position, Quaternion.Identity, Vector3.One);
}

/// <summary>
/// The link from a child to its parent: component <c>nobara.entity.parent</c>.
/// Change it with <see cref="EntityWorld.SetParent"/> and
/// <see cref="EntityWorld.ClearParent"/>, or their commands; it cannot be
/// written directly.
/// </summary>
/// <remarks>
/// The link is a value, so changing the parent of an entity does not move it
/// to another archetype. An entity that was detached keeps the component,
/// with <see cref="EntityId.Null"/> as its parent.
/// </remarks>
public readonly struct ParentLink
{
    [HotPath]
    internal ParentLink(EntityId parent, int depth, OrphanPolicy whenParentDestroyed)
    {
        Parent = parent;
        Depth = depth;
        WhenParentDestroyed = whenParentDestroyed;
    }

    /// <summary>Gets the parent, or <see cref="EntityId.Null"/> for a root.</summary>
    public EntityId Parent { get; }

    /// <summary>Gets the number of ancestors of the entity: 1 for the child of a root, 0 for a root.</summary>
    public int Depth { get; }

    /// <summary>Gets what happens to the entity when its parent is destroyed.</summary>
    public OrphanPolicy WhenParentDestroyed { get; }
}
