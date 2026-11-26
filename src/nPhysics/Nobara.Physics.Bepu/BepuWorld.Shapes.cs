// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.InteropServices;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Nobara.Core;
using BepuChild = BepuPhysics.Collidables.CompoundChild;

namespace Nobara.Physics.Bepu;

/// <summary>
/// The shapes of a world: in BEPUphysics, a shape belongs to one simulation,
/// so each world builds the shapes its bodies use, the first time one uses it,
/// and frees a shape once no body and no compound of the world uses it.
/// </summary>
/// <remarks>
/// BEPUphysics centers a convex hull on the centroid of its points; the world
/// places such a hull in a compound of one child, so that the origin of the
/// shape stays where nPhysics put it. A triangle of a mesh collides on its
/// front face, the side from which its vertices turn counterclockwise.
/// </remarks>
internal sealed partial class BepuWorld
{
    private readonly Dictionary<PhysicsShape, int> _shapeSlots = [];
    private readonly List<ShapeEntry> _shapeEntries = [];
    private int _shapeFree = -1;

    /// <summary>Gets the number of shapes the world holds.</summary>
    public int ShapeCount => _shapeSlots.Count;

    /// <summary>Gets the shape of BEPUphysics that a body uses.</summary>
    [HotPath]
    private TypedIndex ShapeIndex(int slot) => _shapeEntries[slot].Index;

    /// <summary>Finds or builds the shape of a neutral shape, and counts one more use.</summary>
    [HotPath]
    private int AcquireShape(PhysicsShape shape)
    {
        if (_shapeSlots.TryGetValue(shape, out int slot))
        {
            ShapeEntry existing = _shapeEntries[slot];
            existing.Uses++;
            _shapeEntries[slot] = existing;
            return slot;
        }

        ShapeDescription description = _shapeSource.Get(shape);
        ShapeEntry entry = Build(description);
        entry.Key = shape;
        entry.Uses = 1;
        if (_shapeFree >= 0)
        {
            slot = _shapeFree;
            _shapeFree = _shapeEntries[slot].NextFree;
            _shapeEntries[slot] = entry;
        }
        else
        {
            slot = _shapeEntries.Count;
            _shapeEntries.Add(entry);
        }

        _shapeSlots.Add(shape, slot);
        return slot;
    }

    /// <summary>Counts one use less, and frees the shape when nothing of the world uses it.</summary>
    [HotPath]
    private void ReleaseShape(int slot)
    {
        ShapeEntry entry = _shapeEntries[slot];
        entry.Uses--;
        if (entry.Uses > 0)
        {
            _shapeEntries[slot] = entry;
            return;
        }

        _shapeSlots.Remove(entry.Key);
        Simulation.Shapes.RemoveAndDispose(entry.Index, _pool);
        if (entry.Inner.Exists)
        {
            Simulation.Shapes.RemoveAndDispose(entry.Inner, _pool);
        }

        if (entry.GridCells != 0)
        {
            GCHandle.FromIntPtr(entry.GridCells).Free();
        }

        if (entry.Children.Allocated)
        {
            for (int i = 0; i < entry.Children.Length; i++)
            {
                ReleaseShape(entry.Children[i]);
            }

            _pool.Return(ref entry.Children);
        }

        _shapeEntries[slot] = new ShapeEntry { NextFree = _shapeFree };
        _shapeFree = slot;
    }

    /// <summary>Builds the shape of BEPUphysics of a neutral description.</summary>
    [HotPath]
    private ShapeEntry Build(ShapeDescription description)
    {
        Shapes shapes = Simulation.Shapes;
        switch (description.Kind)
        {
            case ShapeKind.Sphere:
                return Convex(shapes.Add(new Sphere(description.Radius)));
            case ShapeKind.Capsule:
                return Convex(shapes.Add(new Capsule(description.Radius, description.Length)));
            case ShapeKind.Box:
                return Convex(shapes.Add(new Box(description.Size.X, description.Size.Y, description.Size.Z)));
            case ShapeKind.Cylinder:
                return Convex(shapes.Add(new Cylinder(description.Radius, description.Length)));
            case ShapeKind.ConvexHull:
                return BuildHull(description);
            case ShapeKind.Compound:
                return BuildCompound(description);
            case ShapeKind.Voxels:
                return BuildVoxels(description);
            default:
                return BuildMesh(description);
        }
    }

