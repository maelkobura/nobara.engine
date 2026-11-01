// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Renderer;

/// <summary>An object of the render world. The default value names no object.</summary>
/// <param name="Index">The slot of the object.</param>
/// <param name="Generation">The generation of the slot, so that an id of a destroyed object never names a newer one.</param>
[HotPath]
public readonly record struct RenderObjectId(int Index, int Generation)
{
    /// <summary>Gets whether the id was given by <see cref="RenderWorld.Create"/>; it may name a destroyed object.</summary>
    public bool IsValid => Generation != 0;
}

/// <summary>Describes a new object of the render world: a mesh, drawn with the materials of its slots or with one material, at a transform.</summary>
/// <param name="Mesh">The mesh.</param>
/// <param name="Transform">The transform.</param>
public readonly record struct RenderObjectDescription(RenderMesh Mesh, RenderTransform Transform)
{
    /// <summary>Gets the material of every part of the mesh, or <see langword="null"/> for the materials of its slots.</summary>
    public RenderMaterial? Material { get; init; }

    /// <summary>Gets whether the object is drawn; <see langword="true"/> by default.</summary>
    public bool Visible { get; init; } = true;
}

/// <summary>A part of an object to draw in a view: one submesh, its material, and its transform seen from the camera.</summary>
/// <param name="Object">The object.</param>
/// <param name="Mesh">The mesh.</param>
/// <param name="Submesh">The index of the part in <see cref="RenderMesh.Submeshes"/>.</param>
/// <param name="Material">The material of the part.</param>
/// <param name="Model">The matrix of the object, interpolated, with a translation relative to the camera; for row vectors.</param>
public readonly record struct RenderItem(RenderObjectId Object, RenderMesh Mesh, int Submesh, RenderMaterial Material, Matrix4x4 Model);

/// <summary>
/// The copy of the scene that nRenderer draws from: objects (a mesh, its
/// materials, and a transform), kept from frame to frame. Modules change it
/// only during extraction (<see cref="IRenderExtractor"/>), with the changes
/// since the previous frame.
/// </summary>
/// <remarks>
/// <para>
/// The world keeps the last two states of the simulation of each object. An
/// object that <see cref="Move"/> changes during a tick is drawn between its
/// previous and its new transform, by the fraction of tick the loop has
/// accumulated (<see cref="Core.Loop.PhaseTime.Alpha"/>); one changed by
/// <see cref="Teleport"/>, or just created, is drawn at its new transform.
/// When the loop has no fixed phase, nothing is interpolated.
/// </para>
/// <para>Use it on the loop thread of the client context. Its methods allocate nothing once its arrays have grown to the number of objects.</para>
/// </remarks>
public sealed class RenderWorld
{
    private ObjectSlot[] _slots = new ObjectSlot[64];
    private int[] _free = new int[64];
    private int _freeCount;
    private int _used;
    private bool _writable;

    internal RenderWorld()
    {
    }

    /// <summary>Gets the number of objects.</summary>
    public int Count { get; private set; }

    /// <summary>Gets whether the world can be changed now: during extraction.</summary>
    public bool IsWritable
    {
        [HotPath]
        get => _writable;
    }

    /// <summary>Gets the last tick of the simulation that the world has seen.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Gets whether objects moved with <see cref="Move"/> are interpolated between ticks: the loop has a fixed phase.</summary>
    public bool Interpolates { get; internal set; }

    internal ObjectSlot[] Slots => _slots;

    internal int SlotCount => _used;

    /// <summary>Creates an object.</summary>
    /// <param name="description">The object.</param>
    /// <returns>Its id.</returns>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentNullException">The mesh is missing.</exception>
    [HotPath]
    public RenderObjectId Create(in RenderObjectDescription description)
    {
        CheckWritable();
        ArgumentNullException.ThrowIfNull(description.Mesh);
        int index;
        if (_freeCount > 0)
        {
            index = _free[--_freeCount];
        }
        else
        {
            if (_used == _slots.Length)
            {
                Array.Resize(ref _slots, _slots.Length * 2);
                Array.Resize(ref _free, _slots.Length);
            }

            index = _used++;
        }

        ref ObjectSlot slot = ref _slots[index];
        int generation = slot.Generation + 1;
        slot = new ObjectSlot
        {
            Generation = generation,
            Alive = true,
            Visible = description.Visible,
            Mesh = description.Mesh,
            Material = description.Material,
            Previous = description.Transform,
            Current = description.Transform,
            MovedTick = ulong.MaxValue,
        };
        Count++;
        return new RenderObjectId(index, generation);
    }

