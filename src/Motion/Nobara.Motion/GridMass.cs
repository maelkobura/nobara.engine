// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Core;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>The mass of one block and where it lies in its cell.</summary>
internal readonly record struct BlockMass
{
    /// <summary>Creates the mass of a block.</summary>
    /// <param name="mass">The mass, in kilograms; 0 for air and the parts of a multi-block other than its origin.</param>
    /// <param name="center">Its center of mass, from the corner of its cell, in blocks.</param>
    [HotPath]
    public BlockMass(float mass, Vector3 center) => (Mass, Center) = (mass, center);

    /// <summary>Gets the mass, in kilograms.</summary>
    public float Mass { get; }

    /// <summary>Gets the center of mass, from the corner of the cell, in blocks.</summary>
    public Vector3 Center { get; }
}

/// <summary>
/// The mass of every block state, read once when Motion starts: the weight of
/// its type (<c>Weight</c>, or <c>Motion.Weight</c>), or
/// <see cref="BlockMasses.DefaultWeight"/>; a multi-block counted once, at
/// its origin, at the middle of its cells; a state whose shape depends on its
/// instance asks its shape for each cell.
/// </summary>
internal sealed class BlockMasses
{
    /// <summary>The weight of a block whose type gives none, in kilograms: one cubic block of water.</summary>
    public const float DefaultWeight = 1000f;

    private static readonly Vector3 Middle = new(0.5f);

    private readonly BlockMass[] _states;
    private readonly bool[] _instance;
    private readonly IShapeRegistry _shapes;

    private BlockMasses(BlockMass[] states, bool[] instance, IShapeRegistry shapes) => (_states, _instance, _shapes) = (states, instance, shapes);

    /// <summary>Builds the table from the weights that nBlocks resolved for each state.</summary>
    public static BlockMasses Build(IBlockRegistry blocks, IShapeRegistry shapes, IMultiBlockRegistry multiBlocks, GenericElement?[] weights, Action<string> warn)
    {
        int count = blocks.StateCount;
        var states = new BlockMass[count];
        bool[] instance = new bool[count];
        for (uint state = 1; state < (uint)count; state++)
        {
            BlockType type = blocks.TypeOf(state);
            if (type.IsAir)
            {
                continue;
            }

            float weight = DefaultWeight;
            if (weights[state] is { } element)
            {
                if (element.Value is { } value && value.TryGetNumber(out double number) && double.IsFinite(number) && number >= 0)
                {
                    weight = (float)number;
                }
                else
                {
                    warn($"The weight of {type.ObjectId} is not a number of kilograms, 0 or more: {DefaultWeight} kilograms are used.");
                }
            }

            instance[state] = shapes.TryGet(type.Shape, out IBlockShape? shape) && shape!.IsInstanceDependent;
            if (!multiBlocks.IsMultiBlock(state))
            {
                states[state] = new BlockMass(weight, Middle);
            }
            else if (multiBlocks.IsOrigin(state))
            {
                IReadOnlyList<BlockCell> footprint = multiBlocks.GetFootprint(state);
                Vector3 sum = Vector3.Zero;
                foreach (BlockCell cell in footprint)
                {
                    sum += new Vector3(cell.X, cell.Y, cell.Z);
                }

                states[state] = new BlockMass(weight, (sum / footprint.Count) + Middle);
            }
        }

        return new BlockMasses(states, instance, shapes);
    }

    /// <summary>Gets whether the mass of a state depends on the block data of its cell.</summary>
    [HotPath]
    public bool IsInstance(uint state) => state < (uint)_instance.Length && _instance[state];

    /// <summary>Gets the mass of a state whose mass does not depend on its cell.</summary>
    [HotPath]
    public BlockMass Of(uint state) => state < (uint)_states.Length ? _states[state] : default;

    /// <summary>
    /// Gets the mass of a cell whose shape depends on its instance: the mass
    /// the shape gives for its block data, or the weight of its type, at the
    /// middle of the collision boxes the shape gives, weighted by their volume.
    /// </summary>
    [HotPath]
    public BlockMass OfInstance(uint state, in BlockDataView data)
    {
        ShapeEvaluation evaluation = _shapes.Get(state, data);
        float mass = evaluation.Mass is { } given && float.IsFinite(given) && given >= 0 ? given : Of(state).Mass;
        double volume = 0;
        Vector3 moment = Vector3.Zero;
        CollisionShape collision = evaluation.Collision;
        for (int i = 0; i < collision.Count; i++)
        {
            CollisionBox box = collision[i];
            Vector3 size = box.Maximum - box.Minimum;
            float boxVolume = MathF.Max(size.X, 0) * MathF.Max(size.Y, 0) * MathF.Max(size.Z, 0);
            volume += boxVolume;
            moment += boxVolume * ((box.Minimum + box.Maximum) / 2);
        }

        return new BlockMass(mass, volume > 0 ? moment / (float)volume : Of(state).Center);
    }
}

