// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>The contact events of the bodies that report their contacts, against the voxels and against other bodies.</summary>
[Collection(Serial.Name)]
public sealed class ContactTests
{
    private static readonly ChunkArea Small = new(1, 1);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_000_000_000, -1_000_000_000)]
    public void ABoxPutOnTheGroundBeginsAContactThatEndsWhenItIsLifted(double x, double z)
    {
        using var scene = new VoxelScene(Small, WorldPosition.FromBlocks(x + 16, 16, z + 16));
        var events = new ContactLog(scene);
        BodyHandle box = Reporting(scene, scene.At(16.5, 0.5, 16.5), ContactReporting.Default);

        scene.Tick(5);
        ContactEvent begun = Assert.Single(events.Begun);
        Assert.Equal(box, begun.Body);
        Assert.True(begun.IsBlock);
        Assert.Equal(scene.Ground, begun.Grid);
        Assert.Equal(-1, begun.Block.Y);
        Assert.InRange(begun.Block.X, scene.Block(15, 0, 0).X, scene.Block(17, 0, 0).X);   // under the deepest contact, a corner of the box
        Assert.Equal(scene.State("stone"), begun.State);
        Assert.True(Vector3.Dot(begun.Normal, Vector3.UnitY) > 0.99f, $"normal {begun.Normal}");
        Assert.True(begun.Point.RelativeTo(scene.At(16.5, 0, 16.5)).Length() < 0.8f);
        Assert.Empty(events.Impacts);

        scene.Tick(90);   // it rests, then sleeps: the contact goes on
        Assert.True(scene.State(box).IsAsleep);
        Assert.Single(events.Begun);
        Assert.Empty(events.Ended);

        scene.Physics.Teleport(box, scene.At(16.5, 5, 16.5), Quaternion.Identity);
        scene.Tick(ContactEvents.GraceSteps - 1);
        Assert.Empty(events.Ended);
        scene.Tick();
        ContactEvent ended = Assert.Single(events.Ended);
        Assert.Equal(box, ended.Body);
        Assert.Equal(scene.Ground, ended.Grid);
    }

    [Fact]
    public void AFallReportsAnImpactAboveTheSpeedOfItsBodyOnly()
    {
        using var scene = new VoxelScene(Small);
        var events = new ContactLog(scene);
        BodyHandle fast = Reporting(scene, scene.At(10.5, 3.5, 10.5), ContactReporting.Default, mass: 4);
        BodyHandle hard = Reporting(scene, scene.At(14.5, 3.5, 10.5), new ContactReporting(ImpactSpeed: 20));
        BodyHandle gentle = Reporting(scene, scene.At(18.5, 0.51, 10.5), ContactReporting.Default);

        scene.Tick(60);

        ContactEvent impact = Assert.Single(events.Impacts);
        Assert.Equal(fast, impact.Body);
        float expected = MathF.Sqrt(2 * 9.81f * 3);   // a fall of 3 blocks
        Assert.InRange(impact.Speed, expected - 1, expected + 1);
        Assert.InRange(impact.Impulse, 4 * (expected - 1), 4 * (expected + 1));   // a mass of 4 against the ground, no bounce
        Assert.Contains(events.Begun, e => e.Body == hard);
        Assert.Contains(events.Begun, e => e.Body == gentle);
        Assert.Contains(events.Begun, e => e.Body == fast);
    }

    [Fact]
    public void ABodyThatDoesNotReportItsContactsGivesNoEvents()
    {
        using var scene = new VoxelScene(Small);
        var events = new ContactLog(scene);
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        BodyHandle silent = scene.Add(scene.At(10.5, 3.5, 10.5), crate);
        BodyHandle other = scene.Add(scene.At(14.5, 0.5, 10.5), crate);

        scene.Tick(60);
        Assert.Empty(events.All);

        scene.Physics.SetContactReporting(silent, ContactReporting.Default);
        scene.Physics.Teleport(silent, scene.At(14.5, 2, 10.5), Quaternion.Identity);   // falls onto the other crate
        scene.Tick(30);
        ContactEvent onCrate = Assert.Single(events.Begun, e => e.Other == other);
        Assert.Equal(silent, onCrate.Body);
        Assert.True(Vector3.Dot(onCrate.Normal, Vector3.UnitY) > 0.99f, $"normal {onCrate.Normal}");
        Assert.Contains(events.Impacts, e => e.Other == other);
        Assert.DoesNotContain(events.All, e => e.Body == other);

        scene.Physics.SetContactReporting(silent, null);
        scene.Physics.Teleport(silent, scene.At(20.5, 3.5, 10.5), Quaternion.Identity);
        events.Clear();
        scene.Tick(60);
        Assert.Empty(events.All);
    }

    [Fact]
    public void TwoBodiesThatReportTheirContactsGiveOneEventPerPair()
    {
        using var scene = new VoxelScene(Small);
        var events = new ContactLog(scene);
        BodyHandle below = Reporting(scene, scene.At(10.5, 0.5, 10.5), ContactReporting.Default);
        BodyHandle above = Reporting(scene, scene.At(10.5, 2.5, 10.5), ContactReporting.Default);

        scene.Tick(60);

        // The box above may bounce off once before it rests: each begin of the pair is one event, never two.
        ContactEvent pair = events.Begun.First(e => e.Grid is null);
        Assert.All(events.Begun.Where(e => e.Grid is null), e => Assert.Equal((below, above), (e.Body, e.Other)));   // the lower slot first
        Assert.True(Vector3.Dot(pair.Normal, -Vector3.UnitY) > 0.99f, $"normal {pair.Normal}");   // from the other part toward the body below
        Assert.InRange(pair.Speed, 3.5f, 5f);   // a fall of 1 block
        Assert.Single(events.Begun, e => e.Body == below && e.Grid is not null);
        Assert.DoesNotContain(events.Begun, e => e.Body == above);   // never on the ground
    }

    [Fact]
    public void AContactEndsWhenItsBodyIsRemoved()
    {
        using var scene = new VoxelScene(Small);
        var events = new ContactLog(scene);
        BodyHandle box = Reporting(scene, scene.At(16.5, 0.5, 16.5), ContactReporting.Default);
        scene.Tick(5);
        Assert.Single(events.Begun);

        scene.Physics.RemoveBody(box);
        scene.Tick();

        ContactEvent ended = Assert.Single(events.Ended);
        Assert.Equal(box, ended.Body);
        Assert.False(scene.Physics.IsAlive(ended.Body));
    }

    private static BodyHandle Reporting(VoxelScene scene, WorldPosition position, ContactReporting reporting, float mass = 1)
    {
        EntityId entity = scene.Probe.CreateEntity(WorldTransform.At(position));
        return scene.Physics.AddBody(DimensionId.Default, entity, new BodyDescription
        {
            Shape = scene.Physics.CreateBox(Vector3.One),
            Mass = mass,
            Contacts = reporting,
        });
    }

    /// <summary>Keeps the contact events of the steps, on the loop thread.</summary>
    private sealed class ContactLog : IPhysicsStepObserver
    {
        public ContactLog(VoxelScene scene) => scene.Physics.SubscribeStepEnd(scene.Probe.Context, this);

        public List<ContactEvent> Begun { get; } = [];

        public List<ContactEvent> Ended { get; } = [];

        public List<ContactEvent> Impacts { get; } = [];

        public IEnumerable<ContactEvent> All => [.. Begun, .. Ended, .. Impacts];

        public void Clear()
        {
            Begun.Clear();
            Ended.Clear();
            Impacts.Clear();
        }

        public void OnStepEnd(in PhysicsStepEnd step)
        {
            Begun.AddRange(step.ContactsBegun);
            Ended.AddRange(step.ContactsEnded);
            Impacts.AddRange(step.Impacts);
        }
    }
}
