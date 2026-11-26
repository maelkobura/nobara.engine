// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>Rays, sweeps, and overlaps through nPhysics, against the voxels of the terrain and the bodies of BEPUphysics.</summary>
[Collection(Serial.Name)]
public sealed class QueryTests
{
    private static readonly ChunkArea Small = new(1, 1);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_000_000_000, -1_000_000_000)]
    [InlineData(-1_000_000_000, 1_000_000_000)]
    public void ARayGivesTheBlockBeforeTheBodyAndTheBodyBeforeTheBlock(double x, double z)
    {
        using var scene = new VoxelScene(Small, WorldPosition.FromBlocks(x + 16, 16, z + 16));
        scene.Place((scene.Block(20, 1, 16), scene.State("stone")));
        BodyHandle crate = scene.Add(scene.At(24.5, 1.5, 16.5), scene.Physics.CreateBox(Vector3.One), mass: 1);
        scene.Physics.SetKind(crate, BodyKind.Static);
        scene.Tick();
        IPhysicsSystem physics = scene.Physics;

        Assert.True(physics.RayCast(DimensionId.Default, scene.At(16.5, 1.5, 16.5), Vector3.UnitX, 20, out PhysicsHit block));
        Assert.Equal(PhysicsHitKind.Block, block.Kind);
        Assert.Equal(scene.Block(20, 1, 16), block.Block);
        Assert.Equal(scene.Ground, block.Grid);
        Assert.Equal(scene.State("stone"), block.State);
        Assert.Equal(3.5f, block.Distance, 1e-3f);
        Assert.Equal(-Vector3.UnitX, block.Normal);
        Assert.True(block.Point.RelativeTo(scene.At(20, 1.5, 16.5)).Length() < 1e-3f);

        Assert.True(physics.RayCast(DimensionId.Default, scene.At(30.5, 1.5, 16.5), -Vector3.UnitX, 20, out PhysicsHit body));
        Assert.Equal(PhysicsHitKind.Body, body.Kind);
        Assert.Equal(crate, body.Body);
        Assert.Equal(5.5f, body.Distance, 1e-3f);
        Assert.Equal(Vector3.UnitX, body.Normal);

        // Without the layer of the voxels, the ray goes through the block to the body.
        Assert.True(physics.RayCast(DimensionId.Default, scene.At(16.5, 1.5, 16.5), Vector3.UnitX, 20, out PhysicsHit through, new QueryFilter(~CollisionFilter.VoxelLayer)));
        Assert.Equal(crate, through.Body);
        Assert.Equal(7.5f, through.Distance, 1e-3f);

        var hits = new PhysicsHit[4];
        int count = physics.RayCastAll(DimensionId.Default, scene.At(16.5, 1.5, 16.5), Vector3.UnitX, 20, hits);
        Assert.True(count >= 2);
        Assert.Equal(PhysicsHitKind.Block, hits[0].Kind);
        Assert.Equal(PhysicsHitKind.Body, hits[1].Kind);

        // Down to the ground, from inside the crate, which it ignores.
        Assert.True(physics.RayCast(DimensionId.Default, scene.At(24.5, 1.5, 16.5), -Vector3.UnitY, 5, out PhysicsHit ground, new QueryFilter(uint.MaxValue, crate)));
        Assert.Equal(scene.Block(24, -1, 16), ground.Block);
        Assert.Equal(1.5f, ground.Distance, 1e-3f);
        Assert.Equal(Vector3.UnitY, ground.Normal);
    }

    [Fact]
    public void ARayMeetsTheBodiesOfEveryRegionItCrosses()
    {
        using var scene = new BepuScene(h => h.ActiveArea = null);
        scene.Probe.Activate(WorldPosition.FromBlocks(16, 16, 16), Small);    // chunks -1 to 1
        scene.Probe.Activate(WorldPosition.FromBlocks(112, 16, 16), Small);   // chunks 2 to 4: the border is at x = 64
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        BodyHandle here = scene.Add(WorldPosition.FromBlocks(40.5, 16.5, 16.5), crate, BodyKind.Static);
        BodyHandle there = scene.Add(WorldPosition.FromBlocks(100.5, 16.5, 16.5), crate, BodyKind.Static);
        scene.Tick();
        Assert.Equal(2, scene.Physics.Worlds.Count);

        var hits = new PhysicsHit[4];
        int count = scene.Physics.RayCastAll(DimensionId.Default, WorldPosition.FromBlocks(10.5, 16.5, 16.5), Vector3.UnitX, 120, hits);

        Assert.Equal(2, count);
        Assert.Equal(here, hits[0].Body);
        Assert.Equal(29.5f, hits[0].Distance, 1e-3f);
        Assert.Equal(there, hits[1].Body);
        Assert.Equal(89.5f, hits[1].Distance, 1e-3f);
        Assert.True(hits[1].Point.RelativeTo(WorldPosition.FromBlocks(100, 16.5, 16.5)).Length() < 1e-3f);

        Assert.True(scene.Physics.RayCast(DimensionId.Default, WorldPosition.FromBlocks(120.5, 16.5, 16.5), -Vector3.UnitX, 120, out PhysicsHit back));
        Assert.Equal(there, back.Body);
        Assert.True(scene.Physics.RayCast(DimensionId.Default, WorldPosition.FromBlocks(80.5, 16.5, 16.5), -Vector3.UnitX, 120, out back));
        Assert.Equal(here, back.Body);   // from the other region
    }

    [Fact]
    public void ACapsuleSweptAgainstAStepStopsAtItsFaceAndPassesAboveIt()
    {
        using var scene = new VoxelScene(Small);
        scene.Place((scene.Block(20, 0, 16), scene.State("stone")));
        scene.Tick();
        IPhysicsSystem physics = scene.Physics;
        QueryShape capsule = QueryShape.Capsule(0.3f, 1.2f);   // 1.8 blocks high

        Assert.True(physics.Sweep(DimensionId.Default, capsule, scene.At(16.5, 0.91, 16.5), Quaternion.Identity, Vector3.UnitX, 10, out PhysicsHit hit));
        Assert.Equal(PhysicsHitKind.Block, hit.Kind);
        Assert.Equal(scene.Block(20, 0, 16), hit.Block);
        Assert.Equal(3.2f, hit.Distance, 0.01f);   // to x = 19.7, its radius from the face
        Assert.True(Vector3.Dot(hit.Normal, -Vector3.UnitX) > 0.99f, $"normal {hit.Normal}");
        Assert.InRange(hit.Point.RelativeTo(scene.At(20, 0, 16.5)).X, -0.01f, 0.01f);

        // One block up, it passes over the step.
        Assert.False(physics.Sweep(DimensionId.Default, capsule, scene.At(16.5, 1.95, 16.5), Quaternion.Identity, Vector3.UnitX, 10, out _));

        // A box and a sphere that fall onto the ground meet its top.
        Assert.True(physics.Sweep(DimensionId.Default, QueryShape.Box(new Vector3(1, 0.5f, 1)), scene.At(10.5, 5, 10.5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), -Vector3.UnitY, 10, out hit));
        Assert.Equal(4.75f, hit.Distance, 0.01f);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.99f, $"normal {hit.Normal}");
        Assert.Equal(-1, hit.Block.Y);
        Assert.True(physics.Sweep(DimensionId.Default, QueryShape.Sphere(0.5f), scene.At(10.5, 5, 10.5), Quaternion.Identity, -Vector3.UnitY, 10, out hit));
        Assert.Equal(4.5f, hit.Distance, 0.01f);

        // A shape that starts inside a block meets it at once.
        Assert.True(physics.Sweep(DimensionId.Default, QueryShape.Sphere(0.2f), scene.At(20.5, 0.5, 16.5), Quaternion.Identity, Vector3.UnitX, 4, out hit));
        Assert.Equal(0, hit.Distance);
        Assert.Equal(-Vector3.UnitX, hit.Normal);
    }

    [Fact]
    public void AnOverlapKeepsTheLayersOfItsFilter()
    {
        using var scene = new VoxelScene(Small);
        PhysicsShape ball = scene.Physics.CreateSphere(0.4f);
        BodyHandle first = scene.Add(scene.At(16, 0.5, 16), ball, filter: new CollisionFilter(1 << 1, uint.MaxValue));
        BodyHandle second = scene.Add(scene.At(17, 0.5, 16), ball, filter: new CollisionFilter(1 << 2, uint.MaxValue));
        scene.Physics.SetKind(first, BodyKind.Static);
        scene.Physics.SetKind(second, BodyKind.Static);
        scene.Tick();
        IPhysicsSystem physics = scene.Physics;
        var hits = new PhysicsHit[16];
        QueryShape box = QueryShape.Box(new Vector3(3, 1, 1));

        int count = physics.Overlap(DimensionId.Default, box, scene.At(16.5, 0.4, 16), Quaternion.Identity, hits, new QueryFilter(1 << 1));
        Assert.Equal(1, count);
        Assert.Equal(first, hits[0].Body);

        count = physics.Overlap(DimensionId.Default, box, scene.At(16.5, 0.4, 16), Quaternion.Identity, hits, new QueryFilter((1 << 2) | CollisionFilter.VoxelLayer));
        Assert.Contains(hits[..count], h => h.Kind == PhysicsHitKind.Body && h.Body == second);
        Assert.DoesNotContain(hits[..count], h => h.Body == first);
        Assert.Contains(hits[..count], h => h.Kind == PhysicsHitKind.Block && h.Block == scene.Block(16, -1, 15));   // the box reaches 0.1 block into the ground

        count = physics.Overlap(DimensionId.Default, QueryShape.Sphere(0.3f), scene.At(10, 2, 10), Quaternion.Identity, hits);
        Assert.Equal(0, count);
    }

    [Fact]
    public void AQueryReportsAChunkThatIsNotLoadedAndNothingPastIt()
    {
        using var scene = new VoxelScene(Small);   // the chunks from x = -32 to 64 are loaded
        BodyHandle crate = scene.Add(scene.At(50.5, 5.5, 16.5), scene.Physics.CreateBox(Vector3.One));
        scene.Physics.SetKind(crate, BodyKind.Static);
        scene.Tick();
        IPhysicsSystem physics = scene.Physics;

        Assert.True(physics.RayCast(DimensionId.Default, scene.At(52, 5.5, 16.5), Vector3.UnitX, 40, out PhysicsHit hit));
        Assert.Equal(PhysicsHitKind.NotLoaded, hit.Kind);
        Assert.Equal(12f, hit.Distance, 1e-3f);
        Assert.Equal(new ChunkCoordinate(2, 0, 0), hit.Chunk);

        var hits = new PhysicsHit[8];
        int count = physics.RayCastAll(DimensionId.Default, scene.At(40, 5.5, 16.5), Vector3.UnitX, 40, hits);
        Assert.Equal(2, count);
        Assert.Equal(crate, hits[0].Body);
        Assert.Equal(PhysicsHitKind.NotLoaded, hits[1].Kind);
        Assert.Equal(24f, hits[1].Distance, 1e-3f);

        Assert.True(physics.Sweep(DimensionId.Default, QueryShape.Sphere(0.5f), scene.At(55, 5.5, 16.5), Quaternion.Identity, Vector3.UnitX, 40, out hit));
        Assert.Equal(PhysicsHitKind.NotLoaded, hit.Kind);
        Assert.Equal(8.5f, hit.Distance, 0.01f);

        count = physics.Overlap(DimensionId.Default, QueryShape.Box(new Vector3(2, 2, 2)), scene.At(64, 5, 16), Quaternion.Identity, hits);
        Assert.Contains(hits[..count], h => h.Kind == PhysicsHitKind.NotLoaded && h.Chunk == new ChunkCoordinate(2, 0, 0));
    }

    [Fact]
    public void ABatchOfAThousandRaysAllocatesNothing()
    {
        using var scene = new VoxelScene(Small);
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        for (int i = 0; i < 20; i++)
        {
            scene.Add(scene.At(4.5 + i, 0.5, 8.5 + (i % 3)), crate);
        }

        scene.Tick(30);
        var random = new Random(3);
        var queries = new RayQuery[1000];
        for (int i = 0; i < queries.Length; i++)
        {
            queries[i] = new RayQuery(scene.At(random.NextDouble() * 40 - 4, 4 + random.NextDouble() * 4, random.NextDouble() * 40 - 4), new Vector3(random.NextSingle() - 0.5f, -1, random.NextSingle() - 0.5f), 20);
        }

        var hits = new PhysicsHit[queries.Length];
        scene.Physics.RayCast(DimensionId.Default, queries, hits);   // the tables grow to the batch
        scene.Physics.RayCast(DimensionId.Default, queries, hits);

        // The batch runs on the workers too, so the count is the whole process's: the quietest of three windows counts.
        long allocated = long.MaxValue;
        for (int window = 0; window < 3 && allocated != 0; window++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < 5; i++)
            {
                scene.Physics.RayCast(DimensionId.Default, queries, hits);
            }

            allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        Assert.Equal(0, allocated);
        Assert.All(hits, h => Assert.NotEqual(PhysicsHitKind.None, h.Kind));   // every ray goes down to the ground or a crate
        for (int i = 0; i < queries.Length; i += 97)
        {
            Assert.True(scene.Physics.RayCast(DimensionId.Default, queries[i].Origin, queries[i].Direction, queries[i].MaxDistance, out PhysicsHit alone));
            Assert.Equal(alone.Kind, hits[i].Kind);
            Assert.Equal(alone.Distance, hits[i].Distance, 1e-4f);
            Assert.Equal(alone.Body, hits[i].Body);
            Assert.Equal(alone.Block, hits[i].Block);
        }

        scene.Tick();
        Assert.True(scene.Physics.Statistics.Queries >= 5000);
    }

    [Fact]
    public void ABatchOfSweepsGivesWhatEachSweepGivesAlone()
    {
        using var scene = new VoxelScene(Small);
        scene.Place((scene.Block(20, 0, 16), scene.State("stone")), (scene.Block(12, 0, 10), scene.State("slab")));
        scene.Tick();
        var queries = new SweepQuery[200];
        var random = new Random(9);
        for (int i = 0; i < queries.Length; i++)
        {
            QueryShape shape = (i % 3) switch
            {
                0 => QueryShape.Sphere(0.3f),
                1 => QueryShape.Capsule(0.3f, 1.2f),
                _ => QueryShape.Box(new Vector3(0.6f, 1.8f, 0.6f)),
            };
            queries[i] = new SweepQuery(shape, scene.At(8 + (random.NextDouble() * 16), 1 + (random.NextDouble() * 2), 8 + (random.NextDouble() * 16)), Quaternion.Identity, new Vector3(random.NextSingle() - 0.5f, -0.5f, random.NextSingle() - 0.5f), 12);
        }

        var hits = new PhysicsHit[queries.Length];
        scene.Physics.Sweep(DimensionId.Default, queries, hits);

        for (int i = 0; i < queries.Length; i++)
        {
            SweepQuery query = queries[i];
            bool found = scene.Physics.Sweep(DimensionId.Default, query.Shape, query.Origin, query.Rotation, query.Direction, query.MaxDistance, out PhysicsHit alone);
            Assert.Equal(found ? alone.Kind : PhysicsHitKind.None, hits[i].Kind);
            Assert.Equal(alone.Distance, hits[i].Distance, 1e-4f);
            Assert.Equal(alone.Block, hits[i].Block);
        }
    }
}
