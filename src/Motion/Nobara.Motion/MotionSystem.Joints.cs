// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>
/// The joints between grids, and between a grid and the terrain, as joints of
/// nPhysics: the carrier is the first body, so that a motor drives the grid
/// relative to it. The terrain is no body: a joint to the terrain holds a
/// static body of nPhysics that Motion places at the point of the joint, and
/// that collides with nothing.
/// </summary>
internal sealed partial class MotionSystem
{
    /// <summary>The radius of the sphere of the anchors in the terrain, in blocks: it collides with nothing.</summary>
    private const float AnchorRadius = 0.05f;

    private JointEntry[] _joints = new JointEntry[8];
    private int _jointHigh;
    private int _jointFree = -1;
    private int _jointCount;
    private PhysicsShape? _anchorShape;
    private IEntityWorlds? _entities;
    private ComponentType<PhysicsBody> _bodyComponent;

    /// <summary>Gets the number of joints that hold.</summary>
    internal int JointCount => _jointCount;

    /// <summary>Gets the slots of the joints to look at, from 0.</summary>
    internal int JointSlots => _jointHigh;

    public GridJointHandle Link(Grid grid, Grid carrier, in GridJointDescription description)
    {
        GridRecord record = Require(grid);
        ArgumentNullException.ThrowIfNull(carrier);
        if (carrier == grid)
        {
            throw new ArgumentException($"The grid {grid.Identifier} cannot carry itself.", nameof(carrier));
        }

        if (carrier.IsDestroyed || carrier.Dimension != grid.Dimension)
        {
            throw new ArgumentException($"The carrier {carrier.Identifier} is destroyed, or not in the dimension of the grid {grid.Identifier}.", nameof(carrier));
        }

        GridRecord? carried = null;
        if (!carrier.IsTerrain && !_records.TryGetValue(carrier, out carried))
        {
            throw new ArgumentException($"The carrier {carrier.Identifier} is neither the terrain nor a grid that Motion moves.", nameof(carrier));
        }

        Vector3 axis = RequireDescription(description);
        WorldTransform transform = grid.Transform;
        Quaternion rotation = Quaternion.Normalize(transform.Rotation);
        WorldPosition point = transform.TransformPoint(description.Anchor);
        Vector3 axisInWorld = Vector3.Transform(axis, rotation);
        BodyHandle first;
        Vector3 anchorOnCarrier;
        Vector3 axisOnCarrier;
        EntityWorld? anchorWorld = null;
        EntityId anchor = EntityId.Null;
        if (carried is not null)
        {
            WorldTransform frame = carrier.Transform;
            Quaternion inverse = Quaternion.Conjugate(Quaternion.Normalize(frame.Rotation));
            first = carried.Body;
            anchorOnCarrier = Vector3.Transform(point.RelativeTo(frame.Position), inverse) - BodyCenter(carrier);
            axisOnCarrier = Vector3.Transform(axisInWorld, inverse);
        }
        else
        {
            anchorWorld = EntityWorldOf(grid);
            anchor = anchorWorld.Create(anchorWorld.GetArchetype(_entities!.WorldTransform));
            anchorWorld.Set(anchor, _entities.WorldTransform, WorldTransform.At(point));
            _anchorShape ??= _physics!.CreateSphere(AnchorRadius);
            first = _physics!.AddBody(grid.Dimension, anchor, new BodyDescription { Kind = BodyKind.Static, Shape = _anchorShape.Value, Filter = new CollisionFilter(0, 0) });
            anchorOnCarrier = Vector3.Zero;
            axisOnCarrier = axisInWorld;
        }

        JointHandle joint;
        try
        {
            joint = _physics!.CreateJoint(first, record.Body, new JointDescription(description.Kind, anchorOnCarrier, description.Anchor - BodyCenter(grid), axisOnCarrier, axis, description.Motor));
        }
        catch
        {
            ReleaseAnchor(anchorWorld, anchor, first);
            throw;
        }

        int slot = AllocateJoint();
        ref JointEntry entry = ref _joints[slot];
        entry.Grid = grid;
        entry.Carrier = carrier;
        entry.Description = description;
        entry.Joint = joint;
        entry.AnchorWorld = anchorWorld;
        entry.Anchor = anchor;
        entry.AnchorBody = carried is null ? first : default;
        return new GridJointHandle(slot, entry.Generation);
    }

    public void SetMotor(GridJointHandle joint, JointMotor? motor)
    {
        ref JointEntry entry = ref RequireJoint(joint);
        _physics!.SetMotor(entry.Joint, motor);   // checks the motor and the kind
        entry.Description = entry.Description with { Motor = motor };
    }

    public void Unlink(GridJointHandle joint)
    {
        ref JointEntry entry = ref RequireJoint(joint);
        if (_physics!.IsAlive(entry.Joint))
        {
            _physics.DestroyJoint(entry.Joint);
        }

        ReleaseJoint(joint.Index);
    }

    public bool IsLinked(GridJointHandle joint) =>
        joint.Generation != 0 && (uint)joint.Index < (uint)_jointHigh && _joints[joint.Index].Alive && _joints[joint.Index].Generation == joint.Generation
        && _physics!.IsAlive(_joints[joint.Index].Joint);

    public GridJointState GetJoint(GridJointHandle joint)
    {
        ref JointEntry entry = ref RequireJoint(joint);
        return new GridJointState(entry.Grid!, entry.Carrier!, entry.Description, entry.Joint);
    }

