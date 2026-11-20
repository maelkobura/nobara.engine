// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Voxel;

namespace Nobara.Physics.Tests.Support;

/// <summary>A backend of nPhysics for the tests: it integrates bodies without collisions and records what it is asked.</summary>
internal sealed class TestPhysicsBackend(string name = "test") : IPhysicsBackend
{
    public string Name { get; } = name;

    public List<TestPhysicsWorld> Worlds { get; } = [];

    /// <summary>Gets or sets whether the worlds keep a journal of calls; off for measures of allocations.</summary>
    public bool Records { get; set; } = true;

    public BodyInertia ComputeInertia(ShapeDescription shape, float mass) => BodyInertia.Diagonal(new Vector3(mass));

    public PhysicsBackendWorld CreateWorld(in PhysicsWorldOptions options)
    {
        var world = new TestPhysicsWorld(this, options);
        Worlds.Add(world);
        return world;
    }
}

internal sealed class TestPhysicsWorld(TestPhysicsBackend backend, PhysicsWorldOptions options) : PhysicsBackendWorld
{
    private readonly List<TestBody> _bodies = [];
    private readonly List<BackendJoint?> _joints = [];

    public PhysicsWorldOptions Options { get; } = options;

    public List<string> Calls { get; } = [];

    public List<PhysicsStepSettings> Steps { get; } = [];

    public int StepCount { get; private set; }

    public int SubstepCount { get; private set; }

    /// <summary>Gets the largest coordinate the world received or reported, in absolute value: positions relative to the origin stay small.</summary>
    public float LargestCoordinate { get; private set; }

    public bool IsDisposed { get; private set; }

    public IReadOnlyList<TestBody> Bodies => _bodies;

    public IReadOnlyList<BackendJoint?> Joints => _joints;

    public TestBody Body(int id) => _bodies[id];

    /// <summary>Puts a body to sleep, as a backend does when it rests.</summary>
    public void Sleep(int id)
    {
        TestBody body = _bodies[id];
        body.Asleep = true;
        body.Linear = Vector3.Zero;
        body.Angular = Vector3.Zero;
    }

    public override int AddBody(in BackendBody body)
    {
        Track(body.Position);
        _bodies.Add(new TestBody
        {
            Description = body,
            Kind = body.Kind,
            Shape = body.Shape,
            Position = body.Position,
            Rotation = body.Rotation,
            Linear = body.LinearVelocity,
            Angular = body.AngularVelocity,
            Mass = body.Mass,
        });
        if (Records) Calls.Add($"add {body.Kind} {body.Position}");
        return _bodies.Count - 1;
    }

    public override void RemoveBody(int body)
    {
        _bodies[body].Removed = true;
        if (Records) Calls.Add($"remove {body}");
    }

    public override void SetPose(int body, Vector3 position, Quaternion rotation)
    {
        Track(position);
        _bodies[body].Position = position;
        _bodies[body].Rotation = rotation;
        if (Records) Calls.Add($"pose {body} {position}");
    }

    public override void SetVelocity(int body, Vector3 linear, Vector3 angular)
    {
        _bodies[body].Linear = linear;
        _bodies[body].Angular = angular;
        _bodies[body].Asleep = false;
        if (Records) Calls.Add($"velocity {body} {linear}");
    }

    public override void ApplyImpulse(int body, Vector3 impulse, Vector3 offset)
    {
        Track(offset);
        TestBody target = _bodies[body];
        target.Linear += impulse / target.Mass;
        target.Asleep = false;
        target.LastOffset = offset;
        if (Records) Calls.Add($"impulse {body} {impulse}");
    }

    public override void ApplyAngularImpulse(int body, Vector3 impulse)
    {
        _bodies[body].Angular += impulse / _bodies[body].Mass;
        _bodies[body].Asleep = false;
        if (Records) Calls.Add($"angular {body} {impulse}");
    }

