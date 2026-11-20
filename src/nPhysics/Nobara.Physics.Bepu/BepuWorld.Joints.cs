// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using BepuPhysics;
using BepuPhysics.Constraints;
using BepuUtilities.Memory;
using Nobara.Core;

namespace Nobara.Physics.Bepu;

using BodyHandle = BepuPhysics.BodyHandle;

/// <summary>
/// The joints of a world, as constraints of BEPUphysics: a hinge is a
/// <see cref="Hinge"/>, plus an <see cref="AngularAxisMotor"/> when it has a
/// motor; a slider is a <see cref="PointOnLineServo"/> and an
/// <see cref="AngularServo"/> that keeps the rotation the bodies had when it
/// was made, plus a <see cref="LinearAxisMotor"/>; a fixed joint is a
/// <see cref="Weld"/>.
/// </summary>
/// <remarks>
/// The motor drives the second body relative to the first: about the axis of
/// the first body for a hinge, along it for a slider. A slider uses the axis of
/// the first body only.
/// </remarks>
internal sealed partial class BepuWorld
{
    /// <summary>The softness of the motors: small, so that a motor holds its speed within its effort.</summary>
    public const float MotorSoftness = 1e-4f;

    private Buffer<JointEntry> _joints;
    private int _jointHigh;
    private int _jointFree = -1;

    /// <summary>Gets the stiffness of the joints: as stiff as the contacts.</summary>
    private static SpringSettings JointSpring
    {
        [HotPath]
        get => new(NarrowPhaseCallbacks.ContactFrequency, 1f);
    }

    /// <inheritdoc/>
    public override int AddJoint(in BackendJoint joint)
    {
        ref BodyEntry a = ref Require(joint.BodyA);
        ref BodyEntry b = ref Require(joint.BodyB);
        int id = AllocateJoint();
        ref JointEntry entry = ref _joints[id];
        entry.Kind = joint.Kind;
        entry.BodyA = joint.BodyA;
        entry.BodyB = joint.BodyB;
        entry.AnchorA = joint.AnchorA;
        entry.AnchorB = joint.AnchorB;
        entry.AxisA = SafeAxis(joint.AxisA);
        entry.AxisB = SafeAxis(joint.AxisB);
        entry.HasMotor = joint.Motor.HasValue;
        entry.Motor = joint.Motor.GetValueOrDefault();
        a.Joints++;
        b.Joints++;
        Promote(ref a);
        Promote(ref b);
        ReadPose(_bodies[joint.BodyA], out RigidPose poseA, out _);
        ReadPose(_bodies[joint.BodyB], out RigidPose poseB, out _);
        Quaternion inverseA = Quaternion.Conjugate(poseA.Orientation);
        entry.Relative = Quaternion.Normalize(inverseA * poseB.Orientation);
        entry.Offset = Vector3.Transform(poseB.Position - poseA.Position, inverseA);
        CreateConstraints(ref entry);
        return id;
    }

    /// <inheritdoc/>
    public override void RemoveJoint(int joint)
    {
        ref JointEntry entry = ref RequireJoint(joint);
        RemoveConstraints(ref entry);
        ref BodyEntry a = ref Require(entry.BodyA);
        a.Joints--;
        Demote(ref a);
        ref BodyEntry b = ref Require(entry.BodyB);
        b.Joints--;
        Demote(ref b);
        entry = default;
        entry.NextFree = _jointFree;
        _jointFree = joint;
    }

    /// <inheritdoc/>
    public override void SetJointMotor(int joint, JointMotor? motor)
    {
        ref JointEntry entry = ref RequireJoint(joint);
        if (entry.Kind == JointKind.Fixed)
        {
            return;
        }

        entry.HasMotor = motor.HasValue;
        entry.Motor = motor.GetValueOrDefault();
        if (entry.HasMotor)
        {
            if (entry.MotorConstraint.Value >= 0 && Simulation.Solver.ConstraintExists(entry.MotorConstraint))
            {
                ApplyMotor(entry);
            }
            else
            {
                entry.MotorConstraint = AddMotor(entry);
            }
        }
        else if (entry.MotorConstraint.Value >= 0)
        {
            Simulation.Solver.Remove(entry.MotorConstraint);
            entry.MotorConstraint = new ConstraintHandle(-1);
        }

        Awaken(_bodies[entry.BodyA].BodyHandle);
        Awaken(_bodies[entry.BodyB].BodyHandle);
    }

    /// <summary>Builds the constraints of the joints of a body again, after the body moved between the statics and the bodies.</summary>
    [HotPath]
    private void ReattachJoints(int body)
    {
        for (int i = 0; i < _jointHigh; i++)
        {
            ref JointEntry entry = ref _joints[i];
            if (entry.Used && (entry.BodyA == body || entry.BodyB == body) && entry.Built)
            {
                RemoveConstraints(ref entry);
                CreateConstraints(ref entry);
            }
        }
    }

    /// <summary>Makes a static body with joints a kinematic body of BEPUphysics, which constraints can hold.</summary>
    [HotPath]
    private void Promote(ref BodyEntry body)
    {
        if (!body.InBodies && NeedsBody(body))
        {
            Replace(ref body);
        }
    }

    /// <summary>Makes a static body that has lost its last joint a static of BEPUphysics again.</summary>
    [HotPath]
    private void Demote(ref BodyEntry body)
    {
        if (body.InBodies && !NeedsBody(body))
        {
            Replace(ref body);
        }
    }

