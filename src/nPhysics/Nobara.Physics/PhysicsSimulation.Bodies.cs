// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>Bodies, joints, and the commands that are queued for the next step.</summary>
internal sealed partial class PhysicsSimulation
{
    private BodyRecord[] _bodies = new BodyRecord[64];
    private BodyPose[] _poses = new BodyPose[64];
    private int _bodyHigh;
    private int _bodyFree = -1;
    private JointRecord[] _joints = new JointRecord[16];
    private int _jointHigh;
    private int _jointFree = -1;
    private int _jointCount;
    private PhysicsCommand[] _commands = new PhysicsCommand[64];
    private int _commandCount;
    private int _staticBodies;
    private int _kinematicBodies;
    private int _dynamicBodies;
    private int _sleepingBodies;

    public BodyHandle AddBody(DimensionId dimension, EntityId entity, BodyDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        RequireStarted();
        ShapeDescription shape = GetShape(description.Shape);
        if (shape.Kind == ShapeKind.Mesh && description.Kind != BodyKind.Static)
        {
            throw new ArgumentException("A mesh is the shape of a static body only.", nameof(description));
        }

        RequirePositive(description.Mass, nameof(description));
        RequireMaterial(description.Material);
        if (description.Contacts is { } contacts)
        {
            RequireReporting(contacts);
        }
        if (!IsFinite(description.Center) || !IsFinite(description.LinearVelocity) || !IsFinite(description.AngularVelocity))
        {
            throw new ArgumentException("The center and the velocities of a body are finite.", nameof(description));
        }

        if (!_entities!.TryGet(dimension, out EntityWorld? world))
        {
            throw new ArgumentException($"The {dimension} has no entity world.", nameof(dimension));
        }

        if (!world.IsAlive(entity))
        {
            throw new ArgumentException($"The {entity} is not alive in {dimension}.", nameof(entity));
        }

        if (world.IsLocked)
        {
            throw new InvalidOperationException("A body is added outside entity systems: the structure of the world is locked while one runs.");
        }

        if (!world.Has(entity, _transform))
        {
            throw new ArgumentException($"The {entity} has no world transform: a body starts where its entity is.", nameof(entity));
        }

        if (!world.GetParent(entity).IsNull)
        {
            throw new ArgumentException($"The {entity} has a parent: a body is a root entity, placed in the world.", nameof(entity));
        }

        if (world.Has(entity, _characterComponent))
        {
            throw new ArgumentException($"The {entity} is a character: remove its character before making it a body.", nameof(entity));
        }

        if (world.Has(entity, _bodyComponent))
        {
            throw new ArgumentException($"The {entity} is a body already.", nameof(entity));
        }

        WorldTransform transform = world.Read(entity, _transform);
        Grid? grid = null;
        if (world.Has(entity, _voxels!.GridComponent) && _voxels.TryGet(dimension, world.Read(entity, _voxels.GridComponent).Handle, out Grid? found))
        {
            grid = found;
        }

        BodyInertia inertia = description.Inertia ?? (shape.Kind == ShapeKind.Mesh ? default : _backend!.ComputeInertia(shape, description.Mass));
        Quaternion rotation = Quaternion.Normalize(transform.Rotation);
        bool moves = description.Kind != BodyKind.Static;
        UpdateRegions();
        int slot = AllocateBody();
        ref BodyRecord body = ref _bodies[slot];
        body.EntityWorld = world;
        body.Dimension = dimension;
        body.Entity = entity;
        body.Grid = grid;
        body.Kind = description.Kind;
        body.Shape = description.Shape;
        body.Mass = description.Mass;
        body.Inertia = inertia;
        body.ExplicitInertia = description.Inertia is not null;
        body.Center = description.Center;
        body.Material = description.Material;
        body.Filter = description.Filter;
        body.Continuous = description.Continuous;
        body.CanSleep = description.CanSleep;
        body.ReportsContacts = description.Contacts.HasValue;
        body.ImpactSpeed = description.Contacts?.ImpactSpeed ?? ContactReporting.DefaultImpactSpeed;
        body.Asleep = false;
        body.Dirty = false;
        body.TargetPending = false;
        body.Position = transform.Position.Offset(Vector3.Transform(description.Center, rotation));
        body.Rotation = rotation;
        body.Linear = moves ? description.LinearVelocity : Vector3.Zero;
        body.Angular = moves ? description.AngularVelocity : Vector3.Zero;
        body.BackendId = -1;
        if (FindRegion(dimension, body.Position) is { } region)
        {
            WorldEntry entry = EnsureWorld(region);
            body.BackendId = entry.Backend.AddBody(Describe(slot, body, entry));
            body.World = entry;
            entry.Bodies++;
        }
        else
        {
            _frozenBodies++;
        }

        AcquireShape(description.Shape);
        CountKind(description.Kind, 1);
        var handle = new BodyHandle(slot, body.Generation);
        world.Add(entity, _bodyComponent, new PhysicsBody { Handle = handle, Kind = description.Kind, Flags = FlagsOf(body), Filter = description.Filter, Center = description.Center });
        world.Add(entity, _shapeComponent, new BodyShape { Shape = description.Shape });
        world.Add(entity, _materialComponent, new BodyMaterial { Material = description.Material });
        world.Add(entity, _massComponent, MassOf(body));
        if (world.Has(entity, _velocityComponent))
        {
            world.Set(entity, _velocityComponent, new BodyVelocity { Linear = body.Linear, Angular = body.Angular });   // kept from a character
        }
        else
        {
            world.Add(entity, _velocityComponent, new BodyVelocity { Linear = body.Linear, Angular = body.Angular });
        }

        return handle;
    }

