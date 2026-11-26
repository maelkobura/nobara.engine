// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>The members of a world that change a body after it is added, the chunks of voxels, and the rays.</summary>
public sealed class WorldTests
{
    [Fact]
    public void ARayOfTheWorldGoesThroughItsChunksOfVoxels()
    {
        using var scene = new BepuScene();
        BodyHandle ball = scene.Add(WorldPosition.FromBlocks(8.5, 20, 8.5), scene.Physics.CreateSphere(0.5f), BodyKind.Static);
        scene.Ground(height: 32);   // nPhysics tests the voxels itself: the backend leaves them out of its queries
        var hits = new BackendQueryHit[4];

        int count = scene.World.RayCast(Ray(new Vector3(8.5f, 40, 8.5f), -Vector3.UnitY, 60), 0, hits);

        Assert.Equal(1, count);
        Assert.Equal(ball.Index, hits[0].Slot);
        Assert.Equal(Vector3.UnitY, hits[0].Normal);
    }

    [Fact]
    public void ARayGivesTheClosestBodiesThatItsMaskLetsThrough()
    {
        using var scene = new BepuScene();
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        BodyHandle near = scene.Add(WorldPosition.FromBlocks(5, 10, 10), crate, BodyKind.Static, filter: new CollisionFilter(1 << 2, uint.MaxValue));
        BodyHandle far = scene.Add(WorldPosition.FromBlocks(9, 10, 10), crate, BodyKind.Static);
        BepuWorld world = scene.World;
        var hits = new BackendQueryHit[1];

        Assert.Equal(1, world.RayCast(Ray(new Vector3(0, 10, 10), Vector3.UnitX, 20), 0, hits));
        Assert.Equal(4.5f, hits[0].Distance, 1e-3f);
        Assert.Equal(-Vector3.UnitX, hits[0].Normal);
        Assert.Equal(near.Index, hits[0].Slot);
        Assert.Equal(1, world.RayCast(Ray(new Vector3(0, 10, 10), Vector3.UnitX, 20, mask: ~(1u << 2)), 0, hits));
        Assert.Equal(8.5f, hits[0].Distance, 1e-3f);
        Assert.Equal(far.Index, hits[0].Slot);
        Assert.Equal(0, world.RayCast(Ray(new Vector3(0, 10, 10), Vector3.UnitX, 4), 0, hits));
        Assert.Equal(0, world.RayCast(Ray(new Vector3(0, 10, 10), Vector3.UnitX, 20, ignored: near.Index, mask: 1u << 2), 0, hits));

        var both = new BackendQueryHit[4];
        Assert.Equal(2, world.RayCast(Ray(new Vector3(0, 10, 10), Vector3.UnitX, 20), 0, both));
    }

    [Fact]
    public void ASweepAndAnOverlapMeetTheBodiesOfTheWorld()
    {
        using var scene = new BepuScene();
        BodyHandle wall = scene.Add(WorldPosition.FromBlocks(10, 10, 10), scene.Physics.CreateBox(new Vector3(1, 4, 4)), BodyKind.Static);
        BepuWorld world = scene.World;
        var hits = new BackendQueryHit[2];
        var sweep = new BackendQuery { Origin = new Vector3(0, 10, 10), Rotation = Quaternion.Identity, Direction = Vector3.UnitX, MaxDistance = 20, Shape = QueryShape.Sphere(0.5f), Mask = uint.MaxValue, IgnoredSlot = -1 };

        Assert.Equal(1, world.Sweep(sweep, 0, hits));
        Assert.Equal(wall.Index, hits[0].Slot);
        Assert.Equal(9f, hits[0].Distance, 0.01f);   // the face at x = 9.5, less the radius
        Assert.True(Vector3.Dot(hits[0].Normal, -Vector3.UnitX) > 0.99f, $"normal {hits[0].Normal}");

        var inside = sweep with { Origin = new Vector3(10.3f, 10, 10) };
        Assert.Equal(1, world.Overlap(inside, 1, hits));
        Assert.Equal(0, world.Overlap(sweep, 1, hits));
    }

    private static BackendQuery Ray(Vector3 origin, Vector3 direction, float length, uint mask = uint.MaxValue, int ignored = -1) =>
        new() { Origin = origin, Rotation = Quaternion.Identity, Direction = direction, MaxDistance = length, Mask = mask, IgnoredSlot = ignored };

    [Fact]
    public void ABodyFallsOnceItsChunkIsRemoved()
    {
        using var scene = new BepuScene();
        BodyHandle box = scene.Add(WorldPosition.FromBlocks(16, 1.5, 16), scene.Physics.CreateBox(Vector3.One));
        int chunk = scene.Ground();
        scene.Tick(30);
        Assert.Equal(1.5f, scene.Local(box).Y, 0.02f);
        Assert.Equal(1, scene.World.ChunkCount);

        scene.World.RemoveVoxelChunk(chunk);
        scene.Physics.Wake(box);
        scene.Tick(30);

        Assert.Equal(0, scene.World.ChunkCount);
        Assert.True(scene.Height(box) < 0);
        Assert.Throws<ArgumentException>(() => scene.World.RemoveVoxelChunk(chunk));
    }

