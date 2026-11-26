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

/// <summary>A physics world, as <see cref="IPhysicsSystem.Worlds"/> lists it.</summary>
/// <param name="Dimension">The dimension it simulates.</param>
/// <param name="Origin">Its origin chunk.</param>
/// <param name="Bodies">The number of its bodies.</param>
/// <param name="Joints">The number of its joints.</param>
public readonly record struct PhysicsWorldInfo(DimensionId Dimension, ChunkCoordinate Origin, int Bodies, int Joints);

/// <summary>A region, as <see cref="IPhysicsSystem.Regions"/> lists it: an island of active chunks that nPhysics simulates in a physics world of its own.</summary>
/// <param name="Id">The number of the region, from 1, which it keeps while it grows, shrinks, merges, or splits.</param>
/// <param name="Dimension">The dimension it simulates.</param>
/// <param name="Minimum">The chunk at the minimum corner of its box, in the world.</param>
/// <param name="Maximum">The chunk at the maximum corner of its box, included.</param>
/// <param name="Origin">The origin chunk of its physics world.</param>
/// <param name="Bodies">The number of its bodies.</param>
/// <param name="Ghosts">The number of ghosts it holds: bodies of the regions next to it, seen near its border.</param>
/// <param name="Joints">The number of its joints.</param>
/// <param name="HasWorld">Whether it has a physics world, which it gets when a body enters it and loses when the last one leaves.</param>
public readonly record struct PhysicsRegionInfo(int Id, DimensionId Dimension, ChunkCoordinate Minimum, ChunkCoordinate Maximum, ChunkCoordinate Origin, int Bodies, int Ghosts, int Joints, bool HasWorld);

/// <summary>The counts of nPhysics after the last step.</summary>
/// <param name="Worlds">The physics worlds.</param>
/// <param name="Shapes">The shapes.</param>
/// <param name="StaticBodies">The static bodies.</param>
/// <param name="KinematicBodies">The kinematic bodies.</param>
/// <param name="DynamicBodies">The dynamic bodies.</param>
/// <param name="SleepingBodies">The kinematic and dynamic bodies that sleep.</param>
/// <param name="Joints">The joints.</param>
/// <param name="Steps">The steps since the start.</param>
/// <param name="LastStep">The duration of the last step, commands and writing included.</param>
public readonly record struct PhysicsStatistics(
    int Worlds,
    int Shapes,
    int StaticBodies,
    int KinematicBodies,
    int DynamicBodies,
    int SleepingBodies,
    int Joints,
    ulong Steps,
    TimeSpan LastStep)
{
    /// <summary>Gets the kinematic and dynamic bodies that are awake, in a region.</summary>
    public int AwakeBodies { get; init; }

    /// <summary>Gets the regions.</summary>
    public int Regions { get; init; }

    /// <summary>Gets the frozen bodies, which are not simulated: those out of every region, and those that reach a chunk of the terrain that is not loaded or not active.</summary>
    public int FrozenBodies { get; init; }

    /// <summary>Gets the ghosts: bodies seen by a region next to their own.</summary>
    public int Ghosts { get; init; }

    /// <summary>Gets the moves of a body from the world of one region to another since the start, recentered regions included.</summary>
    public long Transfers { get; init; }

    /// <summary>Gets the merges of regions since the start.</summary>
    public long Merges { get; init; }

    /// <summary>Gets the splits of regions since the start.</summary>
    public long Splits { get; init; }

    /// <summary>Gets the times the origin of a region moved since the start.</summary>
    public long Recenters { get; init; }

    /// <summary>Gets the chunks of voxels that the physics worlds hold, those near their dynamic bodies.</summary>
    public int VoxelChunks { get; init; }

    /// <summary>Gets the times changed blocks woke the bodies of a world around them since the start.</summary>
    public long VoxelWakes { get; init; }

    /// <summary>Gets the characters that the character controller moves.</summary>
    public int Characters { get; init; }

    /// <summary>Gets the queries asked between the last two steps, each query of a batch counted once.</summary>
    public int Queries { get; init; }

    /// <summary>Gets the contact events of the last step: contacts begun, contacts ended, and impacts.</summary>
    public int ContactEvents { get; init; }
}

