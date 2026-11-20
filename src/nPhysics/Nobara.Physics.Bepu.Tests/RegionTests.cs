// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>Regions on BEPUphysics: contacts across a border, precision far from the origin, and the cost of transfers and merges.</summary>
public sealed class RegionTests(ITestOutputHelper output)
{
    private static readonly ChunkArea Small = new(1, 1);

    [Fact]
    public void KeepsAStackAcrossABorderStanding()
    {
        using var scene = new BepuScene(h => h.ActiveArea = null);
        scene.Probe.Activate(Chunk(0), Small);   // chunks -1 to 1
        scene.Probe.Activate(Chunk(3), Small);   // chunks 2 to 4: the border is at x = 64
        scene.Add(WorldPosition.FromBlocks(60, 15.5, 16), scene.Physics.CreateBox(new Vector3(20, 1, 20)), BodyKind.Static);
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        var boxes = new BodyHandle[6];
        for (int i = 0; i < boxes.Length; i++)
        {
            // Every other box on the other side of the border.
            boxes[i] = scene.Add(WorldPosition.FromBlocks(i % 2 == 0 ? 63.8 : 64.2, 16.5 + i, 16), crate);
        }

        scene.Tick(30);
        Assert.Equal(2, scene.Physics.Worlds.Count);
        Assert.True(scene.Physics.Statistics.Ghosts > 0);
        scene.Tick(120);

        for (int i = 0; i < boxes.Length; i++)
        {
            WorldPosition position = scene.State(boxes[i]).Transform.Position;
            Vector3 offset = position.RelativeTo(WorldPosition.FromBlocks(i % 2 == 0 ? 63.8 : 64.2, 16.5 + i, 16));
            Assert.True(offset.Length() < 0.1f, $"Box {i} moved by {offset}.");
        }
    }

    [Theory]
    [InlineData(1_000_000_000.0)]
    [InlineData(-1_000_000_000.0)]
    public void AFallOfBoxesEndsTheSameFarFromTheOriginAsNearIt(double offset)
    {
        double far = Math.Round(offset / VoxelCoordinates.ChunkSize) * VoxelCoordinates.ChunkSize;
        WorldPosition[] near = FallOfBoxes(0);
        WorldPosition[] distant = FallOfBoxes(far);

        for (int i = 0; i < near.Length; i++)
        {
            Vector3 there = distant[i].RelativeTo(WorldPosition.FromBlocks(far, 0, far));
            Vector3 here = near[i].RelativeTo(WorldPosition.FromBlocks(0, 0, 0));
            Assert.True(Vector3.Distance(here, there) < 1e-3f, $"Box {i}: {here} near the origin, {there} far from it.");
        }
    }

    [Fact]
    public void MeasuresTheCostOfATransferAndOfAMerge()
    {
        using var scene = new BepuScene(h => h.ActiveArea = null);
        scene.Probe.Activate(Chunk(0), Small);
        AreaTicket moving = scene.Probe.Activate(Chunk(6), new ChunkArea(2, 1));
        PhysicsShape crate = scene.Physics.CreateBox(new Vector3(0.8f));
        for (int i = 0; i < 200; i++)
        {
            scene.Add(WorldPosition.FromBlocks(2 + (i % 20), 30 + (i / 20 * 2), 16), crate, BodyKind.Kinematic);
            scene.Add(WorldPosition.FromBlocks(134 + (i % 20), 30 + (i / 20 * 2), 16), crate, BodyKind.Kinematic);
        }

        scene.Tick(5);
        TimeSpan separate = TimeSpan.Zero, merge = TimeSpan.Zero, merged = TimeSpan.Zero, split = TimeSpan.Zero;
        for (int cycle = 0; cycle < 6; cycle++)
        {
            separate = Step(scene);
            moving.Move(Chunk(3));
            merge = Step(scene);   // 200 bodies move to the world of the region that keeps its world
            merged = Step(scene);
            Assert.Single(scene.Physics.Worlds);
            moving.Move(Chunk(6));
            split = Step(scene);   // and back
            Assert.Equal(2, scene.Physics.Worlds.Count);
        }

        Assert.Equal(6 * 400, scene.Physics.Statistics.Transfers);
        output.WriteLine($"Step with two regions: {separate.TotalMilliseconds:0.000} ms; with the merge of 200 bodies: {merge.TotalMilliseconds:0.000} ms; merged: {merged.TotalMilliseconds:0.000} ms; with the split: {split.TotalMilliseconds:0.000} ms.");
        output.WriteLine($"Transfer: about {(merge - merged).TotalMicroseconds / 200:0.0} µs per body.");
    }

    private static TimeSpan Step(BepuScene scene)
    {
        long start = Stopwatch.GetTimestamp();
        scene.Tick();
        return Stopwatch.GetElapsedTime(start);
    }

    private static WorldPosition[] FallOfBoxes(double offset)
    {
        using var scene = new BepuScene(h => h.ActiveArea = null);
        scene.Probe.Activate(WorldPosition.FromBlocks(offset + 16, 16, offset + 16), Small);
        scene.Add(WorldPosition.FromBlocks(offset + 16, 0.5, offset + 16), scene.Physics.CreateBox(new Vector3(16, 1, 16)), BodyKind.Static);
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        var boxes = new BodyHandle[8];
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = scene.Add(WorldPosition.FromBlocks(offset + 14.25 + (i % 3), 3 + (i * 1.2), offset + 15.75 + (i % 2 * 0.3)), crate, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * 0.2f));
        }

        scene.Tick(120);
        return [.. boxes.Select(b => scene.State(b).Transform.Position)];
    }

    private static WorldPosition Chunk(int x) => WorldPosition.FromBlocks((x * 32) + 16, 16, 16);
}
