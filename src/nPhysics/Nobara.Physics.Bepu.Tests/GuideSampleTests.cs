// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Samples;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>The samples of the guides "Make a character walk" and "Query the physics" do what the guides say.</summary>
[Collection(Serial.Name)]
public sealed class GuideSampleTests
{
    [Fact]
    public void AFarmerWalksRunsJumpsAndSpectates()
    {
        FarmerMovement? movement = null;
        using var scene = new VoxelScene(new ChunkArea(1, 1), configure: harness =>
        {
            Action<BepuProbe>? terrain = harness.Configure;
            harness.Configure = probe =>
            {
                terrain?.Invoke(probe);
                movement = new FarmerMovement(probe.Context);
            };
        });
        var damage = new FallDamage();
        scene.Physics.SubscribeStepEnd(scene.Probe.Context, damage);
        EntityWorld world = scene.Probe.World;
        Assert.True(scene.Probe.Entities.TryGetComponent(out ComponentType<FarmerInput> input));
        EntityId farmer = movement!.Spawn(world, scene.At(16.5, 0, 16.5));
        scene.Tick();
        Assert.True(movement.IsOnGround(farmer));

        world.Set(farmer, input, new FarmerInput { Move = new Vector2(0, 1) });   // forward, facing -Z
        scene.Tick(30);
        Vector3 walked = Feet(scene, world, farmer);
        Assert.InRange(walked.Z, 16.5 - FarmerMovement.WalkSpeed - 0.1, 16.5 - FarmerMovement.WalkSpeed + 0.2);

        world.Set(farmer, input, new FarmerInput { Move = new Vector2(1, 0), Yaw = MathF.PI / 2, Run = true, Jump = true });   // facing -X, to the right: -Z
        scene.Tick(2);
        Assert.False(movement.IsOnGround(farmer));

        world.Set(farmer, input, default);
        movement.Spectate(farmer);
        WorldTransform left = world.Read(farmer, scene.Probe.Entities.WorldTransform);
        scene.Tick(10);
        Assert.Equal(left, world.Read(farmer, scene.Probe.Entities.WorldTransform));   // it stays in the air: its module moves it now

        movement.Resume(farmer);
        scene.Tick(40);
        Assert.True(movement.IsOnGround(farmer));
        Assert.Equal(0, damage.Damage);
    }

    [Fact]
    public void FarmersPickCratesScarecrowsScareCrowsAndCratesBreakWhenTheyFall()
    {
        CratePhysics? crates = null;
        using var scene = new VoxelScene(new ChunkArea(1, 1), configure: harness =>
        {
            Action<BepuProbe>? terrain = harness.Configure;
            harness.Configure = probe =>
            {
                terrain?.Invoke(probe);
                crates = new CratePhysics(probe.Context);
            };
        });
        scene.Tick();
        BodyHandle low = crates!.AddCrate(scene.Probe.CreateEntity(WorldTransform.At(scene.At(10.5, 1.5, 10.5))));
        BodyHandle high = crates.AddCrate(scene.Probe.CreateEntity(WorldTransform.At(scene.At(14.5, 6, 10.5))));
        EntityId crowEntity = scene.Probe.CreateEntity(WorldTransform.At(scene.At(20.5, 1, 20.5)));
        BodyHandle crow = scene.Physics.AddBody(DimensionId.Default, crowEntity, new BodyDescription { Shape = scene.Physics.CreateSphere(0.2f), Filter = new CollisionFilter(CratePhysics.CrowLayer, uint.MaxValue) });
        scene.Tick(60);

        Assert.Equal([high], crates.Broken);   // a fall of 1.1 blocks does not break a crate; one of 5.6 does

        Assert.True(crates.TryPick(scene.At(10.5, 0.5, 14), -Vector3.UnitZ, out PhysicsHit picked));   // a farmer who kneels
        Assert.Equal(low, picked.Body);
        Assert.True(crates.TryPick(scene.At(4.5, 1.6, 4.5), -Vector3.UnitY, out picked));
        Assert.Equal(PhysicsHitKind.Block, picked.Kind);

        Assert.Equal(1, crates.Scare(scene.At(21, 1, 21)));
        Assert.Equal(0, crates.Scare(scene.At(2, 1, 2)));
        scene.Tick();
        Assert.True(scene.State(crow).LinearVelocity.Y > 0);

        ReadOnlySpan<PhysicsHit> wetted = crates.Water([scene.At(8, 3, 8), scene.At(12, 3, 12)]);
        Assert.Equal(2, wetted.Length);
        Assert.Equal(3f, wetted[0].Distance, 1e-3f);
        Assert.Equal(-1, wetted[1].Block.Y);
    }

    private static Vector3 Feet(VoxelScene scene, EntityWorld world, EntityId farmer) =>
        world.Read(farmer, scene.Probe.Entities.WorldTransform).Position.RelativeTo(new WorldPosition(scene.Center.ChunkX, 0, scene.Center.ChunkZ, Vector3.Zero));
}
