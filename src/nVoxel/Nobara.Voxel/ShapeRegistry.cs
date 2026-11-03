// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Collections.Immutable;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Core.Resources;

namespace Nobara.Voxel;

internal sealed class ShapeRegistry : IShapeRegistry
{
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBlockShape> _native = new(StringComparer.Ordinal);
    private IBlockRegistry? _blocks;
    private ShapeEvaluation[] _cache = [];
    private IReadOnlyList<IBlockShape> _shapes = [];

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<IBlockShape> Shapes => IsFrozen ? _shapes : throw new InvalidOperationException("Voxel shapes are read after nVoxel starts.");

    public IDisposable Register(ModuleContext owner, IBlockShape shape)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(shape);
        if (IsFrozen) throw new InvalidOperationException("Voxel shapes are already frozen.");
        if (!IsShapeId(shape.Id)) throw new ArgumentException("The shape identifier must be a shape object identifier.", nameof(shape));
        if (_registrations.ContainsKey(shape.Id) || _native.ContainsKey(shape.Id)) throw new InvalidOperationException($"The shape '{shape.Id}' is already registered.");
        var registration = new Registration(this, owner.Module.Id, shape);
        _registrations.Add(shape.Id, registration);
        return registration;
    }

    public bool TryGet(string id, out IBlockShape? shape)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!IsFrozen)
        {
            shape = null;
            return false;
        }

        if (_native.TryGetValue(id, out shape)) return true;
        if (_registrations.TryGetValue(id, out Registration? registration) && !registration.Revoked)
        {
            shape = registration.Shape;
            return true;
        }

        shape = null;
        return false;
    }

    public ShapeEvaluation Get(uint state, in BlockDataView data)
    {
        if (!IsFrozen || _blocks is null) throw new InvalidOperationException("Voxel shapes are not initialized.");
        if (state >= (uint)_cache.Length) throw new ArgumentOutOfRangeException(nameof(state));
        BlockType type = _blocks.TypeOf(state);
        if (type.IsAir) return new ShapeEvaluation(CollisionShape.Empty, FaceMask.Empty, ContactMasks.Empty);
        IBlockShape shape = FindShape(type.Shape);
        if (!shape.IsInstanceDependent) return _cache[state];
        return Evaluate(state, type, shape, data);
    }

    internal void RegisterNative(IBlockShape shape)
    {
        if (IsFrozen) throw new InvalidOperationException("Voxel shapes are already frozen.");
        _native.Add(shape.Id, shape);
    }

    internal void Freeze(IBlockRegistry blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (IsFrozen) throw new InvalidOperationException("Voxel shapes are already frozen.");
        _blocks = blocks;
        var problems = new List<BlockProblem>();
        foreach (BlockType type in blocks.Types)
        {
            if (type.IsAir) continue;
            bool known = _native.ContainsKey(type.Shape) || (_registrations.TryGetValue(type.Shape, out Registration? registration) && !registration.Revoked);
            if (!known)
            {
                problems.Add(new BlockProblem(type.ObjectId, $"the shape '{type.Shape}' is not registered with nVoxel.", type.Source));
            }
        }

        if (problems.Count > 0) throw new BlockLoadException(problems);
        _shapes = Array.AsReadOnly(_native.Values.Concat(_registrations.Values.Where(r => !r.Revoked).Select(r => r.Shape)).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray());
        _cache = new ShapeEvaluation[blocks.StateCount];
        IsFrozen = true;
        for (uint state = 0; state < (uint)_cache.Length; state++)
        {
            BlockType type = blocks.TypeOf(state);
            if (type.IsAir)
            {
                _cache[state] = new ShapeEvaluation(CollisionShape.Empty, FaceMask.Empty, ContactMasks.Empty);
                continue;
            }

            IBlockShape shape = FindShape(type.Shape);
            if (!shape.IsInstanceDependent) _cache[state] = Evaluate(state, type, shape, BlockDataView.Empty);
        }
    }

    private ShapeEvaluation Evaluate(uint state, BlockType type, IBlockShape shape, in BlockDataView data)
    {
        int part = 0;
        uint referenceState = state;
        if (type.TryGetProperty("part", out StateProperty? partProperty)) part = _blocks!.GetValue(state, partProperty);
        if (type.TryGetProperty("orientation", out StateProperty? orientationProperty)) referenceState = _blocks!.With(state, orientationProperty, 0);

        ShapeEvaluation result = shape.Evaluate(new BlockShapeRequest(referenceState, type, part, data));
        if (_blocks!.GetCollision(state) == BlockCollision.None)
        {
            result = new ShapeEvaluation(CollisionShape.Empty, FaceMask.Empty, ContactMasks.Empty, result.Mass, result.FluidCapacity, result.NormalizationState);
        }

        int orientation = type.TryGetProperty("orientation", out orientationProperty) ? _blocks.GetValue(state, orientationProperty) : 0;
        if (orientation == 0) return result;
        Matrix3 matrix = OrientationMatrices.Get(type.Orientations, orientation);
        CollisionShape collision = Rotate(result.Collision, matrix);
        FaceMask opacity = Rotate(result.Opacity, matrix);
        ContactMasks contact = Rotate(result.Contact, matrix);
        return new ShapeEvaluation(collision, opacity, contact, result.Mass, result.FluidCapacity, result.NormalizationState);
    }

    [HotPath]
    private IBlockShape FindShape(string id) => _native.TryGetValue(id, out IBlockShape? native)
        ? native
        : _registrations.TryGetValue(id, out Registration? registration) && !registration.Revoked
            ? registration.Shape
            : throw new InvalidOperationException($"The shape '{id}' is not registered.");

    private static CollisionShape Rotate(CollisionShape source, Matrix3 matrix)
    {
        if (source.Count == 0) return CollisionShape.Empty;
        var boxes = new CollisionBox[source.Count];
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < source.Count; i++)
        {
            CollisionBox box = source[i];
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);
            corners[0] = new(box.Minimum.X, box.Minimum.Y, box.Minimum.Z);
            corners[1] = new(box.Maximum.X, box.Minimum.Y, box.Minimum.Z);
            corners[2] = new(box.Minimum.X, box.Maximum.Y, box.Minimum.Z);
            corners[3] = new(box.Maximum.X, box.Maximum.Y, box.Minimum.Z);
            corners[4] = new(box.Minimum.X, box.Minimum.Y, box.Maximum.Z);
            corners[5] = new(box.Maximum.X, box.Minimum.Y, box.Maximum.Z);
            corners[6] = new(box.Minimum.X, box.Maximum.Y, box.Maximum.Z);
            corners[7] = new(box.Maximum.X, box.Maximum.Y, box.Maximum.Z);
            for (int c = 0; c < corners.Length; c++)
            {
                Vector3 transformed = matrix.Apply(corners[c]);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            boxes[i] = new CollisionBox(min, max);
        }

        return new CollisionShape(boxes);
    }

    private static FaceMask Rotate(FaceMask source, Matrix3 matrix)
    {
        byte bits = 0;
        for (BlockFace face = BlockFace.North; face <= BlockFace.Down; face++)
        {
            if (!source[face]) continue;
            Vector3 normal = FaceNormal(face);
            Vector3 turned = matrix.ApplyDirection(normal);
            bits |= (byte)(1 << (int)FaceFromNormal(turned));
        }

        return new FaceMask(bits);
    }

    private static ContactMasks Rotate(ContactMasks source, Matrix3 matrix)
    {
        ushort north = 0;
        ushort east = 0;
        ushort south = 0;
        ushort west = 0;
        ushort up = 0;
        ushort down = 0;

        for (BlockFace face = BlockFace.North; face <= BlockFace.Down; face++)
        {
            ushort cells = source[face];
            for (int index = 0; index < 16; index++)
            {
                if ((cells & (1 << index)) == 0) continue;
                int u = index % 4;
                int v = index / 4;
                Vector3 point = FacePoint(face, u, v);
                Vector3 turnedPoint = matrix.Apply(point);
                BlockFace turnedFace = FaceFromNormal(matrix.ApplyDirection(FaceNormal(face)));
                GetFaceCell(turnedFace, turnedPoint, out int turnedU, out int turnedV);
                ushort bit = (ushort)(1 << (turnedV * 4 + turnedU));
                switch (turnedFace)
                {
                    case BlockFace.North: north |= bit; break;
                    case BlockFace.East: east |= bit; break;
                    case BlockFace.South: south |= bit; break;
                    case BlockFace.West: west |= bit; break;
                    case BlockFace.Up: up |= bit; break;
                    case BlockFace.Down: down |= bit; break;
                }
            }
        }

        return new ContactMasks(north, east, south, west, up, down);
    }

    private static Vector3 FacePoint(BlockFace face, int u, int v)
    {
        float first = (u + 0.5f) / 4;
        float second = (v + 0.5f) / 4;
        return face switch
        {
            BlockFace.North => new(first, second, 0),
            BlockFace.East => new(1, second, 1 - first),
            BlockFace.South => new(first, second, 1),
            BlockFace.West => new(0, second, first),
            BlockFace.Up => new(first, 1, second),
            _ => new(first, 0, 1 - second),
        };
    }

    private static void GetFaceCell(BlockFace face, Vector3 point, out int u, out int v)
    {
        (float first, float second) = face switch
        {
            BlockFace.North => (point.X, point.Y),
            BlockFace.East => (1 - point.Z, point.Y),
            BlockFace.South => (point.X, point.Y),
            BlockFace.West => (point.Z, point.Y),
            BlockFace.Up => (point.X, point.Z),
            _ => (point.X, 1 - point.Z),
        };
        u = Math.Clamp((int)MathF.Floor(first * 4), 0, 3);
        v = Math.Clamp((int)MathF.Floor(second * 4), 0, 3);
    }

    private static Vector3 FaceNormal(BlockFace face) => face switch
    {
        BlockFace.North => new(0, 0, -1),
        BlockFace.East => new(1, 0, 0),
        BlockFace.South => new(0, 0, 1),
        BlockFace.West => new(-1, 0, 0),
        BlockFace.Up => new(0, 1, 0),
        _ => new(0, -1, 0),
    };

    private static BlockFace FaceFromNormal(Vector3 normal)
    {
        if (normal.Y > 0.5f) return BlockFace.Up;
        if (normal.Y < -0.5f) return BlockFace.Down;
        if (normal.X > 0.5f) return BlockFace.East;
        if (normal.X < -0.5f) return BlockFace.West;
        return normal.Z > 0.5f ? BlockFace.South : BlockFace.North;
    }

    private static bool IsShapeId(string id) => id.StartsWith("shape:", StringComparison.Ordinal) && id.Length > 6 && !id.Contains(' ');

    private sealed class Registration(ShapeRegistry registry, string ownerId, IBlockShape shape) : IDisposable
    {
        public string OwnerId { get; } = ownerId;
        public IBlockShape Shape { get; } = shape;
        public bool Revoked { get; private set; }

        public void Dispose()
        {
            if (Revoked) return;
            if (registry.IsFrozen) throw new InvalidOperationException("Voxel shape registrations cannot be revoked after initialization.");
            Revoked = true;
            registry._registrations.Remove(Shape.Id);
        }
    }
}

