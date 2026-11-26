// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// Where physics backends register. A backend module resolves it with
/// <c>context.Resolve&lt;IPhysicsBackends&gt;()</c> during its initialization
/// and registers its backend there.
/// </summary>
public interface IPhysicsBackends
{
    /// <summary>Registers a backend. nPhysics chooses one when it starts, from <c>physics.backend</c>.</summary>
    /// <param name="module">The module of the backend.</param>
    /// <param name="backend">The backend.</param>
    /// <exception cref="InvalidOperationException">nPhysics has started already, or a backend of the same name is registered.</exception>
    void Register(ModuleContext module, IPhysicsBackend backend);
}

/// <summary>A physics backend: the code of one physics library, behind the neutral API of nPhysics.</summary>
public interface IPhysicsBackend
{
    /// <summary>Gets the name of the backend, which <c>physics.backend</c> uses.</summary>
    string Name { get; }

    /// <summary>Computes the inertia tensor of a shape of uniform density.</summary>
    /// <param name="shape">A shape other than a mesh.</param>
    /// <param name="mass">The mass, above 0.</param>
    /// <returns>The tensor, in the frame of the shape.</returns>
    [HotPath]
    BodyInertia ComputeInertia(ShapeDescription shape, float mass);

    /// <summary>Creates a physics world: a simulation of its own, which shares nothing with another world.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The world; nPhysics disposes it.</returns>
    PhysicsBackendWorld CreateWorld(in PhysicsWorldOptions options);
}

/// <summary>Gives a backend the description of a shape it meets for the first time in a world.</summary>
public interface IPhysicsShapeSource
{
    /// <summary>Gets the description of a shape.</summary>
    /// <param name="shape">A shape that a body of the world uses.</param>
    /// <returns>The description, which does not change.</returns>
    [HotPath]
    ShapeDescription Get(PhysicsShape shape);
}

/// <summary>What nPhysics gives a backend when it creates a world.</summary>
/// <param name="Origin">
/// The origin of the world, a whole chunk: positions in the world are offsets
/// from the corner of this chunk, in blocks, so that no absolute coordinate
/// reaches the backend.
/// </param>
/// <param name="Gravity">The gravity, in blocks per square second.</param>
/// <param name="Shapes">The descriptions of the shapes.</param>
/// <param name="Logger">The logger of nPhysics.</param>
public readonly record struct PhysicsWorldOptions(ChunkCoordinate Origin, Vector3 Gravity, IPhysicsShapeSource Shapes, Logger Logger)
{
    /// <summary>
    /// Gets the number of threads that may query the world at once, from 1:
    /// the queries give each its index, from 0, so that a backend can keep
    /// what a query needs per thread.
    /// </summary>
    public int QueryThreads { get; init; } = 1;
}

/// <summary>How far one step of a world goes.</summary>
/// <param name="Delta">The duration of the step, in seconds.</param>
/// <param name="Substeps">The number of substeps, from 1.</param>
/// <param name="VelocityIterations">The number of velocity iterations of the solver in each substep, from 1.</param>
/// <param name="MaySpread">
/// Whether the backend may spread the step over the worker threads of the job
/// system: <see langword="true"/> when nPhysics steps the world on the loop
/// thread, <see langword="false"/> when it steps it from a job, where waiting
/// for other jobs is not allowed.
/// </param>
[HotPath]
public readonly record struct PhysicsStepSettings(float Delta, int Substeps, int VelocityIterations, bool MaySpread = false);

/// <summary>A body as nPhysics adds it to a world. Positions are relative to the origin of the world.</summary>
public struct BackendBody
{
    /// <summary>
    /// The slot of the body in nPhysics, from 0: a backend can index its own tables with it.
    /// The ghost of a body in the world of a region next to its own carries the slot of that
    /// body; a world holds at most one of the body and its ghost.
    /// </summary>
    public int Slot;

    /// <summary>Whether the body is static, kinematic, or dynamic.</summary>
    public BodyKind Kind;

    /// <summary>The shape the backend builds the body from, through <see cref="IPhysicsShapeSource"/>.</summary>
    public PhysicsShape Shape;

    /// <summary>The position of its center, relative to the origin of the world.</summary>
    public Vector3 Position;

    /// <summary>Its rotation.</summary>
    public Quaternion Rotation;

    /// <summary>The linear velocity, in blocks per second.</summary>
    public Vector3 LinearVelocity;

    /// <summary>The angular velocity, in radians per second.</summary>
    public Vector3 AngularVelocity;

    /// <summary>The mass of a dynamic body, in kilograms.</summary>
    public float Mass;

    /// <summary>The inertia tensor of a dynamic body.</summary>
    public BodyInertia Inertia;

    /// <summary>The friction and restitution of its surface.</summary>
    public PhysicsMaterial Material;

