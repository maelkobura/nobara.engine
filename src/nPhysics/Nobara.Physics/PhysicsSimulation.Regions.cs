// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The regions: one physics world for each island of active chunks, around
/// an origin of its own. The regions follow the active area tickets of the
/// terrain; bodies belong to the region that holds their center, move to
/// another region when they leave theirs, and are seen as ghosts by the
/// regions next to them.
/// </summary>
internal sealed partial class PhysicsSimulation
{
    /// <summary>Two regions whose boxes are at most this far apart, in chunks, merge: 0 when they share a chunk.</summary>
    internal const int MergeDistance = 0;

    /// <summary>The islands of one region split once their boxes are this far apart, in chunks: two empty chunks between them.</summary>
    internal const int SplitDistance = 3;

    /// <summary>How far past the bounds of a body a region next to it sees it as a ghost, in blocks, before its velocity over a step is added.</summary>
    internal const float GhostMargin = 2f;

    /// <summary>How far out of its region a body stays in it, in blocks, so that a body on a border does not go back and forth.</summary>
    internal const float BodyHysteresis = 1f;

    /// <summary>How far the center of a region can drift from its origin, in chunks along one axis, before the origin moves to it.</summary>
    internal const int RecenterDistance = 8;

    private readonly List<RegionEntry> _regions = [];
    private readonly List<WorldEntry> _retiring = [];
    private IChunkLoading? _loading;
    private TicketBox[] _tickets = new TicketBox[8];
    private TicketBox[] _previousTickets = new TicketBox[8];
    private int _ticketCount;
    private int _previousTicketCount = -1;
    private int[] _ticketParent = new int[8];
    private int[] _ticketCluster = new int[8];
    private ClusterBox[] _clusters = new ClusterBox[8];
    private RegionEntry?[] _targets = new RegionEntry?[64];
    private int[] _groups = new int[64];
    private PhysicsBackendWorld[] _stepping = new PhysicsBackendWorld[4];
    private Exception?[] _stepFailures = new Exception?[4];
    private int _nextRegionId = 1;
    private bool _regionsChanged;
    private long _transfers;
    private long _merges;
    private long _splits;
    private long _recenters;
    private int _frozenBodies;

    public IReadOnlyList<PhysicsRegionInfo> Regions
    {
        get
        {
            var regions = new PhysicsRegionInfo[_regions.Count];
            for (int i = 0; i < regions.Length; i++)
            {
                RegionEntry region = _regions[i];
                WorldEntry? world = region.World;
                regions[i] = new PhysicsRegionInfo(region.Id, region.Dimension, region.Minimum, region.Maximum, region.Origin, world?.Bodies ?? 0, world?.Ghosts ?? 0, world?.Joints ?? 0, world is not null);
            }

            return regions;
        }
    }

    /// <summary>
    /// Places the bodies before the step: the regions follow the tickets, the
    /// bodies move to the world of their region, and the ghosts follow them.
    /// </summary>
    /// <remarks>
    /// Not hot path code as a whole: it creates the regions, their worlds, and
    /// the records of the ghosts when they appear. It allocates nothing once
    /// the regions are stable, which a test checks.
    /// </remarks>
    private void PlaceBodies(float delta)
    {
        _placeDelta = delta;
        UpdateRegions();
        KeepTerrains();
        UpdateMembership();
        UpdateGhosts(delta);
        UpdateVoxels(delta);
    }

    /// <summary>
    /// Derives the regions from the active area tickets of the terrain grids:
    /// boxes of tickets that share a chunk join, the islands of one region stay
    /// together until they are <see cref="SplitDistance"/> chunks apart, and a
    /// region keeps its world when it grows, shrinks, merges, or splits. Does
    /// nothing while the tickets do not change.
    /// </summary>
    private void UpdateRegions()
    {
        CollectTickets();
        if (SameTickets())
        {
            return;
        }

        KeepTickets();
        _regionsChanged = true;
        int clusters = Cluster();
        foreach (RegionEntry region in _regions)
        {
            region.Claimed = false;
            region.Overlaps = 0;
        }

        for (int c = 0; c < clusters; c++)
        {
            ClaimRegion(ref _clusters[c]);
        }

        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            RegionEntry region = _regions[i];
            if (region.Overlaps >= 2)
            {
                _splits++;
            }

            if (!region.Claimed)
            {
                RetireRegion(region);
                _regions.RemoveAt(i);
            }
        }

