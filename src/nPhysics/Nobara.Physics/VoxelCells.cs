// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.CompilerServices;
using Nobara.Core;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The cells of one chunk of a grid as the voxel layer of nPhysics gives them
/// to a backend: the collision boxes of each cell, read from the chunk where it
/// is, and the rule that sets apart the contacts of inner faces. A backend
/// reads it from the threads of its solver while a world steps.
/// </summary>
/// <remarks>
/// <para>
/// The boxes of a cell come from the shape of its block, turned by its
/// orientation, as nVoxel gives them: a cube is one box, a model or a part of
/// a multi-block the boxes of its shape, a shape that depends on the instance
/// the boxes it gave for the block data of the cell. A cell of air, or of a
/// block without collision (<c>&lt;Collision&gt;none</c>), has none. Boxes are
/// in the frame of the cell, from (0, 0, 0) to (1, 1, 1); cell (x, y, z) of the
/// chunk spans from (x, y, z) to (x + 1, y + 1, z + 1) in the frame of the
/// chunk.
/// </para>
/// <para>
/// nPhysics changes nothing in it while a world steps: blocks change at the
/// stage <c>World</c>, before the step, and the chunk does not change during
/// it. The threads of the solver read the chunk, the table of cells whose
/// shape depends on their instance, and the chunks of the grid next to it:
/// all of them only change on the loop thread, outside the step.
/// </para>
/// </remarks>
public sealed class VoxelChunkCells
{
    /// <summary>The largest number of boxes of one cell. A shape that gives more has its last boxes merged into one.</summary>
    public const int MaxBoxesPerCell = 16;

    private const int Size = VoxelCoordinates.ChunkSize;

    private readonly VoxelShapes? _shapes;
    private readonly CollisionBox[] _cube = [new CollisionBox(Vector3.Zero, Vector3.One)];
    private readonly Dictionary<int, InstanceCell> _instances = [];

    internal VoxelChunkCells(VoxelShapes? shapes, Grid? grid, Chunk chunk, Quaternion rotation)
    {
        _shapes = shapes;
        Grid = grid;
        Chunk = chunk;
        Rotation = rotation;
    }

    /// <summary>Gets the grid of the chunk, or <see langword="null"/> for a chunk on its own.</summary>
    public Grid? Grid { get; }

    /// <summary>Gets the chunk, read in place.</summary>
    public Chunk Chunk { get; }

    /// <summary>Gets the rotation of the grid: it turns the frame of the chunk into the frame of the world.</summary>
    public Quaternion Rotation { get; }

    /// <summary>
    /// Creates the cells of a chunk whose every cell that holds a block collides
    /// as a whole cube, whatever the block: for a backend tested without
    /// nPhysics, or a chunk whose states are not blocks.
    /// </summary>
    /// <param name="grid">The grid of the chunk, whose neighboring chunks are read across its borders; <see langword="null"/> for none.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="rotation">The rotation of the grid.</param>
    /// <returns>The cells.</returns>
    public static VoxelChunkCells Cubes(Grid? grid, Chunk chunk, Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return new VoxelChunkCells(null, grid, chunk, rotation);
    }

    /// <summary>Gets the collision boxes of a cell, in the frame of the cell.</summary>
    /// <param name="cell">The local index of the cell (<see cref="VoxelCoordinates.ToIndex(LocalBlockCoordinate)"/>).</param>
    /// <returns>The boxes, at most <see cref="MaxBoxesPerCell"/>; none for air or a block without collision.</returns>
    [HotPath]
    public ReadOnlySpan<CollisionBox> GetBoxes(int cell)
    {
        uint state = Chunk.Palette.Get(cell);
        if (state == 0)
        {
            return default;
        }

        if (_shapes is null)
        {
            return _cube;
        }

        ref readonly StateShape shape = ref _shapes.Get(state);
        if (shape.Instance)
        {
            return _instances.TryGetValue(cell, out InstanceCell instance) ? instance.Boxes : default;
        }

        return _shapes.Boxes(shape);
    }