    /// <summary>The layers it belongs to and collides with.</summary>
    public CollisionFilter Filter;

    /// <summary>Whether continuous collision detection is on.</summary>
    public bool Continuous;

    /// <summary>Whether the body may fall asleep.</summary>
    public bool CanSleep;

    /// <summary>Whether the backend reports the contacts of the body in <see cref="PhysicsBackendWorld.Contacts"/>.</summary>
    public bool ReportsContacts;
}

/// <summary>A body as a backend reports it after a step.</summary>
public struct BackendBodyState
{
    /// <summary>The position of its center, relative to the origin of the world.</summary>
    public Vector3 Position;

    /// <summary>Its rotation.</summary>
    public Quaternion Rotation;

    /// <summary>The linear velocity, in blocks per second.</summary>
    public Vector3 LinearVelocity;

    /// <summary>The angular velocity, in radians per second.</summary>
    public Vector3 AngularVelocity;

    /// <summary>Whether it sleeps.</summary>
    public bool IsAsleep;
}

/// <summary>A joint as nPhysics adds it to a world, between two bodies of that world.</summary>
public struct BackendJoint
{
    /// <summary>Whether the joint is a hinge, a slider, or fixed.</summary>
    public JointKind Kind;

    /// <summary>The first body, as the backend numbered it.</summary>
    public int BodyA;

    /// <summary>The second body, as the backend numbered it.</summary>
    public int BodyB;

    /// <summary>The point of the joint on the first body, in its frame.</summary>
    public Vector3 AnchorA;

    /// <summary>The point of the joint on the second body, in its frame.</summary>
    public Vector3 AnchorB;

    /// <summary>The axis on the first body, in its frame.</summary>
    public Vector3 AxisA;

    /// <summary>The axis on the second body, in its frame.</summary>
    public Vector3 AxisB;

    /// <summary>The speed and effort that drive the joint, or <see langword="null"/> for a free joint.</summary>
    public JointMotor? Motor;
}

/// <summary>
/// One chunk of a grid that holds blocks, as the voxel layer of nPhysics hands
/// it to a world: the backend reads its cells in place while it steps, and
/// collides bodies with them.
/// </summary>
/// <param name="Grid">The grid.</param>
/// <param name="Chunk">The chunk, read without a copy.</param>
/// <param name="Position">The corner of the chunk, relative to the origin of the world.</param>
/// <param name="Rotation">The rotation of the grid.</param>
public readonly record struct VoxelChunkCollider(Grid Grid, Chunk Chunk, Vector3 Position, Quaternion Rotation)
{
    /// <summary>
    /// Gets the cells of the chunk: the collision boxes of each cell and the
    /// rule of inner faces. nPhysics always gives them; <see langword="null"/>
    /// makes every cell that holds a block collide as a whole cube.
    /// </summary>
    public VoxelChunkCells? Cells { get; init; }
}

/// <summary>A query that nPhysics asks a world: a ray, a sweep, or an overlap, among the bodies of the world.</summary>
[HotPath]
public struct BackendQuery
{
    /// <summary>The start of a ray, or the center of the shape of a sweep or an overlap, relative to the origin of the world.</summary>
    public Vector3 Origin;

    /// <summary>The rotation of the shape.</summary>
    public Quaternion Rotation;

    /// <summary>The direction of a ray or a sweep, of length 1.</summary>
    public Vector3 Direction;

    /// <summary>How far a ray or a sweep goes, in blocks.</summary>
    public float MaxDistance;

    /// <summary>The shape of a sweep or an overlap.</summary>
    public QueryShape Shape;

    /// <summary>The layers the query meets: a body is met when it belongs to one of them.</summary>
    public uint Mask;

    /// <summary>The slot of a body the query goes through (<see cref="BackendBody.Slot"/>), or -1 for none.</summary>
    public int IgnoredSlot;
}