    public void RemoveBody(BodyHandle body)
    {
        ref BodyRecord record = ref RequireBody(body);
        if (record.EntityWorld!.IsLocked)
        {
            throw new InvalidOperationException("A body is removed outside entity systems: the structure of the world is locked while one runs.");
        }

        RemoveBodyCore(body.Index, removeComponents: true);
    }

    [HotPath]
    public bool IsAlive(BodyHandle body) =>
        body.Generation != 0 && (uint)body.Index < (uint)_bodyHigh && _bodies[body.Index].Alive && _bodies[body.Index].Generation == body.Generation;

    [HotPath]
    public bool TryGetBody(DimensionId dimension, EntityId entity, out BodyHandle body)
    {
        if (_entities!.TryGet(dimension, out EntityWorld? world) && world.IsAlive(entity) && world.Has(entity, _bodyComponent))
        {
            body = world.Read(entity, _bodyComponent).Handle;
            return true;
        }

        body = default;
        return false;
    }

    [HotPath]
    public BodyState GetState(BodyHandle body)
    {
        ref BodyRecord record = ref RequireBody(body);
        ref readonly BodyVelocity velocity = ref record.EntityWorld!.Read(record.Entity, _velocityComponent);
        return new BodyState(record.Kind, record.Dimension, record.Entity, record.EntityWorld.Read(record.Entity, _transform), velocity.Linear, velocity.Angular, record.Asleep);
    }

    [HotPath]
    public void Teleport(BodyHandle body, in WorldPosition position, Quaternion rotation)
    {
        RequireBody(body);
        RequirePose(position, rotation);
        ref PhysicsCommand command = ref Enqueue(CommandKind.Teleport, body);
        command.Point = position;
        command.Rotation = Quaternion.Normalize(rotation);
    }

    [HotPath]
    public void SetVelocity(BodyHandle body, Vector3 linear, Vector3 angular)
    {
        RequireMoving(body);
        RequireFiniteVector(linear);
        RequireFiniteVector(angular);
        ref PhysicsCommand command = ref Enqueue(CommandKind.SetVelocity, body);
        command.A = linear;
        command.B = angular;
    }

    [HotPath]
    public void SetKinematicTarget(BodyHandle body, in WorldPosition position, Quaternion rotation)
    {
        if (RequireBody(body).Kind != BodyKind.Kinematic)
        {
            throw new ArgumentException($"The {body} is not kinematic: only a kinematic body follows a target.", nameof(body));
        }

        RequirePose(position, rotation);
        ref PhysicsCommand command = ref Enqueue(CommandKind.KinematicTarget, body);
        command.Point = position;
        command.Rotation = Quaternion.Normalize(rotation);
    }

    [HotPath]
    public void ApplyForce(BodyHandle body, Vector3 force) => EnqueueDynamic(CommandKind.Force, body, force);

    [HotPath]
    public void ApplyForceAt(BodyHandle body, Vector3 force, in WorldPosition point) => EnqueueDynamic(CommandKind.ForceAt, body, force).Point = point;

    [HotPath]
    public void ApplyImpulse(BodyHandle body, Vector3 impulse) => EnqueueDynamic(CommandKind.Impulse, body, impulse);

