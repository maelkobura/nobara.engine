// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;

namespace Nobara.Physics;

/// <summary>
/// The step, in the stage <c>Physics</c>: bodies whose entity is gone are
/// removed, commands apply in order, the regions follow the tickets, bodies
/// move to the world of their region and leave ghosts next to it, every
/// world steps, then nPhysics writes the transforms of the bodies that moved,
/// moves the characters, and publishes the end of the step.
/// </summary>
internal sealed partial class PhysicsSimulation
{
    private ulong _steps;
    private TimeSpan _lastStep;
    private int _publishing = int.MaxValue;

    /// <inheritdoc/>
    public void Run(in TickTime time)
    {
        long start = Stopwatch.GetTimestamp();
        float delta = (float)time.Delta.TotalSeconds;
        RemoveOrphans();
        ApplyCommands(delta, time.Tick);
        StopReachedTargets(time.Tick);
        _placeBodies(delta);   // through a delegate: not hot path code as a whole
        StepWorlds(delta);
        int written = WriteTransforms();
        MoveCharacters(delta);
        Publish(time, written);
        _steps++;
        _lastStep = Stopwatch.GetElapsedTime(start);
    }

    /// <summary>Removes the bodies whose entity, entity world, or grid is gone: their components went with them.</summary>
    [HotPath]
    private void RemoveOrphans()
    {
        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (body.Alive && (body.EntityWorld!.IsDestroyed || !body.EntityWorld.IsAlive(body.Entity) || (body.Grid is not null && body.Grid.IsDestroyed)))
            {
                RemoveBodyCore(i, removeComponents: false);
            }
        }
    }

    [HotPath]
    private void ApplyCommands(float delta, ulong tick)
    {
        for (int i = 0; i < _commandCount; i++)
        {
            ref PhysicsCommand command = ref _commands[i];
            switch (command.Command)
            {
                case CommandKind.WakeArea:
                    ApplyWakeArea(command);
                    break;
                case CommandKind.SetMotor:
                    if (IsAlive(command.Joint))
                    {
                        ref JointRecord joint = ref _joints[command.Joint.Index];
                        joint.Description.Motor = command.HasMotor ? command.Motor : null;
                        joint.World?.Backend.SetJointMotor(joint.BackendId, joint.Description.Motor);
                    }

                    break;
                case CommandKind.SetShape:
                    if (IsAlive(command.Body))
                    {
                        ApplyShape(ref _bodies[command.Body.Index], command.Shape, command.A);
                    }
                    else
                    {
                        ReleaseShape(command.Shape);
                    }

                    break;
                default:
                    if (IsAlive(command.Body))
                    {
                        Apply(ref _bodies[command.Body.Index], command, delta, tick);
                    }

                    break;
            }
        }

        _commandCount = 0;
    }

    /// <summary>Applies a command to a body. A frozen body keeps what it is made of, its pose, and its velocities; it takes no force, impulse, or target.</summary>
    [HotPath]
    private void Apply(ref BodyRecord body, in PhysicsCommand command, float delta, ulong tick)
    {
        PhysicsBackendWorld? backend = body.World?.Backend;
        int id = body.BackendId;
        switch (command.Command)
        {
            case CommandKind.Teleport:
                Teleport(ref body, command);
                break;
            case CommandKind.SetVelocity:
                if (body.Kind != BodyKind.Static)
                {
                    backend?.SetVelocity(id, command.A, command.B);
                    body.Linear = command.A;
                    body.Angular = command.B;
                    body.TargetPending = false;
                }

                break;
            case CommandKind.KinematicTarget:
                if (body.Kind == BodyKind.Kinematic && backend is not null)
                {
                    FollowTarget(ref body, command, delta, tick);
                }

                break;
            case CommandKind.Force:
            case CommandKind.Impulse:
            case CommandKind.ForceAt:
            case CommandKind.ImpulseAt:
                if (body.Kind == BodyKind.Dynamic && backend is not null)
                {
                    bool force = command.Command is CommandKind.Force or CommandKind.ForceAt;
                    Vector3 offset = Vector3.Zero;
                    if (command.Command is CommandKind.ForceAt or CommandKind.ImpulseAt)
                    {
                        backend.ReadBody(id, out BackendBodyState state);
                        offset = PhysicsFrame.ToLocal(body.World!.Origin, command.Point) - state.Position;
                    }

                    backend.ApplyImpulse(id, force ? command.A * delta : command.A, offset);
                }

                break;
            case CommandKind.Torque:
            case CommandKind.AngularImpulse:
                if (body.Kind == BodyKind.Dynamic && backend is not null)
                {
                    backend.ApplyAngularImpulse(id, command.Command == CommandKind.Torque ? command.A * delta : command.A);
                }

                break;
            case CommandKind.Wake:
                backend?.Wake(id);
                break;
            case CommandKind.SetKind:
                if (command.Kind != body.Kind)
                {
                    CountKind(body.Kind, -1);
                    CountKind(command.Kind, 1);
                    if (body.Asleep)
                    {
                        _sleepingBodies--;
                        body.Asleep = false;
                    }

                    body.Kind = command.Kind;
                    body.TargetPending = false;
                    backend?.SetKind(id, body.Kind, body.Mass, body.Inertia);
                    ref PhysicsBody component = ref body.EntityWorld!.Write(body.Entity, _bodyComponent);
                    component.Kind = body.Kind;
                    component.Flags = FlagsOf(body);
                    body.EntityWorld.Set(body.Entity, _massComponent, MassOf(body));
                    if (body.Kind == BodyKind.Static)
                    {
                        body.Linear = body.Angular = Vector3.Zero;
                        body.EntityWorld.Set(body.Entity, _velocityComponent, default);
                    }
                }

                break;
            case CommandKind.SetMass:
                body.Mass = command.Value;
                body.ExplicitInertia = command.HasInertia;
                body.Inertia = command.HasInertia ? command.Inertia : _backend!.ComputeInertia(Get(body.Shape), body.Mass);
                if (body.Kind == BodyKind.Dynamic)
                {
                    backend?.SetMass(id, body.Mass, body.Inertia);
                }

                body.EntityWorld!.Set(body.Entity, _massComponent, MassOf(body));
                break;
            case CommandKind.SetMaterial:
                body.Material = command.Material;
                backend?.SetMaterial(id, command.Material);
                body.EntityWorld!.Set(body.Entity, _materialComponent, new BodyMaterial { Material = command.Material });
                break;
            case CommandKind.SetFilter:
                body.Filter = command.Filter;
                backend?.SetFilter(id, command.Filter);
                body.EntityWorld!.Write(body.Entity, _bodyComponent).Filter = command.Filter;
                break;
            case CommandKind.SetContinuous:
                body.Continuous = command.Flag;
                backend?.SetContinuous(id, command.Flag);
                body.EntityWorld!.Write(body.Entity, _bodyComponent).Flags = FlagsOf(body);
                break;
        }
    }

    /// <summary>
    /// Moves a body at once. nPhysics keeps the new pose in the world, so that
    /// the body can enter another region with it exactly; the backend gets it
    /// only when the body stays in its region.
    /// </summary>
    [HotPath]
    private void Teleport(ref BodyRecord body, in PhysicsCommand command)
    {
        body.Position = command.Point.Offset(Vector3.Transform(body.Center, command.Rotation));
        body.Rotation = command.Rotation;
        body.Teleported = true;
        body.Dirty = true;
        if (body.World is { } world && world.Region.World == world && Holds(world.Region, body.Position, BodyHysteresis))
        {
            world.Backend.SetPose(body.BackendId, PhysicsFrame.ToLocal(world.Origin, body.Position), body.Rotation);
            if (body.Kind != BodyKind.Static)
            {
                world.Backend.Wake(body.BackendId);
            }
        }
    }

    /// <summary>Gives a kinematic body the velocities that bring it to its target at the end of the step.</summary>
    [HotPath]
    private void FollowTarget(ref BodyRecord body, in PhysicsCommand command, float delta, ulong tick)
    {
        PhysicsBackendWorld backend = body.World!.Backend;
        backend.ReadBody(body.BackendId, out BackendBodyState state);
        Vector3 target = PhysicsFrame.ToLocal(body.World.Origin, command.Point) + Vector3.Transform(body.Center, command.Rotation);
        Vector3 linear = (target - state.Position) / delta;
        Quaternion turn = command.Rotation * Quaternion.Conjugate(Quaternion.Normalize(state.Rotation));
        if (turn.W < 0)
        {
            turn = Quaternion.Negate(turn);
        }

        var axis = new Vector3(turn.X, turn.Y, turn.Z);
        float sine = axis.Length();
        Vector3 angular = sine < 1e-6f
            ? axis * (2f / delta)
            : axis / sine * (2f * MathF.Atan2(sine, turn.W) / delta);
        backend.SetVelocity(body.BackendId, linear, angular);
        body.TargetPending = true;
        body.TargetTick = tick;
    }

    /// <summary>Stops the kinematic bodies that reached their target at the last step and got no new one.</summary>
    [HotPath]
    private void StopReachedTargets(ulong tick)
    {
        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (body.Alive && body.TargetPending && body.TargetTick != tick)
            {
                body.World?.Backend.SetVelocity(body.BackendId, Vector3.Zero, Vector3.Zero);
                body.Linear = body.Angular = Vector3.Zero;
                body.TargetPending = false;
            }
        }
    }

    [HotPath]
    private void ApplyShape(ref BodyRecord body, PhysicsShape shape, Vector3 center)
    {
        PhysicsBackendWorld? backend = body.World?.Backend;
        if (backend is not null && !body.Teleported)
        {
            backend.ReadBody(body.BackendId, out BackendBodyState state);
            body.Position = PhysicsFrame.ToWorld(body.World!.Origin, state.Position);
            body.Rotation = Quaternion.Normalize(state.Rotation);
        }

        WorldPosition entity = body.Position.Offset(-Vector3.Transform(body.Center, body.Rotation));
        ReleaseShape(body.Shape);
        body.Shape = shape;   // the command acquired it
        body.Center = center;
        body.Position = entity.Offset(Vector3.Transform(center, body.Rotation));
        if (backend is not null)
        {
            backend.SetShape(body.BackendId, shape);
            backend.SetPose(body.BackendId, PhysicsFrame.ToLocal(body.World!.Origin, body.Position), body.Rotation);
        }

        if (!body.ExplicitInertia && Get(shape).Kind != ShapeKind.Mesh)
        {
            body.Inertia = _backend!.ComputeInertia(Get(shape), body.Mass);
            if (body.Kind == BodyKind.Dynamic)
            {
                backend?.SetMass(body.BackendId, body.Mass, body.Inertia);
            }
        }

        EntityWorld world = body.EntityWorld!;
        world.Set(body.Entity, _shapeComponent, new BodyShape { Shape = shape });
        world.Write(body.Entity, _bodyComponent).Center = center;
        world.Set(body.Entity, _massComponent, MassOf(body));
    }

    [HotPath]
    private void ApplyWakeArea(in PhysicsCommand command)
    {
        for (int i = 0; i < _worlds.Count; i++)
        {
            WorldEntry world = _worlds[i];
            if (world.Region.Dimension == command.Dimension)
            {
                Vector3 origin = PhysicsFrame.ToLocal(world.Origin, command.Point);
                world.Backend.WakeArea(origin + command.A, origin + command.B);
            }
        }
    }

    /// <summary>Writes the transform and the velocities of each body that moved, and keeps its pose for the end of the step.</summary>
    /// <returns>The number of poses kept.</returns>
    [HotPath]
    private int WriteTransforms()
    {
        for (int i = 0; i < _worlds.Count; i++)
        {
            _worlds[i].Awake = 0;
        }

        int written = 0;
        int sleeping = 0;
        int frozen = 0;
        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            body.Teleported = false;
            if (!body.Alive)
            {
                continue;
            }

            if (body.World is null)
            {
                frozen++;
                if (body.Dirty)
                {
                    written = Write(i, ref body, body.Position, body.Rotation, Vector3.Zero, Vector3.Zero, written);   // teleported while frozen
                }

                continue;
            }

            if (body.Kind == BodyKind.Static && !body.Dirty)
            {
                continue;
            }

            body.World.Backend.ReadBody(body.BackendId, out BackendBodyState state);
            bool asleep = body.Kind != BodyKind.Static && state.IsAsleep;
            bool fellAsleep = asleep && !body.Asleep;
            bool changed = asleep != body.Asleep;
            if (asleep)
            {
                sleeping++;
            }
            else if (body.Kind != BodyKind.Static)
            {
                body.World.Awake++;
            }

            body.Asleep = asleep;
            if (changed)
            {
                body.EntityWorld!.Write(body.Entity, _bodyComponent).Flags = FlagsOf(body);
            }

            if (asleep && !fellAsleep && !body.Dirty)
            {
                continue;   // still asleep: it did not move
            }

            Quaternion rotation = Quaternion.Normalize(state.Rotation);
            body.Position = PhysicsFrame.ToWorld(body.World.Origin, state.Position);
            body.Rotation = rotation;
            body.Linear = asleep ? Vector3.Zero : state.LinearVelocity;
            body.Angular = asleep ? Vector3.Zero : state.AngularVelocity;
            written = Write(i, ref body, body.Position, rotation, body.Linear, body.Angular, written);
        }

        _sleepingBodies = sleeping;
        _frozenBodies = frozen;
        return written;
    }

    /// <summary>Writes the transform and the velocities of a body from the pose of its center, and keeps its pose for the end of the step.</summary>
    [HotPath]
    private int Write(int slot, ref BodyRecord body, in WorldPosition center, Quaternion rotation, Vector3 linear, Vector3 angular, int written)
    {
        EntityWorld world = body.EntityWorld!;
        WorldPosition position = center.Offset(-Vector3.Transform(body.Center, rotation));
        var transform = new WorldTransform(position, rotation, world.Read(body.Entity, _transform).Scale);
        if (body.Grid is not null)
        {
            _gridWriter!.Set(body.Grid, transform);
        }
        else
        {
            world.Set(body.Entity, _transform, transform);
        }

        world.Set(body.Entity, _velocityComponent, new BodyVelocity { Linear = linear, Angular = angular });
        body.Dirty = false;
        _poses[written] = new BodyPose(new BodyHandle(slot, body.Generation), body.Dimension, body.Entity, transform, linear, angular);
        return written + 1;
    }

    private void RemoveObserver(PhysicsStepSubscription subscription)
    {
        int index = _observers.IndexOf(subscription);
        if (index >= 0)
        {
            _observers.RemoveAt(index);
            if (index <= _publishing)
            {
                _publishing--;   // the observer after it is not skipped
            }
        }
    }

    [HotPath]
    private void Publish(in TickTime time, int written)
    {
        if (_observers.Count == 0)
        {
            return;
        }

        var end = new PhysicsStepEnd(time.Tick, time.Delta, new ReadOnlySpan<BodyPose>(_poses, 0, written))
        {
            CharacterSteps = new ReadOnlySpan<CharacterStep>(_characterSteps, 0, _characterStepCount),
            CharacterLandings = new ReadOnlySpan<CharacterLanding>(_characterLandings, 0, _characterLandingCount),
        };
        for (_publishing = 0; _publishing < _observers.Count; _publishing++)
        {
            PhysicsStepSubscription subscription = _observers[_publishing];
            try
            {
                subscription.Observer.OnStepEnd(end);
            }
            catch (Exception exception)
            {
                subscription.Module.ReportFault(exception);
            }
        }

        _publishing = int.MaxValue;
    }
}
