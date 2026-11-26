// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>The step on the job system: no thread of its own, no managed allocation, and worlds that share nothing.</summary>
[Collection(Serial.Name)]
public sealed class DispatchTests
{
    private const float Delta = 1f / 30;

    [Fact]
    public void ALargeStepRunsOnTheLoopThreadAndTheWorkersOfTheJobSystemOnly()
    {
        using var scene = new BepuScene(harness => harness.Settings[PhysicsSettings.Substeps] = "4");
        scene.Harness.Backend.SpreadThreshold = 50;
        AddPile(scene, 200);
        scene.Ground();
        BepuWorld world = scene.World;
        Assert.NotNull(world.Dispatcher);
        var threads = new HashSet<string>();
        world.Dispatcher!.OnWorker = _ =>
        {
            Thread thread = Thread.CurrentThread;
            string name = thread.ManagedThreadId == Environment.CurrentManagedThreadId ? "loop" : thread.Name ?? "unnamed";
            lock (threads)
            {
                threads.Add(name);
            }
        };
        int loop = Environment.CurrentManagedThreadId;

        scene.Tick(20);

        Assert.True(world.SpreadSteps > 0);
        Assert.True(world.Dispatcher.Dispatches > 0);
        Assert.Contains("loop", threads);
        Assert.All(threads, name => Assert.True(name == "loop" || name.StartsWith("Nobara worker ", StringComparison.Ordinal), $"A worker ran on the thread '{name}'."));
        Assert.Equal(loop, Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void ASmallStepStaysOnTheLoopThread()
    {
        using var scene = new BepuScene();
        AddPile(scene, 20);
        scene.Ground();

        scene.Tick(10);

        Assert.Equal(0, scene.World.SpreadSteps);
        Assert.Equal(0, scene.World.Dispatcher!.Dispatches);
    }

    [Fact]
    public unsafe void ADispatchToNoWorkerRunsNothing()
    {
        using var scene = new BepuScene();
        int calls = 0;
        JobThreadDispatcher dispatcher = new(scene.Probe.Context.Jobs, scene.Probe.Context.Logger, 3);

        dispatcher.DispatchWorkers(_ => Interlocked.Increment(ref calls), maximumWorkerCount: 0);
        Assert.Equal(0, calls);
        dispatcher.DispatchWorkers(_ => Interlocked.Increment(ref calls));
        Assert.Equal(3, calls);
        dispatcher.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStepInASteadyStateAllocatesNoManagedMemory(bool spread)
    {
        using var scene = new BepuScene();
        BepuBackend backend = scene.Harness.Backend;
        backend.SpreadThreshold = 1;
        using BepuWorld world = NewWorld(scene);
        using Chunk ground = BepuScene.Solid(1);
        world.AddVoxelChunk(new VoxelChunkCollider(null!, ground, Vector3.Zero, Quaternion.Identity));
        PhysicsShape crate = scene.Physics.CreateBox(new Vector3(0.9f));
        PhysicsShape ball = scene.Physics.CreateSphere(0.4f);
        int first = -1;
        for (int i = 0; i < 64; i++)
        {
            int body = world.AddBody(Body(scene, i % 2 == 0 ? crate : ball, new Vector3(2 + (i % 8 * 3), 1.5f, 2 + (i / 8 * 3)), canSleep: false));
            first = first < 0 ? body : first;
        }

        var settings = new PhysicsStepSettings(Delta, 8, 1, spread);
        for (int i = 0; i < 120; i++)
        {
            world.Step(settings);
        }

        // Spread, the step runs on other threads too, so the count is the whole process's, where the threads of the test
        // runner may still allocate for earlier tests. A step that allocates does so in every window: the quietest one counts.
        // Alone, the step runs on this thread only, and its count is exact.
        long allocated = long.MaxValue;
        int windows = 0;
        while (windows < (spread ? 3 : 1) && allocated != 0)
        {
            long before = spread ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                world.ApplyImpulse(first, new Vector3(0.01f, 0, 0), Vector3.Zero);
                world.Step(settings);
                world.ReadBody(first, out _);
            }

            allocated = Math.Min(allocated, (spread ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread()) - before);
            windows++;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(spread ? 120 + (60 * windows) : 0, world.SpreadSteps);
    }

    [Fact]
    public void TwoWorldsSteppedInTurnMatchTheSameWorldsSteppedAlone()
    {
        using var scene = new BepuScene();
        Vector3[] alone = Run(scene, interleave: false);
        Vector3[] together = Run(scene, interleave: true);

        Assert.Equal(alone, together);
        Assert.NotEqual(alone[0], alone[1]);
    }

    private static Vector3[] Run(BepuScene scene, bool interleave)
    {
        PhysicsShape crate = scene.Physics.CreateBox(Vector3.One);
        using Chunk ground = BepuScene.Solid(1);
        using BepuWorld first = NewWorld(scene);
        using BepuWorld second = NewWorld(scene);
        first.AddVoxelChunk(new VoxelChunkCollider(null!, ground, Vector3.Zero, Quaternion.Identity));
        second.AddVoxelChunk(new VoxelChunkCollider(null!, ground, Vector3.Zero, Quaternion.Identity));
        int a = first.AddBody(Body(scene, crate, new Vector3(8, 5, 8), canSleep: true, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f)));
        int b = second.AddBody(Body(scene, crate, new Vector3(8, 3, 8), canSleep: true, rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.9f)));
        var settings = new PhysicsStepSettings(Delta, 8, 1, MaySpread: false);
        if (interleave)
        {
            for (int i = 0; i < 90; i++)
            {
                first.Step(settings);
                second.Step(settings);
            }
        }
        else
        {
            for (int i = 0; i < 90; i++)
            {
                first.Step(settings);
            }

            for (int i = 0; i < 90; i++)
            {
                second.Step(settings);
            }
        }

        first.ReadBody(a, out BackendBodyState stateA);
        second.ReadBody(b, out BackendBodyState stateB);
        return [stateA.Position, stateB.Position];
    }

    private static BepuWorld NewWorld(BepuScene scene) =>
        (BepuWorld)scene.Harness.Backend.CreateWorld(new PhysicsWorldOptions(default, new Vector3(0, -9.81f, 0), new ShapeSource(scene.Physics), scene.Probe.Context.Logger));

    private static BackendBody Body(BepuScene scene, PhysicsShape shape, Vector3 position, bool canSleep, Quaternion? rotation = null) => new()
    {
        Kind = BodyKind.Dynamic,
        Shape = shape,
        Position = position,
        Rotation = rotation ?? Quaternion.Identity,
        Mass = 1,
        Inertia = scene.Harness.Backend.ComputeInertia(scene.Physics.GetShape(shape), 1),
        Material = PhysicsMaterial.Default,
        Filter = CollisionFilter.Default,
        CanSleep = canSleep,
    };

    private static void AddPile(BepuScene scene, int count)
    {
        PhysicsShape crate = scene.Physics.CreateBox(new Vector3(0.8f));
        for (int i = 0; i < count; i++)
        {
            scene.Add(WorldPosition.FromBlocks(2 + (i % 10 * 2.5), 2 + (i / 100 * 2), 2 + (i / 10 % 10 * 2.5)), crate);
        }
    }

    private sealed class ShapeSource(IPhysicsSystem physics) : IPhysicsShapeSource
    {
        public ShapeDescription Get(PhysicsShape shape) => physics.GetShape(shape);
    }
}