/// <summary>
/// The physics of a context: shapes, bodies, joints, and the commands that
/// move bodies. nPhysics is the only module that writes the position of a
/// simulated entity or grid; other modules ask it through this service.
/// </summary>
/// <remarks>
/// Resolve it with <c>context.Resolve&lt;IPhysicsSystem&gt;()</c> from the
/// initialization of a module that depends on <c>nobara.physics</c>, and call
/// it on the loop thread of the context. Commands are queued and applied, in
/// the order they were given, before the next step, in the stage
/// <c>Physics</c>; the step then writes the transforms of the bodies that
/// moved.
/// </remarks>
public interface IPhysicsSystem
{
    /// <summary>Gets the name of the backend in use; empty before nPhysics starts.</summary>
    string BackendName { get; }

    /// <summary>Gets the number of substeps of each step, from <c>physics.substeps</c>.</summary>
    int Substeps { get; }

    /// <summary>Gets the gravity of the physics worlds, in blocks per square second.</summary>
    Vector3 Gravity { get; }

    /// <summary>Gets the physics worlds, in the order of their creation: one for each region that has bodies.</summary>
    /// <remarks>Each read builds a new list: read it outside hot path code.</remarks>
    IReadOnlyList<PhysicsWorldInfo> Worlds { get; }

    /// <summary>Gets the regions, in the order of their creation.</summary>
    /// <remarks>Each read builds a new list: read it outside hot path code. The regions follow the active area tickets of the terrain grids, and change at the next step or the next added body.</remarks>
    IReadOnlyList<PhysicsRegionInfo> Regions { get; }

    /// <summary>Gets the counts after the last step.</summary>
    PhysicsStatistics Statistics { get; }

    /// <summary>Creates a sphere.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not finite and above 0.</exception>
    PhysicsShape CreateSphere(float radius);

    /// <summary>Creates a capsule along Y.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <param name="length">The length of the cylinder between the two half spheres, from 0.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not finite and above 0, or the length is negative or not finite.</exception>
    PhysicsShape CreateCapsule(float radius, float length);

    /// <summary>Creates a box.</summary>
    /// <param name="size">The size along each axis, above 0.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not finite and above 0.</exception>
    PhysicsShape CreateBox(Vector3 size);

    /// <summary>Creates a cylinder along Y.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <param name="length">The length, above 0.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius or the length is not finite and above 0.</exception>
    PhysicsShape CreateCylinder(float radius, float length);

    /// <summary>Creates the convex hull of points.</summary>
    /// <param name="points">At least four points that do not lie in one plane, in blocks; copied.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentException">There are fewer than four points, a point is not finite, or they lie in one plane.</exception>
    PhysicsShape CreateConvexHull(ReadOnlySpan<Vector3> points);

    /// <summary>Creates a compound of convex shapes.</summary>
    /// <param name="children">At least one child; copied.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentException">There is no child, a child names no shape or a shape that is not convex, or its place is not finite.</exception>
    PhysicsShape CreateCompound(ReadOnlySpan<CompoundChild> children);

