// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using Nobara.Core;
using BepuInertia = BepuPhysics.BodyInertia;

namespace Nobara.Physics.Bepu;

using BodyDescription = BepuPhysics.BodyDescription;
using BodyHandle = BepuPhysics.BodyHandle;
using BodyVelocity = BepuPhysics.BodyVelocity;

/// <summary>
/// One physics world on BEPUphysics: a <see cref="Simulation"/>, its pool of
/// unmanaged memory, the tables that map the numbers of nPhysics to the handles
/// of BEPUphysics, and the shapes the world built.
/// </summary>
/// <remarks>
/// <para>
/// A static body is a static of BEPUphysics, except while a joint holds it:
/// BEPUphysics joins bodies only, so a static body with joints is a kinematic
/// body that never moves. A neutral joint is one to three constraints.
/// </para>
/// <para>
/// The tables are buffers of the pool of the world, not managed arrays: they
/// grow with the number of bodies and are freed with the world.
/// </para>
/// </remarks>
internal sealed partial class BepuWorld : PhysicsBackendWorld
{
    private readonly BufferPool _pool = new();
    private readonly CollidableProperty<Surface> _surfaces;
    private readonly IPhysicsShapeSource _shapeSource;
    private readonly JobThreadDispatcher? _dispatcher;
    private readonly BepuBackend _backend;
    private readonly Bounces _bounces;
    private readonly ContactReports _reports;
    private readonly StrongBox<Vector3> _gravity;
    private Buffer<BodyEntry> _bodies;
    private int _bodyHigh;
    private int _bodyFree = -1;
    private QuickList<BodyHandle> _waking;
    private bool _disposed;

    /// <summary>Creates a world.</summary>
    /// <param name="options">The options nPhysics gives.</param>
    /// <param name="dispatcher">The dispatcher that a large step spreads over, or <see langword="null"/>; the world owns it.</param>
    /// <param name="backend">The backend, for its settings.</param>
    public BepuWorld(in PhysicsWorldOptions options, JobThreadDispatcher? dispatcher, BepuBackend backend)
    {
        Origin = options.Origin;
        _shapeSource = options.Shapes;
        _dispatcher = dispatcher;
        _backend = backend;
        _surfaces = new CollidableProperty<Surface>(_pool);
        _bounces = new Bounces(dispatcher?.ThreadCount ?? 1);
        _reports = new ContactReports(dispatcher?.ThreadCount ?? 1);
        _gravity = new StrongBox<Vector3>(options.Gravity);
        _queryPools = new BufferPool?[Math.Max(1, options.QueryThreads)];
        _queryHits = new BackendQueryHit[_queryPools.Length][];
        for (int i = 0; i < _queryPools.Length; i++)
        {
            _queryPools[i] = i == 0 ? null : new BufferPool();   // thread 0 uses the pool of the world
            _queryHits[i] = new BackendQueryHit[8];
        }
        Simulation = Simulation.Create(
            _pool,
            new NarrowPhaseCallbacks(_surfaces, _bounces, _reports),
            new PoseIntegratorCallbacks(_gravity),
            new SolveDescription(1, 8));
        VoxelCollisionTasks.Register(Simulation);
        _pool.Take(16, out _bodies);
        _waking = new QuickList<BodyHandle>(16, _pool);
        _pool.Take(16, out _joints);
        _pool.Take(16, out _chunks);
    }

    /// <summary>Gets the origin of the world.</summary>
    public Voxel.ChunkCoordinate Origin { get; }

    /// <summary>Gets the simulation of BEPUphysics.</summary>
    public Simulation Simulation { get; }

    /// <summary>Gets the dispatcher of the world, or <see langword="null"/>.</summary>
    public JobThreadDispatcher? Dispatcher => _dispatcher;

    /// <summary>Gets the number of steps that were spread over the job system.</summary>
    public long SpreadSteps { get; private set; }

    /// <summary>Gets the unmanaged memory of the world, dispatcher included, in bytes.</summary>
    public ulong AllocatedBytes => _pool.GetTotalAllocatedByteCount() + (_dispatcher?.AllocatedBytes ?? 0);

    /// <inheritdoc/>
    public override int AddBody(in BackendBody body)
    {
        int id = AllocateBody();
        ref BodyEntry entry = ref _bodies[id];
        entry.Slot = body.Slot;
        entry.Kind = body.Kind;
        entry.Shape = AcquireShape(body.Shape);
        entry.Mass = body.Mass;
        entry.Inertia = body.Inertia;
        entry.Continuous = body.Continuous;
        entry.CanSleep = body.CanSleep;
        entry.Joints = 0;
        entry.Surface = new Surface
        {
            Friction = body.Material.Friction,
            Restitution = body.Material.Restitution,
            Layers = body.Filter.Layers,
            Mask = body.Filter.Mask,
            Body = id,
            Slot = body.Slot,
            Chunk = -1,
            Reports = body.ReportsContacts,
            Continuous = body.Continuous,
        };
        var pose = new RigidPose(body.Position, Quaternion.Normalize(body.Rotation));
        var velocity = new BodyVelocity(body.LinearVelocity, body.AngularVelocity);
        Place(ref entry, pose, velocity);
        return id;
    }