internal readonly struct Matrix3(int m00, int m01, int m02, int m10, int m11, int m12, int m20, int m21, int m22)
{
    public readonly int M00 = m00, M01 = m01, M02 = m02, M10 = m10, M11 = m11, M12 = m12, M20 = m20, M21 = m21, M22 = m22;

    public Vector3 Apply(Vector3 value) => new(
        M00 * value.X + M01 * value.Y + M02 * value.Z + Offset(M00, M01, M02),
        M10 * value.X + M11 * value.Y + M12 * value.Z + Offset(M10, M11, M12),
        M20 * value.X + M21 * value.Y + M22 * value.Z + Offset(M20, M21, M22));

    public Vector3 ApplyDirection(Vector3 value) => new(
        M00 * value.X + M01 * value.Y + M02 * value.Z,
        M10 * value.X + M11 * value.Y + M12 * value.Z,
        M20 * value.X + M21 * value.Y + M22 * value.Z);

    private static int Offset(int a, int b, int c) => a < 0 || b < 0 || c < 0 ? 1 : 0;
}

internal static class OrientationMatrices
{
    private static readonly Matrix3 Turn = new(0, 0, -1, 0, 1, 0, 1, 0, 0);
    private static readonly ImmutableArray<Matrix3> Full = [.. BuildFull()];