    /// <summary>
    /// Tells whether a contact between a box of a cell and a body goes through
    /// an inner face: the face of the box that the normal leaves from lies on
    /// the face of its cell, and the cell next to it covers that face whole.
    /// A body cannot reach that face without going through the neighbor, so a
    /// backend drops the contact; otherwise a body that slides over a flat
    /// floor would catch on the joints between its cells.
    /// </summary>
    /// <param name="cell">The local index of the cell.</param>
    /// <param name="box">The index of the box in <see cref="GetBoxes"/>.</param>
    /// <param name="normal">The normal of the contact, out of the box toward the body, along the axes of the physics world.</param>
    /// <returns><see langword="true"/> when the contact goes through an inner face.</returns>
    [HotPath]
    public bool IsInnerContact(int cell, int box, Vector3 normal)
    {
        ReadOnlySpan<CollisionBox> boxes = GetBoxes(cell);
        if ((uint)box >= (uint)boxes.Length)
        {
            return false;
        }

        Vector3 local = Vector3.Transform(normal, Quaternion.Conjugate(Rotation));
        float x = MathF.Abs(local.X);
        float y = MathF.Abs(local.Y);
        float z = MathF.Abs(local.Z);
        CollisionBox bounds = boxes[box];
        BlockFace face;
        bool flush;
        if (y >= x && y >= z)
        {
            face = local.Y >= 0 ? BlockFace.Up : BlockFace.Down;
            flush = local.Y >= 0 ? bounds.Maximum.Y >= 1f : bounds.Minimum.Y <= 0f;
        }
        else if (x >= z)
        {
            face = local.X >= 0 ? BlockFace.East : BlockFace.West;
            flush = local.X >= 0 ? bounds.Maximum.X >= 1f : bounds.Minimum.X <= 0f;
        }
        else
        {
            face = local.Z >= 0 ? BlockFace.South : BlockFace.North;
            flush = local.Z >= 0 ? bounds.Maximum.Z >= 1f : bounds.Minimum.Z <= 0f;
        }

        return flush && IsFaceCovered(cell, face);
    }

    /// <summary>Tells whether the cell next to a cell, across one of its faces, covers the whole face they share.</summary>
    /// <param name="cell">The local index of the cell.</param>
    /// <param name="face">The face of the cell.</param>
    /// <returns><see langword="true"/> when the neighbor covers the face; <see langword="false"/> when it is air, does not cover it whole, or is in a chunk that is not in memory.</returns>
    [HotPath]
    public bool IsFaceCovered(int cell, BlockFace face)
    {
        int x = cell % Size;
        int z = cell / Size % Size;
        int y = cell / (Size * Size);
        int dx = 0;
        int dy = 0;
        int dz = 0;
        switch (face)
        {
            case BlockFace.North: dz = -1; break;
            case BlockFace.East: dx = 1; break;
            case BlockFace.South: dz = 1; break;
            case BlockFace.West: dx = -1; break;
            case BlockFace.Up: dy = 1; break;
            default: dy = -1; break;
        }

        int nx = x + dx;
        int ny = y + dy;
        int nz = z + dz;
        BlockFace opposite = Opposite(face);
        if ((uint)nx < Size && (uint)ny < Size && (uint)nz < Size)
        {
            return Covers(this, Index(nx, ny, nz), opposite);
        }

        if (Grid is null || Grid.IsDestroyed)
        {
            return false;
        }

        ChunkCoordinate coordinate = Chunk.Coordinate;
        var next = new ChunkCoordinate(coordinate.X + dx, coordinate.Y + dy, coordinate.Z + dz);
        if (!Grid.Storage.TryGetChunk(next, out Chunk? neighbor) || neighbor is null)
        {
            return false;
        }

        uint state = neighbor.Palette.Get(Index(nx & (Size - 1), ny & (Size - 1), nz & (Size - 1)));
        if (state == 0)
        {
            return false;
        }

        if (_shapes is null)
        {
            return true;
        }

        ref readonly StateShape shape = ref _shapes.Get(state);
        return !shape.Instance && (shape.Covers & (1 << (int)opposite)) != 0;   // the cells of another chunk that depend on their instance are not read
    }

