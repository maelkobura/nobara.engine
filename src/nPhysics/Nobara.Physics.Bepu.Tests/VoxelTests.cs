// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>Bodies against the voxels of the terrain and of grids, through nPhysics and its voxel layer.</summary>
[Collection(Serial.Name)]
public sealed class VoxelTests(ITestOutputHelper output)
{
    private static readonly ChunkArea Small = new(1, 1);

    [Fact]
    public void ABoxDroppedOnFlatGroundComesToRestWithoutJitter()
    {
        using var scene = new VoxelScene(Small);
        BodyHandle box = scene.Add(scene.At(16.5, 3, 16.5), scene.Physics.CreateBox(Vector3.One));

        scene.Tick(90);
        double low = double.MaxValue;
        double high = double.MinValue;
        for (int tick = 0; tick < 30; tick++)
        {
            scene.Tick();
            low = Math.Min(low, scene.Height(box));
            high = Math.Max(high, scene.Height(box));
        }

        Assert.InRange(scene.Height(box), 0.48, 0.52);
        Assert.True(high - low < 1e-3, $"The box moved by {high - low} blocks at rest.");
        Assert.True(scene.State(box).IsAsleep);
        Assert.True(scene.Physics.Statistics.VoxelChunks > 0);
    }

    [Fact]
    public void ABoxSlidesOverTheJointsOfTheCellsWithoutCatching()
    {
        using var scene = new VoxelScene(Small);
        BodyHandle box = scene.Add(scene.At(27.5, 0.6, 16.5), scene.Physics.CreateBox(Vector3.One));
        scene.Tick(30);   // settled on the ground
        scene.Physics.SetVelocity(box, new Vector3(8, 0, 0), Vector3.Zero);   // across the joints of the cells, and from one chunk to the next at x = 32

        float upright = 1;
        double low = double.MaxValue;
        double high = double.MinValue;
        for (int tick = 0; tick < 60; tick++)
        {
            scene.Tick();
            upright = MathF.Min(upright, Vector3.Transform(Vector3.UnitY, scene.State(box).Transform.Rotation).Y);
            low = Math.Min(low, scene.Height(box));
            high = Math.Max(high, scene.Height(box));
        }

        float travelled = scene.Offset(box).X - 27.5f;
        output.WriteLine($"Travelled {travelled} blocks; heights from {low} to {high}; up axis at {MathF.Acos(MathF.Min(upright, 1)) * 180 / MathF.PI} degrees from vertical.");
        Assert.True(upright > 0.9995f, $"The box tipped: its up axis is at {upright}.");   // under 1.8°: a box that catches on a joint tips forward
        Assert.InRange(low, 0.49, 0.51);
        Assert.InRange(high, 0.49, 0.51);
        Assert.InRange(travelled, 4.5f, 6.5f);   // friction alone stops it: 8² / (2 × 0.6 × 9.81) = 5.4 blocks
    }

    [Fact]
    public void ABoxFallsIntoAHoleOfOneBlock()
    {
        using var scene = new VoxelScene(Small);
        scene.Break(scene.Block(16, -1, 16), scene.Block(16, -2, 16));
        scene.Tick();
        BodyHandle box = scene.Add(scene.At(16.5, 3, 16.5), scene.Physics.CreateBox(new Vector3(0.8f)));
        BodyHandle beside = scene.Add(scene.At(18.5, 3, 16.5), scene.Physics.CreateBox(new Vector3(0.8f)));

        scene.Tick(90);

        Assert.InRange(scene.Height(box), -1.62, -1.58);
        Assert.InRange(scene.Height(beside), 0.38, 0.42);
    }