    [HotPath]
    public void ApplyImpulseAt(BodyHandle body, Vector3 impulse, in WorldPosition point) => EnqueueDynamic(CommandKind.ImpulseAt, body, impulse).Point = point;

    [HotPath]
    public void ApplyTorque(BodyHandle body, Vector3 torque) => EnqueueDynamic(CommandKind.Torque, body, torque);

    [HotPath]
    public void ApplyAngularImpulse(BodyHandle body, Vector3 impulse) => EnqueueDynamic(CommandKind.AngularImpulse, body, impulse);

    [HotPath]
    public void Wake(BodyHandle body)
    {
        RequireBody(body);
        Enqueue(CommandKind.Wake, body);
    }

    [HotPath]
    public void WakeArea(DimensionId dimension, in WorldBox area)
    {
        ref PhysicsCommand command = ref Enqueue(CommandKind.WakeArea, default);
        command.Dimension = dimension;
        command.Point = area.Origin;
        command.A = area.Minimum;
        command.B = area.Maximum;
    }

    [HotPath]
    public void SetKind(BodyHandle body, BodyKind kind)
    {
        ref BodyRecord record = ref RequireBody(body);
        if (kind != BodyKind.Static && Get(record.Shape).Kind == ShapeKind.Mesh)
        {
            throw new ArgumentException("A body with a mesh stays static.", nameof(kind));
        }

        Enqueue(CommandKind.SetKind, body).Kind = kind;
    }

    [HotPath]
    public void SetShape(BodyHandle body, PhysicsShape shape, Vector3 center)
    {
        ref BodyRecord record = ref RequireBody(body);
        if (Get(shape).Kind == ShapeKind.Mesh && record.Kind != BodyKind.Static)
        {
            throw new ArgumentException("A mesh is the shape of a static body only.", nameof(shape));
        }

        RequireFiniteVector(center);
        AcquireShape(shape);   // held by the command until it applies
        ref PhysicsCommand command = ref Enqueue(CommandKind.SetShape, body);
        command.Shape = shape;
        command.A = center;
    }