    /// <summary>Moves an object as the simulation did during the last tick: it is drawn between its previous and its new transform until the next tick.</summary>
    /// <param name="id">The object.</param>
    /// <param name="transform">The transform at the last tick.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The object does not exist.</exception>
    [HotPath]
    public void Move(RenderObjectId id, in RenderTransform transform)
    {
        ref ObjectSlot slot = ref Get(id);
        if (!Interpolates)
        {
            slot.Previous = transform;
        }
        else if (slot.MovedTick != Tick)
        {
            // The first change of a tick: the transform drawn so far becomes the previous state.
            slot.Previous = slot.Current;
            slot.MovedTick = Tick;
        }

        slot.Current = transform;
    }

    /// <summary>Places an object at a transform without interpolation, for a jump or for an object that the frame moves rather than the tick.</summary>
    /// <param name="id">The object.</param>
    /// <param name="transform">The transform.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The object does not exist.</exception>
    [HotPath]
    public void Teleport(RenderObjectId id, in RenderTransform transform)
    {
        ref ObjectSlot slot = ref Get(id);
        slot.Previous = transform;
        slot.Current = transform;
        slot.MovedTick = ulong.MaxValue;
    }

    /// <summary>Changes the mesh and the material of an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="mesh">The mesh.</param>
    /// <param name="material">The material of every part, or <see langword="null"/> for the materials of the slots of the mesh.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The object does not exist.</exception>
    [HotPath]
    public void SetMesh(RenderObjectId id, RenderMesh mesh, RenderMaterial? material = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ref ObjectSlot slot = ref Get(id);
        slot.Mesh = mesh;
        slot.Material = material;
    }

    /// <summary>Shows or hides an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="visible"><see langword="true"/> to draw it.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The object does not exist.</exception>
    [HotPath]
    public void SetVisible(RenderObjectId id, bool visible) => Get(id).Visible = visible;

    /// <summary>Destroys an object; it is no longer drawn from this frame on.</summary>
    /// <param name="id">The object.</param>
    /// <exception cref="InvalidOperationException">The world is not being extracted.</exception>
    /// <exception cref="ArgumentException">The object does not exist.</exception>
    [HotPath]
    public void Destroy(RenderObjectId id)
    {
        ref ObjectSlot slot = ref Get(id);
        int generation = slot.Generation;
        slot = new ObjectSlot { Generation = generation };
        _free[_freeCount++] = id.Index;
        Count--;
    }

    /// <summary>Gets whether an object exists.</summary>
    /// <param name="id">The object.</param>
    /// <returns><see langword="true"/> when it was created and not destroyed.</returns>
    [HotPath]
    public bool Contains(RenderObjectId id) =>
        (uint)id.Index < (uint)_used && _slots[id.Index].Alive && _slots[id.Index].Generation == id.Generation;

    /// <summary>Gets the last transform given to an object.</summary>
    /// <param name="id">The object.</param>
    /// <returns>The transform.</returns>
    /// <exception cref="ArgumentException">The object does not exist.</exception>
    [HotPath]
    public RenderTransform GetTransform(RenderObjectId id)
    {
        CheckExists(id);
        return _slots[id.Index].Current;
    }

    /// <summary>Opens the world to the extractors of a frame.</summary>
    [HotPath]
    internal void BeginExtraction(ulong tick)
    {
        Tick = tick;
        _writable = true;
    }

    /// <summary>Closes the world until the next frame.</summary>
    [HotPath]
    internal void EndExtraction() => _writable = false;

    [HotPath]
    private ref ObjectSlot Get(RenderObjectId id)
    {
        CheckWritable();
        CheckExists(id);
        return ref _slots[id.Index];
    }

    [HotPath]
    private void CheckExists(RenderObjectId id)
    {
        if (!Contains(id))
        {
            throw new ArgumentException($"The render object {id} does not exist: it was destroyed, or never created by this world.", nameof(id));
        }
    }

    [HotPath]
    private void CheckWritable()
    {
        if (!_writable)
        {
            throw new InvalidOperationException("The render world changes during extraction only: change it from an extractor (IRenderer.AddExtractor).");
        }
    }

    /// <summary>The state of one object.</summary>
    internal struct ObjectSlot
    {
        public int Generation;
        public bool Alive;
        public bool Visible;
        public RenderMesh? Mesh;
        public RenderMaterial? Material;
        public RenderTransform Previous;
        public RenderTransform Current;

        // The tick of the last call to Move, or MaxValue: the object is not interpolated.
        public ulong MovedTick;
    }
}
