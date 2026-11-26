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

/// <summary>The sample of the guide "Make a character walk" does what the guide says.</summary>
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

    private static Vector3 Feet(VoxelScene scene, EntityWorld world, EntityId farmer) =>
        world.Read(farmer, scene.Probe.Entities.WorldTransform).Position.RelativeTo(new WorldPosition(scene.Center.ChunkX, 0, scene.Center.ChunkZ, Vector3.Zero));
}
