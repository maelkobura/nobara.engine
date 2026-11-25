// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Physics;

/// <summary>
/// The ghosts: a body near the border of a region next to its own is seen
/// there as a body of the same kind, placed again where the body is before
/// each step, so that the bodies of that region collide with it. A dynamic
/// ghost has the mass of its body, so that what it rests on carries its
/// weight; what the step of the other world does to it is dropped. The ghost
/// of a sleeping body is kinematic and immobile.
/// </summary>
internal sealed partial class PhysicsSimulation
{
    private GhostRecord[] _ghosts = new GhostRecord[16];
    private int _ghostHigh;
    private int _ghostFree = -1;
    private int _ghostCount;
    private int _ghostStamp;

    /// <summary>
    /// Gives each body a ghost in every other world of its dimension whose
    /// region is within its bounds, grown by <see cref="GhostMargin"/> and by
    /// its velocity over the step; places the ghosts that follow a moving body,
    /// and removes the ghosts no body needs any more.
    /// </summary>
    private void UpdateGhosts(float delta)
    {
        if (_worlds.Count < 2 && _ghostCount == 0)
        {
            return;
        }

        _ghostStamp++;
        if (_worlds.Count >= 2)
        {
            for (int i = 0; i < _bodyHigh; i++)
            {
                ref BodyRecord body = ref _bodies[i];
                if (!body.Alive || body.World is null)
                {
                    continue;
                }

                double reach = _shapes[body.Shape.Index].Radius + GhostMargin + (body.Linear.Length() * delta);
                for (int w = 0; w < _worlds.Count; w++)
                {
                    WorldEntry host = _worlds[w];
                    if (host != body.World && host.Region.Dimension == body.Dimension && DistanceTo(host.Region, body.Position) <= reach)
                    {
                        Ghost(i, ref body, host);
                    }
                }
            }
        }

        for (int i = 0; i < _ghostHigh; i++)
        {
            ref GhostRecord ghost = ref _ghosts[i];
            if (ghost.Alive && ghost.Stamp != _ghostStamp)
            {
                ghost.Host!.Backend.RemoveBody(ghost.BackendId);
                FreeGhost(i);
            }
        }
    }

    /// <summary>Adds the ghost of a body to a world, or places it where the body is.</summary>
    [HotPath]
    private void Ghost(int slot, ref BodyRecord body, WorldEntry host)
    {
        // A sleeping body does not move: its ghost is kinematic and left alone, so that it does not wake what rests on it.
        bool moves = body.Kind != BodyKind.Static && !body.Asleep;
        BodyKind kind = body.Kind == BodyKind.Dynamic && body.Asleep ? BodyKind.Kinematic : body.Kind;
        Vector3 linear = Vector3.Zero;
        Vector3 angular = Vector3.Zero;
        if (moves)
        {
            body.World!.Backend.ReadBody(body.BackendId, out BackendBodyState state);   // the velocities the commands left
            linear = state.LinearVelocity;
            angular = state.AngularVelocity;
        }

        Vector3 position = PhysicsFrame.ToLocal(host.Origin, body.Position);
        PhysicsBackendWorld backend = host.Backend;
        int index = FindGhost(body, host);
        if (index < 0)
        {
            index = AllocateGhost();
            ref GhostRecord added = ref _ghosts[index];
            added.Source = slot;
            added.Host = host;
            added.Kind = kind;
            added.Shape = body.Shape;
            added.Material = body.Material;
            added.Filter = body.Filter;
            added.BackendId = backend.AddBody(new BackendBody
            {
                Slot = slot,
                Kind = kind,
                Shape = body.Shape,
                Position = position,
                Rotation = body.Rotation,
                LinearVelocity = linear,
                AngularVelocity = angular,
                Mass = body.Mass,
                Inertia = body.Inertia,
                Material = body.Material,
                Filter = body.Filter,
                CanSleep = true,
            });
            added.NextOfBody = body.FirstGhost;
            body.FirstGhost = index + 1;
            host.Ghosts++;
            added.Stamp = _ghostStamp;
            return;
        }

        ref GhostRecord ghost = ref _ghosts[index];
        ghost.Stamp = _ghostStamp;
        bool turned = ghost.Kind != kind;
        if (turned)
        {
            ghost.Kind = kind;
            backend.SetKind(ghost.BackendId, kind, body.Mass, body.Inertia);
        }

        if (ghost.Shape != body.Shape)
        {
            ghost.Shape = body.Shape;
            backend.SetShape(ghost.BackendId, body.Shape);
        }

        if (ghost.Material != body.Material)
        {
            ghost.Material = body.Material;
            backend.SetMaterial(ghost.BackendId, body.Material);
        }

        if (ghost.Filter != body.Filter)
        {
            ghost.Filter = body.Filter;
            backend.SetFilter(ghost.BackendId, body.Filter);
        }

        if (moves || turned || body.Dirty)
        {
            // A dynamic ghost moves during the step like its body would; the step of its own world wins, and it is placed again here.
            backend.SetPose(ghost.BackendId, position, body.Rotation);
            if (kind != BodyKind.Static)
            {
                backend.SetVelocity(ghost.BackendId, linear, angular);
            }
        }
    }

