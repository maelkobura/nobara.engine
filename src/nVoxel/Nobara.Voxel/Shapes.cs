// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Core;

namespace Nobara.Voxel;

/// <summary>The six faces of a voxel cell, in world-axis order.</summary>
public enum BlockFace : byte
{
    /// <summary>The face toward negative Z.</summary>
    North,
    /// <summary>The face toward positive X.</summary>
    East,
    /// <summary>The face toward positive Z.</summary>
    South,
    /// <summary>The face toward negative X.</summary>
    West,
    /// <summary>The face toward positive Y.</summary>
    Up,
    /// <summary>The face toward negative Y.</summary>
    Down,
}

/// <summary>A mask of the faces covered by a shape.</summary>
public readonly struct FaceMask : IEquatable<FaceMask>
{
    private readonly byte _bits;

    /// <summary>Creates a face mask.</summary>
    /// <param name="bits">One bit for each <see cref="BlockFace"/>.</param>
    public FaceMask(byte bits) => _bits = (byte)(bits & 0x3F);

    /// <summary>Gets an empty mask.</summary>
    public static FaceMask Empty => default;

    /// <summary>Gets a mask containing all six faces.</summary>
    public static FaceMask All => new(0x3F);

    /// <summary>Gets whether a face is present.</summary>
    /// <param name="face">The face.</param>
    public bool this[BlockFace face] => (_bits & (1 << (int)face)) != 0;

    /// <summary>Gets the bits of this mask.</summary>
    public byte Bits => _bits;

    /// <inheritdoc/>
    public bool Equals(FaceMask other) => _bits == other._bits;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FaceMask other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _bits;

    /// <summary>Compares two face masks.</summary>
    public static bool operator ==(FaceMask left, FaceMask right) => left.Equals(right);

    /// <summary>Compares two face masks.</summary>
    public static bool operator !=(FaceMask left, FaceMask right) => !left.Equals(right);

    internal static FaceMask Set(BlockFace face) => new((byte)(1 << (int)face));
}