    /// <summary>Creates a mesh, for static bodies.</summary>
    /// <param name="vertices">The vertices, in blocks; copied.</param>
    /// <param name="triangles">The indices of the vertices, three per triangle; copied.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentException">There is no triangle, the number of indices is not a multiple of 3, an index names no vertex, or a vertex is not finite.</exception>
    PhysicsShape CreateMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> triangles);

    /// <summary>
    /// Creates the shape of the voxels of a grid, for the body of that grid:
    /// the collision boxes of its blocks, read where nVoxel keeps them, as the
    /// voxel layer reads them, with no geometry to rebuild when a block
    /// changes. It collides with bodies, except meshes, not with the voxels of
    /// other grids.
    /// </summary>
    /// <param name="grid">The grid, not destroyed.</param>
    /// <param name="origin">Where the origin of the shape lies in the frame of the grid, in blocks: the center of the body, its center of mass.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grid"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The grid is destroyed, or <paramref name="origin"/> is not finite.</exception>
    PhysicsShape CreateVoxels(Grid grid, Vector3 origin);

    /// <summary>Gets the description of a shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentException">The handle names no shape.</exception>
    ShapeDescription GetShape(PhysicsShape shape);

    /// <summary>Destroys a shape that no body and no compound uses.</summary>
    /// <param name="shape">The shape.</param>
    /// <exception cref="ArgumentException">The handle names no shape.</exception>
    /// <exception cref="InvalidOperationException">A body or a compound still uses it.</exception>
    void DestroyShape(PhysicsShape shape);

    /// <summary>
    /// Makes an entity a body: adds the components of nPhysics to it, and the
    /// body to the physics world of its dimension. From then on, nPhysics
    /// writes the world transform of the entity, or the transform of the grid
    /// when the entity is a grid.
    /// </summary>
    /// <param name="dimension">The dimension of the entity.</param>
    /// <param name="entity">An entity with a world transform and no parent, outside entity systems.</param>
    /// <param name="description">The body.</param>
    /// <returns>The handle of the body.</returns>
    /// <exception cref="ArgumentException">The entity is not alive, has a parent, has no world transform, is a body already, or the description is invalid.</exception>
    /// <exception cref="InvalidOperationException">nPhysics has not started, or an entity system runs on the world.</exception>
    BodyHandle AddBody(DimensionId dimension, EntityId entity, BodyDescription description);

    /// <summary>Removes a body and its joints; the entity keeps its components of other modules and its last transform.</summary>
    /// <param name="body">The body.</param>
    /// <exception cref="ArgumentException">The body is not alive.</exception>
    /// <exception cref="InvalidOperationException">An entity system runs on the world of the entity.</exception>
    void RemoveBody(BodyHandle body);

    /// <summary>Gets whether a handle names a body.</summary>
    /// <param name="body">The handle.</param>
    /// <returns><see langword="true"/> when the body exists.</returns>
    bool IsAlive(BodyHandle body);

    /// <summary>Gets the body of an entity.</summary>
    /// <param name="dimension">The dimension of the entity.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="body">The body.</param>
    /// <returns><see langword="true"/> when the entity is a body.</returns>
    bool TryGetBody(DimensionId dimension, EntityId entity, out BodyHandle body);

    /// <summary>Reads the state of a body, as the last step left it.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentException">The body is not alive.</exception>
    BodyState GetState(BodyHandle body);

    /// <summary>Moves a body at once to a place, without sweeping the way; its velocities are kept.</summary>
    /// <param name="body">The body.</param>
    /// <param name="position">The new position of its entity.</param>
    /// <param name="rotation">The new rotation of its entity.</param>
    /// <exception cref="ArgumentException">The body is not alive, the position or the rotation is not finite, or the rotation is zero.</exception>
    void Teleport(BodyHandle body, in WorldPosition position, Quaternion rotation);

    /// <summary>Sets the velocities of a kinematic or dynamic body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="linear">The linear velocity, in blocks per second.</param>
    /// <param name="angular">The angular velocity, in radians per second, along the axes of the world.</param>
    /// <exception cref="ArgumentException">The body is not alive, is static, or a velocity is not finite.</exception>
    void SetVelocity(BodyHandle body, Vector3 linear, Vector3 angular);

    /// <summary>Gives a kinematic body the place it reaches at the end of the next step; it stops there unless it gets another target.</summary>
    /// <param name="body">A kinematic body.</param>
    /// <param name="position">The position of its entity at the end of the step.</param>
    /// <param name="rotation">The rotation of its entity at the end of the step.</param>
    /// <exception cref="ArgumentException">The body is not alive, is not kinematic, or the pose is not finite.</exception>
    void SetKinematicTarget(BodyHandle body, in WorldPosition position, Quaternion rotation);

    /// <summary>Applies a force to the center of a dynamic body during the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="force">The force, in kilograms times blocks per square second.</param>
    /// <exception cref="ArgumentException">The body is not alive, is not dynamic, or the vector is not finite.</exception>
    void ApplyForce(BodyHandle body, Vector3 force);

    /// <summary>Applies a force at a point of a dynamic body during the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="force">The force.</param>
    /// <param name="point">Where it applies, in the world.</param>
    /// <exception cref="ArgumentException">The body is not alive, is not dynamic, or the vector is not finite.</exception>
    void ApplyForceAt(BodyHandle body, Vector3 force, in WorldPosition point);

    /// <summary>Applies an impulse to the center of a dynamic body before the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="impulse">The impulse, in kilograms times blocks per second.</param>
    /// <exception cref="ArgumentException">The body is not alive, is not dynamic, or the vector is not finite.</exception>
    void ApplyImpulse(BodyHandle body, Vector3 impulse);

    /// <summary>Applies an impulse at a point of a dynamic body before the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="impulse">The impulse.</param>
    /// <param name="point">Where it applies, in the world.</param>
    /// <exception cref="ArgumentException">The body is not alive, is not dynamic, or the vector is not finite.</exception>
    void ApplyImpulseAt(BodyHandle body, Vector3 impulse, in WorldPosition point);

    /// <summary>Applies a torque to a dynamic body during the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="torque">The torque, along the axes of the world.</param>
    /// <exception cref="ArgumentException">The body is not alive, is not dynamic, or the vector is not finite.</exception>
    void ApplyTorque(BodyHandle body, Vector3 torque);

    /// <summary>Applies an angular impulse to a dynamic body before the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="impulse">The angular impulse, along the axes of the world.</param>
    /// <exception cref="ArgumentException">The body is not alive, is not dynamic, or the vector is not finite.</exception>
    void ApplyAngularImpulse(BodyHandle body, Vector3 impulse);

    /// <summary>Wakes a body.</summary>
    /// <param name="body">The body.</param>
    /// <exception cref="ArgumentException">The body is not alive.</exception>
    void Wake(BodyHandle body);

    /// <summary>Wakes every body of a dimension that overlaps a box.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="area">The box.</param>
    void WakeArea(DimensionId dimension, in WorldBox area);

    /// <summary>Changes how a body moves. A body with a mesh stays static.</summary>
    /// <param name="body">The body.</param>
    /// <param name="kind">The new kind.</param>
    /// <exception cref="ArgumentException">The body is not alive, or has a mesh and the kind is not static.</exception>
    void SetKind(BodyHandle body, BodyKind kind);

    /// <summary>Changes the shape of a body, and where its center sits in the frame of the entity.</summary>
    /// <param name="body">The body.</param>
    /// <param name="shape">The new shape; a mesh for a static body only.</param>
    /// <param name="center">Where the center of the body sits in the frame of the entity.</param>
    /// <exception cref="ArgumentException">The body is not alive, the handle names no shape, the shape is a mesh and the body is not static, or the center is not finite.</exception>
    void SetShape(BodyHandle body, PhysicsShape shape, Vector3 center);

    /// <summary>Changes the mass of a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="mass">The mass, above 0.</param>
    /// <param name="inertia">The inertia tensor; <see langword="null"/> lets the backend compute it from the shape.</param>
    /// <exception cref="ArgumentException">The body is not alive.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mass is not finite and above 0.</exception>
    void SetMass(BodyHandle body, float mass, BodyInertia? inertia = null);

    /// <summary>Changes the material of a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="material">The material.</param>
    /// <exception cref="ArgumentException">The body is not alive, or the friction is negative or the restitution outside 0 to 1.</exception>
    void SetMaterial(BodyHandle body, PhysicsMaterial material);

    /// <summary>Changes the collision layers of a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="filter">The layers.</param>
    /// <exception cref="ArgumentException">The body is not alive.</exception>
    void SetFilter(BodyHandle body, CollisionFilter filter);

    /// <summary>Turns continuous collision detection on or off for a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="continuous">Whether it is on.</param>
    /// <exception cref="ArgumentException">The body is not alive.</exception>
    void SetContinuous(BodyHandle body, bool continuous);

    /// <summary>Changes how a body reports its contacts, from the next step on.</summary>
    /// <param name="body">The body.</param>
    /// <param name="reporting">How it reports them, or <see langword="null"/> for no contact events.</param>
    /// <exception cref="ArgumentException">The body is not alive, or the impact speed is negative or not finite.</exception>
    void SetContactReporting(BodyHandle body, ContactReporting? reporting);

    /// <summary>Finds the closest block, body, or chunk that is not loaded along a ray, in every grid and every region of a dimension. Call the queries on the loop thread; batches spread over the workers.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="origin">The start of the ray.</param>
    /// <param name="direction">Its direction; it need not be of length 1.</param>
    /// <param name="maxDistance">Its length, in blocks.</param>
    /// <param name="hit">The closest hit.</param>
    /// <param name="filter">What the ray meets.</param>
    /// <returns><see langword="true"/> when the ray met something.</returns>
    /// <exception cref="ArgumentException">The origin or the direction is not finite, the direction is zero, or the length is negative or not finite.</exception>
    bool RayCast(DimensionId dimension, in WorldPosition origin, Vector3 direction, float maxDistance, out PhysicsHit hit, QueryFilter filter = default);

    /// <summary>Finds the closest blocks and bodies along a ray, closest first, up to the first chunk that is not loaded.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="origin">The start of the ray.</param>
    /// <param name="direction">Its direction; it need not be of length 1.</param>
    /// <param name="maxDistance">Its length, in blocks.</param>
    /// <param name="hits">Receives the closest hits, as many as it holds; a chunk that is not loaded is the last one.</param>
    /// <param name="filter">What the ray meets.</param>
    /// <returns>The number of hits written.</returns>
    /// <exception cref="ArgumentException">The origin or the direction is not finite, the direction is zero, or the length is negative or not finite.</exception>
    int RayCastAll(DimensionId dimension, in WorldPosition origin, Vector3 direction, float maxDistance, Span<PhysicsHit> hits, QueryFilter filter = default);

    /// <summary>Finds the closest block, body, or chunk that is not loaded that a shape meets as it moves along a line, without turning.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="origin">Where its center starts.</param>
    /// <param name="rotation">Its rotation.</param>
    /// <param name="direction">The direction it moves along; it need not be of length 1.</param>
    /// <param name="maxDistance">How far it moves, in blocks.</param>
    /// <param name="hit">The closest hit; at distance 0 when the shape starts inside it.</param>
    /// <param name="filter">What the shape meets.</param>
    /// <returns><see langword="true"/> when the shape met something.</returns>
    /// <exception cref="ArgumentException">The shape is the default, the origin, the rotation, or the direction is not finite, the direction or the rotation is zero, or the length is negative or not finite.</exception>
    bool Sweep(DimensionId dimension, in QueryShape shape, in WorldPosition origin, Quaternion rotation, Vector3 direction, float maxDistance, out PhysicsHit hit, QueryFilter filter = default);

    /// <summary>Finds the closest blocks and bodies that a shape meets as it moves along a line, closest first, up to the first chunk that is not loaded.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="origin">Where its center starts.</param>
    /// <param name="rotation">Its rotation.</param>
    /// <param name="direction">The direction it moves along; it need not be of length 1.</param>
    /// <param name="maxDistance">How far it moves, in blocks.</param>
    /// <param name="hits">Receives the closest hits, as many as it holds; a chunk that is not loaded is the last one.</param>
    /// <param name="filter">What the shape meets.</param>
    /// <returns>The number of hits written.</returns>
    /// <exception cref="ArgumentException">The shape is the default, the origin, the rotation, or the direction is not finite, the direction or the rotation is zero, or the length is negative or not finite.</exception>
    int SweepAll(DimensionId dimension, in QueryShape shape, in WorldPosition origin, Quaternion rotation, Vector3 direction, float maxDistance, Span<PhysicsHit> hits, QueryFilter filter = default);

    /// <summary>Finds the blocks, the bodies, and the chunks that are not loaded that a shape overlaps.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="position">Where its center is.</param>
    /// <param name="rotation">Its rotation.</param>
    /// <param name="hits">Receives the hits, as many as it holds, in no particular order.</param>
    /// <param name="filter">What the shape meets.</param>
    /// <returns>The number of hits written.</returns>
    /// <exception cref="ArgumentException">The shape is the default, or the position or the rotation is not finite, or the rotation is zero.</exception>
    int Overlap(DimensionId dimension, in QueryShape shape, in WorldPosition position, Quaternion rotation, Span<PhysicsHit> hits, QueryFilter filter = default);

    /// <summary>
    /// Casts a batch of rays, in parallel on the job system, and gives the
    /// closest hit of each. Call it on the loop thread, outside a job; it
    /// returns once every ray is cast, and allocates nothing once its tables
    /// have grown to the largest batch.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="queries">The rays.</param>
    /// <param name="hits">Receives the closest hit of each ray, at its index; <see cref="PhysicsHitKind.None"/> for a ray that met nothing.</param>
    /// <exception cref="ArgumentException"><paramref name="hits"/> is shorter than <paramref name="queries"/>, or a ray is not valid.</exception>
    void RayCast(DimensionId dimension, ReadOnlySpan<RayQuery> queries, Span<PhysicsHit> hits);

    /// <summary>Moves a batch of shapes, in parallel on the job system, and gives the closest hit of each, as <see cref="RayCast(DimensionId, ReadOnlySpan{RayQuery}, Span{PhysicsHit})"/> does for rays.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="queries">The sweeps.</param>
    /// <param name="hits">Receives the closest hit of each sweep, at its index; <see cref="PhysicsHitKind.None"/> for a sweep that met nothing.</param>
    /// <exception cref="ArgumentException"><paramref name="hits"/> is shorter than <paramref name="queries"/>, or a sweep is not valid.</exception>
    void Sweep(DimensionId dimension, ReadOnlySpan<SweepQuery> queries, Span<PhysicsHit> hits);

    /// <summary>Joins two bodies of the same dimension.</summary>
    /// <param name="first">The first body.</param>
    /// <param name="second">The second body.</param>
    /// <param name="description">The joint.</param>
    /// <returns>The handle of the joint.</returns>
    /// <exception cref="ArgumentException">A body is not alive, both are the same, they are in different physics worlds, or the description is invalid.</exception>
    JointHandle CreateJoint(BodyHandle first, BodyHandle second, in JointDescription description);

    /// <summary>Changes the motor of a hinge or a slider.</summary>
    /// <param name="joint">The joint.</param>
    /// <param name="motor">The motor, or <see langword="null"/> to remove it.</param>
    /// <exception cref="ArgumentException">The joint is not alive, is fixed and a motor is given, or the motor is not finite or has a negative effort.</exception>
    void SetMotor(JointHandle joint, JointMotor? motor);

    /// <summary>Destroys a joint.</summary>
    /// <param name="joint">The joint.</param>
    /// <exception cref="ArgumentException">The joint is not alive.</exception>
    void DestroyJoint(JointHandle joint);

    /// <summary>Gets whether a handle names a joint.</summary>
    /// <param name="joint">The handle.</param>
    /// <returns><see langword="true"/> when the joint exists.</returns>
    bool IsAlive(JointHandle joint);

    /// <summary>Adds a provider of medium, asked after those added before it.</summary>
    /// <param name="module">The module of the provider.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The registration; dispose it to remove the provider.</returns>
    MediumProviderHandle AddMediumProvider(ModuleContext module, IMediumProvider provider);

    /// <summary>Asks the providers of medium what fills a point; the first that describes it answers.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="position">The point.</param>
    /// <param name="sample">The medium.</param>
    /// <returns><see langword="true"/> when a provider describes the point.</returns>
    bool TrySampleMedium(DimensionId dimension, in WorldPosition position, out MediumSample sample);

    /// <summary>Receives the end of each step.</summary>
    /// <param name="module">The module of the observer.</param>
    /// <param name="observer">The observer.</param>
    /// <returns>The subscription; dispose it to stop.</returns>
    PhysicsStepSubscription SubscribeStepEnd(ModuleContext module, IPhysicsStepObserver observer);

    /// <summary>
    /// Makes an entity a character: from the next step on, the character
    /// controller moves it after its <see cref="MovementIntent"/>, and writes
    /// its transform, its <see cref="CharacterState"/>, and its
    /// <see cref="BodyVelocity"/>. It adds <see cref="CharacterBody"/>,
    /// <see cref="CharacterState"/>, and, when the entity lacks them,
    /// <see cref="MovementIntent"/> and <see cref="BodyVelocity"/>.
    /// </summary>
    /// <param name="dimension">The dimension of the entity.</param>
    /// <param name="entity">An entity with a world transform, no parent, and no body; it starts where it is, with the velocity of its <see cref="BodyVelocity"/> if it has one.</param>
    /// <param name="character">The size and the other values of the character.</param>
    /// <exception cref="ArgumentException">The dimension has no entity world, the entity is not alive, has no world transform, has a parent, is a body or a character already, or a value of the character is out of range.</exception>
    /// <exception cref="InvalidOperationException">nPhysics has not started, or an entity system runs on the world.</exception>
    void AddCharacter(DimensionId dimension, EntityId entity, in CharacterBody character);

    /// <summary>
    /// Stops moving a character, at once: it leaves its support grid and keeps
    /// its place in the world, its <see cref="BodyVelocity"/> keeps its last
    /// velocity, and nPhysics writes its transform no more. It loses
    /// <see cref="CharacterBody"/> and <see cref="CharacterState"/>, and keeps
    /// <see cref="MovementIntent"/>. Removing <see cref="CharacterBody"/> from
    /// the entity does the same at the next step.
    /// </summary>
    /// <param name="dimension">The dimension of the entity.</param>
    /// <param name="entity">The character.</param>
    /// <exception cref="ArgumentException">The entity is not a character of the dimension.</exception>
    /// <exception cref="InvalidOperationException">An entity system runs on the world.</exception>
    void RemoveCharacter(DimensionId dimension, EntityId entity);

    /// <summary>Tells whether an entity is a character.</summary>
    /// <param name="dimension">The dimension of the entity.</param>
    /// <param name="entity">The entity.</param>
    /// <returns><see langword="true"/> when the character controller moves it.</returns>
    bool IsCharacter(DimensionId dimension, EntityId entity);
}
