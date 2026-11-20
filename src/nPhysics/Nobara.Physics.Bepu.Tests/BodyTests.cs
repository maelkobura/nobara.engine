// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>Bodies simulated by BEPUphysics: falls, bounces, sleep, stacks, friction, layers, and continuous detection.</summary>
public sealed class BodyTests
{
    [Fact]
    public void RegistersTheBackendBepu()
    {
        using var scene = new BepuScene();

        Assert.Equal("bepu", scene.Physics.BackendName);
    }

    [Fact]
    public void ABoxFallsOnTheInjectedGroundAndFallsAsleep()
    {
        using var scene = new BepuScene();
        BodyHandle box = scene.Add(WorldPosition.FromBlocks(16, 6, 16), scene.Physics.CreateBox(Vector3.One));
        scene.Ground();

        scene.Tick(30);
        Assert.True(scene.Local(box).Y < 6);   // it falls
        scene.Tick(150);

        BodyState state = scene.State(box);
        Assert.Equal(1.5f, scene.Local(box).Y, 0.02f);   // on top of the layer of blocks, from 0 to 1
        Assert.True(state.IsAsleep);
    }

    [Fact]
    public void AnImpulseWakesASleepingBody()
    {
        using var scene = new BepuScene();
        BodyHandle box = scene.Add(WorldPosition.FromBlocks(16, 1.5, 16), scene.Physics.CreateBox(Vector3.One));
        scene.Ground();
        scene.Tick(90);
        Assert.True(scene.State(box).IsAsleep);

        scene.Physics.ApplyImpulse(box, new Vector3(0, 6, 0));
        scene.Tick();

        BodyState state = scene.State(box);
        Assert.False(state.IsAsleep);
        Assert.True(state.LinearVelocity.Y > 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.2f)]
    [InlineData(0.5f)]
    [InlineData(0.8f)]
    [InlineData(1f)]
    public void ABallBouncesWithTheShareOfSpeedOfItsRestitution(float restitution)
    {
        using var scene = new BepuScene();
        BodyHandle ball = scene.Add(WorldPosition.FromBlocks(16, 6, 16), scene.Physics.CreateSphere(0.5f), material: new PhysicsMaterial(0.6f, restitution));
        scene.Ground();

        float impact = 0;
        float rebound = 0;
        for (int i = 0; i < 60; i++)
        {
            scene.Tick();
            float velocity = scene.State(ball).LinearVelocity.Y;
            impact = MathF.Min(impact, velocity);
            if (impact < -5)
            {
                rebound = MathF.Max(rebound, velocity);
            }
        }

        // A fall of 4.5 blocks: about 9.4 blocks per second at the impact.
        Assert.InRange(impact, -10.5f, -8.5f);
        Assert.InRange(rebound / -impact, restitution - 0.02f, restitution + 0.02f);
    }

    [Fact]
    public void AColumnOfBoxesStandsAndFallsAsleep()
    {
        using var scene = new BepuScene();
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        var boxes = new BodyHandle[6];
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = scene.Add(WorldPosition.FromBlocks(16.5, 1.5 + i, 16.5), crate);
        }

        scene.Ground();
        scene.Tick(300);