/// <summary>A unit-cube axis-aligned box used by the neutral voxel collision API.</summary>
public readonly struct CollisionBox : IEquatable<CollisionBox>
{
    /// <summary>Creates a box in local block coordinates.</summary>
    /// <param name="minimum">The inclusive minimum corner, in `[0,1]³`.</param>
    /// <param name="maximum">The exclusive maximum corner, in `[0,1]³`.</param>
    /// <exception cref="ArgumentException">A corner is outside the unit cube or the maximum is below the minimum.</exception>
    public CollisionBox(Vector3 minimum, Vector3 maximum)
    {
        if (!IsUnit(minimum) || !IsUnit(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
        {
            throw new ArgumentException("A collision box must be ordered and contained in the unit cube.");
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the minimum corner.</summary>
    public Vector3 Minimum { get; }

    /// <summary>Gets the maximum corner.</summary>
    public Vector3 Maximum { get; }

    /// <inheritdoc/>
    public bool Equals(CollisionBox other) => Minimum == other.Minimum && Maximum == other.Maximum;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CollisionBox other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Minimum, Maximum);

    private static bool IsUnit(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && value.X >= 0 && value.X <= 1 && value.Y >= 0 && value.Y <= 1 && value.Z >= 0 && value.Z <= 1;
}

/// <summary>An immutable collection of neutral collision boxes.</summary>
public sealed class CollisionShape
{
    private readonly CollisionBox[] _boxes;

    /// <summary>Creates a collision shape from boxes.</summary>
    /// <param name="boxes">The boxes, all inside the unit cube.</param>
    public CollisionShape(IEnumerable<CollisionBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        _boxes = boxes.ToArray();
    }

    /// <summary>Gets an empty collision shape.</summary>
    public static CollisionShape Empty { get; } = new(Array.Empty<CollisionBox>());

    /// <summary>Gets the full unit-cube collision shape.</summary>
    public static CollisionShape FullBlock { get; } = new([new CollisionBox(Vector3.Zero, Vector3.One)]);

    /// <summary>Gets the number of boxes.</summary>
    public int Count => _boxes.Length;

    /// <summary>Gets a box by index.</summary>
    /// <param name="index">The box index.</param>
    public CollisionBox this[int index] => _boxes[index];
}

/// <summary>The 4 × 4 contact mask of the six faces of a shape.</summary>
public readonly struct ContactMasks : IEquatable<ContactMasks>
{
    private readonly ushort _north;
    private readonly ushort _east;
    private readonly ushort _south;
    private readonly ushort _west;
    private readonly ushort _up;
    private readonly ushort _down;

    internal ContactMasks(ushort north, ushort east, ushort south, ushort west, ushort up, ushort down) =>
        (_north, _east, _south, _west, _up, _down) = (north, east, south, west, up, down);

    /// <summary>Gets an empty set of masks.</summary>
    public static ContactMasks Empty => default;

    /// <summary>Gets a full 4 × 4 mask on every face.</summary>
    public static ContactMasks Full => new(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);

    /// <summary>Gets the 16-bit mask of one face.</summary>
    /// <param name="face">The face.</param>
    public ushort this[BlockFace face] => face switch
    {
        BlockFace.North => _north,
        BlockFace.East => _east,
        BlockFace.South => _south,
        BlockFace.West => _west,
        BlockFace.Up => _up,
        BlockFace.Down => _down,
        _ => 0,
    };

    /// <summary>Computes contact cells from boxes that touch a face.</summary>
    /// <param name="collision">The collision boxes.</param>
    /// <returns>The derived masks.</returns>
    public static ContactMasks FromCollision(CollisionShape collision)
    {
        ArgumentNullException.ThrowIfNull(collision);
        ushort[] masks = new ushort[6];
        for (int i = 0; i < collision.Count; i++)
        {
            CollisionBox box = collision[i];
            AddFace(masks, BlockFace.North, box.Minimum.Z == 0, box.Minimum.X, box.Maximum.X, box.Minimum.Y, box.Maximum.Y);
            AddFace(masks, BlockFace.East, box.Maximum.X == 1, 1 - box.Maximum.Z, 1 - box.Minimum.Z, box.Minimum.Y, box.Maximum.Y);
            AddFace(masks, BlockFace.South, box.Maximum.Z == 1, box.Minimum.X, box.Maximum.X, box.Minimum.Y, box.Maximum.Y);
            AddFace(masks, BlockFace.West, box.Minimum.X == 0, box.Minimum.Z, box.Maximum.Z, box.Minimum.Y, box.Maximum.Y);
            AddFace(masks, BlockFace.Up, box.Maximum.Y == 1, box.Minimum.X, box.Maximum.X, box.Minimum.Z, box.Maximum.Z);
            AddFace(masks, BlockFace.Down, box.Minimum.Y == 0, box.Minimum.X, box.Maximum.X, 1 - box.Maximum.Z, 1 - box.Minimum.Z);
        }

        return new ContactMasks(masks[0], masks[1], masks[2], masks[3], masks[4], masks[5]);
    }

    /// <inheritdoc/>
    public bool Equals(ContactMasks other) => _north == other._north && _east == other._east && _south == other._south && _west == other._west && _up == other._up && _down == other._down;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ContactMasks other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_north, _east, _south, _west, _up, _down);

    /// <summary>Compares two contact mask sets.</summary>
    public static bool operator ==(ContactMasks left, ContactMasks right) => left.Equals(right);

    /// <summary>Compares two contact mask sets.</summary>
    public static bool operator !=(ContactMasks left, ContactMasks right) => !left.Equals(right);

    private static void AddFace(ushort[] masks, BlockFace face, bool touches, float u0, float u1, float v0, float v1)
    {
        if (!touches) return;
        int fromU = Math.Clamp((int)MathF.Floor(u0 * 4), 0, 3);
        int toU = Math.Clamp((int)MathF.Ceiling(u1 * 4) - 1, 0, 3);
        int fromV = Math.Clamp((int)MathF.Floor(v0 * 4), 0, 3);
        int toV = Math.Clamp((int)MathF.Ceiling(v1 * 4) - 1, 0, 3);
        for (int v = fromV; v <= toV; v++)
        {
            for (int u = fromU; u <= toU; u++)
            {
                masks[(int)face] |= (ushort)(1 << (v * 4 + u));
            }
        }
    }
}

/// <summary>
/// A read-only view of block data at one cell: of the cell of a chunk, read as
/// it is now (<see cref="Chunk.GetBlockDataView"/>), or of a frozen copy that
/// a background job may read (<see cref="Chunk.CopyBlockData"/>).
/// </summary>
public readonly struct BlockDataView
{
    private readonly Chunk? _chunk;
    private readonly int _localIndex;
    private readonly BlockDataEntry[]? _entries;

    internal BlockDataView(Chunk chunk, int localIndex) => (_chunk, _localIndex, _entries) = (chunk, localIndex, null);

    internal BlockDataView(BlockDataEntry[] entries) => (_chunk, _localIndex, _entries) = (null, 0, entries);

    /// <summary>Gets an empty view used for type-only shape evaluation.</summary>
    public static BlockDataView Empty => default;

    /// <summary>Gets whether the view is a frozen copy, which reads nothing of its chunk.</summary>
    public bool IsCopy => _entries is not null;

    /// <summary>Reads a typed value, or the default value when none is attached.</summary>
    /// <typeparam name="T">The block-data value type.</typeparam>
    /// <param name="type">The declared data type.</param>
    /// <returns>The attached value, or its default.</returns>
    [HotPath]
    public T Get<T>(BlockDataType<T> type)
    {
        if (_entries is not null)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (ReferenceEquals(_entries[i].Type, type)) return (T)_entries[i].Value;
            }

            return default!;
        }

        return _chunk is null ? default! : _chunk.GetBlockData(type, _localIndex);
    }
}

/// <summary>The input supplied to a block shape.</summary>
public readonly struct BlockShapeRequest
{
    internal BlockShapeRequest(uint state, BlockType type, int part, BlockDataView data) => (State, Type, Part, Data) = (state, type, part, data);

    /// <summary>Gets the state with orientation reset to the reference orientation.</summary>
    public uint State { get; }

    /// <summary>Gets the type of the block.</summary>
    public BlockType Type { get; }

    /// <summary>Gets the multi-block part index.</summary>
    public int Part { get; }

    /// <summary>Gets the block-data view.</summary>
    public BlockDataView Data { get; }
}

/// <summary>The result of evaluating a block shape in its reference orientation.</summary>
public readonly struct ShapeEvaluation
{
    /// <summary>Creates an evaluation result.</summary>
    /// <param name="collision">The neutral collision.</param>
    /// <param name="opacity">The faces fully covered by the shape.</param>
    /// <param name="contact">The contact masks; use <see cref="ContactMasks.FromCollision"/> for the normal derived value.</param>
    /// <param name="mass">An optional instance mass.</param>
    /// <param name="fluidCapacity">An optional fluid capacity from 0 to 1.</param>
    /// <param name="normalizationState">An optional replacement state requested by the shape.</param>
    public ShapeEvaluation(CollisionShape collision, FaceMask opacity, ContactMasks contact, float? mass = null, float? fluidCapacity = null, uint? normalizationState = null)
    {
        Collision = collision ?? throw new ArgumentNullException(nameof(collision));
        Opacity = opacity;
        Contact = contact;
        Mass = mass;
        FluidCapacity = fluidCapacity;
        NormalizationState = normalizationState;
    }

    /// <summary>Gets the collision.</summary>
    public CollisionShape Collision { get; }

    /// <summary>Gets the opaque face mask.</summary>
    public FaceMask Opacity { get; }

    /// <summary>Gets the contact masks.</summary>
    public ContactMasks Contact { get; }

    /// <summary>Gets the optional instance mass.</summary>
    public float? Mass { get; }

    /// <summary>Gets the optional instance fluid capacity.</summary>
    public float? FluidCapacity { get; }

    /// <summary>Gets the optional state to replace this block with.</summary>
    public uint? NormalizationState { get; }
}

/// <summary>A shape implementation registered with nVoxel.</summary>
public interface IBlockShape
{
    /// <summary>Gets the qualified shape identifier.</summary>
    string Id { get; }

    /// <summary>Gets whether evaluation reads per-instance block data.</summary>
    bool IsInstanceDependent { get; }

    /// <summary>Evaluates the shape in the reference orientation.</summary>
    /// <param name="request">The type, normalized state, part, and data view.</param>
    /// <returns>The collision and optional instance properties.</returns>
    ShapeEvaluation Evaluate(in BlockShapeRequest request);
}

/// <summary>The shape registry and the read-only shape query API.</summary>
public interface IShapeRegistry
{
    /// <summary>Gets whether registration is closed and validation is complete.</summary>
    bool IsFrozen { get; }

    /// <summary>Gets the registered shapes, in identifier order.</summary>
    IReadOnlyList<IBlockShape> Shapes { get; }

    /// <summary>Registers a shape before nVoxel initializes.</summary>
    /// <param name="owner">The module registering the shape.</param>
    /// <param name="shape">The shape implementation.</param>
    /// <returns>A handle that can revoke the registration before initialization completes.</returns>
    IDisposable Register(ModuleContext owner, IBlockShape shape);

    /// <summary>Finds a registered shape.</summary>
    /// <param name="id">The qualified shape identifier.</param>
    /// <param name="shape">The registered shape, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the shape exists.</returns>
    bool TryGet(string id, out IBlockShape? shape);

    /// <summary>Gets the evaluated shape for a block state and data view.</summary>
    /// <param name="state">The nBlocks state index.</param>
    /// <param name="data">The block-data view.</param>
    /// <returns>The oriented evaluation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The state is unknown.</exception>
    ShapeEvaluation Get(uint state, in BlockDataView data);

    /// <summary>
    /// Gets the orientation of a block state: the rotation that turns the
    /// block as its shape is described, in the reference orientation, into the
    /// block of this state. It acts on positions in the cell, from (0, 0, 0)
    /// to (1, 1, 1), and turns them around the center of the cell; the
    /// collision and the face opacity of <see cref="Get"/> are turned by it.
    /// </summary>
    /// <param name="state">The nBlocks state index.</param>
    /// <returns>The matrix, for row vectors (<see cref="Vector3.Transform(Vector3, Matrix4x4)"/>); the identity for a state without orientation.</returns>
    /// <exception cref="InvalidOperationException">nVoxel did not start.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The state is unknown.</exception>
    Matrix4x4 GetOrientation(uint state);
}