    /// <summary>Gets the number of cells of the chunk whose boxes depend on their block data.</summary>
    internal int InstanceCount => _instances.Count;

    /// <summary>Gets or sets the number of physics worlds that hold the chunk.</summary>
    internal int Users { get; set; }

    /// <summary>Evaluates the boxes of every cell of the chunk whose shape depends on its block data. Not hot path code: it allocates the boxes it keeps.</summary>
    internal void EvaluateInstances(IShapeRegistry registry)
    {
        _instances.Clear();
        if (_shapes is null || !_shapes.HasInstanceShapes || !HoldsInstanceStates())
        {
            return;
        }

        for (int cell = 0; cell < Chunk.Volume; cell++)
        {
            uint state = Chunk.Palette.Get(cell);
            if (state != 0 && _shapes.Get(state).Instance)
            {
                Evaluate(registry, cell, state);
            }
        }
    }

    /// <summary>Follows a change of a cell: its boxes are evaluated again when its shape depends on its block data. Not hot path code.</summary>
    internal void Refresh(IShapeRegistry registry, int cell)
    {
        uint state = Chunk.Palette.Get(cell);
        if (_shapes is not null && state != 0 && _shapes.Get(state).Instance)
        {
            Evaluate(registry, cell, state);
        }
        else
        {
            _instances.Remove(cell);
        }
    }

    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(int x, int y, int z) => x + (Size * (z + (Size * y)));

    [HotPath]
    private static bool Covers(VoxelChunkCells cells, int cell, BlockFace face)
    {
        uint state = cells.Chunk.Palette.Get(cell);
        if (state == 0)
        {
            return false;
        }

        if (cells._shapes is null)
        {
            return true;
        }

        ref readonly StateShape shape = ref cells._shapes.Get(state);
        if (shape.Instance)
        {
            return cells._instances.TryGetValue(cell, out InstanceCell instance) && (instance.Covers & (1 << (int)face)) != 0;
        }

        return (shape.Covers & (1 << (int)face)) != 0;
    }

    [HotPath]
    private static BlockFace Opposite(BlockFace face) => face switch
    {
        BlockFace.North => BlockFace.South,
        BlockFace.East => BlockFace.West,
        BlockFace.South => BlockFace.North,
        BlockFace.West => BlockFace.East,
        BlockFace.Up => BlockFace.Down,
        _ => BlockFace.Up,
    };

    private bool HoldsInstanceStates()
    {
        Palette palette = Chunk.Palette;
        for (int i = 0; i < palette.Count; i++)
        {
            uint state = palette.GetEntry(i);
            if (state != 0 && _shapes!.Get(state).Instance)
            {
                return true;
            }
        }

        return false;
    }

    private void Evaluate(IShapeRegistry registry, int cell, uint state)
    {
        ShapeEvaluation evaluation = registry.Get(state, Chunk.GetBlockDataView(cell));
        CollisionBox[] boxes = VoxelShapes.Bound(evaluation.Collision);
        _instances[cell] = new InstanceCell(boxes, VoxelShapes.CoveredFaces(boxes));
    }

    /// <summary>The boxes of a cell whose shape depends on its block data, and the faces they cover whole.</summary>
    private readonly record struct InstanceCell(CollisionBox[] Boxes, byte Covers);
}

/// <summary>The collision of a block state, as the voxel layer keeps it.</summary>
internal struct StateShape
{
    /// <summary>The index of the first box in <see cref="VoxelShapes"/>.</summary>
    public int First;

    /// <summary>The number of boxes.</summary>
    public byte Count;

    /// <summary>The faces of the cell that one box covers whole, one bit for each <see cref="BlockFace"/>.</summary>
    public byte Covers;

    /// <summary>Whether the boxes depend on the block data of each cell.</summary>
    public bool Instance;
}

/// <summary>
/// The collision of every block state, read once from nVoxel when nPhysics
/// starts: its boxes, turned by its orientation, and the faces they cover.
/// </summary>
internal sealed class VoxelShapes
{
    private readonly StateShape[] _states;
    private readonly CollisionBox[] _boxes;

