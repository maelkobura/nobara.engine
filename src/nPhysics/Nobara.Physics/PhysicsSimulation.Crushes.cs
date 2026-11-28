// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// Kinematic grids push the characters they move into, after the transforms
/// are written and before the characters move. A character that cannot get
/// out of the way is crushed: the resolver decides what happens, and by
/// default the grid goes back to where it was before the step.
/// </summary>
internal sealed partial class PhysicsSimulation
{
    /// <summary>The smallest motion of a grid at a character, in blocks, that pushes it.</summary>
    private const float SmallestPush = 1e-5f;

    private Pusher[] _pushers = new Pusher[8];
    private int _pusherCount;
    private CrushResolverHandle? _crushResolver;
    private Grid? _ignoredGrid;
    private int _crushes;
    private int _crushBlocks;

    /// <summary>Gets the number of characters crushed at the last step.</summary>
    internal int LastCrushes => _crushes;

    /// <summary>Gets the number of grids that went back to their pose at the last step.</summary>
    internal int LastBlocks => _crushBlocks;

    public CrushResolverHandle SetCrushResolver(ModuleContext module, ICrushResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(resolver);
        if (_crushResolver is not null)
        {
            throw new InvalidOperationException($"The module {_crushResolver.Module.Module.Id} set the resolver of crushes already.");
        }

        var handle = new CrushResolverHandle(module, resolver, h =>
        {
            if (_crushResolver == h)
            {
                _crushResolver = null;
            }
        });
        _crushResolver = handle;
        return handle;
    }

    /// <summary>Keeps a kinematic grid body that moved during the step, with its pose before it, to push the characters in its way.</summary>
    [HotPath]
    private void AddPusher(int slot, in WorldPosition position, Quaternion rotation, int pose)
    {
        ref BodyRecord body = ref _bodies[slot];
        if (body.Position.RelativeTo(position).LengthSquared() < SmallestPush * SmallestPush && MathF.Abs(Quaternion.Dot(rotation, body.Rotation)) > 1f - 1e-9f)
        {
            return;   // it did not move
        }

        if (_pusherCount == _pushers.Length)
        {
            Array.Resize(ref _pushers, _pushers.Length * 2);
        }

        _pushers[_pusherCount++] = new Pusher { Slot = slot, Position = position, Rotation = rotation, Pose = pose };
    }

    /// <summary>Lets every kinematic grid that moved push the characters it went into.</summary>
    [HotPath]
    private void PushCharacters()
    {
        _crushes = 0;
        _crushBlocks = 0;
        if (_pusherCount == 0 || _characterCount == 0)
        {
            return;
        }

        for (int i = 0; i < _characterHigh; i++)
        {
            ref CharacterRecord record = ref _characters[i];
            if (record.Alive && !record.World!.IsDestroyed && record.World.IsAlive(record.Entity) && record.World.Has(record.Entity, _characterComponent))
            {
                PushCharacter(ref record);
            }
        }
    }