    /// <summary>Gets a joint by its slot, for the window of nDebug.</summary>
    internal bool TryGetJoint(int slot, out GridJointState state)
    {
        ref JointEntry entry = ref _joints[slot];
        if (!entry.Alive)
        {
            state = default;
            return false;
        }

        state = new GridJointState(entry.Grid!, entry.Carrier!, entry.Description, entry.Joint);
        return true;
    }

    /// <summary>Resolves what the joints need: the entity worlds, for the anchors in the terrain, and the component of the bodies, for their centers.</summary>
    private void InitializeJoints(IEntityWorlds entities)
    {
        _entities = entities;
        _bodyComponent = entities.TryGetComponent(out ComponentType<PhysicsBody> body) ? body : throw new InvalidOperationException("nPhysics registers the bodies before Motion initializes.");
    }

    /// <summary>Forgets the joints that nPhysics destroyed with one of their bodies, and removes their anchors.</summary>
    [HotPath]
    private void ReleaseBrokenJoints()
    {
        if (_jointCount == 0)
        {
            return;
        }

        for (int i = 0; i < _jointHigh; i++)
        {
            if (_joints[i].Alive && !_physics!.IsAlive(_joints[i].Joint))
            {
                ReleaseJoint(i);
            }
        }
    }

    /// <summary>Forgets every joint at the end of the world session: the entities and the joints of nPhysics are gone with it.</summary>
    private void ResetJoints()
    {
        Array.Clear(_joints);
        _jointHigh = 0;
        _jointFree = -1;
        _jointCount = 0;
    }

    /// <summary>Gets where nPhysics has the center of the body of a grid, in the frame of the grid: the anchors of nPhysics start from it.</summary>
    private Vector3 BodyCenter(Grid grid) => EntityWorldOf(grid).Read(grid.Entity, _bodyComponent).Center;

    private EntityWorld EntityWorldOf(Grid grid) =>
        _entities!.TryGet(grid.Dimension, out EntityWorld? world) ? world : throw new ArgumentException($"The dimension of the grid {grid.Identifier} has no entity world.", nameof(grid));

    private static Vector3 RequireDescription(in GridJointDescription description)
    {
        if (!Enum.IsDefined(description.Kind))
        {
            throw new ArgumentException($"The kind {description.Kind} of a joint is not defined.", nameof(description));
        }

        Vector3 anchor = description.Anchor;
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(anchor.Z))
        {
            throw new ArgumentException($"The anchor {anchor} of a joint is not finite.", nameof(description));
        }

        if (description.Kind == JointKind.Fixed)
        {
            return Vector3.UnitY;   // a fixed joint uses no axis
        }

        Vector3 axis = description.Axis;
        if (!float.IsFinite(axis.X) || !float.IsFinite(axis.Y) || !float.IsFinite(axis.Z) || axis.LengthSquared() < 1e-12f)
        {
            throw new ArgumentException($"The axis {axis} of a hinge or a slider is finite and not zero.", nameof(description));
        }

        return Vector3.Normalize(axis);
    }

    [HotPath]
    private void ReleaseJoint(int slot)
    {
        ref JointEntry entry = ref _joints[slot];
        ReleaseAnchor(entry.AnchorWorld, entry.Anchor, entry.AnchorBody);
        int generation = entry.Generation == int.MaxValue ? 1 : entry.Generation + 1;
        entry = default;
        entry.Generation = generation;
        entry.NextFree = _jointFree;
        _jointFree = slot;
        _jointCount--;
    }

    /// <summary>Removes the body and the entity of an anchor in the terrain, if there is one.</summary>
    [HotPath]
    private void ReleaseAnchor(EntityWorld? world, EntityId anchor, BodyHandle body)
    {
        if (world is null)
        {
            return;
        }

        if (!body.IsNone && _physics!.IsAlive(body))
        {
            _physics.RemoveBody(body);
        }

        if (!world.IsDestroyed && world.IsAlive(anchor))
        {
            world.Destroy(anchor);
        }
    }

    private int AllocateJoint()
    {
        int slot;
        if (_jointFree >= 0)
        {
            slot = _jointFree;
            _jointFree = _joints[slot].NextFree;
        }
        else
        {
            if (_jointHigh == _joints.Length)
            {
                Array.Resize(ref _joints, _joints.Length * 2);
            }

            slot = _jointHigh++;
            _joints[slot].Generation = 1;
        }

        _joints[slot].Alive = true;
        _joints[slot].NextFree = -1;
        _jointCount++;
        return slot;
    }

    private ref JointEntry RequireJoint(GridJointHandle joint)
    {
        if (joint.Generation == 0 || (uint)joint.Index >= (uint)_jointHigh || !_joints[joint.Index].Alive || _joints[joint.Index].Generation != joint.Generation)
        {
            throw new ArgumentException($"The {joint} names no joint.", nameof(joint));
        }

        return ref _joints[joint.Index];
    }

    /// <summary>A joint that Motion made.</summary>
    private struct JointEntry
    {
        public int Generation;
        public bool Alive;
        public int NextFree;
        public Grid? Grid;
        public Grid? Carrier;
        public GridJointDescription Description;
        public JointHandle Joint;

        /// <summary>The entity world of the anchor in the terrain, or <see langword="null"/> for a joint between two grids.</summary>
        public EntityWorld? AnchorWorld;
        public EntityId Anchor;
        public BodyHandle AnchorBody;
    }
}