    [HotPath]
    private int FindGhost(in BodyRecord body, WorldEntry host)
    {
        for (int index = body.FirstGhost - 1; index >= 0; index = _ghosts[index].NextOfBody - 1)
        {
            if (_ghosts[index].Host == host)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Removes the ghost of a body from a world, before the body itself enters it.</summary>
    [HotPath]
    private void RemoveGhost(int slot, WorldEntry host)
    {
        int index = FindGhost(_bodies[slot], host);
        if (index >= 0)
        {
            host.Backend.RemoveBody(_ghosts[index].BackendId);
            FreeGhost(index);
        }
    }

    /// <summary>Removes every ghost of a body, which is removed.</summary>
    [HotPath]
    private void RemoveGhosts(int slot)
    {
        while (_bodies[slot].FirstGhost > 0)
        {
            int index = _bodies[slot].FirstGhost - 1;
            _ghosts[index].Host!.Backend.RemoveBody(_ghosts[index].BackendId);
            FreeGhost(index);
        }
    }

    /// <summary>Forgets the ghosts of a world that is disposed with them.</summary>
    [HotPath]
    private void ForgetGhosts(WorldEntry world)
    {
        for (int i = 0; i < _ghostHigh && world.Ghosts > 0; i++)
        {
            if (_ghosts[i].Alive && _ghosts[i].Host == world)
            {
                FreeGhost(i);
            }
        }
    }

    [HotPath]
    private int AllocateGhost()
    {
        int index;
        if (_ghostFree >= 0)
        {
            index = _ghostFree;
            _ghostFree = _ghosts[index].NextFree;
        }
        else
        {
            if (_ghostHigh == _ghosts.Length)
            {
                Array.Resize(ref _ghosts, _ghosts.Length * 2);
            }

            index = _ghostHigh++;
        }

        _ghosts[index] = new GhostRecord { Alive = true, NextFree = -1 };
        _ghostCount++;
        return index;
    }

    /// <summary>Unlinks a ghost from its body and frees its record; its body in the backend is removed by the caller, or disposed with its world.</summary>
    [HotPath]
    private void FreeGhost(int index)
    {
        ref GhostRecord ghost = ref _ghosts[index];
        ref BodyRecord body = ref _bodies[ghost.Source];
        if (body.FirstGhost == index + 1)
        {
            body.FirstGhost = ghost.NextOfBody;
        }
        else
        {
            for (int previous = body.FirstGhost - 1; previous >= 0; previous = _ghosts[previous].NextOfBody - 1)
            {
                if (_ghosts[previous].NextOfBody == index + 1)
                {
                    _ghosts[previous].NextOfBody = ghost.NextOfBody;
                    break;
                }
            }
        }

        ghost.Host!.Ghosts--;
        Release(ref ghost.Voxels);
        int[]? footprint = ghost.Voxels.Slots;
        ghost = default;
        ghost.Voxels.Slots = footprint;   // kept for the next ghost of the record
        ghost.NextFree = _ghostFree;
        _ghostFree = index;
        _ghostCount--;
    }

    /// <summary>The ghost of a body in the world of another region.</summary>
    private struct GhostRecord
    {
        public bool Alive;
        public int NextFree;

        /// <summary>The slot of the body it follows.</summary>
        public int Source;

        /// <summary>The world it is in.</summary>
        public WorldEntry? Host;

        /// <summary>Its number in that world.</summary>
        public int BackendId;

        public BodyKind Kind;
        public PhysicsShape Shape;
        public PhysicsMaterial Material;
        public CollisionFilter Filter;

        /// <summary>The pass on the ghosts that last needed it.</summary>
        public int Stamp;

        /// <summary>The next ghost of the same body, plus one; 0 at the end.</summary>
        public int NextOfBody;

        /// <summary>The chunks of voxels the ghost holds in its world, while it is dynamic.</summary>
        public VoxelFootprint Voxels;
    }
}