    [HotPath]
    private void CreateConstraints(ref JointEntry entry)
    {
        BodyHandle a = _bodies[entry.BodyA].BodyHandle;
        BodyHandle b = _bodies[entry.BodyB].BodyHandle;
        Solver solver = Simulation.Solver;
        entry.First = new ConstraintHandle(-1);
        entry.Second = new ConstraintHandle(-1);
        entry.MotorConstraint = new ConstraintHandle(-1);
        switch (entry.Kind)
        {
            case JointKind.Hinge:
                entry.First = solver.Add(a, b, new Hinge
                {
                    LocalOffsetA = entry.AnchorA,
                    LocalHingeAxisA = entry.AxisA,
                    LocalOffsetB = entry.AnchorB,
                    LocalHingeAxisB = entry.AxisB,
                    SpringSettings = JointSpring,
                });
                break;
            case JointKind.Slider:
                entry.First = solver.Add(a, b, new PointOnLineServo
                {
                    LocalOffsetA = entry.AnchorA,
                    LocalOffsetB = entry.AnchorB,
                    LocalDirection = entry.AxisA,
                    ServoSettings = ServoSettings.Default,
                    SpringSettings = JointSpring,
                });
                entry.Second = solver.Add(a, b, new AngularServo
                {
                    TargetRelativeRotationLocalA = entry.Relative,
                    ServoSettings = ServoSettings.Default,
                    SpringSettings = JointSpring,
                });
                break;
            default:
                entry.First = solver.Add(a, b, new Weld
                {
                    LocalOffset = entry.Offset,
                    LocalOrientation = entry.Relative,
                    SpringSettings = JointSpring,
                });
                break;
        }

        if (entry.HasMotor && entry.Kind != JointKind.Fixed)
        {
            entry.MotorConstraint = AddMotor(entry);
        }

        entry.Built = true;
    }

    [HotPath]
    private void RemoveConstraints(ref JointEntry entry)
    {
        Solver solver = Simulation.Solver;
        if (entry.First.Value >= 0)
        {
            solver.Remove(entry.First);
        }

        if (entry.Second.Value >= 0)
        {
            solver.Remove(entry.Second);
        }

        if (entry.MotorConstraint.Value >= 0)
        {
            solver.Remove(entry.MotorConstraint);
        }

        entry.First = new ConstraintHandle(-1);
        entry.Second = new ConstraintHandle(-1);
        entry.MotorConstraint = new ConstraintHandle(-1);
        entry.Built = false;
    }

    [HotPath]
    private ConstraintHandle AddMotor(in JointEntry entry)
    {
        BodyHandle a = _bodies[entry.BodyA].BodyHandle;
        BodyHandle b = _bodies[entry.BodyB].BodyHandle;
        return entry.Kind == JointKind.Hinge
            ? Simulation.Solver.Add(a, b, HingeMotor(entry))
            : Simulation.Solver.Add(a, b, SliderMotor(entry));
    }

    [HotPath]
    private void ApplyMotor(in JointEntry entry)
    {
        if (entry.Kind == JointKind.Hinge)
        {
            Simulation.Solver.ApplyDescription(entry.MotorConstraint, HingeMotor(entry));
        }
        else
        {
            Simulation.Solver.ApplyDescription(entry.MotorConstraint, SliderMotor(entry));
        }
    }

    // The angular motor of BEPUphysics drives the first body relative to the second: its neutral target is negated.
    [HotPath]
    private static AngularAxisMotor HingeMotor(in JointEntry entry) => new()
    {
        LocalAxisA = entry.AxisA,
        TargetVelocity = -entry.Motor.TargetVelocity,
        Settings = new MotorSettings(entry.Motor.MaxForce, MotorSoftness),
    };

    [HotPath]
    private static LinearAxisMotor SliderMotor(in JointEntry entry) => new()
    {
        LocalOffsetA = entry.AnchorA,
        LocalOffsetB = entry.AnchorB,
        LocalAxis = entry.AxisA,
        TargetVelocity = entry.Motor.TargetVelocity,
        Settings = new MotorSettings(entry.Motor.MaxForce, MotorSoftness),
    };

    [HotPath]
    private static Vector3 SafeAxis(Vector3 axis) => axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitY;

    [HotPath]
    private ref JointEntry RequireJoint(int joint)
    {
        if ((uint)joint >= (uint)_jointHigh || !_joints[joint].Used)
        {
            throw new ArgumentException($"The world holds no joint {joint}.", nameof(joint));
        }

        return ref _joints[joint];
    }

    [HotPath]
    private int AllocateJoint()
    {
        int id;
        if (_jointFree >= 0)
        {
            id = _jointFree;
            _jointFree = _joints[id].NextFree;
        }
        else
        {
            if (_jointHigh == _joints.Length)
            {
                _pool.ResizeToAtLeast(ref _joints, _joints.Length * 2, _jointHigh);
            }

            id = _jointHigh++;
        }

        _joints[id] = default;
        _joints[id].Used = true;
        _joints[id].NextFree = -1;
        return id;
    }

    /// <summary>A joint of the world, and the constraints that make it.</summary>
    private struct JointEntry
    {
        public bool Used;
        public bool Built;
        public JointKind Kind;
        public int BodyA;
        public int BodyB;
        public Vector3 AnchorA;
        public Vector3 AnchorB;
        public Vector3 AxisA;
        public Vector3 AxisB;
        public bool HasMotor;
        public JointMotor Motor;

        /// <summary>The rotation of the second body in the frame of the first, when the joint was made.</summary>
        public Quaternion Relative;

        /// <summary>The center of the second body in the frame of the first, when the joint was made.</summary>
        public Vector3 Offset;

        public ConstraintHandle First;
        public ConstraintHandle Second;
        public ConstraintHandle MotorConstraint;
        public int NextFree;
    }
}