    [HotPath]
    private void PushCharacter(ref CharacterRecord record)
    {
        EntityWorld world = record.World!;
        EntityId entity = record.Entity;
        CharacterBody body = Sanitize(world.Read(entity, _characterComponent));
        if ((body.Filter.Mask & CollisionFilter.VoxelLayer) == 0)
        {
            return;   // it goes through voxels
        }

        Grid? support = record.Support;
        if (support is not null && (support.IsDestroyed || world.GetParent(entity) != support.Entity))
        {
            support = null;   // gone: the character moves from where it was in the world
        }

        FrameOf(world, entity, support, out Frame frame, out Vector3 feet);
        var half = new Vector3(body.Size.X / 2, 0, body.Size.Z / 2);
        var extent = new Vector3(half.X, body.Size.Y / 2, half.Z);
        Quaternion inverse = Quaternion.Conjugate(frame.Rotation);
        bool moved = false;
        for (int p = 0; p < _pusherCount; p++)
        {
            ref Pusher pusher = ref _pushers[p];
            if (pusher.Blocked)
            {
                continue;
            }

            ref BodyRecord grid = ref _bodies[pusher.Slot];
            Grid voxels = grid.Grid!;
            if (voxels == support || grid.Dimension != record.Dimension || voxels.IsDestroyed || !body.Filter.CollidesWith(grid.Filter))
            {
                continue;
            }

            Vector3 center = feet + new Vector3(0, extent.Y, 0);
            WorldPosition centerInWorld = frame.ToWorld(center);
            _boxCount = 0;
            GatherCells(voxels, frame, center, extent, centerInWorld);
            if (_boxCount == 0)
            {
                continue;
            }

            Vector3 push = Vector3.Transform(Displacement(pusher, grid, centerInWorld), inverse);
            float length = push.Length();
            if (length < SmallestPush)
            {
                continue;   // the grid turns about the character
            }

            Vector3 direction = push / length;
            Vector3 minimum = feet - half;
            Vector3 maximum = feet + half + new Vector3(0, body.Size.Y, 0);
            float depth = Separation(minimum, maximum, direction);
            if (depth <= 0)
            {
                continue;   // it does not reach into the character
            }

            push = direction * (depth + Skin);
            _ignoredGrid = voxels;
            GatherBoxes(record.Dimension, frame, Vector3.Min(minimum, minimum + push) - new Vector3(Skin), Vector3.Max(maximum, maximum + push) + new Vector3(Skin), body.Filter);
            _ignoredGrid = null;
            Vector3 made = Collide(minimum, maximum, push, out _);
            if (Vector3.DistanceSquared(made, push) <= Skin * Skin)
            {
                feet += made;
                moved = true;
                continue;
            }

            var crush = new CharacterCrush(record.Dimension, entity, voxels, new BodyHandle(pusher.Slot, grid.Generation), Vector3.Transform(direction, frame.Rotation), depth);
            _crushes++;
            CrushResponse response = Resolve(crush);
            if (response.Kind == CrushResponseKind.Block)
            {
                Block(ref pusher);
            }
            else if (response.Kind == CrushResponseKind.Move)
            {
                if (moved)
                {
                    WriteFeet(world, entity, support, frame, feet);
                }

                MoveCrushed(ref record, response.Position);
                return;   // somewhere else now: the other grids push it from the next step on
            }
        }

        if (moved)
        {
            WriteFeet(world, entity, support, frame, feet);
        }
    }

    /// <summary>Gets how far the point of a grid at a place of the world moved during the step, in the world.</summary>
    [HotPath]
    private static Vector3 Displacement(in Pusher pusher, in BodyRecord grid, in WorldPosition point)
    {
        Difference(point, pusher.Position, out double x, out double y, out double z);
        Rotate(Quaternion.Conjugate(pusher.Rotation), x, y, z, out double lx, out double ly, out double lz);   // the point on the grid before the step
        Rotate(grid.Rotation, lx, ly, lz, out double wx, out double wy, out double wz);
        Difference(grid.Position, point, out double ox, out double oy, out double oz);
        return new Vector3((float)(ox + wx), (float)(oy + wy), (float)(oz + wz));
    }

    /// <summary>
    /// Gets how far a box has to move along a direction to leave the gathered
    /// boxes it reaches into: for each of them, the shortest way out along an
    /// axis, then the longest of these.
    /// </summary>
    [HotPath]
    private float Separation(Vector3 minimum, Vector3 maximum, Vector3 direction)
    {
        float most = 0;
        for (int i = 0; i < _boxCount; i++)
        {
            ref CharacterBox box = ref _boxes[i];
            if (box.Max.X <= minimum.X + Skin || box.Min.X >= maximum.X - Skin
                || box.Max.Y <= minimum.Y + Skin || box.Min.Y >= maximum.Y - Skin
                || box.Max.Z <= minimum.Z + Skin || box.Min.Z >= maximum.Z - Skin)
            {
                continue;
            }

            float least = float.PositiveInfinity;
            least = Exit(direction.X, box.Min.X, box.Max.X, minimum.X, maximum.X, least);
            least = Exit(direction.Y, box.Min.Y, box.Max.Y, minimum.Y, maximum.Y, least);
            least = Exit(direction.Z, box.Min.Z, box.Max.Z, minimum.Z, maximum.Z, least);
            most = MathF.Max(most, least);
        }

        return most;
    }