    /// <inheritdoc/>
    public override void RemoveBody(int body)
    {
        ref BodyEntry entry = ref Require(body);
        Unplace(ref entry);
        ReleaseShape(entry.Shape);
        entry = default;
        entry.NextFree = _bodyFree;
        _bodyFree = body;
    }

    /// <inheritdoc/>
    public override void SetPose(int body, Vector3 position, Quaternion rotation)
    {
        ref BodyEntry entry = ref Require(body);
        var pose = new RigidPose(position, Quaternion.Normalize(rotation));
        if (entry.InBodies)
        {
            BodyReference reference = Simulation.Bodies[entry.BodyHandle];
            reference.Pose = pose;
            reference.Awake = true;
            reference.UpdateBounds();
        }
        else
        {
            Simulation.Statics.ApplyDescription(entry.StaticHandle, new StaticDescription(pose, ShapeIndex(entry.Shape), Continuity(entry.Continuous)));
        }
    }

    /// <inheritdoc/>
    public override void SetVelocity(int body, Vector3 linear, Vector3 angular)
    {
        ref BodyEntry entry = ref Require(body);
        if (entry.InBodies && entry.Kind != BodyKind.Static)
        {
            BodyReference reference = Simulation.Bodies[entry.BodyHandle];
            reference.Velocity = new BodyVelocity(linear, angular);
            reference.Awake = true;
        }
    }

    /// <inheritdoc/>
    public override void ApplyImpulse(int body, Vector3 impulse, Vector3 offset)
    {
        ref BodyEntry entry = ref Require(body);
        if (entry.InBodies && entry.Kind == BodyKind.Dynamic)
        {
            BodyReference reference = Simulation.Bodies[entry.BodyHandle];
            reference.Awake = true;
            reference.ApplyImpulse(impulse, offset);
        }
    }

    /// <inheritdoc/>
    public override void ApplyAngularImpulse(int body, Vector3 impulse)
    {
        ref BodyEntry entry = ref Require(body);
        if (entry.InBodies && entry.Kind == BodyKind.Dynamic)
        {
            BodyReference reference = Simulation.Bodies[entry.BodyHandle];
            reference.Awake = true;
            reference.ApplyAngularImpulse(impulse);
        }
    }

    /// <inheritdoc/>
    public override void SetKind(int body, BodyKind kind, float mass, in BodyInertia inertia)
    {
        ref BodyEntry entry = ref Require(body);
        entry.Kind = kind;
        entry.Mass = mass;
        entry.Inertia = inertia;
        if (entry.InBodies != NeedsBody(entry))
        {
            Replace(ref entry);
            return;
        }

        if (entry.InBodies)
        {
            Simulation.Bodies.SetLocalInertia(entry.BodyHandle, InertiaOf(entry));
            BodyReference reference = Simulation.Bodies[entry.BodyHandle];
            if (kind == BodyKind.Static)
            {
                reference.Velocity = default;
            }

            reference.Awake = true;
        }
    }

    /// <inheritdoc/>
    public override void SetShape(int body, PhysicsShape shape)
    {
        ref BodyEntry entry = ref Require(body);
        int previous = entry.Shape;
        entry.Shape = AcquireShape(shape);
        if (entry.InBodies)
        {
            Simulation.Bodies.SetShape(entry.BodyHandle, ShapeIndex(entry.Shape));
            Awaken(entry.BodyHandle);
        }
        else
        {
            Simulation.Statics.SetShape(entry.StaticHandle, ShapeIndex(entry.Shape));
        }

        ReleaseShape(previous);
    }

    /// <inheritdoc/>
    public override void SetMass(int body, float mass, in BodyInertia inertia)
    {
        ref BodyEntry entry = ref Require(body);
        entry.Mass = mass;
        entry.Inertia = inertia;
        if (entry.InBodies && entry.Kind == BodyKind.Dynamic)
        {
            Simulation.Bodies.SetLocalInertia(entry.BodyHandle, InertiaOf(entry));
            Awaken(entry.BodyHandle);
        }
    }

    /// <inheritdoc/>
    public override void SetMaterial(int body, PhysicsMaterial material)
    {
        ref BodyEntry entry = ref Require(body);
        entry.Surface.Friction = material.Friction;
        entry.Surface.Restitution = material.Restitution;
        WriteSurface(entry);
    }