/// <summary>A body that a query of a world met.</summary>
/// <param name="Slot">The slot of the body (<see cref="BackendBody.Slot"/>), which its ghost shares.</param>
/// <param name="Distance">How far along the direction the query went before it met the body; 0 for an overlap, or when the shape starts inside.</param>
/// <param name="Point">Where the hit is, relative to the origin of the world.</param>
/// <param name="Normal">The normal of the surface of the body, toward the query; zero when unknown.</param>
[HotPath]
public readonly record struct BackendQueryHit(int Slot, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>
/// A pair that touches, or will touch during the step, as a world reports it
/// after a step for the bodies that report their contacts.
/// </summary>
/// <param name="SlotA">The slot of a body of the pair that reports its contacts (<see cref="BackendBody.Slot"/>).</param>
/// <param name="SlotB">The slot of the other body, or -1 for a chunk of voxels.</param>
/// <param name="VoxelChunk">The number of the chunk of voxels, from <see cref="PhysicsBackendWorld.AddVoxelChunk"/>; -1 for a body.</param>
/// <param name="Point">The deepest contact, relative to the origin of the world.</param>
/// <param name="Normal">The normal of that contact, from the second part toward the first.</param>
/// <param name="Depth">Its depth; negative while the parts are apart.</param>
/// <param name="Speed">The speed at which the parts approach along the normal before the step, in blocks per second.</param>
[HotPath]
public readonly record struct BackendContact(int SlotA, int SlotB, int VoxelChunk, Vector3 Point, Vector3 Normal, float Depth, float Speed)
{
    /// <summary>
    /// How far apart, in blocks, two parts may be once their approach over the
    /// step is counted, and still be reported: a pair is reported when its
    /// depth plus its speed of approach times the duration of the step is at
    /// least the opposite of this. nPhysics begins a contact closer than
    /// <see cref="BeginReach"/>, and ends it once the pair is no longer
    /// reported: a body that settles with small hops does not begin and end
    /// its contact again and again.
    /// </summary>
    public const float Reach = 0.05f;

    /// <summary>How far apart, in blocks, two parts may be, their approach over the step counted, for a contact to begin.</summary>
    public const float BeginReach = 0.01f;
}

/// <summary>
/// One world of a backend: bodies, joints, and their simulation, around an
/// origin. Only nPhysics calls it, never from two threads at once on the
/// same world; two worlds share nothing and can be called at the same time.
/// </summary>
/// <remarks>
/// Positions are offsets from <see cref="PhysicsWorldOptions.Origin"/>, in
/// blocks; rotations and axes are along the axes of the world. Bodies and
/// joints are numbered by the backend; a number stays valid until it is
/// removed.
/// </remarks>
[HotPath]
public abstract class PhysicsBackendWorld : IDisposable
{
    /// <summary>Adds a body.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The number of the body in this world.</returns>
    public abstract int AddBody(in BackendBody body);

    /// <summary>Removes a body, after its joints.</summary>
    /// <param name="body">The number of the body.</param>
    public abstract void RemoveBody(int body);

    /// <summary>Moves a body at once, without sweeping the way: a teleport.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="position">The position of its center.</param>
    /// <param name="rotation">Its rotation.</param>
    public abstract void SetPose(int body, Vector3 position, Quaternion rotation);

    /// <summary>Sets the velocities of a body, and wakes it.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="linear">The linear velocity.</param>
    /// <param name="angular">The angular velocity.</param>
    public abstract void SetVelocity(int body, Vector3 linear, Vector3 angular);

    /// <summary>Applies an impulse to a dynamic body, and wakes it.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="impulse">The impulse, in kilograms times blocks per second.</param>
    /// <param name="offset">Where it applies, from the center of the body, along the axes of the world.</param>
    public abstract void ApplyImpulse(int body, Vector3 impulse, Vector3 offset);

    /// <summary>Applies an angular impulse to a dynamic body, and wakes it.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="impulse">The angular impulse, along the axes of the world.</param>
    public abstract void ApplyAngularImpulse(int body, Vector3 impulse);

    /// <summary>Changes how a body moves, and wakes it.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="kind">The new kind.</param>
    /// <param name="mass">The mass, used by a dynamic body.</param>
    /// <param name="inertia">The inertia tensor, used by a dynamic body.</param>
    public abstract void SetKind(int body, BodyKind kind, float mass, in BodyInertia inertia);

    /// <summary>Changes the shape of a body, and wakes it.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="shape">The new shape.</param>
    public abstract void SetShape(int body, PhysicsShape shape);

    /// <summary>Changes the mass of a dynamic body, and wakes it.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="mass">The mass.</param>
    /// <param name="inertia">The inertia tensor.</param>
    public abstract void SetMass(int body, float mass, in BodyInertia inertia);

    /// <summary>Changes the material of a body.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="material">The material.</param>
    public abstract void SetMaterial(int body, PhysicsMaterial material);

    /// <summary>Changes the collision layers of a body.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="filter">The layers.</param>
    public abstract void SetFilter(int body, CollisionFilter filter);

    /// <summary>Turns continuous collision detection on or off for a body.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="continuous">Whether it is on.</param>
    public abstract void SetContinuous(int body, bool continuous);

    /// <summary>Wakes a body.</summary>
    /// <param name="body">The number of the body.</param>
    public abstract void Wake(int body);

    /// <summary>Wakes every body that overlaps a box.</summary>
    /// <param name="minimum">The minimum corner, relative to the origin.</param>
    /// <param name="maximum">The maximum corner, relative to the origin.</param>
    public abstract void WakeArea(Vector3 minimum, Vector3 maximum);

    /// <summary>Reads a body.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="state">Its state.</param>
    public abstract void ReadBody(int body, out BackendBodyState state);

    /// <summary>Adds a joint.</summary>
    /// <param name="joint">The joint.</param>
    /// <returns>The number of the joint in this world.</returns>
    public abstract int AddJoint(in BackendJoint joint);

    /// <summary>Removes a joint.</summary>
    /// <param name="joint">The number of the joint.</param>
    public abstract void RemoveJoint(int joint);

    /// <summary>Changes the motor of a hinge or a slider, and wakes its bodies.</summary>
    /// <param name="joint">The number of the joint.</param>
    /// <param name="motor">The motor, or <see langword="null"/> to remove it.</param>
    public abstract void SetJointMotor(int joint, JointMotor? motor);

    /// <summary>Advances the simulation by one step.</summary>
    /// <param name="settings">The duration and the substeps.</param>
    public abstract void Step(in PhysicsStepSettings settings);

    /// <summary>
    /// Adds a chunk of voxels that bodies collide with: the backend reads its
    /// cells through <see cref="VoxelChunkCollider.Cells"/> while the world
    /// steps, and drops the contacts that <see cref="VoxelChunkCells.IsInnerContact"/>
    /// sets apart. nPhysics adds the chunks around the dynamic bodies of the
    /// world, and removes a chunk before it leaves memory.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    /// <returns>The number of the chunk in this world.</returns>
    public abstract int AddVoxelChunk(in VoxelChunkCollider chunk);

    /// <summary>Removes a chunk of voxels.</summary>
    /// <param name="chunk">The number of the chunk.</param>
    public abstract void RemoveVoxelChunk(int chunk);

    /// <summary>
    /// Finds the closest bodies along a ray. The chunks of voxels are not
    /// part of the queries: nPhysics tests the voxels of every grid itself.
    /// </summary>
    /// <remarks>
    /// The query members may run on several threads at once, each with its
    /// own <paramref name="thread"/>, while no other member of the world runs.
    /// </remarks>
    /// <param name="query">The ray.</param>
    /// <param name="thread">The index of the calling thread among those that query at once, below <see cref="PhysicsWorldOptions.QueryThreads"/>.</param>
    /// <param name="hits">Receives the closest bodies met, as many as it holds, in any order; a body is met once.</param>
    /// <returns>The number of hits written.</returns>
    public abstract int RayCast(in BackendQuery query, int thread, Span<BackendQueryHit> hits);

    /// <summary>Finds the closest bodies that a shape meets as it moves along a line, without turning; a body it starts inside is met at distance 0.</summary>
    /// <remarks>Like <see cref="RayCast"/>, it may run on several threads at once.</remarks>
    /// <param name="query">The sweep.</param>
    /// <param name="thread">The index of the calling thread, below <see cref="PhysicsWorldOptions.QueryThreads"/>.</param>
    /// <param name="hits">Receives the closest bodies met, as many as it holds, in any order; a body is met once.</param>
    /// <returns>The number of hits written.</returns>
    public abstract int Sweep(in BackendQuery query, int thread, Span<BackendQueryHit> hits);

    /// <summary>Finds the bodies that overlap a shape.</summary>
    /// <remarks>Like <see cref="RayCast"/>, it may run on several threads at once.</remarks>
    /// <param name="query">The overlap; its direction and distance are not used.</param>
    /// <param name="thread">The index of the calling thread, below <see cref="PhysicsWorldOptions.QueryThreads"/>.</param>
    /// <param name="hits">Receives the bodies met, as many as it holds; a body is met once.</param>
    /// <returns>The number of hits written.</returns>
    public abstract int Overlap(in BackendQuery query, int thread, Span<BackendQueryHit> hits);

    /// <summary>Turns the report of the contacts of a body on or off.</summary>
    /// <param name="body">The number of the body.</param>
    /// <param name="reports">Whether its contacts are reported.</param>
    public abstract void SetReportsContacts(int body, bool reports);

    /// <summary>
    /// Gets the pairs of the last step that hold a body that reports its
    /// contacts and touch, or will touch during the step (see
    /// <see cref="BackendContact.Reach"/>): one entry per pair of a body and a
    /// body or a chunk of voxels, at its deepest contact. Valid until the next
    /// step; read on the thread that stepped the world, or after.
    /// </summary>
    public abstract ReadOnlySpan<BackendContact> Contacts { get; }

    /// <summary>Changes the gravity of the world, from the next step on.</summary>
    /// <param name="gravity">The gravity, in blocks per square second.</param>
    public abstract void SetGravity(Vector3 gravity);

    /// <summary>Releases the world and everything in it.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the world.</summary>
    /// <param name="disposing"><see langword="true"/> when called by <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }
}