        for (int i = 0; i < boxes.Length; i++)
        {
            Vector3 position = scene.Local(boxes[i]);
            Assert.Equal(1.5f + i, position.Y, 0.05f);
            Assert.Equal(16.5f, position.X, 0.05f);
            Assert.Equal(16.5f, position.Z, 0.05f);
            Assert.True(scene.State(boxes[i]).IsAsleep);
        }
    }

    [Fact]
    public void FrictionStopsASlidingBoxAndIceDoesNot()
    {
        using var scene = new BepuScene();
        // A floor of one box: the joints between the cells of the voxels catch a sliding box, which the voxel layer deals with.
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        scene.Add(WorldPosition.FromBlocks(16, 0.5, 16), scene.Physics.CreateBox(new Vector3(32, 1, 32)), BodyKind.Static);
        BodyHandle rough = scene.Add(WorldPosition.FromBlocks(4, 1.5, 4), crate, material: new PhysicsMaterial(1f, 0f));
        BodyHandle ice = scene.Add(WorldPosition.FromBlocks(4, 1.5, 12), crate, material: new PhysicsMaterial(0f, 0f));
        scene.Tick();
        scene.Physics.SetVelocity(rough, new Vector3(4, 0, 0), Vector3.Zero);
        scene.Physics.SetVelocity(ice, new Vector3(4, 0, 0), Vector3.Zero);

        scene.Tick(30);

        // Friction combines as the geometric mean: 1 with the ground of 0.6 is 0.77, which stops it within a second.
        Assert.Equal(0f, scene.State(rough).LinearVelocity.X, 0.05f);
        Assert.True(scene.Local(rough).X < 6);
        Assert.Equal(4f, scene.State(ice).LinearVelocity.X, 0.05f);
        Assert.True(scene.Local(ice).X > 7.5f);
    }

    [Fact]
    public void BodiesWhoseLayersDoNotMeetPassThroughEachOther()
    {
        using var scene = new BepuScene();
        PhysicsShape slab = scene.Physics.CreateBox(new Vector3(4, 0.5f, 4));
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        scene.Add(WorldPosition.FromBlocks(8, 5, 8), slab, BodyKind.Static, filter: new CollisionFilter(Layers: 1 << 1, Mask: uint.MaxValue));
        BodyHandle stopped = scene.Add(WorldPosition.FromBlocks(8, 8, 8), crate);
        BodyHandle ghost = scene.Add(WorldPosition.FromBlocks(9.5, 8, 9.5), crate, filter: new CollisionFilter(Layers: 1, Mask: ~(1u << 1)));
        scene.Ground();

        scene.Tick(90);

        Assert.Equal(5.75f, scene.Local(stopped).Y, 0.05f);   // on the slab
        Assert.Equal(1.5f, scene.Local(ghost).Y, 0.05f);      // through the slab, on the ground
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContinuousDetectionStopsAFastSphereAtAThinWall(bool continuous)
    {
        using var scene = new BepuScene();
        PhysicsShape wall = scene.Physics.CreateBox(new Vector3(0.1f, 8, 8));
        scene.Add(WorldPosition.FromBlocks(20, 8, 16), wall, BodyKind.Static);
        BodyHandle bullet = scene.Add(WorldPosition.FromBlocks(4, 8, 16), scene.Physics.CreateSphere(0.1f), mass: 0.05f, continuous: continuous);
        scene.Tick();
        scene.Physics.SetVelocity(bullet, new Vector3(300, 0, 0), Vector3.Zero);   // 10 blocks per step

        scene.Tick(10);

        // Without it, the contact is found but too soft to stop the sphere in time.
        Assert.Equal(continuous, scene.Local(bullet).X < 20);
    }

    [Theory]
    [InlineData(1_000_000_000.0)]
    [InlineData(-1_000_000_000.0)]
    public void ABodyFarFromTheOriginMovesExactlyAsNearIt(double offset)
    {
        // Both chunks are whole: the same scene in the frame of its world, a billion blocks apart.
        double far = Math.Round(offset / VoxelCoordinates.ChunkSize) * VoxelCoordinates.ChunkSize;
        WorldPosition near = Fall(0);
        WorldPosition distant = Fall(far);

        Assert.Equal(WorldPosition.FromBlocks(far + 16.25, 1.5, far + 16.75).ChunkX, distant.ChunkX);
        Assert.Equal(near.Local, distant.Local);
        Assert.Equal(1.5f, distant.Local.Y, 0.02f);
    }

    private static WorldPosition Fall(double offset)
    {
        using var scene = new BepuScene();
        BodyHandle box = scene.Add(WorldPosition.FromBlocks(offset + 16.25, 4, offset + 16.75), scene.Physics.CreateBox(Vector3.One), rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f));
        scene.Ground();
        scene.Tick(120);
        return scene.State(box).Transform.Position;
    }

    [Fact]
    public void ABlockPlacedInTheChunkIsSeenAtTheNextStep()
    {
        using var scene = new BepuScene();
        BodyHandle box = scene.Add(WorldPosition.FromBlocks(8.5, 6, 8.5), scene.Physics.CreateBox(new Vector3(0.8f)));
        Chunk chunk = BepuScene.Solid(0);
        scene.World.AddVoxelChunk(new VoxelChunkCollider(null!, chunk, Vector3.Zero, Quaternion.Identity));
        chunk.Palette.Set(VoxelCoordinates.ToIndex(new LocalBlockCoordinate(8, 2, 8)), 1);

        scene.Tick(90);

        Assert.Equal(3.4f, scene.Local(box).Y, 0.05f);   // on the block from 2 to 3
        chunk.Dispose();
    }
}