    [Fact]
    public void BodiesRestOnTheBoxesOfTheShapeOfEachBlock()
    {
        using var scene = new VoxelScene(Small);
        scene.Place(
            (scene.Block(10, 0, 10), scene.State("model")),
            (scene.Block(12, 0, 10), scene.State("slab")),
            (scene.Block(14, 0, 10), scene.State("bench")),
            (scene.Block(18, 0, 10), scene.State("grass")),
            (scene.Block(20, 0, 10), scene.Half("east")),
            (scene.Block(22, 0, 10), scene.Half("north")));
        scene.Tick();
        PhysicsShape ball = scene.Physics.CreateSphere(0.2f);
        BodyHandle model = scene.Add(scene.At(10.5, 3, 10.5), ball);
        BodyHandle slab = scene.Add(scene.At(12.5, 3, 10.5), ball);
        BodyHandle benchFull = scene.Add(scene.At(14.5, 3, 10.5), ball);
        BodyHandle benchLow = scene.Add(scene.At(15.5, 3, 10.5), ball);
        BodyHandle grass = scene.Add(scene.At(18.5, 3, 10.5), ball);
        BodyHandle eastHalf = scene.Add(scene.At(20.75, 3, 10.5), ball);
        BodyHandle westOfEastHalf = scene.Add(scene.At(20.25, 3, 10.5), ball);
        BodyHandle northHalf = scene.Add(scene.At(22.5, 3, 10.25), ball);
        BodyHandle southOfNorthHalf = scene.Add(scene.At(22.5, 3, 10.75), ball);

        scene.Tick(60);

        Assert.InRange(scene.Height(model), 1.18, 1.22);              // a model collides as a whole block
        Assert.InRange(scene.Height(slab), 0.68, 0.72);               // a slab, as its lower half
        Assert.InRange(scene.Height(benchFull), 1.18, 1.22);          // the first part of the multi-block is whole
        Assert.InRange(scene.Height(benchLow), 0.68, 0.72);           // its second part is a slab
        Assert.InRange(scene.Height(grass), 0.18, 0.22);              // no collision: the ball goes through it
        Assert.InRange(scene.Height(eastHalf), 1.18, 1.22);           // the half block turned east fills the east half of its cell
        Assert.InRange(scene.Height(westOfEastHalf), 0.18, 0.22);
        Assert.InRange(scene.Height(northHalf), 1.18, 1.22);          // turned north, as its shape is drawn, the north half
        Assert.InRange(scene.Height(southOfNorthHalf), 0.18, 0.22);
    }

    [Fact]
    public void AShapeThatDependsOnTheInstanceKeepsTheBoxesOfEachCell()
    {
        using var scene = new VoxelScene(Small);
        scene.Place((scene.Block(12, 0, 12), scene.State("carved")));
        scene.Tick();
        PhysicsShape ball = scene.Physics.CreateSphere(0.2f);
        BodyHandle low = scene.Add(scene.At(12.5, 3, 12.5), ball);
        scene.Tick(45);

        scene.Carved.Height = 0.75f;
        scene.Place((scene.Block(16, 0, 12), scene.State("carved")));
        scene.Tick();
        BodyHandle high = scene.Add(scene.At(16.5, 3, 12.5), ball);
        scene.Tick(45);

        Assert.InRange(scene.Height(low), 0.43, 0.47);
        Assert.InRange(scene.Height(high), 0.93, 0.97);
        Assert.True(scene.Carved.Calls >= 2);
    }

    [Fact]
    public void ABodyRestsOnAGridTurnedByThirtyDegrees()
    {
        using var scene = new VoxelScene(Small);
        Quaternion slope = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6);
        Grid ramp = scene.Probe.Voxels.CreateGrid(scene.Probe.Context, DimensionId.Default, new GridOptions
        {
            Transform = new WorldTransform(scene.At(10, 4, 10), slope, Vector3.One),
            Bounds = new GridBounds(new BlockCoordinate(0, 0, 0), new BlockCoordinate(7, 0, 7)),
        });
        var floor = new List<(BlockCoordinate, uint)>();
        for (int x = 0; x < 8; x++)
        {
            for (int z = 0; z < 8; z++)
            {
                floor.Add((new BlockCoordinate(x, 0, z), scene.State("stone")));
            }
        }

        scene.Place(ramp, [.. floor]);
        scene.Tick();
        Vector3 up = Vector3.Transform(Vector3.UnitY, slope);
        WorldPosition top = ramp.Transform.TransformPoint(new Vector3(4, 1, 4));
        BodyHandle box = scene.Add(top.Offset(up * 0.6f), scene.Physics.CreateBox(new Vector3(0.8f)), material: new PhysicsMaterial(1, 0), rotation: slope);

        scene.Tick(90);