    [HotPath]
    private static ShapeEntry Convex(TypedIndex index) => new() { Index = index, Raw = index };

    [HotPath]
    private ShapeEntry BuildHull(ShapeDescription description)
    {
        ConvexHull hull = Inertia.BuildHull(description.Points.Span, _pool, out Vector3 center);
        TypedIndex raw = Simulation.Shapes.Add(hull);
        if (center.LengthSquared() < 1e-12f)
        {
            return Convex(raw);
        }

        // One child at the centroid keeps the origin of the shape where the points put it.
        _pool.Take(1, out Buffer<BepuChild> children);
        children[0] = new BepuChild { LocalPosition = center, LocalOrientation = Quaternion.Identity, ShapeIndex = raw };
        TypedIndex wrapper = Simulation.Shapes.Add(new Compound(children));
        return new ShapeEntry { Index = wrapper, Inner = raw, Raw = raw, RawOffset = center };
    }

    [HotPath]
    private ShapeEntry BuildCompound(ShapeDescription description)
    {
        ReadOnlySpan<CompoundChild> source = description.Children.Span;
        _pool.Take(source.Length, out Buffer<int> slots);
        _pool.Take(source.Length, out Buffer<BepuChild> children);
        for (int i = 0; i < source.Length; i++)
        {
            CompoundChild child = source[i];
            int slot = AcquireShape(child.Shape);
            slots[i] = slot;
            ShapeEntry convex = _shapeEntries[slot];
            Quaternion rotation = Quaternion.Normalize(child.Rotation);
            children[i] = new BepuChild { LocalPosition = child.Position + Vector3.Transform(convex.RawOffset, rotation), LocalOrientation = rotation, ShapeIndex = convex.Raw };
        }

        TypedIndex index = Simulation.Shapes.Add(new Compound(children));
        return new ShapeEntry { Index = index, Raw = index, Children = slots.Slice(source.Length) };
    }

    [HotPath]
    private ShapeEntry BuildVoxels(ShapeDescription description)
    {
        nint cells = GCHandle.ToIntPtr(GCHandle.Alloc(description.Cells!));
        TypedIndex index = Simulation.Shapes.Add(new VoxelGridShape(cells, description.Origin));
        return new ShapeEntry { Index = index, Raw = index, GridCells = cells };
    }

    [HotPath]
    private ShapeEntry BuildMesh(ShapeDescription description)
    {
        ReadOnlySpan<Vector3> vertices = description.Points.Span;
        ReadOnlySpan<int> indices = description.Triangles.Span;
        int count = indices.Length / 3;
        _pool.Take(count, out Buffer<Triangle> triangles);
        for (int i = 0; i < count; i++)
        {
            // BEPUphysics collides on the side that sees the triangle turn clockwise: swap two vertices.
            triangles[i] = new Triangle(vertices[indices[3 * i]], vertices[indices[(3 * i) + 2]], vertices[indices[(3 * i) + 1]]);
        }

        TypedIndex index = Simulation.Shapes.Add(new Mesh(triangles, Vector3.One, _pool));
        return new ShapeEntry { Index = index, Raw = index };
    }

    /// <summary>A shape of the world.</summary>
    private struct ShapeEntry
    {
        /// <summary>The neutral shape.</summary>
        public PhysicsShape Key;

        /// <summary>The shape bodies use.</summary>
        public TypedIndex Index;

        /// <summary>A hull that <see cref="Index"/> wraps, freed with it.</summary>
        public TypedIndex Inner;

        /// <summary>The convex shape a compound holds as a child.</summary>
        public TypedIndex Raw;

        /// <summary>Where <see cref="Raw"/> sits in the frame of the neutral shape.</summary>
        public Vector3 RawOffset;

        /// <summary>The number of bodies and compounds that use the shape.</summary>
        public int Uses;

        /// <summary>The slots of the children of a compound, in the pool of the world.</summary>
        public Buffer<int> Children;

        /// <summary>The handle of the cells of the grid of a shape of voxels, freed with the shape; 0 for the others.</summary>
        public nint GridCells;

        /// <summary>The next free slot.</summary>
        public int NextFree;
    }
}