/// <summary>
/// The sums from which the mass, the center of mass, and the inertia of a
/// grid are computed, in double precision, so that adding and taking away
/// blocks one by one gives the sums that counting them all again would.
/// </summary>
/// <remarks>
/// Each block is a cube of its mass at its center: its own inertia is the one
/// of a cube of one block, a sixth of its mass about each axis.
/// </remarks>
internal struct GridMass
{
    private double _mass;
    private double _x;
    private double _y;
    private double _z;
    private double _xx;
    private double _yy;
    private double _zz;
    private double _xy;
    private double _xz;
    private double _yz;

    /// <summary>Gets the number of cells that hold a block.</summary>
    public int Blocks { get; private set; }

    /// <summary>Gets the mass, in kilograms.</summary>
    public readonly double Mass => _mass;

    /// <summary>Adds a block, or takes it away with a sign of -1.</summary>
    /// <param name="block">The mass of the block.</param>
    /// <param name="corner">The corner of its cell, in the frame of the grid.</param>
    /// <param name="sign">1 to add, -1 to take away.</param>
    /// <param name="counts">Whether the cell counts as a block: every cell that is not air.</param>
    [HotPath]
    public void Add(in BlockMass block, Vector3 corner, int sign, bool counts)
    {
        if (counts)
        {
            Blocks += sign;
        }

        if (block.Mass == 0)
        {
            return;
        }

        double m = sign * (double)block.Mass;
        double x = corner.X + (double)block.Center.X;
        double y = corner.Y + (double)block.Center.Y;
        double z = corner.Z + (double)block.Center.Z;
        _mass += m;
        _x += m * x;
        _y += m * y;
        _z += m * z;
        _xx += m * x * x;
        _yy += m * y * y;
        _zz += m * z * z;
        _xy += m * x * y;
        _xz += m * x * z;
        _yz += m * y * z;
    }

    /// <summary>Forgets every block.</summary>
    [HotPath]
    public void Clear() => this = default;

    /// <summary>Computes the mass, the center of mass, and the inertia about it.</summary>
    /// <param name="minimum">The mass given to a grid whose blocks weigh nothing.</param>
    /// <param name="mass">The mass.</param>
    /// <param name="center">The center of mass, in the frame of the grid; the origin when the blocks weigh nothing.</param>
    /// <param name="inertia">The inertia tensor about the center of mass, along the axes of the grid.</param>
    [HotPath]
    public readonly void Compute(float minimum, out float mass, out Vector3 center, out BodyInertia inertia)
    {
        if (_mass < 1e-6)
        {
            mass = minimum;
            center = Vector3.Zero;
            float cube = minimum / 6f;
            inertia = new BodyInertia(cube, 0, cube, 0, 0, cube);
            return;
        }

        double cx = _x / _mass;
        double cy = _y / _mass;
        double cz = _z / _mass;
        double own = _mass / 6;   // the cubes of the blocks, about their own centers
        double xx = _xx - (_mass * cx * cx);
        double yy = _yy - (_mass * cy * cy);
        double zz = _zz - (_mass * cz * cz);
        double xy = _xy - (_mass * cx * cy);
        double xz = _xz - (_mass * cx * cz);
        double yz = _yz - (_mass * cy * cz);
        mass = (float)_mass;
        center = new Vector3((float)cx, (float)cy, (float)cz);
        inertia = new BodyInertia(
            XX: (float)(own + yy + zz),
            YX: (float)-xy,
            YY: (float)(own + xx + zz),
            ZX: (float)-xz,
            ZY: (float)-yz,
            ZZ: (float)(own + xx + yy));
    }

    /// <summary>Tells whether two sets of sums give the same grid, within a relative tolerance.</summary>
    public readonly bool Matches(in GridMass other, double tolerance)
    {
        return Blocks == other.Blocks
            && Close(_mass, other._mass) && Close(_x, other._x) && Close(_y, other._y) && Close(_z, other._z)
            && Close(_xx, other._xx) && Close(_yy, other._yy) && Close(_zz, other._zz)
            && Close(_xy, other._xy) && Close(_xz, other._xz) && Close(_yz, other._yz);

        bool Close(double a, double b) => Math.Abs(a - b) <= tolerance * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
    }
}