        BodyState state = scene.State(box);
        float along = Vector3.Dot(state.Transform.Position.RelativeTo(top), up);
        Assert.InRange(along, 0.37f, 0.43f);                                                     // resting on the slope, half its size above it
        Assert.True(MathF.Abs(Quaternion.Dot(slope, state.Transform.Rotation)) > 0.999f);        // lying flat on it, friction holding it (tan 30° < 0.77)
        Assert.True(state.Transform.Position.RelativeTo(top).Length() < 0.6f);
    }

    [Fact]
    public void ABlockBrokenUnderASleepingCrateMakesItFallAtTheNextStep()
    {
        using var scene = new VoxelScene(Small);
        BodyHandle crate = scene.Add(scene.At(16.5, 1, 16.5), scene.Physics.CreateBox(new Vector3(0.8f)));
        scene.Tick(120);
        Assert.True(scene.State(crate).IsAsleep);
        long wakes = scene.Physics.Statistics.VoxelWakes;

        scene.Break(scene.Block(16, -1, 16));
        scene.Tick();

        Assert.False(scene.State(crate).IsAsleep);
        Assert.True(scene.State(crate).LinearVelocity.Y < 0);
        Assert.True(scene.Physics.Statistics.VoxelWakes > wakes);
        scene.Tick(30);
        Assert.InRange(scene.Height(crate), -0.62, -0.58);
    }

    [Fact]
    public void ABodyNearAChunkThatIsNotLoadedIsFrozenThenFreedOnceItLoads()
    {
        using var scene = new VoxelScene(Small);   // the chunk (1, 0, 1) is out of the circle of the ticket, so not loaded
        BodyHandle box = scene.Add(scene.At(31.5, 10, 31.5), scene.Physics.CreateBox(new Vector3(0.8f)));
        scene.Tick(15);

        Assert.True(IsFrozen(scene, box));
        Assert.Equal(1, scene.Physics.Statistics.FrozenBodies);
        Assert.InRange(scene.Height(box), 9.999, 10.001);   // neither gravity nor motion

        scene.Probe.Activate(scene.At(48, 16, 48), Small);
        scene.Tick(60);

        Assert.False(IsFrozen(scene, box));
        Assert.InRange(scene.Height(box), 0.38, 0.42);
    }

    [Fact]
    public void ABodyWhoseMaskLeavesOutTheVoxelsGoesThroughThem()
    {
        using var scene = new VoxelScene(Small);
        BodyHandle ghost = scene.Add(scene.At(16.5, 3, 16.5), scene.Physics.CreateSphere(0.3f), filter: new CollisionFilter(1, ~CollisionFilter.VoxelLayer));
        BodyHandle ball = scene.Add(scene.At(18.5, 3, 16.5), scene.Physics.CreateSphere(0.3f));

        scene.Tick(30);

        Assert.True(scene.Height(ghost) < 0);
        Assert.InRange(scene.Height(ball), 0.28, 0.32);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_000_000_000, -1_000_000_000)]
    [InlineData(-1_000_000_000, 1_000_000_000)]
    public void BodiesRestTheSameWayFarFromTheOrigin(double x, double z)
    {
        using var scene = new VoxelScene(Small, WorldPosition.FromBlocks(x + 16, 16, z + 16));
        scene.Place((scene.Block(12, 0, 16), scene.State("slab")));
        scene.Tick();
        BodyHandle box = scene.Add(scene.At(16.5, 3, 16.5), scene.Physics.CreateBox(Vector3.One));
        BodyHandle ball = scene.Add(scene.At(12.5, 3, 16.5), scene.Physics.CreateSphere(0.2f));
        BodyHandle slider = scene.Add(scene.At(20.5, 0.5, 16.5), scene.Physics.CreateBox(Vector3.One));
        scene.Physics.SetVelocity(slider, new Vector3(8, 0, 0), Vector3.Zero);

        scene.Tick(90);

        Assert.InRange(scene.Height(box), 0.48, 0.52);
        Assert.InRange(scene.Height(ball), 0.68, 0.72);
        Assert.InRange(scene.Offset(slider).X - 20.5f, 4.5f, 6.5f);
        Assert.InRange(scene.Offset(box).X, 16.49f, 16.51f);
        Assert.InRange(scene.Offset(box).Z, 16.49f, 16.51f);
    }

    [Fact]
    public void FiveHundredBoxesComeToRestOnTheTerrainAndSleep()
    {
        using var scene = new VoxelScene(new ChunkArea(2, 1));
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        var random = new Random(5);
        var boxes = new BodyHandle[500];
        for (int i = 0; i < boxes.Length; i++)
        {
            Quaternion tilt = Quaternion.CreateFromYawPitchRoll(random.NextSingle() * 6.3f, (random.NextSingle() - 0.5f) * 0.4f, (random.NextSingle() - 0.5f) * 0.4f);
            boxes[i] = scene.Add(scene.At(-8 + ((i % 25) * 2), 2 + random.NextSingle() * 3, -4 + ((i / 25) * 2)), crate, rotation: tilt);
        }

        var falling = Stopwatch.StartNew();
        scene.Tick(30);
        falling.Stop();
        scene.Tick(120);
        var resting = Stopwatch.StartNew();
        scene.Tick(30);
        resting.Stop();
        float fastest = 0;
        var rested = new WorldPosition[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            fastest = MathF.Max(fastest, scene.State(boxes[i]).LinearVelocity.Length());
            rested[i] = scene.State(boxes[i]).Transform.Position;
        }

        int seconds = 6;
        while (seconds < 30 && !boxes.All(box => scene.State(box).IsAsleep))
        {
            scene.Tick(30);
            seconds++;
        }

        double lowest = boxes.Min(scene.Height);
        int flat = boxes.Count(box => scene.Height(box) < 0.51);
        float drift = 0;
        for (int i = 0; i < boxes.Length; i++)
        {
            drift = MathF.Max(drift, scene.State(boxes[i]).Transform.Position.RelativeTo(rested[i]).Length());
        }

        output.WriteLine($"500 boxes: {falling.Elapsed.TotalMilliseconds / 30:F2} ms per tick while they fall, {resting.Elapsed.TotalMilliseconds / 30:F2} ms per tick once they rest; fastest {fastest:F4} blocks/s after 6 s; all asleep after {seconds} s; lowest {lowest:F4}; {flat} flat on the ground; drift {drift:F5} blocks since 6 s; {scene.Physics.Statistics.VoxelChunks} chunks of voxels.");
        Assert.True(fastest < 0.05f, $"A box still moves at {fastest} blocks per second after 6 s.");
        Assert.True(boxes.All(box => scene.State(box).IsAsleep), "Some boxes never sleep.");
        Assert.InRange(lowest, 0.49, 0.51);   // none through the ground
        Assert.True(drift < 0.01f, $"A box drifted by {drift} blocks at rest.");   // no jitter
        Assert.True(flat >= 490, $"Only {flat} boxes lie on the ground; the others lean on a neighbor.");
    }

    [Fact]
    public void AStepWithBodiesOnTheVoxelsAllocatesNothingOnceStable()
    {
        using var scene = new VoxelScene(Small);
        PhysicsShape crate = scene.Physics.CreateBox(new Vector3(0.8f));
        var boxes = new BodyHandle[20];
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = scene.Add(scene.At(8 + i, 0.4, 16.5), crate);
        }

        for (int tick = 0; tick < 40; tick++)
        {
            Push(scene, boxes, tick);
            scene.Tick();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = 40; tick < 70; tick++)
        {
            Push(scene, boxes, tick);
            scene.Tick();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static bool IsFrozen(VoxelScene scene, BodyHandle body)
    {
        Assert.True(scene.Probe.Entities.TryGetComponent(out Nobara.Entity.ComponentType<PhysicsBody> component));
        return (scene.Probe.World.Read(scene.State(body).Entity, component).Flags & BodyFlags.Frozen) != 0;
    }

    /// <summary>Keeps the boxes sliding back and forth, so that they stay awake and their footprints change.</summary>
    private static void Push(VoxelScene scene, BodyHandle[] boxes, int tick)
    {
        var velocity = new Vector3((tick / 10 % 2 == 0) ? 3 : -3, 0, 0);
        foreach (BodyHandle box in boxes)
        {
            scene.Physics.SetVelocity(box, velocity, Vector3.Zero);
        }
    }
}
