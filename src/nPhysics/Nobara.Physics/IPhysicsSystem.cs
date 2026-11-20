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

    /// <summary>Gets the bodies out of every region, which are not simulated.</summary>
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
}