    [HotPath]
    private static float Exit(float direction, float boxMinimum, float boxMaximum, float minimum, float maximum, float least)
    {
        if (direction > 1e-6f)
        {
            return MathF.Min(least, (boxMaximum - minimum) / direction);
        }

        if (direction < -1e-6f)
        {
            return MathF.Min(least, (maximum - boxMinimum) / -direction);
        }

        return least;
    }

    [HotPath]
    private CrushResponse Resolve(in CharacterCrush crush)
    {
        if (_crushResolver is not { } handle)
        {
            return CrushResponse.Block;
        }

        try
        {
            return handle.Resolver.Resolve(crush);
        }
        catch (Exception exception)
        {
            handle.Module.ReportFault(exception);
            return CrushResponse.Block;
        }
    }

    /// <summary>Puts a grid back where it was before the step: its body, its transform, and its pose in the end of the step. Its velocities stay, so that it goes on once the way is free.</summary>
    [HotPath]
    private void Block(ref Pusher pusher)
    {
        pusher.Blocked = true;
        _crushBlocks++;
        ref BodyRecord body = ref _bodies[pusher.Slot];
        body.Position = pusher.Position;
        body.Rotation = pusher.Rotation;
        if (body.World is { } world)
        {
            world.Backend.SetPose(body.BackendId, PhysicsFrame.ToLocal(world.Origin, body.Position), body.Rotation);
        }

        Grid grid = body.Grid!;
        var transform = new WorldTransform(body.Position.Offset(-Vector3.Transform(body.Center, body.Rotation)), body.Rotation, grid.Transform.Scale);
        _gridWriter!.Set(grid, transform);
        _poses[pusher.Pose] = _poses[pusher.Pose] with { Transform = transform };
    }

    /// <summary>Puts a crushed character where the resolver says, off its support grid, with its velocity in the world.</summary>
    [HotPath]
    private void MoveCrushed(ref CharacterRecord record, in WorldPosition feet)
    {
        EntityWorld world = record.World!;
        EntityId entity = record.Entity;
        if (record.Support is not null)
        {
            record.Velocity = world.Read(entity, _velocityComponent).Linear;
            ChangeSupport(ref record, feet, null);
            ref CharacterState state = ref world.Write(entity, _characterStateComponent);
            state.Support = EntityId.Null;
        }
        else
        {
            world.Set(entity, _transform, world.Read(entity, _transform) with { Position = feet });
        }
    }

    /// <summary>Writes where the feet of a character are, in the frame of its support grid or in the world.</summary>
    [HotPath]
    private void WriteFeet(EntityWorld world, EntityId entity, Grid? support, in Frame frame, Vector3 feet)
    {
        if (support is not null)
        {
            LocalTransform local = world.Read(entity, _localTransform) with { Position = feet };
            world.Set(entity, _localTransform, local);
            world.Set(entity, _transform, support.Transform.Combine(local.Position, local.Rotation, local.Scale));
        }
        else
        {
            world.Set(entity, _transform, world.Read(entity, _transform) with { Position = frame.ToWorld(feet) });
        }
    }

    /// <summary>A kinematic grid body that moved during the step.</summary>
    private struct Pusher
    {
        public int Slot;

        /// <summary>The center of its body before the step.</summary>
        public WorldPosition Position;
        public Quaternion Rotation;

        /// <summary>Its pose in the end of the step.</summary>
        public int Pose;

        /// <summary>Whether a crush put it back during this step: it pushes no more.</summary>
        public bool Blocked;
    }
}