    public override void SetKind(int body, BodyKind kind, float mass, in BodyInertia inertia)
    {
        _bodies[body].Kind = kind;
        _bodies[body].Mass = mass;
        _bodies[body].Asleep = false;
        if (Records) Calls.Add($"kind {body} {kind}");
    }

    public override void SetShape(int body, PhysicsShape shape)
    {
        _bodies[body].Shape = shape;
        if (Records) Calls.Add($"shape {body} {shape}");
    }

    public override void SetMass(int body, float mass, in BodyInertia inertia)
    {
        _bodies[body].Mass = mass;
        if (Records) Calls.Add($"mass {body} {mass}");
    }

    public override void SetMaterial(int body, PhysicsMaterial material)
    {
        if (Records) Calls.Add($"material {body} {material}");
    }

    public override void SetFilter(int body, CollisionFilter filter)
    {
        if (Records) Calls.Add($"filter {body} {filter}");
    }

    public override void SetContinuous(int body, bool continuous)
    {
        if (Records) Calls.Add($"continuous {body} {continuous}");
    }

    public override void Wake(int body)
    {
        _bodies[body].Asleep = false;
        if (Records) Calls.Add($"wake {body}");
    }

    public override void WakeArea(Vector3 minimum, Vector3 maximum)
    {
        Track(minimum);
        Track(maximum);
        if (Records) Calls.Add($"wake area {minimum} {maximum}");
    }

    public override void ReadBody(int body, out BackendBodyState state)
    {
        TestBody read = _bodies[body];
        state = new BackendBodyState { Position = read.Position, Rotation = read.Rotation, LinearVelocity = read.Linear, AngularVelocity = read.Angular, IsAsleep = read.Asleep };
    }

    public override int AddJoint(in BackendJoint joint)
    {
        _joints.Add(joint);
        if (Records) Calls.Add($"joint {joint.Kind} {joint.BodyA} {joint.BodyB}");
        return _joints.Count - 1;
    }

    public override void RemoveJoint(int joint)
    {
        _joints[joint] = null;
        if (Records) Calls.Add($"remove joint {joint}");
    }

    public override void SetJointMotor(int joint, JointMotor? motor)
    {
        BackendJoint changed = _joints[joint]!.Value;
        changed.Motor = motor;
        _joints[joint] = changed;
        if (Records) Calls.Add($"motor {joint} {motor}");
    }

    public override void Step(in PhysicsStepSettings settings)
    {
        StepCount++;
        if (backend.Records)
        {
            Steps.Add(settings);
        }

        float delta = settings.Delta / settings.Substeps;
        for (int substep = 0; substep < settings.Substeps; substep++)
        {
            SubstepCount++;
            foreach (TestBody body in _bodies)
            {
                if (body.Removed || body.Asleep || body.Kind == BodyKind.Static)
                {
                    continue;
                }

                if (body.Kind == BodyKind.Dynamic)
                {
                    body.Linear += Options.Gravity * delta;
                }

                body.Position += body.Linear * delta;
                if (body.Angular != Vector3.Zero)
                {
                    float angle = body.Angular.Length() * delta;
                    body.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(body.Angular), angle) * body.Rotation);
                }

                Track(body.Position);
            }
        }

        Record("step");
    }

    public override int AddVoxelChunk(in VoxelChunkCollider chunk) => throw new NotSupportedException();

    public override void RemoveVoxelChunk(int chunk) => throw new NotSupportedException();

    public override bool RayCast(Vector3 origin, Vector3 direction, float maxDistance, CollisionFilter filter, out BackendRayHit hit) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        Record("dispose");
    }

    private void Track(Vector3 value) =>
        LargestCoordinate = MathF.Max(LargestCoordinate, MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z))));

    private bool Records => backend.Records;

    private void Record(string call)
    {
        if (backend.Records)
        {
            Calls.Add(call);
        }
    }
}

internal sealed class TestBody
{
    public BackendBody Description;
    public BodyKind Kind;
    public PhysicsShape Shape;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Linear;
    public Vector3 Angular;
    public float Mass;
    public bool Asleep;
    public bool Removed;
    public Vector3 LastOffset;
}