    public static Matrix3 Get(BlockOrientations set, int index)
    {
        if (set == BlockOrientations.Full) return Full[index];
        if (set is BlockOrientations.Horizontal or BlockOrientations.Facing)
        {
            if (index < 4) return Full[index * 4];
            return index == 4 ? new Matrix3(1, 0, 0, 0, 0, -1, 0, 1, 0) : new Matrix3(1, 0, 0, 0, 0, 1, 0, -1, 0);
        }

        return new Matrix3(1, 0, 0, 0, 1, 0, 0, 0, 1);
    }

    private static Matrix3[] BuildFull()
    {
        Matrix3 identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
        Matrix3 up = new(1, 0, 0, 0, 0, -1, 0, 1, 0);
        Matrix3 down = new(1, 0, 0, 0, 0, 1, 0, -1, 0);
        Matrix3 roll = new(0, -1, 0, 1, 0, 0, 0, 0, 1);
        var facings = new Matrix3[6];
        facings[0] = identity;
        for (int i = 1; i < 4; i++) facings[i] = Multiply(Turn, facings[i - 1]);
        facings[4] = up;
        facings[5] = down;
        var result = new Matrix3[24];
        for (int facing = 0; facing < 6; facing++)
        {
            Matrix3 rolled = identity;
            for (int r = 0; r < 4; r++)
            {
                result[(facing * 4) + r] = Multiply(facings[facing], rolled);
                rolled = Multiply(roll, rolled);
            }
        }

        return result;
    }

    private static Matrix3 Multiply(Matrix3 a, Matrix3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);
}

internal sealed class CubeShape(string id) : IBlockShape
{
    public string Id { get; } = id;

    public bool IsInstanceDependent => false;

    public ShapeEvaluation Evaluate(in BlockShapeRequest request) => new(CollisionShape.FullBlock, FaceMask.All, ContactMasks.Full);
}