    private VoxelShapes(StateShape[] states, CollisionBox[] boxes, bool hasInstanceShapes)
    {
        _states = states;
        _boxes = boxes;
        HasInstanceShapes = hasInstanceShapes;
    }

    /// <summary>Gets whether a state has a shape that depends on the block data.</summary>
    public bool HasInstanceShapes { get; }

    /// <summary>Gets the number of states.</summary>
    public int StateCount => _states.Length;

    /// <summary>Reads the collision of every state from the registries. Not hot path code.</summary>
    public static VoxelShapes Build(int stateCount, Func<uint, bool> isInstance, Func<uint, CollisionShape> collision)
    {
        var states = new StateShape[Math.Max(stateCount, 1)];
        var boxes = new List<CollisionBox>();
        bool hasInstanceShapes = false;
        for (uint state = 1; state < (uint)stateCount; state++)
        {
            if (isInstance(state))
            {
                states[state] = new StateShape { Instance = true };
                hasInstanceShapes = true;
                continue;
            }

            CollisionBox[] bounded = Bound(collision(state));
            states[state] = new StateShape { First = boxes.Count, Count = (byte)bounded.Length, Covers = CoveredFaces(bounded) };
            boxes.AddRange(bounded);
        }

        return new VoxelShapes(states, [.. boxes], hasInstanceShapes);
    }

    /// <summary>Gets the collision of a state.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly StateShape Get(uint state) => ref _states[state < (uint)_states.Length ? state : 0];

    /// <summary>Gets the boxes of a state that does not depend on its instance.</summary>
    [HotPath]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<CollisionBox> Boxes(in StateShape shape) => new(_boxes, shape.First, shape.Count);

    /// <summary>Copies the boxes of a shape, merging those past <see cref="VoxelChunkCells.MaxBoxesPerCell"/> into one. Not hot path code.</summary>
    public static CollisionBox[] Bound(CollisionShape shape)
    {
        int count = Math.Min(shape.Count, VoxelChunkCells.MaxBoxesPerCell);
        var boxes = new CollisionBox[count];
        for (int i = 0; i < count; i++)
        {
            boxes[i] = shape[i];
        }

        if (shape.Count > count)
        {
            Vector3 minimum = boxes[count - 1].Minimum;
            Vector3 maximum = boxes[count - 1].Maximum;
            for (int i = count; i < shape.Count; i++)
            {
                minimum = Vector3.Min(minimum, shape[i].Minimum);
                maximum = Vector3.Max(maximum, shape[i].Maximum);
            }

            boxes[count - 1] = new CollisionBox(minimum, maximum);
        }

        return boxes;
    }

    /// <summary>Gets the faces of the cell that one box covers whole, one bit for each <see cref="BlockFace"/>.</summary>
    public static byte CoveredFaces(CollisionBox[] boxes)
    {
        int covers = 0;
        foreach (CollisionBox box in boxes)
        {
            bool spansX = box.Minimum.X <= 0f && box.Maximum.X >= 1f;
            bool spansY = box.Minimum.Y <= 0f && box.Maximum.Y >= 1f;
            bool spansZ = box.Minimum.Z <= 0f && box.Maximum.Z >= 1f;
            if (spansX && spansY)
            {
                covers |= (box.Minimum.Z <= 0f ? 1 << (int)BlockFace.North : 0) | (box.Maximum.Z >= 1f ? 1 << (int)BlockFace.South : 0);
            }

            if (spansY && spansZ)
            {
                covers |= (box.Minimum.X <= 0f ? 1 << (int)BlockFace.West : 0) | (box.Maximum.X >= 1f ? 1 << (int)BlockFace.East : 0);
            }

            if (spansX && spansZ)
            {
                covers |= (box.Minimum.Y <= 0f ? 1 << (int)BlockFace.Down : 0) | (box.Maximum.Y >= 1f ? 1 << (int)BlockFace.Up : 0);
            }
        }

        return (byte)covers;
    }
}