    [Fact]
    public void WakingAnAreaWakesTheSleepingBodiesInItOnly()
    {
        using var scene = new BepuScene();
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        BodyHandle inside = scene.Add(WorldPosition.FromBlocks(4, 1.5, 4), crate);
        BodyHandle outside = scene.Add(WorldPosition.FromBlocks(24, 1.5, 24), crate);
        scene.Ground();
        scene.Tick(90);
        Assert.True(scene.State(inside).IsAsleep && scene.State(outside).IsAsleep);

        scene.Physics.WakeArea(DimensionId.Default, WorldBox.Around(WorldPosition.FromBlocks(4, 2, 4), new Vector3(2)));
        scene.Tick();

        Assert.False(scene.State(inside).IsAsleep);
        Assert.True(scene.State(outside).IsAsleep);
    }

    [Fact]
    public void ABodyChangesKindBetweenStaticAndDynamicWhereItIs()
    {
        using var scene = new BepuScene();
        BodyHandle box = scene.Add(WorldPosition.FromBlocks(16, 8, 16), scene.Physics.CreateBox(Vector3.One), BodyKind.Static);
        scene.Ground();
        scene.Tick(10);
        Assert.Equal(8f, scene.Local(box).Y, 1e-4f);   // a static body stays

        scene.Physics.SetKind(box, BodyKind.Dynamic);
        scene.Tick(60);
        Assert.Equal(1.5f, scene.Local(box).Y, 0.02f);   // it fell

        scene.Physics.SetKind(box, BodyKind.Static);
        scene.Physics.Teleport(box, WorldPosition.FromBlocks(16, 6, 16), Quaternion.Identity);
        scene.Tick(30);
        Assert.Equal(6f, scene.Local(box).Y, 1e-4f);    // static again, where it was put
    }

    [Fact]
    public void ATeleportedBodyAndATeleportedStaticCollideWhereTheyArrive()
    {
        using var scene = new BepuScene();
        BodyHandle slab = scene.Add(WorldPosition.FromBlocks(6, 3, 6), scene.Physics.CreateBox(new Vector3(4, 1, 4)), BodyKind.Static);
        BodyHandle crate = scene.Add(WorldPosition.FromBlocks(24, 1.5, 24), scene.Physics.CreateBox(Vector3.One));
        scene.Ground();
        scene.Tick(10);

        scene.Physics.Teleport(slab, WorldPosition.FromBlocks(24, 4, 24), Quaternion.Identity);
        scene.Physics.Teleport(crate, WorldPosition.FromBlocks(24, 7, 24), Quaternion.Identity);
        scene.Tick(60);

        Assert.Equal(4f, scene.Local(slab).Y, 1e-4f);
        Assert.Equal(5f, scene.Local(crate).Y, 0.02f);   // on the slab at its new place
    }

    [Fact]
    public void ABodyThatChangesShapeRestsOnItsNewShape()
    {
        using var scene = new BepuScene();
        PhysicsShape small = scene.Physics.CreateBox(Vector3.One);
        PhysicsShape tall = scene.Physics.CreateBox(new Vector3(1, 3, 1));
        BodyHandle box = scene.Add(WorldPosition.FromBlocks(16, 2.5, 16), tall);
        scene.Ground();
        scene.Tick(90);
        Assert.Equal(2.5f, scene.Local(box).Y, 0.02f);   // half of 3 above the ground
        Assert.True(scene.State(box).IsAsleep);

        scene.Physics.SetShape(box, small, Vector3.Zero);
        scene.Tick(60);

        Assert.Equal(1.5f, scene.Local(box).Y, 0.02f);   // woken, it falls on its new shape
        Assert.Equal(1, scene.World.ShapeCount);         // the tall box is freed
    }

    [Fact]
    public void ContinuousDetectionTurnedOnLaterStopsAFastSphere()
    {
        using var scene = new BepuScene();
        scene.Add(WorldPosition.FromBlocks(20, 8, 16), scene.Physics.CreateBox(new Vector3(0.1f, 8, 8)), BodyKind.Static);
        BodyHandle bullet = scene.Add(WorldPosition.FromBlocks(4, 8, 16), scene.Physics.CreateSphere(0.1f), mass: 0.05f);
        scene.Tick();

        scene.Physics.SetContinuous(bullet, true);
        scene.Physics.SetVelocity(bullet, new Vector3(300, 0, 0), Vector3.Zero);
        scene.Tick(10);

        Assert.True(scene.Local(bullet).X < 20);
    }
}
