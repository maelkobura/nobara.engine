// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Tests;

/// <summary>How nPhysics asks the worlds of the backend for queries and reads their contacts, with a test backend.</summary>
public sealed class QueryAndContactTests : IDisposable
{
    private static readonly ChunkArea Small = new(1, 1);
    private static readonly QueryFilter Bodies = new(~CollisionFilter.VoxelLayer);
    private readonly PhysicsHarness _harness = new() { ActiveArea = null };

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000_000)]
    [InlineData(-1_000_000_000)]
    public void AQueryReachesTheWorldsOfItsWayInTheirOwnFrames(int offset)
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(offset, 0), Small);
        probe.Activate(Chunk(offset, 100), Small);
        BodyHandle near = probe.CreateBody(Blocks(offset, 10), BodyKind.Static);
        BodyHandle far = probe.CreateBody(Blocks(offset, 3210), BodyKind.Static);
        _harness.Tick(engine);
        TestPhysicsWorld here = WorldOf(offset, 0);
        TestPhysicsWorld there = WorldOf(offset, 100);
        here.QueryHits.Add(new BackendQueryHit(near.Index, 5, new Vector3(10, 16, 16), -Vector3.UnitX));
        there.QueryHits.Add(new BackendQueryHit(far.Index, 3195, new Vector3(0, 16, 16), -Vector3.UnitX));

        Assert.True(probe.Physics.RayCast(DimensionId.Default, Blocks(offset, 5), Vector3.UnitX, 10, out PhysicsHit hit, Bodies));
        Assert.Equal(near, hit.Body);
        Assert.Single(here.Queries);
        Assert.Empty(there.Queries);   // too far for a short ray
        Assert.True(here.LargestCoordinate < 64, $"The world got a coordinate of {here.LargestCoordinate}.");

        var hits = new PhysicsHit[4];
        int count = probe.Physics.RayCastAll(DimensionId.Default, Blocks(offset, 5), Vector3.UnitX, 4000, hits, Bodies);
        Assert.Equal(2, count);
        Assert.Equal(near, hits[0].Body);
        Assert.Equal(far, hits[1].Body);
        Assert.Equal(Blocks(offset, 3200).ChunkX, hits[1].Point.ChunkX);   // from the frame of its world back to the world
        Assert.Single(there.Queries);
    }

    [Fact]
    public void AQueryPassesItsMaskAndItsIgnoredBodyToTheWorld()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0, 0), Small);
        BodyHandle body = probe.CreateBody(Blocks(0, 10), BodyKind.Static);
        _harness.Tick(engine);
        TestPhysicsWorld world = WorldOf(0, 0);
        var hits = new PhysicsHit[2];

        probe.Physics.Overlap(DimensionId.Default, QueryShape.Box(Vector3.One), Blocks(0, 10), Quaternion.Identity, hits, new QueryFilter(1 << 3, body));
        probe.Physics.Sweep(DimensionId.Default, QueryShape.Capsule(0.2f, 1), Blocks(0, 10), Quaternion.Identity, Vector3.UnitY, 2, out _, new QueryFilter(1 << 4));

        Assert.Equal(1u << 3, world.Queries[0].Mask);
        Assert.Equal(body.Index, world.Queries[0].IgnoredSlot);
        Assert.Equal(ShapeKind.Box, world.Queries[0].Shape.Kind);
        Assert.Equal(-1, world.Queries[1].IgnoredSlot);
        Assert.Equal(ShapeKind.Capsule, world.Queries[1].Shape.Kind);
        Assert.Equal(Vector3.UnitY, world.Queries[1].Direction);
        Assert.Throws<ArgumentException>(() => probe.Physics.RayCast(DimensionId.Default, Blocks(0, 10), Vector3.Zero, 2, out _));
        Assert.Throws<ArgumentException>(() => probe.Physics.Sweep(DimensionId.Default, default, Blocks(0, 10), Quaternion.Identity, Vector3.UnitX, 2, out _));
        Assert.Throws<ArgumentException>(() => probe.Physics.RayCast(DimensionId.Default, [new RayQuery(Blocks(0, 10), Vector3.UnitX, 2)], []));
    }

    [Fact]
    public void ContactsBeginOnceGoOnWhileTheBodySleepsAndEndAfterTheGraceSteps()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0, 0), Small);
        BodyHandle crate = Reporting(probe, Blocks(0, 10), new ContactReporting(2));
        BodyHandle floor = probe.CreateBody(Blocks(0, 12), BodyKind.Static);
        var log = new ContactLog();
        probe.Physics.SubscribeStepEnd(probe.Context, log);
        _harness.Tick(engine);
        TestPhysicsWorld world = WorldOf(0, 0);
        var touch = new BackendContact(crate.Index, floor.Index, -1, new Vector3(11, 16, 16), Vector3.UnitY, 0.001f, 3);

        world.ReportedContacts.Add(touch);
        world.ReportedContacts.Add(touch with { Depth = 0.002f, Speed = 1 });   // the same pair from a ghost: one event, at its fastest
        _harness.Tick(engine);
        ContactEvent begun = Assert.Single(log.Begun);
        Assert.Equal((crate, floor), (begun.Body, begun.Other));
        Assert.Equal(3f, begun.Speed);
        Assert.Equal(3f, begun.Impulse, 1e-4f);   // a mass of 1 against a static body
        Assert.Single(log.Impacts);

        world.ReportedContacts[0] = touch with { Speed = 1.5f };
        world.ReportedContacts.RemoveAt(1);
        _harness.Tick(engine);
        Assert.Single(log.Begun);
        Assert.Single(log.Impacts);   // below the impact speed of the crate

        world.ReportedContacts.Clear();
        world.Sleep(BackendIdOf(world, crate));
        _harness.Tick(engine, 10);
        Assert.Empty(log.Ended);   // the backend no longer looks at a body that sleeps

        world.Wake(BackendIdOf(world, crate));
        _harness.Tick(engine, ContactEvents.GraceSteps - 1);
        Assert.Empty(log.Ended);
        _harness.Tick(engine);
        ContactEvent ended = Assert.Single(log.Ended);
        Assert.Equal(crate, ended.Body);
    }

    [Fact]
    public void AContactBeginsOnlyCloseEnoughAndEndsWithItsBody()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0, 0), Small);
        BodyHandle crate = Reporting(probe, Blocks(0, 10), ContactReporting.Default);
        BodyHandle other = Reporting(probe, Blocks(0, 12), ContactReporting.Default);
        var log = new ContactLog();
        probe.Physics.SubscribeStepEnd(probe.Context, log);
        _harness.Tick(engine);
        TestPhysicsWorld world = WorldOf(0, 0);

        // Reported by the backend 3 centimeters apart: near, not touching.
        world.ReportedContacts.Add(new BackendContact(other.Index, crate.Index, -1, new Vector3(11, 16, 16), Vector3.UnitX, -0.03f, 0));
        _harness.Tick(engine);
        Assert.Empty(log.Begun);

        world.ReportedContacts[0] = world.ReportedContacts[0] with { Depth = -0.005f };
        _harness.Tick(engine);
        ContactEvent begun = Assert.Single(log.Begun);
        Assert.Equal((crate, other), (begun.Body, begun.Other));   // both report: the lower slot first
        Assert.Equal(-Vector3.UnitX, begun.Normal);                 // turned toward the body of the event

        world.ReportedContacts.Clear();
        probe.Physics.RemoveBody(other);
        _harness.Tick(engine);
        ContactEvent ended = Assert.Single(log.Ended);
        Assert.False(probe.Physics.IsAlive(ended.Other));
    }

    [Fact]
    public void ABodyThatStopsReportingDropsItsContactsWithoutEvents()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        probe.Activate(Chunk(0, 0), Small);
        BodyHandle crate = Reporting(probe, Blocks(0, 10), ContactReporting.Default);
        BodyHandle floor = probe.CreateBody(Blocks(0, 12), BodyKind.Static);
        var log = new ContactLog();
        probe.Physics.SubscribeStepEnd(probe.Context, log);
        _harness.Tick(engine);
        TestPhysicsWorld world = WorldOf(0, 0);
        world.ReportedContacts.Add(new BackendContact(crate.Index, floor.Index, -1, new Vector3(11, 16, 16), Vector3.UnitY, 0, 0));
        _harness.Tick(engine);
        Assert.Single(log.Begun);
        Assert.True((probe.World.Read(probe.Physics.GetState(crate).Entity, PhysicsBodyOf(probe)).Flags & BodyFlags.ReportsContacts) != 0);

        probe.Physics.SetContactReporting(crate, null);
        world.ReportedContacts.Clear();
        _harness.Tick(engine, 5);

        Assert.Empty(log.Ended);
        Assert.Contains($"reports {BackendIdOf(world, crate)} False", world.Calls);
        Assert.Equal(0, probe.Physics.Statistics.ContactEvents);
    }

    private static BodyHandle Reporting(PhysicsProbe probe, in WorldPosition position, ContactReporting reporting)
    {
        EntityId entity = probe.CreateEntity(WorldTransform.At(position));
        return probe.Physics.AddBody(DimensionId.Default, entity, new BodyDescription { Shape = probe.Physics.CreateSphere(0.5f), Contacts = reporting });
    }

    private static ComponentType<PhysicsBody> PhysicsBodyOf(PhysicsProbe probe) =>
        probe.Entities.TryGetComponent(out ComponentType<PhysicsBody> body) ? body : throw new InvalidOperationException("No body component.");

    private static int BackendIdOf(TestPhysicsWorld world, BodyHandle body)
    {
        for (int i = 0; i < world.Bodies.Count; i++)
        {
            if (!world.Bodies[i].Removed && world.Bodies[i].Description.Slot == body.Index)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"The world holds no {body}.");
    }

    private TestPhysicsWorld WorldOf(int offset, int chunkX) => _harness.Backend.Worlds.Single(w => !w.IsDisposed && w.Options.Origin.X == (offset / 32) + chunkX);

    private static WorldPosition Chunk(int offset, int x) => Blocks(offset, (x * 32) + 16);

    private static WorldPosition Blocks(int offset, double x) => WorldPosition.FromBlocks(offset + x, 16, 16);

    private sealed class ContactLog : IPhysicsStepObserver
    {
        public List<ContactEvent> Begun { get; } = [];

        public List<ContactEvent> Ended { get; } = [];

        public List<ContactEvent> Impacts { get; } = [];

        public void OnStepEnd(in PhysicsStepEnd step)
        {
            Begun.AddRange(step.ContactsBegun);
            Ended.AddRange(step.ContactsEnded);
            Impacts.AddRange(step.Impacts);
        }
    }
}