    /// <inheritdoc/>
    public override void SetFilter(int body, CollisionFilter filter)
    {
        ref BodyEntry entry = ref Require(body);
        entry.Surface.Layers = filter.Layers;
        entry.Surface.Mask = filter.Mask;
        WriteSurface(entry);
    }

    /// <inheritdoc/>
    public override void SetContinuous(int body, bool continuous)
    {
        ref BodyEntry entry = ref Require(body);
        entry.Continuous = continuous;
        entry.Surface.Continuous = continuous;
        WriteSurface(entry);
        if (entry.InBodies)
        {
            Simulation.Bodies[entry.BodyHandle].Collidable.Continuity = Continuity(continuous);
        }
        else
        {
            Simulation.Statics[entry.StaticHandle].Continuity = Continuity(continuous);
        }
    }

    /// <inheritdoc/>
    public override void SetReportsContacts(int body, bool reports)
    {
        ref BodyEntry entry = ref Require(body);
        entry.Surface.Reports = reports;
        WriteSurface(entry);
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<BackendContact> Contacts => _reports.Gathered;

    /// <inheritdoc/>
    public override void SetGravity(Vector3 gravity) => _gravity.Value = gravity;

    /// <inheritdoc/>
    public override void Wake(int body)
    {
        ref BodyEntry entry = ref Require(body);
        if (entry.InBodies)
        {
            Awaken(entry.BodyHandle);
        }
    }

    /// <inheritdoc/>
    public override void WakeArea(Vector3 minimum, Vector3 maximum)
    {
        _waking.Count = 0;
        var collector = new BodyCollector(this);
        Simulation.BroadPhase.GetOverlaps(Vector3.Min(minimum, maximum), Vector3.Max(minimum, maximum), _pool, ref collector);
        for (int i = 0; i < _waking.Count; i++)
        {
            Simulation.Awakener.AwakenBody(_waking[i]);
        }

        _waking.Count = 0;
    }

    /// <inheritdoc/>
    public override void ReadBody(int body, out BackendBodyState state)
    {
        ref BodyEntry entry = ref Require(body);
        state = default;
        if (entry.InBodies)
        {
            BodyReference reference = Simulation.Bodies[entry.BodyHandle];
            RigidPose pose = reference.Pose;
            state.Position = pose.Position;
            state.Rotation = pose.Orientation;
            state.LinearVelocity = reference.Velocity.Linear;
            state.AngularVelocity = reference.Velocity.Angular;
            state.IsAsleep = !reference.Awake;
        }
        else
        {
            RigidPose pose = Simulation.Statics[entry.StaticHandle].Pose;
            state.Position = pose.Position;
            state.Rotation = pose.Orientation;
        }
    }

    /// <inheritdoc/>
    public override void Step(in PhysicsStepSettings settings)
    {
        Solver solver = Simulation.Solver;
        if (solver.SubstepCount != settings.Substeps)
        {
            solver.SubstepCount = settings.Substeps;
        }

        if (solver.VelocityIterationCount != settings.VelocityIterations)
        {
            solver.VelocityIterationCount = settings.VelocityIterations;
        }

        _bounces.Prepare(settings.Delta, settings.Substeps);
        _reports.Prepare(settings.Delta);
        if (settings.MaySpread && _dispatcher is not null && Simulation.Bodies.ActiveSet.Count >= _backend.SpreadThreshold)
        {
            SpreadSteps++;
            Simulation.Timestep(settings.Delta, _dispatcher);
        }
        else
        {
            Simulation.Timestep(settings.Delta);
        }

        _bounces.Apply();
        _reports.Gather();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            for (int i = 0; i < _chunkHigh; i++)
            {
                if (_chunks[i].Used)
                {
                    GCHandle.FromIntPtr(_chunks[i].Cells).Free();
                }
            }

            DisposeQueries();
            Simulation.Dispose();
            _dispatcher?.Dispose();
            _backend.Forget(this);
            _shapeSlots.Clear();
            _pool.Clear();
        }

        base.Dispose(disposing);
    }

    /// <summary>Gets the detection that a body uses: continuous with a sweep, or passive; both with an unbounded speculative margin.</summary>
    [HotPath]
    private static ContinuousDetection Continuity(bool continuous) =>
        continuous ? ContinuousDetection.Continuous() : ContinuousDetection.Passive;

    /// <summary>Gets whether a body must be a body of BEPUphysics rather than a static.</summary>
    [HotPath]
    private static bool NeedsBody(in BodyEntry entry) => entry.Kind != BodyKind.Static || entry.Joints > 0;

    [HotPath]
    private static BepuInertia InertiaOf(in BodyEntry entry) =>
        entry.Kind == BodyKind.Dynamic ? Inertia.ToBepu(entry.Mass, entry.Inertia) : default;