    [HotPath]
    public void SetMass(BodyHandle body, float mass, BodyInertia? inertia = null)
    {
        RequireBody(body);
        if (!float.IsFinite(mass) || mass <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mass), mass, "The mass is finite and above 0.");
        }

        ref PhysicsCommand command = ref Enqueue(CommandKind.SetMass, body);
        command.Value = mass;
        command.HasInertia = inertia.HasValue;
        command.Inertia = inertia.GetValueOrDefault();
    }

    [HotPath]
    public void SetMaterial(BodyHandle body, PhysicsMaterial material)
    {
        RequireBody(body);
        RequireMaterial(material);
        Enqueue(CommandKind.SetMaterial, body).Material = material;
    }

    [HotPath]
    public void SetFilter(BodyHandle body, CollisionFilter filter)
    {
        RequireBody(body);
        Enqueue(CommandKind.SetFilter, body).Filter = filter;
    }

    [HotPath]
    public void SetContinuous(BodyHandle body, bool continuous)
    {
        RequireBody(body);
        Enqueue(CommandKind.SetContinuous, body).Flag = continuous;
    }

    [HotPath]
    public void SetContactReporting(BodyHandle body, ContactReporting? reporting)
    {
        RequireBody(body);
        if (reporting is { } value)
        {
            RequireReporting(value);
        }

        ref PhysicsCommand command = ref Enqueue(CommandKind.SetContactReporting, body);
        command.Flag = reporting.HasValue;
        command.Value = reporting?.ImpactSpeed ?? ContactReporting.DefaultImpactSpeed;
    }

    public JointHandle CreateJoint(BodyHandle first, BodyHandle second, in JointDescription description)
    {
        ref BodyRecord a = ref RequireBody(first);
        ref BodyRecord b = ref RequireBody(second);
        if (first == second)
        {
            throw new ArgumentException("A joint joins two different bodies.", nameof(second));
        }

        if (a.Dimension != b.Dimension)
        {
            throw new ArgumentException($"The {first} and the {second} are not in the same dimension.", nameof(second));
        }

        if (!IsFinite(description.AnchorA) || !IsFinite(description.AnchorB))
        {
            throw new ArgumentException("The anchors of a joint are finite.", nameof(description));
        }

        Vector3 axisA = description.AxisA;
        Vector3 axisB = description.AxisB;
        if (description.Kind == JointKind.Fixed)
        {
            if (description.Motor is not null)
            {
                throw new ArgumentException("A fixed joint has no motor.", nameof(description));
            }
        }
        else
        {
            axisA = RequireAxis(axisA);
            axisB = RequireAxis(axisB);
        }

        if (description.Motor is { } motor)
        {
            RequireMotor(motor);
        }

        int slot = AllocateJoint();
        ref JointRecord joint = ref _joints[slot];
        joint.First = first;
        joint.Second = second;
        joint.Kind = description.Kind;
        joint.Description = new BackendJoint
        {
            Kind = description.Kind,
            AnchorA = description.AnchorA,
            AnchorB = description.AnchorB,
            AxisA = axisA,
            AxisB = axisB,
            Motor = description.Motor,
        };
        AddJointToWorld(ref joint);   // otherwise once the bodies share a world, before the next step
        return new JointHandle(slot, joint.Generation);
    }

    [HotPath]
    public void SetMotor(JointHandle joint, JointMotor? motor)
    {
        if (RequireJoint(joint).Kind == JointKind.Fixed && motor is not null)
        {
            throw new ArgumentException("A fixed joint has no motor.", nameof(motor));
        }

        if (motor is { } value)
        {
            RequireMotor(value);
        }

        ref PhysicsCommand command = ref Enqueue(CommandKind.SetMotor, default);
        command.Joint = joint;
        command.HasMotor = motor.HasValue;
        command.Motor = motor.GetValueOrDefault();
    }

    public void DestroyJoint(JointHandle joint)
    {
        RequireJoint(joint);
        DestroyJointCore(joint.Index);
    }

    [HotPath]
    public bool IsAlive(JointHandle joint) =>
        joint.Generation != 0 && (uint)joint.Index < (uint)_jointHigh && _joints[joint.Index].Alive && _joints[joint.Index].Generation == joint.Generation;

    [HotPath]
    private ref BodyRecord RequireBody(BodyHandle body)
    {
        if (!IsAlive(body))
        {
            throw new ArgumentException($"The {body} names no body.", nameof(body));
        }

        return ref _bodies[body.Index];
    }

    [HotPath]
    private ref JointRecord RequireJoint(JointHandle joint)
    {
        if (!IsAlive(joint))
        {
            throw new ArgumentException($"The {joint} names no joint.", nameof(joint));
        }

        return ref _joints[joint.Index];
    }

    [HotPath]
    private void RequireMoving(BodyHandle body)
    {
        if (RequireBody(body).Kind == BodyKind.Static)
        {
            throw new ArgumentException($"The {body} is static: it has no velocity.", nameof(body));
        }
    }

    [HotPath]
    private ref PhysicsCommand EnqueueDynamic(CommandKind kind, BodyHandle body, Vector3 value)
    {
        if (RequireBody(body).Kind != BodyKind.Dynamic)
        {
            throw new ArgumentException($"The {body} is not dynamic: only a dynamic body takes forces and impulses.", nameof(body));
        }

        RequireFiniteVector(value);
        ref PhysicsCommand command = ref Enqueue(kind, body);
        command.A = value;
        return ref command;
    }

    [HotPath]
    private ref PhysicsCommand Enqueue(CommandKind kind, BodyHandle body)
    {
        if (_commandCount == _commands.Length)
        {
            // The queue grows to the largest number of commands of one tick, then stays.
            Array.Resize(ref _commands, _commands.Length * 2);
        }

        ref PhysicsCommand command = ref _commands[_commandCount++];
        command = default;
        command.Command = kind;
        command.Body = body;
        return ref command;
    }

    [HotPath]
    private void RemoveBodyCore(int slot, bool removeComponents)
    {
        ref BodyRecord body = ref _bodies[slot];
        var handle = new BodyHandle(slot, body.Generation);
        for (int i = 0; i < _jointHigh; i++)
        {
            ref JointRecord joint = ref _joints[i];
            if (joint.Alive && (joint.First == handle || joint.Second == handle))
            {
                DestroyJointCore(i);
            }
        }

        RemoveGhosts(slot);
        Release(ref body.Voxels);
        if (body.World is { } world)
        {
            world.Backend.RemoveBody(body.BackendId);
            world.Bodies--;
            if (world.Bodies == 0)
            {
                DisposeWorld(world);
            }
        }
        else
        {
            _frozenBodies--;
        }

        ReleaseShape(body.Shape);
        CountKind(body.Kind, -1);
        if (body.Asleep)
        {
            _sleepingBodies--;
        }

        EntityWorld entities = body.EntityWorld!;
        if (removeComponents && !entities.IsDestroyed && entities.IsAlive(body.Entity))
        {
            entities.Remove(body.Entity, _velocityComponent);
            entities.Remove(body.Entity, _massComponent);
            entities.Remove(body.Entity, _materialComponent);
            entities.Remove(body.Entity, _shapeComponent);
            entities.Remove(body.Entity, _bodyComponent);
        }

        FreeBody(slot);
    }

    [HotPath]
    private void DestroyJointCore(int slot)
    {
        ref JointRecord joint = ref _joints[slot];
        if (joint.World is { } world)
        {
            world.Backend.RemoveJoint(joint.BackendId);
            world.Joints--;
        }

        FreeJoint(slot);
    }

    private int AllocateBody()
    {
        int slot;
        if (_bodyFree >= 0)
        {
            slot = _bodyFree;
            _bodyFree = _bodies[slot].NextFree;
        }
        else
        {
            if (_bodyHigh == _bodies.Length)
            {
                Array.Resize(ref _bodies, _bodies.Length * 2);
                Array.Resize(ref _poses, _bodies.Length);
                Array.Resize(ref _targets, _bodies.Length);
                Array.Resize(ref _groups, _bodies.Length);
            }

            slot = _bodyHigh++;
            _bodies[slot].Generation = 1;
        }

        _bodies[slot].Alive = true;
        _bodies[slot].NextFree = -1;
        return slot;
    }

    [HotPath]
    private void FreeBody(int slot)
    {
        ref BodyRecord body = ref _bodies[slot];
        int generation = body.Generation == int.MaxValue ? 1 : body.Generation + 1;
        int[]? footprint = body.Voxels.Slots;
        body = default;
        body.Generation = generation;
        body.Voxels.Slots = footprint;   // kept for the next body of the slot
        body.NextFree = _bodyFree;
        _bodyFree = slot;
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

    [HotPath]
    private void FreeJoint(int slot)
    {
        ref JointRecord joint = ref _joints[slot];
        int generation = joint.Generation == int.MaxValue ? 1 : joint.Generation + 1;
        joint = default;
        joint.Generation = generation;
        joint.NextFree = _jointFree;
        _jointFree = slot;
        _jointCount--;
    }

    [HotPath]
    private void CountKind(BodyKind kind, int delta)
    {
        switch (kind)
        {
            case BodyKind.Static:
                _staticBodies += delta;
                break;
            case BodyKind.Kinematic:
                _kinematicBodies += delta;
                break;
            default:
                _dynamicBodies += delta;
                break;
        }
    }

    [HotPath]
    private static BodyFlags FlagsOf(in BodyRecord body) =>
        (body.Continuous ? BodyFlags.Continuous : BodyFlags.None)
        | (body.CanSleep ? BodyFlags.CanSleep : BodyFlags.None)
        | (body.Asleep ? BodyFlags.Asleep : BodyFlags.None)
        | (body.World is null ? BodyFlags.Frozen : BodyFlags.None)
        | (body.ReportsContacts ? BodyFlags.ReportsContacts : BodyFlags.None);

    [HotPath]
    private static BodyMass MassOf(in BodyRecord body) =>
        body.Kind == BodyKind.Dynamic ? new BodyMass { Mass = body.Mass, Inertia = body.Inertia } : default;

    [HotPath]
    private static void RequireFiniteVector(Vector3 value)
    {
        if (!IsFinite(value))
        {
            throw new ArgumentException($"The vector {value} is not finite.", nameof(value));
        }
    }

    [HotPath]
    private static void RequirePose(in WorldPosition position, Quaternion rotation)
    {
        if (!IsFinite(position.Local) || !IsFinite(rotation) || rotation.LengthSquared() < 1e-6f)
        {
            throw new ArgumentException("The position and the rotation are finite, and the rotation is not zero.", nameof(rotation));
        }
    }

    [HotPath]
    private static void RequireMaterial(PhysicsMaterial material)
    {
        if (!float.IsFinite(material.Friction) || material.Friction < 0 || !float.IsFinite(material.Restitution) || material.Restitution is < 0 or > 1)
        {
            throw new ArgumentException($"The friction of a material is finite and not negative, and its restitution goes from 0 to 1; here {material}.", nameof(material));
        }
    }

    [HotPath]
    private static void RequireReporting(ContactReporting reporting)
    {
        if (!float.IsFinite(reporting.ImpactSpeed) || reporting.ImpactSpeed < 0)
        {
            throw new ArgumentException($"The impact speed of a body is finite and not negative; here {reporting.ImpactSpeed}.", nameof(reporting));
        }
    }

    [HotPath]
    private static void RequireMotor(JointMotor motor)
    {
        if (!float.IsFinite(motor.TargetVelocity) || !float.IsFinite(motor.MaxForce) || motor.MaxForce < 0)
        {
            throw new ArgumentException($"The target velocity of a motor is finite, and its effort finite and not negative; here {motor}.", nameof(motor));
        }
    }

    private static Vector3 RequireAxis(Vector3 axis)
    {
        if (!IsFinite(axis) || axis.LengthSquared() < 1e-12f)
        {
            throw new ArgumentException($"The axis {axis} of a joint is finite and not zero.", nameof(axis));
        }

        return Vector3.Normalize(axis);
    }

    private enum CommandKind : byte
    {
        Teleport,
        SetVelocity,
        KinematicTarget,
        Force,
        ForceAt,
        Impulse,
        ImpulseAt,
        Torque,
        AngularImpulse,
        Wake,
        WakeArea,
        SetKind,
        SetShape,
        SetMass,
        SetMaterial,
        SetFilter,
        SetContinuous,
        SetContactReporting,
        SetMotor,
    }

    /// <summary>One queued command; the fields a kind does not use stay at their default.</summary>
    private struct PhysicsCommand
    {
        public CommandKind Command;
        public BodyHandle Body;
        public JointHandle Joint;
        public DimensionId Dimension;
        public WorldPosition Point;
        public Quaternion Rotation;
        public Vector3 A;
        public Vector3 B;
        public float Value;
        public BodyKind Kind;
        public PhysicsShape Shape;
        public PhysicsMaterial Material;
        public CollisionFilter Filter;
        public bool Flag;
        public bool HasInertia;
        public BodyInertia Inertia;
        public bool HasMotor;
        public JointMotor Motor;
    }

    private struct BodyRecord
    {
        public int Generation;
        public bool Alive;
        public int NextFree;
        /// <summary>The world the body is in; <see langword="null"/> while it is frozen, out of every region.</summary>
        public WorldEntry? World;
        public int BackendId;
        public EntityWorld? EntityWorld;
        public DimensionId Dimension;
        public EntityId Entity;
        public Grid? Grid;
        public BodyKind Kind;
        public PhysicsShape Shape;
        public float Mass;
        public BodyInertia Inertia;
        public bool ExplicitInertia;
        public Vector3 Center;
        public PhysicsMaterial Material;
        public CollisionFilter Filter;
        public bool Continuous;
        public bool CanSleep;

        /// <summary>Whether the body reports its contacts, and the slowest approach it reports as an impact.</summary>
        public bool ReportsContacts;
        public float ImpactSpeed;
        public bool Asleep;
        public bool Dirty;
        public bool TargetPending;
        public ulong TargetTick;

        /// <summary>The center of the body in the world, after the last step or command: where it enters a world, or stays while frozen.</summary>
        public WorldPosition Position;
        public Quaternion Rotation;

        /// <summary>The velocities after the last step, kept while the body is frozen.</summary>
        public Vector3 Linear;
        public Vector3 Angular;

        /// <summary>Whether a teleport set <see cref="Position"/> since the last pass on the bodies.</summary>
        public bool Teleported;

        /// <summary>Whether the body changes world in the current pass on the bodies.</summary>
        public bool Moving;

        /// <summary>The first ghost of the body, plus one; 0 when it has none.</summary>
        public int FirstGhost;

        /// <summary>The chunks of voxels the body holds in its world, while it is dynamic.</summary>
        public VoxelFootprint Voxels;
    }

    private struct JointRecord
    {
        public int Generation;
        public bool Alive;
        public int NextFree;

        /// <summary>The world the joint is in; <see langword="null"/> until its bodies share one.</summary>
        public WorldEntry? World;
        public int BackendId;
        public BodyHandle First;
        public BodyHandle Second;
        public JointKind Kind;

        /// <summary>The joint as nPhysics gives it to a world, without the numbers of its bodies, to add it again after a transfer.</summary>
        public BackendJoint Description;
    }
}
