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
    /// <summary>Gets the kinematic and dynamic bodies that are awake.</summary>
    public int AwakeBodies => KinematicBodies + DynamicBodies - SleepingBodies;
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

    /// <summary>Gets the physics worlds, in the order of their creation.</summary>
    IReadOnlyList<PhysicsWorldInfo> Worlds { get; }

    /// <summary>Gets the counts after the last step.</summary>
    PhysicsStatistics Statistics { get; }

    /// <summary>Creates a sphere.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <returns>The shape.</returns>
    PhysicsShape CreateSphere(float radius);

    /// <summary>Creates a capsule along Y.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <param name="length">The length of the cylinder between the two half spheres, from 0.</param>
    /// <returns>The shape.</returns>
    PhysicsShape CreateCapsule(float radius, float length);

    /// <summary>Creates a box.</summary>
    /// <param name="size">The size along each axis, above 0.</param>
    /// <returns>The shape.</returns>
    PhysicsShape CreateBox(Vector3 size);

    /// <summary>Creates a cylinder along Y.</summary>
    /// <param name="radius">The radius, above 0.</param>
    /// <param name="length">The length, above 0.</param>
    /// <returns>The shape.</returns>
    PhysicsShape CreateCylinder(float radius, float length);

    /// <summary>Creates the convex hull of points.</summary>
    /// <param name="points">At least four points that do not lie in one plane, in blocks; copied.</param>
    /// <returns>The shape.</returns>
    PhysicsShape CreateConvexHull(ReadOnlySpan<Vector3> points);

    /// <summary>Creates a compound of convex shapes.</summary>
    /// <param name="children">At least one child; copied.</param>
    /// <returns>The shape.</returns>
    PhysicsShape CreateCompound(ReadOnlySpan<CompoundChild> children);

    /// <summary>Creates a mesh, for static bodies.</summary>
    /// <param name="vertices">The vertices, in blocks; copied.</param>
    /// <param name="triangles">The indices of the vertices, three per triangle; copied.</param>
    /// <returns>The shape.</returns>
    PhysicsShape CreateMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> triangles);

    /// <summary>Gets the description of a shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>The description.</returns>
    ShapeDescription GetShape(PhysicsShape shape);

    /// <summary>Destroys a shape that no body and no compound uses.</summary>
    /// <param name="shape">The shape.</param>
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
    BodyHandle AddBody(DimensionId dimension, EntityId entity, BodyDescription description);

    /// <summary>Removes a body and its joints; the entity keeps its components of other modules and its last transform.</summary>
    /// <param name="body">The body.</param>
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
    BodyState GetState(BodyHandle body);

    /// <summary>Moves a body at once to a place, without sweeping the way; its velocities are kept.</summary>
    /// <param name="body">The body.</param>
    /// <param name="position">The new position of its entity.</param>
    /// <param name="rotation">The new rotation of its entity.</param>
    void Teleport(BodyHandle body, in WorldPosition position, Quaternion rotation);

    /// <summary>Sets the velocities of a kinematic or dynamic body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="linear">The linear velocity, in blocks per second.</param>
    /// <param name="angular">The angular velocity, in radians per second, along the axes of the world.</param>
    void SetVelocity(BodyHandle body, Vector3 linear, Vector3 angular);

    /// <summary>Gives a kinematic body the place it reaches at the end of the next step; it stops there unless it gets another target.</summary>
    /// <param name="body">A kinematic body.</param>
    /// <param name="position">The position of its entity at the end of the step.</param>
    /// <param name="rotation">The rotation of its entity at the end of the step.</param>
    void SetKinematicTarget(BodyHandle body, in WorldPosition position, Quaternion rotation);

    /// <summary>Applies a force to the center of a dynamic body during the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="force">The force, in kilograms times blocks per square second.</param>
    void ApplyForce(BodyHandle body, Vector3 force);

    /// <summary>Applies a force at a point of a dynamic body during the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="force">The force.</param>
    /// <param name="point">Where it applies, in the world.</param>
    void ApplyForceAt(BodyHandle body, Vector3 force, in WorldPosition point);

    /// <summary>Applies an impulse to the center of a dynamic body before the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="impulse">The impulse, in kilograms times blocks per second.</param>
    void ApplyImpulse(BodyHandle body, Vector3 impulse);

    /// <summary>Applies an impulse at a point of a dynamic body before the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="impulse">The impulse.</param>
    /// <param name="point">Where it applies, in the world.</param>
    void ApplyImpulseAt(BodyHandle body, Vector3 impulse, in WorldPosition point);

    /// <summary>Applies a torque to a dynamic body during the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="torque">The torque, along the axes of the world.</param>
    void ApplyTorque(BodyHandle body, Vector3 torque);

    /// <summary>Applies an angular impulse to a dynamic body before the next step.</summary>
    /// <param name="body">A dynamic body.</param>
    /// <param name="impulse">The angular impulse, along the axes of the world.</param>
    void ApplyAngularImpulse(BodyHandle body, Vector3 impulse);

    /// <summary>Wakes a body.</summary>
    /// <param name="body">The body.</param>
    void Wake(BodyHandle body);

    /// <summary>Wakes every body of a dimension that overlaps a box.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="area">The box.</param>
    void WakeArea(DimensionId dimension, in WorldBox area);

    /// <summary>Changes how a body moves. A body with a mesh stays static.</summary>
    /// <param name="body">The body.</param>
    /// <param name="kind">The new kind.</param>
    void SetKind(BodyHandle body, BodyKind kind);

    /// <summary>Changes the shape of a body, and where its center sits in the frame of the entity.</summary>
    /// <param name="body">The body.</param>
    /// <param name="shape">The new shape; a mesh for a static body only.</param>
    /// <param name="center">Where the center of the body sits in the frame of the entity.</param>
    void SetShape(BodyHandle body, PhysicsShape shape, Vector3 center);

    /// <summary>Changes the mass of a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="mass">The mass, above 0.</param>
    /// <param name="inertia">The inertia tensor; <see langword="null"/> lets the backend compute it from the shape.</param>
    void SetMass(BodyHandle body, float mass, BodyInertia? inertia = null);

    /// <summary>Changes the material of a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="material">The material.</param>
    void SetMaterial(BodyHandle body, PhysicsMaterial material);

    /// <summary>Changes the collision layers of a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="filter">The layers.</param>
    void SetFilter(BodyHandle body, CollisionFilter filter);

    /// <summary>Turns continuous collision detection on or off for a body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="continuous">Whether it is on.</param>
    void SetContinuous(BodyHandle body, bool continuous);

    /// <summary>Joins two bodies of the same dimension.</summary>
    /// <param name="first">The first body.</param>
    /// <param name="second">The second body.</param>
    /// <param name="description">The joint.</param>
    /// <returns>The handle of the joint.</returns>
    /// <exception cref="ArgumentException">A body is not alive, both are the same, they are in different dimensions, or the description is invalid.</exception>
    JointHandle CreateJoint(BodyHandle first, BodyHandle second, in JointDescription description);

    /// <summary>Changes the motor of a hinge or a slider.</summary>
    /// <param name="joint">The joint.</param>
    /// <param name="motor">The motor, or <see langword="null"/> to remove it.</param>
    void SetMotor(JointHandle joint, JointMotor? motor);

    /// <summary>Destroys a joint.</summary>
    /// <param name="joint">The joint.</param>
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