    [HotPath]
    private ref BodyEntry Require(int body)
    {
        if ((uint)body >= (uint)_bodyHigh || !_bodies[body].Used)
        {
            throw new ArgumentException($"The world holds no body {body}.", nameof(body));
        }

        return ref _bodies[body];
    }

    [HotPath]
    private int AllocateBody()
    {
        int id;
        if (_bodyFree >= 0)
        {
            id = _bodyFree;
            _bodyFree = _bodies[id].NextFree;
        }
        else
        {
            if (_bodyHigh == _bodies.Length)
            {
                _pool.ResizeToAtLeast(ref _bodies, _bodies.Length * 2, _bodyHigh);
            }

            id = _bodyHigh++;
        }

        _bodies[id] = default;
        _bodies[id].Used = true;
        _bodies[id].NextFree = -1;
        return id;
    }

    /// <summary>Adds the body to the simulation, as a body or as a static.</summary>
    [HotPath]
    private void Place(ref BodyEntry entry, in RigidPose pose, in BodyVelocity velocity)
    {
        TypedIndex shape = ShapeIndex(entry.Shape);
        ContinuousDetection continuity = Continuity(entry.Continuous);
        if (NeedsBody(entry))
        {
            var collidable = new CollidableDescription(shape, continuity);
            var activity = new BodyActivityDescription(entry.CanSleep ? 0.01f : -1f);
            BodyDescription description = entry.Kind == BodyKind.Dynamic
                ? BodyDescription.CreateDynamic(pose, velocity, InertiaOf(entry), collidable, activity)
                : BodyDescription.CreateKinematic(pose, entry.Kind == BodyKind.Static ? default : velocity, collidable, activity);
            entry.BodyHandle = Simulation.Bodies.Add(description);
            entry.InBodies = true;
            _surfaces.Allocate(entry.BodyHandle) = entry.Surface;
        }
        else
        {
            entry.StaticHandle = Simulation.Statics.Add(new StaticDescription(pose, shape, continuity));
            entry.InBodies = false;
            _surfaces.Allocate(entry.StaticHandle) = entry.Surface;
        }
    }

    /// <summary>Removes the body from the simulation.</summary>
    [HotPath]
    private void Unplace(ref BodyEntry entry)
    {
        if (entry.InBodies)
        {
            Simulation.Bodies.Remove(entry.BodyHandle);
        }
        else
        {
            Simulation.Statics.Remove(entry.StaticHandle);
        }
    }

    /// <summary>Moves a body between the bodies and the statics of the simulation, where it is, with its velocities.</summary>
    [HotPath]
    private void Replace(ref BodyEntry entry)
    {
        ReadPose(entry, out RigidPose pose, out BodyVelocity velocity);
        Unplace(ref entry);
        Place(ref entry, pose, velocity);
        if (entry.InBodies)
        {
            ReattachJoints(entry.Surface.Body);
        }
    }

    [HotPath]
    private void ReadPose(in BodyEntry entry, out RigidPose pose, out BodyVelocity velocity)
    {
        if (entry.InBodies)
        {
            BodyReference reference = Simulation.Bodies[entry.BodyHandle];
            pose = reference.Pose;
            velocity = reference.Velocity;
        }
        else
        {
            pose = Simulation.Statics[entry.StaticHandle].Pose;
            velocity = default;
        }
    }

    [HotPath]
    private void WriteSurface(in BodyEntry entry)
    {
        if (entry.InBodies)
        {
            _surfaces[entry.BodyHandle] = entry.Surface;
        }
        else
        {
            _surfaces[entry.StaticHandle] = entry.Surface;
        }
    }

    [HotPath]
    private void Awaken(BodyHandle body) => Simulation.Awakener.AwakenBody(body);

    [HotPath]
    private void Collect(BodyHandle body) => _waking.Allocate(_pool) = body;

    /// <summary>A body of the world, as the tables of the world keep it.</summary>
    private struct BodyEntry
    {
        public bool Used;
        public bool InBodies;
        public BodyHandle BodyHandle;
        public StaticHandle StaticHandle;
        public int Slot;
        public BodyKind Kind;
        public int Shape;
        public float Mass;
        public BodyInertia Inertia;
        public bool Continuous;
        public bool CanSleep;
        public int Joints;
        public Surface Surface;
        public int NextFree;
    }

    /// <summary>Collects the bodies whose bounds overlap a box.</summary>
    [HotPath]
    private readonly struct BodyCollector(BepuWorld world) : IBreakableForEach<CollidableReference>
    {
        [HotPath]
        public bool LoopBody(CollidableReference collidable)
        {
            if (collidable.Mobility != CollidableMobility.Static)
            {
                world.Collect(collidable.BodyHandle);
            }

            return true;
        }
    }
}
