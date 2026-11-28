// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Tests;

/// <summary>The regions: worlds around local origins that follow the active tickets, transfers, ghosts, merges, splits, and recentering.</summary>
public sealed class RegionTests : IDisposable
{
    private const float Delta = 1f / 30;
    private static readonly ChunkArea Small = new(1, 1);
    private readonly PhysicsHarness _harness = new() { ActiveArea = null };

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SimulatesTwoDistantIslandsInTwoWorldsAroundTheirOwnOrigins()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0), Small);
        probe.Activate(Chunk(100), Small);
        BodyHandle near = probe.CreateBody(WorldPosition.FromBlocks(10, 16, 16), BodyKind.Kinematic);
        BodyHandle far = probe.CreateBody(WorldPosition.FromBlocks(3210, 16, 16), BodyKind.Kinematic);
        probe.Physics.SetVelocity(near, Vector3.UnitX, Vector3.Zero);
        probe.Physics.SetVelocity(far, Vector3.UnitX, Vector3.Zero);

        _harness.Tick(engine, 2);

        PhysicsRegionInfo[] regions = [.. probe.Physics.Regions];
        Assert.Equal(2, regions.Length);
        Assert.Equal(new ChunkCoordinate(0, 0, 0), regions[0].Origin);
        Assert.Equal(new ChunkCoordinate(100, 0, 0), regions[1].Origin);
        Assert.All(regions, r => Assert.Equal(1, r.Bodies));
        Assert.Equal(2, _harness.Backend.Worlds.Count);
        Assert.All(_harness.Backend.Worlds, w => Assert.Equal(2, w.StepCount));
        Assert.All(_harness.Backend.Worlds, w => Assert.True(w.LargestCoordinate < 32));

        // One world steps on the loop thread and may spread; the other in a job, where it may not.
        Assert.Equal(1, _harness.Backend.Worlds.Count(w => w.Steps[^1].MaySpread));
        Assert.Equal(10 + (2 * Delta), X(probe.TransformOf(near).Position), 4);
        Assert.Equal(3210 + (2 * Delta), X(probe.TransformOf(far).Position), 4);
    }

    [Fact]
    public void MovesABodyAcrossABorderWithoutAJolt()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0), Small);   // chunks -1 to 1
        probe.Activate(Chunk(3), Small);   // chunks 2 to 4: the border is at x = 64
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(60, 16, 16), BodyKind.Kinematic);
        probe.CreateBody(WorldPosition.FromBlocks(120, 16, 16), BodyKind.Static);   // the other region has a world
        probe.Physics.SetVelocity(body, new Vector3(6, 0, 0), Vector3.Zero);

        double previous = 60;
        for (int tick = 0; tick < 40; tick++)
        {
            _harness.Tick(engine);
            double x = X(probe.TransformOf(body).Position);
            Assert.Equal(6 * Delta, x - previous, 4);
            previous = x;
            Assert.Equal(new Vector3(6, 0, 0), probe.Physics.GetState(body).LinearVelocity);
        }

        Assert.Equal(1, probe.Physics.Statistics.Transfers);
        PhysicsRegionInfo[] regions = [.. probe.Physics.Regions];
        Assert.Equal(0, regions[0].Bodies);
        Assert.False(regions[0].HasWorld);   // its world went with its last body
        Assert.Equal(2, regions[1].Bodies);
    }

    [Fact]
    public void ShowsBodiesNearABorderAsGhostsInTheRegionNextToIt()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0), Small);
        probe.Activate(Chunk(3), Small);
        BodyHandle wall = probe.CreateBody(WorldPosition.FromBlocks(63, 16, 16), BodyKind.Static);
        BodyHandle lift = probe.CreateBody(WorldPosition.FromBlocks(62, 20, 16), BodyKind.Kinematic);
        probe.CreateBody(WorldPosition.FromBlocks(120, 16, 16), BodyKind.Static);
        probe.Physics.SetVelocity(lift, new Vector3(0, 0, 3), Vector3.Zero);

        _harness.Tick(engine, 3);

        Assert.Equal(2, probe.Physics.Statistics.Ghosts);
        TestPhysicsWorld other = _harness.Backend.Worlds.Single(w => w.Options.Origin == new ChunkCoordinate(3, 0, 0) && !w.IsDisposed);
        TestBody[] ghosts = [.. other.Bodies.Where(b => !b.Removed && b.Description.Slot != 2)];
        TestBody wallGhost = Assert.Single(ghosts, g => g.Kind == BodyKind.Static);
        Assert.Equal(new Vector3(63 - 96, 16, 16), wallGhost.Position);
        TestBody liftGhost = Assert.Single(ghosts, g => g.Kind == BodyKind.Kinematic);
        Vector3 lift3 = probe.TransformOf(lift).Position.RelativeTo(WorldPosition.FromBlocks(96, 0, 0));
        Assert.True(Vector3.Distance(lift3, liftGhost.Position) < 1e-4f, $"The ghost is at {liftGhost.Position}, the body at {lift3}.");
        Assert.Equal(new Vector3(0, 0, 3), liftGhost.Linear);

        probe.Physics.Teleport(lift, WorldPosition.FromBlocks(0, 20, 16), Quaternion.Identity);
        probe.Physics.RemoveBody(wall);
        _harness.Tick(engine);

        Assert.Equal(0, probe.Physics.Statistics.Ghosts);
        Assert.True(liftGhost.Removed);
        Assert.True(wallGhost.Removed);
    }

    [Fact]
    public void MergesAndSplitsWithHysteresisWithoutLosingOrDuplicatingBodies()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0), Small);                     // chunks -1 to 1
        AreaTicket moving = probe.Activate(Chunk(6), new ChunkArea(2, 1));   // chunks 4 to 8
        var bodies = new List<BodyHandle>();
        for (int i = 0; i < 10; i++)
        {
            bodies.Add(probe.CreateBody(WorldPosition.FromBlocks(2 + (i * 2), 16, 16), i % 2 == 0 ? BodyKind.Static : BodyKind.Kinematic));
            bodies.Add(probe.CreateBody(WorldPosition.FromBlocks(134 + (i * 2), 16, 16), i % 2 == 0 ? BodyKind.Static : BodyKind.Kinematic));
        }

        _harness.Tick(engine);
        Assert.Equal(2, probe.Physics.Regions.Count);

        for (int cycle = 0; cycle < 100; cycle++)
        {
            moving.Move(Chunk(3));   // chunks 1 to 5: shares chunk 1, the regions merge
            _harness.Tick(engine);
            AssertBodies(probe, bodies, regions: 1);

            moving.Move(Chunk(5));   // chunks 3 to 7: one empty chunk between, they stay merged
            _harness.Tick(engine);
            AssertBodies(probe, bodies, regions: 1);

            moving.Move(Chunk(6));   // chunks 4 to 8: two empty chunks, they split
            _harness.Tick(engine);
            AssertBodies(probe, bodies, regions: 2);
        }

        PhysicsStatistics statistics = probe.Physics.Statistics;
        Assert.Equal(100, statistics.Merges);
        Assert.Equal(100, statistics.Splits);
        Assert.Equal(0, statistics.FrozenBodies);

        engine.MainContext.Stop();
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Exception is WorldStateLeakException);
    }

    [Fact]
    public void RecentersARegionThatFollowsABodyOverTenThousandBlocks()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        WorldPosition start = WorldPosition.FromBlocks(16, 16, 16);
        AreaTicket ticket = probe.Activate(start, Small);
        BodyHandle traveler = probe.CreateBody(start, BodyKind.Kinematic);
        probe.Physics.SetVelocity(traveler, new Vector3(300, 0, 0), Vector3.Zero);

        double previous = 16;
        for (int tick = 0; tick < 1000; tick++)
        {
            _harness.Tick(engine);
            WorldPosition position = probe.TransformOf(traveler).Position;
            double x = X(position);
            Assert.Equal(10, x - previous, 3);
            previous = x;
            ticket.Move(position);
        }

        Assert.Equal(16 + 10_000, previous, 1);
        Assert.Equal(new Vector3(300, 0, 0), probe.Physics.GetState(traveler).LinearVelocity);
        Assert.True(probe.Physics.Statistics.Recenters >= 30, $"{probe.Physics.Statistics.Recenters} recenters.");
        Assert.All(_harness.Backend.Worlds, w => Assert.True(w.LargestCoordinate < 400, $"A world received {w.LargestCoordinate}."));
        Assert.Single(probe.Physics.Worlds);
    }

    [Fact]
    public void FreezesABodyOutOfEveryRegionAndKeepsItExact()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        WorldPosition far = WorldPosition.FromBlocks(1_000_000_005.25, 16.5, -1_000_000_010.75);
        BodyHandle body = probe.CreateBody(far, BodyKind.Kinematic);
        probe.Physics.SetVelocity(body, new Vector3(0, 0, 3), Vector3.Zero);

        _harness.Tick(engine, 5);

        Assert.Equal(far, probe.TransformOf(body).Position);
        Assert.Equal(1, probe.Physics.Statistics.FrozenBodies);
        Assert.True(probe.Entities.TryGetComponent(out ComponentType<PhysicsBody> component));
        Assert.Equal(BodyFlags.Frozen, probe.World.Read(probe.Physics.GetState(body).Entity, component).Flags & BodyFlags.Frozen);
        Assert.Empty(_harness.Backend.Worlds);

        probe.Activate(far, Small);
        _harness.Tick(engine, 2);

        Vector3 moved = probe.TransformOf(body).Position.RelativeTo(far);
        Assert.True(Vector3.Distance(new Vector3(0, 0, 6 * Delta), moved) < 1e-4f, $"Moved {moved}.");
        Assert.Equal(0, probe.Physics.Statistics.FrozenBodies);
        Assert.True(Assert.Single(_harness.Backend.Worlds).LargestCoordinate < 64);
    }

    [Fact]
    public void KeepsJoinedBodiesInTheWorldOfTheFirstBodyOfTheirGroup()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0), Small);
        probe.Activate(Chunk(3), Small);
        BodyHandle frame = probe.CreateBody(WorldPosition.FromBlocks(62, 16, 16), BodyKind.Static);
        BodyHandle door = probe.CreateBody(WorldPosition.FromBlocks(67, 16, 16), BodyKind.Kinematic);
        Assert.Equal(2, probe.Physics.Worlds.Count);

        JointHandle hinge = probe.Physics.CreateJoint(frame, door, new JointDescription(JointKind.Hinge, Vector3.Zero, Vector3.Zero, Vector3.UnitY, Vector3.UnitY));
        _harness.Tick(engine);

        PhysicsWorldInfo world = Assert.Single(probe.Physics.Worlds);
        Assert.Equal(new ChunkCoordinate(0, 0, 0), world.Origin);
        Assert.Equal((2, 1), (world.Bodies, world.Joints));
        Assert.True(probe.Physics.IsAlive(hinge));

        probe.Physics.DestroyJoint(hinge);
        _harness.Tick(engine);
        Assert.Equal(2, probe.Physics.Worlds.Count);   // free again, the door goes back to its region
    }

    [Fact]
    public void AllocatesNothingPerTickWithRegionsAndGhostsOnceStable()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        _harness.Backend.Records = false;
        probe.Activate(Chunk(0), Small);
        probe.Activate(Chunk(3), Small);
        probe.Activate(Chunk(50), Small);
        var lifts = new BodyHandle[20];
        for (int i = 0; i < lifts.Length; i++)
        {
            lifts[i] = probe.CreateBody(WorldPosition.FromBlocks(62 + (i % 4), 16 + i, 10), BodyKind.Kinematic);   // on both sides of the border
            probe.CreateBody(WorldPosition.FromBlocks(1600 + i, 16, 16), BodyKind.Static);
        }

        for (int tick = 0; tick < 10; tick++)
        {
            Commands(probe, lifts, tick);
            _harness.Tick(engine);
        }

        Assert.Equal(3, probe.Physics.Worlds.Count);
        Assert.True(probe.Physics.Statistics.Ghosts > 0);

        // The runtime may allocate now and then on this thread, such as while it compiles methods again: a round without allocation must come.
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int tick = 10 + (30 * round); tick < 40 + (30 * round); tick++)
            {
                Commands(probe, lifts, tick);
                _harness.Tick(engine);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    private static void Commands(PhysicsProbe probe, BodyHandle[] lifts, int tick)
    {
        foreach (BodyHandle lift in lifts)
        {
            probe.Physics.SetVelocity(lift, new Vector3(0, 0, (tick % 2 == 0) ? 3 : -3), Vector3.Zero);
        }
    }

    private void AssertBodies(PhysicsProbe probe, List<BodyHandle> bodies, int regions)
    {
        Assert.Equal(regions, probe.Physics.Regions.Count);
        Assert.Equal(bodies.Count, probe.Physics.Regions.Sum(r => r.Bodies));
        Assert.All(bodies, b => Assert.True(probe.Physics.IsAlive(b)));

        // In the backend: every body once, in a world not disposed, besides the ghosts.
        TestBody[] live = [.. _harness.Backend.Worlds.Where(w => !w.IsDisposed).SelectMany(w => w.Bodies).Where(b => !b.Removed)];
        Assert.Equal(bodies.Count + probe.Physics.Statistics.Ghosts, live.Length);
        Assert.Equal(bodies.Count, live.Select(b => b.Description.Slot).Distinct().Count());
    }

    /// <summary>Gets the center of a chunk on the X axis, at the height of the bodies.</summary>
    private static WorldPosition Chunk(int x) => WorldPosition.FromBlocks((x * 32) + 16, 16, 16);

    private static double X(in WorldPosition position) => (position.ChunkX * 32.0) + position.Local.X;
}