        for (int c = 0; c < clusters; c++)
        {
            ref ClusterBox cluster = ref _clusters[c];
            ChunkCoordinate center = Center(cluster.Minimum, cluster.Maximum);
            RegionEntry region = cluster.Region ?? CreateRegion(cluster.Dimension, center);
            region.Minimum = cluster.Minimum;
            region.Maximum = cluster.Maximum;
            if (Math.Max(Math.Abs((long)center.X - region.Origin.X), Math.Max(Math.Abs((long)center.Y - region.Origin.Y), Math.Abs((long)center.Z - region.Origin.Z))) > RecenterDistance)
            {
                Recenter(region, center);
            }

            cluster.Region = null;
        }
    }

    /// <summary>Reads the active area tickets of the terrain grids as boxes of chunks of the world.</summary>
    [HotPath]
    private void CollectTickets()
    {
        _ticketCount = 0;
        if (_loading is null)
        {
            return;
        }

        IReadOnlyList<AreaTicketView> tickets = _loading.AreaTickets;
        for (int i = 0; i < tickets.Count; i++)
        {
            AreaTicketView ticket = tickets[i];
            if (ticket.Level != TicketLevel.Active || !ticket.Grid.IsTerrain || ticket.Grid.IsDestroyed)
            {
                continue;
            }

            WorldPosition position = CenterInWorld(ticket.Grid.Transform, ticket.Center);
            ChunkArea area = ticket.Area;
            if (_ticketCount == _tickets.Length)
            {
                Array.Resize(ref _tickets, _tickets.Length * 2);
            }

            _tickets[_ticketCount++] = new TicketBox(
                ticket.Grid.Dimension,
                new ChunkCoordinate(position.ChunkX - area.Radius, position.ChunkY - area.Height, position.ChunkZ - area.Radius),
                new ChunkCoordinate(position.ChunkX + area.Radius, position.ChunkY + area.Height, position.ChunkZ + area.Radius));
        }
    }

    /// <summary>
    /// Gets the center of a chunk of a grid in the world: exactly, in whole
    /// chunks, when the grid is neither turned nor scaled, as the terrain is;
    /// through the transform otherwise.
    /// </summary>
    [HotPath]
    private static WorldPosition CenterInWorld(in WorldTransform transform, ChunkCoordinate chunk)
    {
        const float half = VoxelCoordinates.ChunkSize / 2;
        WorldPosition origin = transform.Position;
        if (transform.Rotation == Quaternion.Identity && transform.Scale == Vector3.One)
        {
            return new WorldPosition(origin.ChunkX + chunk.X, origin.ChunkY + chunk.Y, origin.ChunkZ + chunk.Z, origin.Local + new Vector3(half));
        }

        BlockCoordinate block = VoxelCoordinates.ToBlock(chunk, new LocalBlockCoordinate(VoxelCoordinates.ChunkSize / 2, VoxelCoordinates.ChunkSize / 2, VoxelCoordinates.ChunkSize / 2));
        return VoxelCoordinates.ToWorldPosition(block, transform);
    }

    [HotPath]
    private bool SameTickets()
    {
        if (_ticketCount != _previousTicketCount)
        {
            return false;
        }

        for (int i = 0; i < _ticketCount; i++)
        {
            if (_tickets[i] != _previousTickets[i])
            {
                return false;
            }
        }

        return true;
    }

    [HotPath]
    private void KeepTickets()
    {
        if (_previousTickets.Length < _ticketCount)
        {
            Array.Resize(ref _previousTickets, _tickets.Length);
        }

        Array.Copy(_tickets, _previousTickets, _ticketCount);
        _previousTicketCount = _ticketCount;
    }

    /// <summary>Joins the boxes of the tickets into islands, with the hysteresis of the regions they overlap.</summary>
    /// <returns>The number of islands, in <see cref="_clusters"/>.</returns>
    [HotPath]
    private int Cluster()
    {
        if (_ticketParent.Length < _ticketCount)
        {
            Array.Resize(ref _ticketParent, _tickets.Length);
            Array.Resize(ref _ticketCluster, _tickets.Length);
        }

        for (int i = 0; i < _ticketCount; i++)
        {
            _ticketParent[i] = i;
        }

        for (int i = 0; i < _ticketCount; i++)
        {
            ref TicketBox a = ref _tickets[i];
            for (int j = i + 1; j < _ticketCount; j++)
            {
                ref TicketBox b = ref _tickets[j];
                if (a.Dimension != b.Dimension)
                {
                    continue;
                }

                long distance = Distance(a.Minimum, a.Maximum, b.Minimum, b.Maximum);
                if (distance <= MergeDistance || (distance < SplitDistance && ShareRegion(a, b)))
                {
                    Join(_ticketParent, i, j);
                }
            }
        }

        int clusters = 0;
        for (int i = 0; i < _ticketCount; i++)
        {
            int root = Find(_ticketParent, i);
            ref TicketBox ticket = ref _tickets[i];
            if (root == i)
            {
                if (clusters == _clusters.Length)
                {
                    Array.Resize(ref _clusters, _clusters.Length * 2);
                }

                _ticketCluster[i] = clusters;
                _clusters[clusters++] = new ClusterBox { Dimension = ticket.Dimension, Minimum = ticket.Minimum, Maximum = ticket.Maximum };
            }
            else
            {
                ref ClusterBox cluster = ref _clusters[_ticketCluster[root]];
                cluster.Minimum = Min(cluster.Minimum, ticket.Minimum);
                cluster.Maximum = Max(cluster.Maximum, ticket.Maximum);
            }
        }

        return clusters;
    }

    /// <summary>Tells whether one region overlaps two boxes: two islands of a region stay together until they are far enough apart.</summary>
    [HotPath]
    private bool ShareRegion(in TicketBox a, in TicketBox b)
    {
        foreach (RegionEntry region in _regions)
        {
            if (region.Dimension == a.Dimension
                && Overlap(region.Minimum, region.Maximum, a.Minimum, a.Maximum) > 0
                && Overlap(region.Minimum, region.Maximum, b.Minimum, b.Maximum) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gives an island the region it continues: among the regions it overlaps
    /// and that no island took, the one with the most bodies, so that a merge
    /// moves the fewest bodies; then the one it overlaps most.
    /// </summary>
    [HotPath]
    private void ClaimRegion(ref ClusterBox cluster)
    {
        RegionEntry? best = null;
        long bestOverlap = 0;
        int overlapping = 0;
        foreach (RegionEntry region in _regions)
        {
            if (region.Dimension != cluster.Dimension)
            {
                continue;
            }

            long overlap = Overlap(region.Minimum, region.Maximum, cluster.Minimum, cluster.Maximum);
            if (overlap <= 0)
            {
                continue;
            }

            overlapping++;
            region.Overlaps++;
            if (region.Claimed)
            {
                continue;
            }

            int bodies = region.World?.Bodies ?? 0;
            int bestBodies = best?.World?.Bodies ?? 0;
            if (best is null || bodies > bestBodies || (bodies == bestBodies && overlap > bestOverlap))
            {
                best = region;
                bestOverlap = overlap;
            }
        }

        if (overlapping >= 2)
        {
            _merges++;
        }

        if (best is not null)
        {
            best.Claimed = true;
        }

        cluster.Region = best;
    }

    private RegionEntry CreateRegion(DimensionId dimension, ChunkCoordinate origin)
    {
        var region = new RegionEntry(_nextRegionId++, dimension, origin);
        _regions.Add(region);
        _context.Logger.Debug($"Physics region {region.Id} of {dimension} created at chunk {origin}.");
        return region;
    }

    /// <summary>Ends a region that no island continues; its bodies leave its world at the next pass on the bodies.</summary>
    [HotPath]
    private void RetireRegion(RegionEntry region)
    {
        region.Alive = false;
        if (region.World is { } world)
        {
            _worlds.Remove(world);
            _retiring.Add(world);
            region.World = null;
        }
    }

    /// <summary>Moves the origin of a region: its bodies move to a new world around the new origin at the next pass on the bodies.</summary>
    [HotPath]
    private void Recenter(RegionEntry region, ChunkCoordinate origin)
    {
        _recenters++;
        region.Origin = origin;
        if (region.World is { } world)
        {
            _worlds.Remove(world);
            _retiring.Add(world);
            region.World = null;
        }
    }

    /// <summary>Gets the world of a region, created the first time a body enters it.</summary>
    private WorldEntry EnsureWorld(RegionEntry region)
    {
        if (region.World is { } existing)
        {
            return existing;
        }

        var options = new PhysicsWorldOptions(region.Origin, Gravity, this, _context.Logger);
        var world = new WorldEntry(region, region.Origin, _backend!.CreateWorld(options));
        region.World = world;
        _worlds.Add(world);
        _context.Logger.Debug($"Physics world of region {region.Id} of {region.Dimension} created at chunk {region.Origin}.");
        return world;
    }

    /// <summary>Disposes a world without bodies, with the ghosts it holds.</summary>
    [HotPath]
    private void DisposeWorld(WorldEntry world)
    {
        ForgetGhosts(world);
        ForgetVoxels(world);
        world.Backend.Dispose();
        if (!_worlds.Remove(world))
        {
            _retiring.Remove(world);
        }

        if (world.Region.World == world)
        {
            world.Region.World = null;
        }
    }

    /// <summary>
    /// Gives each body the region it belongs to and moves the bodies whose
    /// region changed: to the world of their new region, with their pose and
    /// their velocities, or out of every world when no region holds them.
    /// Joined bodies follow the region of the first body of their group.
    /// </summary>
    private void UpdateMembership()
    {
        bool moving = false;
        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (!body.Alive)
            {
                continue;
            }

            if (!_regionsChanged && !_chunksChanged && !body.Dirty && (body.World is null || body.Asleep || body.Kind == BodyKind.Static))
            {
                _targets[i] = body.World?.Region;   // nothing moved it, and no region changed
                continue;
            }

            _targets[i] = FindRegion(body);
        }

        if (_jointCount > 0)
        {
            FollowGroups();
        }

        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (body.Alive)
            {
                RegionEntry? target = _targets[i];
                body.Moving = target is null ? body.World is not null : body.World is null || body.World != target.World;
                moving |= body.Moving;
            }
        }

        if (moving)
        {
            MoveBodies();
        }

        for (int i = _retiring.Count - 1; i >= 0; i--)
        {
            DisposeWorld(_retiring[i]);   // every body left it
        }

        for (int i = _worlds.Count - 1; i >= 0; i--)
        {
            if (_worlds[i].Bodies == 0)
            {
                DisposeWorld(_worlds[i]);
            }
        }

        Array.Clear(_targets, 0, _bodyHigh);
        _regionsChanged = false;
        _chunksChanged = false;
    }

    /// <summary>
    /// Finds the region of a body: the one it is in while its center stays near
    /// it, or else the first that holds its center; none when the body reaches
    /// a chunk of the terrain that is not loaded or not active.
    /// </summary>
    [HotPath]
    private RegionEntry? FindRegion(in BodyRecord body)
    {
        RegionEntry? current = body.World?.Region;
        RegionEntry? region = current is { Alive: true } && Holds(current, body.Position, BodyHysteresis)
            ? current
            : FindRegion(body.Dimension, body.Position);
        return region is not null && TouchesInactive(body) ? null : region;
    }

    [HotPath]
    private RegionEntry? FindRegion(DimensionId dimension, in WorldPosition position)
    {
        foreach (RegionEntry region in _regions)
        {
            if (region.Dimension == dimension && Holds(region, position, 0f))
            {
                return region;
            }
        }

        return null;
    }

    /// <summary>Gives every body of a group of joined bodies the region of the first body of the group.</summary>
    [HotPath]
    private void FollowGroups()
    {
        for (int i = 0; i < _bodyHigh; i++)
        {
            _groups[i] = i;
        }

        for (int i = 0; i < _jointHigh; i++)
        {
            ref JointRecord joint = ref _joints[i];
            if (joint.Alive)
            {
                Join(_groups, joint.First.Index, joint.Second.Index);
            }
        }

        for (int i = 0; i < _bodyHigh; i++)
        {
            if (_bodies[i].Alive)
            {
                _targets[i] = _targets[Find(_groups, i)];
            }
        }
    }

    private void MoveBodies()
    {
        for (int i = 0; i < _jointHigh; i++)
        {
            ref JointRecord joint = ref _joints[i];
            if (joint.Alive && joint.World is not null && (_bodies[joint.First.Index].Moving || _bodies[joint.Second.Index].Moving))
            {
                joint.World.Backend.RemoveJoint(joint.BackendId);
                joint.World.Joints--;
                joint.World = null;
            }
        }

        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (!body.Alive || !body.Moving)
            {
                continue;
            }

            body.Moving = false;
            bool wasFrozen = body.World is null;
            if (body.World is { } from)
            {
                from.Backend.ReadBody(body.BackendId, out BackendBodyState state);
                body.Linear = body.Kind == BodyKind.Static ? Vector3.Zero : state.LinearVelocity;
                body.Angular = body.Kind == BodyKind.Static ? Vector3.Zero : state.AngularVelocity;
                from.Backend.RemoveBody(body.BackendId);
                from.Bodies--;
                body.World = null;
                body.BackendId = -1;
            }

            RegionEntry? target = _targets[i];
            if (target is not null)
            {
                WorldEntry to = EnsureWorld(target);
                RemoveGhost(i, to);
                body.BackendId = to.Backend.AddBody(Describe(i, body, to));
                to.Bodies++;
                body.World = to;
                if (!wasFrozen)
                {
                    _transfers++;
                }
            }

            body.Asleep = false;   // a body arrives awake; a frozen body does not sleep
            if (wasFrozen != (body.World is null))
            {
                body.EntityWorld!.Write(body.Entity, _bodyComponent).Flags = FlagsOf(body);
            }
        }

        for (int i = 0; i < _jointHigh; i++)
        {
            ref JointRecord joint = ref _joints[i];
            if (joint.Alive && joint.World is null)
            {
                AddJointToWorld(ref joint);
            }
        }
    }

    /// <summary>Adds a joint to the world of its bodies, once both are in the same one.</summary>
    [HotPath]
    private void AddJointToWorld(ref JointRecord joint)
    {
        ref BodyRecord a = ref _bodies[joint.First.Index];
        ref BodyRecord b = ref _bodies[joint.Second.Index];
        if (a.World is null || a.World != b.World)
        {
            return;
        }

        BackendJoint description = joint.Description;
        description.BodyA = a.BackendId;
        description.BodyB = b.BackendId;
        joint.BackendId = a.World.Backend.AddJoint(description);
        joint.World = a.World;
        a.World.Joints++;
    }

    /// <summary>Describes a body as it enters a world, from the pose and the velocities nPhysics keeps.</summary>
    [HotPath]
    private static BackendBody Describe(int slot, in BodyRecord body, WorldEntry world)
    {
        bool moves = body.Kind != BodyKind.Static;
        return new BackendBody
        {
            Slot = slot,
            Kind = body.Kind,
            Shape = body.Shape,
            Position = PhysicsFrame.ToLocal(world.Origin, body.Position),
            Rotation = body.Rotation,
            LinearVelocity = moves ? body.Linear : Vector3.Zero,
            AngularVelocity = moves ? body.Angular : Vector3.Zero,
            Mass = body.Mass,
            Inertia = body.Inertia,
            Material = body.Material,
            Filter = body.Filter,
            Continuous = body.Continuous,
            CanSleep = body.CanSleep,
        };
    }

    /// <summary>
    /// Steps the worlds: the one with the most awake bodies on the loop thread,
    /// where its backend may spread it over the workers, and the others at the
    /// same time, each in one index of a parallel job, where none may spread.
    /// </summary>
    [HotPath]
    private void StepWorlds(float delta)
    {
        int count = _worlds.Count;
        if (count == 0)
        {
            return;
        }

        var spread = new PhysicsStepSettings(delta, Substeps, VelocityIterations, MaySpread: true);
        if (count == 1)
        {
            _worlds[0].Backend.Step(spread);
            return;
        }

        int busiest = 0;
        for (int i = 1; i < count; i++)
        {
            if (_worlds[i].Awake > _worlds[busiest].Awake)
            {
                busiest = i;
            }
        }

        if (_stepping.Length < count - 1)
        {
            Array.Resize(ref _stepping, count * 2);
            Array.Resize(ref _stepFailures, count * 2);
        }

        int others = 0;
        for (int i = 0; i < count; i++)
        {
            if (i != busiest)
            {
                _stepping[others++] = _worlds[i].Backend;
            }
        }

        // Each index steps a world that nothing else touches until the job completes, below.
        var job = new StepJob(_stepping, _stepFailures, new PhysicsStepSettings(delta, Substeps, VelocityIterations, MaySpread: false));
        Core.Jobs.JobHandle handle = _context.Jobs.ScheduleFor(job, others, 1);
        try
        {
            _worlds[busiest].Backend.Step(spread);
        }
        finally
        {
            handle.Complete();
            Array.Clear(_stepping, 0, others);
        }

        for (int i = 0; i < others; i++)
        {
            if (_stepFailures[i] is { } failure)
            {
                Array.Clear(_stepFailures, 0, others);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            }
        }
    }

    /// <summary>Tells whether a region holds a position, its box grown by a margin in blocks.</summary>
    [HotPath]
    private static bool Holds(RegionEntry region, in WorldPosition position, float margin) =>
        Within(position.ChunkX, position.Local.X, region.Minimum.X, region.Maximum.X, margin)
        && Within(position.ChunkY, position.Local.Y, region.Minimum.Y, region.Maximum.Y, margin)
        && Within(position.ChunkZ, position.Local.Z, region.Minimum.Z, region.Maximum.Z, margin);

    [HotPath]
    private static bool Within(int chunk, float local, int minimum, int maximum, float margin)
    {
        double offset = (((long)chunk - minimum) * (double)VoxelCoordinates.ChunkSize) + local;
        double size = (((long)maximum - minimum) + 1) * (double)VoxelCoordinates.ChunkSize;
        return offset >= -margin && offset < size + margin;
    }

    /// <summary>Gets the distance from a position to the box of a region, in blocks; 0 inside.</summary>
    [HotPath]
    private static double DistanceTo(RegionEntry region, in WorldPosition position)
    {
        double x = Outside(position.ChunkX, position.Local.X, region.Minimum.X, region.Maximum.X);
        double y = Outside(position.ChunkY, position.Local.Y, region.Minimum.Y, region.Maximum.Y);
        double z = Outside(position.ChunkZ, position.Local.Z, region.Minimum.Z, region.Maximum.Z);
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }

    [HotPath]
    private static double Outside(int chunk, float local, int minimum, int maximum)
    {
        double offset = (((long)chunk - minimum) * (double)VoxelCoordinates.ChunkSize) + local;
        double size = (((long)maximum - minimum) + 1) * (double)VoxelCoordinates.ChunkSize;
        return offset < 0 ? -offset : offset > size ? offset - size : 0;
    }

    /// <summary>Gets how far apart two boxes are, in chunks along the axis where they are farthest: 0 or less when they share a chunk, 1 when they touch.</summary>
    [HotPath]
    internal static long Distance(ChunkCoordinate aMinimum, ChunkCoordinate aMaximum, ChunkCoordinate bMinimum, ChunkCoordinate bMaximum)
    {
        long x = Math.Max((long)bMinimum.X - aMaximum.X, (long)aMinimum.X - bMaximum.X);
        long y = Math.Max((long)bMinimum.Y - aMaximum.Y, (long)aMinimum.Y - bMaximum.Y);
        long z = Math.Max((long)bMinimum.Z - aMaximum.Z, (long)aMinimum.Z - bMaximum.Z);
        return Math.Max(x, Math.Max(y, z));
    }

    /// <summary>Gets the number of chunks two boxes share.</summary>
    [HotPath]
    private static long Overlap(ChunkCoordinate aMinimum, ChunkCoordinate aMaximum, ChunkCoordinate bMinimum, ChunkCoordinate bMaximum)
    {
        long x = Math.Min(aMaximum.X, bMaximum.X) - (long)Math.Max(aMinimum.X, bMinimum.X) + 1;
        long y = Math.Min(aMaximum.Y, bMaximum.Y) - (long)Math.Max(aMinimum.Y, bMinimum.Y) + 1;
        long z = Math.Min(aMaximum.Z, bMaximum.Z) - (long)Math.Max(aMinimum.Z, bMinimum.Z) + 1;
        return x <= 0 || y <= 0 || z <= 0 ? 0 : x * y * z;
    }

    [HotPath]
    private static ChunkCoordinate Center(ChunkCoordinate minimum, ChunkCoordinate maximum) =>
        new((int)(((long)minimum.X + maximum.X) >> 1), (int)(((long)minimum.Y + maximum.Y) >> 1), (int)(((long)minimum.Z + maximum.Z) >> 1));

    [HotPath]
    private static ChunkCoordinate Min(ChunkCoordinate a, ChunkCoordinate b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    [HotPath]
    private static ChunkCoordinate Max(ChunkCoordinate a, ChunkCoordinate b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>Joins two sets of a union-find, the larger root under the smaller, so that the root of a set is its first member.</summary>
    [HotPath]
    private static void Join(int[] parents, int a, int b)
    {
        int rootA = Find(parents, a);
        int rootB = Find(parents, b);
        if (rootA < rootB)
        {
            parents[rootB] = rootA;
        }
        else if (rootB < rootA)
        {
            parents[rootA] = rootB;
        }
    }

    [HotPath]
    private static int Find(int[] parents, int index)
    {
        while (parents[index] != index)
        {
            parents[index] = parents[parents[index]];
            index = parents[index];
        }

        return index;
    }

    /// <summary>The box of chunks of one active area ticket, in the world.</summary>
    [HotPath]
    private readonly record struct TicketBox(DimensionId Dimension, ChunkCoordinate Minimum, ChunkCoordinate Maximum);

    /// <summary>An island of ticket boxes, and the region that continues it.</summary>
    private struct ClusterBox
    {
        public DimensionId Dimension;
        public ChunkCoordinate Minimum;
        public ChunkCoordinate Maximum;
        public RegionEntry? Region;
    }

    /// <summary>Steps the worlds of <see cref="_stepping"/>, one per index; a failure is kept for the loop thread.</summary>
    [HotPath]
    private readonly struct StepJob(PhysicsBackendWorld[] worlds, Exception?[] failures, PhysicsStepSettings settings) : Core.Jobs.IJobFor
    {
        [HotPath]
        public void Execute(int index)
        {
            try
            {
                worlds[index].Step(settings);
            }
            catch (Exception exception)
            {
                failures[index] = exception;
            }
        }
    }

    /// <summary>A region: an island of active chunks of a dimension, its box, its origin, and its world once a body enters it.</summary>
    private sealed class RegionEntry(int id, DimensionId dimension, ChunkCoordinate origin)
    {
        public int Id { get; } = id;

        public DimensionId Dimension { get; } = dimension;

        public ChunkCoordinate Origin { get; set; } = origin;

        public ChunkCoordinate Minimum { get; set; }

        public ChunkCoordinate Maximum { get; set; }

        public WorldEntry? World { get; set; }

        public bool Alive { get; set; } = true;

        /// <summary>Gets or sets whether an island continues the region, while the regions are derived.</summary>
        public bool Claimed { get; set; }

        /// <summary>Gets or sets the number of islands that overlap the region, while the regions are derived.</summary>
        public int Overlaps { get; set; }
    }
}
