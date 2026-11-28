// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Bepu.Tests.Support;
using Nobara.Voxel;
using Xunit.Abstractions;

namespace Nobara.Physics.Bepu.Tests;

/// <summary>The character controller against the voxels of the terrain and of grids, static bodies, and dynamic bodies.</summary>
[Collection(Serial.Name)]
public sealed class CharacterTests(ITestOutputHelper output)
{
    private const float Tick = 1f / 30;
    private static readonly ChunkArea Small = new(1, 1);

    [Fact]
    public void ACharacterWalksOnFlatGround()
    {
        using var scene = new CharacterScene(Small);
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));
        scene.Voxels.Tick();
        Assert.True(scene.State(character).IsGrounded);

        scene.Walk(character, new Vector3(4, 0, 0));
        scene.Voxels.Tick(30);

        Vector3 offset = scene.Offset(character);
        Assert.Equal(0f, offset.Y, 4);
        Assert.InRange(offset.X, 16.5 + 3.9, 16.5 + 4.1);
        Assert.Equal(16.5f, offset.Z, 4);
        CharacterState state = scene.State(character);
        Assert.True(state.IsGrounded);
        Assert.Equal(CharacterGroundKind.Block, state.Ground.Kind);
        Assert.Equal(scene.Voxels.State("stone"), state.Ground.State);
        Assert.Equal(4f, scene.Velocity(character).X, 3);
    }

    [Fact]
    public void ACharacterWalksUpAStepAndNotUpAWholeBlock()
    {
        using var scene = new CharacterScene(Small);
        uint slab = scene.Voxels.State("slab");
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Row(scene, 18, 0, slab), .. Row(scene, 19, 0, slab), .. Row(scene, 20, 0, slab), .. Row(scene, 21, 0, slab)]);
        scene.Voxels.Place([.. Row(scene, 22, 0, stone), .. Row(scene, 22, 1, stone)]);
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));
        scene.Voxels.Tick();

        scene.Walk(character, new Vector3(3, 0, 0));
        scene.Voxels.Tick(70);

        Vector3 offset = scene.Offset(character);
        Assert.Equal(0.5f, offset.Y, 3);              // up the slabs, without a jump
        Assert.Equal(22f - 0.3f, offset.X, 3);       // stopped by the wall, 1.5 blocks above the slabs: past the step height
        Assert.True(scene.State(character).IsGrounded);
    }

    [Fact]
    public void AWallOfTwoBlocksStopsAJumpingCharacter()
    {
        using var scene = new CharacterScene(Small);
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Row(scene, 18, 0, stone), .. Row(scene, 18, 1, stone)]);
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));
        scene.Voxels.Tick();

        scene.Walk(character, new Vector3(3, 0, 0), jumpSpeed: 5);   // a jump of about 1.3 blocks
        scene.Voxels.Tick(60);

        Vector3 offset = scene.Offset(character);
        Assert.Equal(18f - 0.3f, offset.X, 3);
        Assert.InRange(offset.Y, -1e-3f, 1.4f);
    }

    [Fact]
    public void ACharacterSlidesAlongAWallItWalksIntoAtAnAngle()
    {
        using var scene = new CharacterScene(Small);
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Row(scene, 18, 0, stone, 8, 26), .. Row(scene, 18, 1, stone, 8, 26)]);
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 12.5));
        scene.Voxels.Tick();

        scene.Walk(character, new Vector3(3, 0, 3));
        scene.Voxels.Tick(30);

        Vector3 offset = scene.Offset(character);
        Assert.Equal(18f - 0.3f, offset.X, 3);
        Assert.InRange(offset.Z, 12.5 + 2.8, 12.5 + 3.1);   // the part along the wall goes on
        Assert.Equal(0f, scene.Velocity(character).X, 3);
        Assert.Equal(3f, scene.Velocity(character).Z, 3);
    }

    [Fact]
    public void ACharacterGoesInAndOutOfAnInnerCornerWithoutCatching()
    {
        using var scene = new CharacterScene(Small);
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Row(scene, 18, 0, stone, 10, 18), .. Row(scene, 18, 1, stone, 10, 18), .. Column(scene, 18, 0, stone, 10, 17), .. Column(scene, 18, 1, stone, 10, 17)]);
        EntityId character = scene.AddCharacter(scene.Voxels.At(14.5, 0, 14.5));
        scene.Voxels.Tick();

        scene.Walk(character, new Vector3(3, 0, 3));
        scene.Voxels.Tick(45);
        Vector3 corner = scene.Offset(character);
        Assert.Equal(18f - 0.3f, corner.X, 3);
        Assert.Equal(18f - 0.3f, corner.Z, 3);

        scene.Walk(character, new Vector3(-3, 0, 1));   // out along one wall, into the other
        scene.Voxels.Tick(15);
        Vector3 after = scene.Offset(character);
        Assert.InRange(after.X, corner.X - 1.6f, corner.X - 1.3f);
        Assert.Equal(18f - 0.3f, after.Z, 3);
    }

    [Fact]
    public void ACharacterJumpsLosesTheGroundAndLands()
    {
        using var scene = new CharacterScene(Small);
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));
        scene.Voxels.Tick(2);
        scene.Events.Clear();

        scene.Walk(character, Vector3.Zero, jumpSpeed: 6);
        scene.Voxels.Tick();
        scene.Walk(character, Vector3.Zero);
        Assert.False(scene.State(character).IsGrounded);
        float highest = 0;
        int ticks = 1;
        while (!scene.State(character).IsGrounded && ticks < 60)
        {
            scene.Voxels.Tick();
            ticks++;
            highest = MathF.Max(highest, scene.Offset(character).Y);
        }

        Assert.InRange(highest, 1.6f, 1.95f);   // 6² / (2 × 9.81) = 1.83
        Assert.InRange(ticks, 35, 38);          // 2 × 6 / 9.81 = 1.22 s
        Assert.Equal(0f, scene.Offset(character).Y, 4);
        CharacterLanding landing = Assert.Single(scene.Events.Landings);
        Assert.Equal(character, landing.Entity);
        Assert.InRange(landing.Speed, 5.5f, 6.5f);
        Assert.Equal(scene.Voxels.State("stone"), landing.Ground.State);
    }

    [Fact]
    public void ACharacterFallsIntoAHoleAndLandsAtItsBottom()
    {
        using var scene = new CharacterScene(Small);
        var hole = new List<BlockCoordinate>();
        for (int y = -3; y <= -1; y++)
        {
            for (int z = 16; z <= 17; z++)
            {
                for (int x = 16; x <= 17; x++)
                {
                    hole.Add(scene.Voxels.Block(x, y, z));
                }
            }
        }

        scene.Voxels.Break([.. hole]);
        scene.Voxels.Tick();
        EntityId character = scene.AddCharacter(scene.Voxels.At(14.5, 0, 17));
        scene.Voxels.Tick();
        scene.Events.Clear();

        scene.Walk(character, new Vector3(3, 0, 0));
        scene.Voxels.Tick(20);
        scene.Walk(character, Vector3.Zero);
        scene.Voxels.Tick(40);

        Assert.Equal(-3f, scene.Offset(character).Y, 4);
        Assert.True(scene.State(character).IsGrounded);
        CharacterLanding landing = Assert.Single(scene.Events.Landings);
        Assert.InRange(landing.Speed, 7f, 8.2f);   // √(2 × 9.81 × 3) = 7.7
        Assert.True(landing.AirSteps > 10);
        Assert.NotEmpty(scene.Events.Steps);
    }

    [Fact]
    public void ACharacterStopsAtAStaticBody()
    {
        using var scene = new CharacterScene(Small);
        scene.Voxels.Scene.Add(scene.Voxels.At(19, 1, 16.5), scene.Physics.CreateBox(new Vector3(2, 2, 8)), BodyKind.Static);
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));
        scene.Voxels.Tick();

        scene.Walk(character, new Vector3(3, 0, 0));
        scene.Voxels.Tick(30);

        Assert.Equal(18f - 0.3f, scene.Offset(character).X, 3);
    }

    [Fact]
    public void ACharacterStaysOnAGridTurnedByThirtyDegrees()
    {
        using var scene = new CharacterScene(Small);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6);
        Grid grid = scene.Platform(scene.Voxels.At(16, 4, 16), turn);
        EntityId character = scene.AddCharacter(grid.Transform.TransformPoint(new Vector3(0.5f, 1.5f, 0.5f)));
        scene.Voxels.Tick(20);

        CharacterState state = scene.State(character);
        Assert.True(state.IsGrounded);
        Assert.Equal(grid.Entity, state.Support);
        Assert.Equal(grid.Entity, scene.World.GetParent(character));
        Assert.Equal(1f, scene.Local(character).Y, 4);

        scene.Walk(character, new Vector3(2, 0, 0));   // along X of the world, across the turned cells
        scene.Voxels.Tick(15);
        Vector3 moved = scene.Transform(character).Position.RelativeTo(grid.Transform.TransformPoint(new Vector3(0.5f, 1f, 0.5f)));
        Assert.Equal(1f, moved.X, 1);
        Assert.Equal(0f, moved.Z, 3);
        Assert.Equal(1f, scene.Local(character).Y, 4);
        Assert.True(scene.State(character).IsGrounded);
    }

    [Fact]
    public void ACharacterStaysOnAKinematicGridThatTurnsFor1000Ticks()
    {
        using var scene = new CharacterScene(Small);
        Grid grid = scene.Platform(scene.Voxels.At(16, 4, 16), Quaternion.Identity);
        BodyHandle body = scene.MakeKinematic(grid);
        EntityId character = scene.AddCharacter(grid.Transform.TransformPoint(new Vector3(2.5f, 1.2f, 0.5f)));
        scene.Voxels.Tick(10);
        Assert.Equal(grid.Entity, scene.State(character).Support);
        Vector3 start = scene.Local(character);

        scene.Physics.SetVelocity(body, Vector3.Zero, new Vector3(0, 1, 0));   // one radian per second
        int grounded = 0;
        float drift = 0;
        float error = 0;
        for (int tick = 0; tick < 1000; tick++)
        {
            scene.Voxels.Tick();
            grounded += scene.State(character).IsGrounded ? 1 : 0;
            drift = MathF.Max(drift, Vector3.Distance(scene.Local(character), start));
            error = MathF.Max(error, scene.Transform(character).Position.RelativeTo(grid.Transform.TransformPoint(scene.Local(character))).Length());
        }

        output.WriteLine($"Drift in the grid: {drift}; error of the world position: {error}; turn: {2 * MathF.Acos(MathF.Abs(grid.Transform.Rotation.W))} rad.");
        Assert.Equal(1000, grounded);
        Assert.True(drift < 1e-4f, $"The character drifted by {drift} blocks on the grid.");
        Assert.True(error < 1e-3f, $"The world position is {error} blocks off the grid.");
        Assert.Equal(grid.Entity, scene.State(character).Support);
        Assert.Equal(1f, scene.Angular(character).Y, 3);
    }

    [Fact]
    public void ACharacterPassesFromOneGridToAnotherWithoutAJolt()
    {
        using var scene = new CharacterScene(Small);
        Grid first = scene.Platform(scene.Voxels.At(14, 4, 16), Quaternion.Identity);
        Grid second = scene.Platform(scene.Voxels.At(21, 4.0, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        EntityId character = scene.AddCharacter(first.Transform.TransformPoint(new Vector3(0.5f, 1.2f, 0.5f)));
        scene.Voxels.Tick(10);
        Assert.Equal(first.Entity, scene.State(character).Support);

        scene.Walk(character, new Vector3(3, 0, 0));
        WorldPosition previous = scene.Transform(character).Position;
        float worst = 0;
        bool changed = false;
        for (int tick = 0; tick < 75; tick++)   // from x = 14.5 to 22, on the second grid from x = 18
        {
            scene.Voxels.Tick();
            WorldPosition now = scene.Transform(character).Position;
            worst = MathF.Max(worst, MathF.Abs(now.RelativeTo(previous).X - (3 * Tick)) + MathF.Abs(now.RelativeTo(previous).Y) + MathF.Abs(now.RelativeTo(previous).Z));
            previous = now;
            changed |= scene.State(character).Support == second.Entity;
        }

        Assert.True(changed, "The character never stood on the second grid.");
        Assert.Equal(second.Entity, scene.World.GetParent(character));
        Assert.True(worst < 0.01f, $"A step went {worst} blocks off the straight line.");
        Assert.Equal(3f, scene.Velocity(character).X, 3);
    }

    [Fact]
    public void ACharacterThatJumpsOffAMovingGridKeepsItsVelocity()
    {
        using var scene = new CharacterScene(Small);
        Grid grid = scene.Platform(scene.Voxels.At(12, 4, 16), Quaternion.Identity);
        BodyHandle body = scene.MakeKinematic(grid);
        EntityId character = scene.AddCharacter(grid.Transform.TransformPoint(new Vector3(0.5f, 1.2f, 0.5f)));
        scene.Voxels.Tick(10);
        scene.Physics.SetVelocity(body, new Vector3(3, 0, 0), Vector3.Zero);
        scene.Voxels.Tick(5);
        Assert.Equal(3f, scene.Velocity(character).X, 2);   // carried
        Vector3 takeoff = scene.Local(character);

        scene.Walk(character, Vector3.Zero, jumpSpeed: 5);
        scene.Voxels.Tick();
        scene.Walk(character, Vector3.Zero);
        scene.Voxels.Tick(CharacterController.SupportGraceSteps + 1);
        Assert.True(scene.State(character).Support.IsNull);
        Assert.True(scene.World.GetParent(character).IsNull);
        Assert.Equal(3f, scene.Velocity(character).X, 2);   // the velocity of the grid, kept in the world

        int ticks = 0;
        while (!scene.State(character).IsGrounded && ticks++ < 60)
        {
            scene.Voxels.Tick();
        }

        Assert.Equal(grid.Entity, scene.State(character).Support);
        Assert.True(Vector3.Distance(scene.Local(character), takeoff) < 0.15f, $"It landed {Vector3.Distance(scene.Local(character), takeoff)} blocks from where it jumped.");
    }

    [Fact]
    public void ACharacterPushesALightBoxAndHardlyAHeavyOne()
    {
        using var scene = new CharacterScene(Small);
        PhysicsShape crate = scene.Physics.CreateBox(new Vector3(0.5f));
        BodyHandle light = scene.Voxels.Add(scene.Voxels.At(18.5, 0.25, 14.5), crate, mass: 2);
        BodyHandle heavy = scene.Voxels.Add(scene.Voxels.At(18.5, 0.25, 18.5), crate, mass: 2000);
        EntityId pushing = scene.AddCharacter(scene.Voxels.At(16.5, 0, 14.5));
        EntityId leaning = scene.AddCharacter(scene.Voxels.At(16.5, 0, 18.5));
        scene.Voxels.Tick(20);
        Vector3 lightStart = scene.Voxels.Offset(light);
        Vector3 heavyStart = scene.Voxels.Offset(heavy);

        scene.Walk(pushing, new Vector3(3, 0, 0));
        scene.Walk(leaning, new Vector3(3, 0, 0));
        scene.Voxels.Tick(30);

        float lightMoved = scene.Voxels.Offset(light).X - lightStart.X;
        float heavyMoved = scene.Voxels.Offset(heavy).X - heavyStart.X;
        output.WriteLine($"Light box moved {lightMoved} blocks, heavy box {heavyMoved}.");
        Assert.True(lightMoved > 0.5f, $"The light box moved {lightMoved} blocks.");
        Assert.True(heavyMoved < 0.1f, $"The heavy box moved {heavyMoved} blocks.");
        Assert.True(scene.Offset(leaning).X > 19f, "A dynamic body stopped the character.");
    }

    [Fact]
    public void ACharacterTakenOffATurningGridStaysWhereItIsAndComesBack()
    {
        using var scene = new CharacterScene(Small);
        Grid grid = scene.Platform(scene.Voxels.At(16, 4, 16), Quaternion.Identity);
        BodyHandle body = scene.MakeKinematic(grid);
        EntityId character = scene.AddCharacter(grid.Transform.TransformPoint(new Vector3(2.5f, 1.2f, 0.5f)));
        scene.Voxels.Tick(10);
        scene.Physics.SetVelocity(body, Vector3.Zero, new Vector3(0, 1, 0));
        scene.Voxels.Tick(20);
        WorldPosition onGrid = grid.Transform.TransformPoint(scene.Local(character));
        Vector3 velocity = scene.Velocity(character);

        scene.Physics.RemoveCharacter(DimensionId.Default, character);

        Assert.True(scene.World.GetParent(character).IsNull);
        Assert.True(scene.Transform(character).Position.RelativeTo(onGrid).Length() < 1e-4f);
        Assert.False(scene.Physics.IsCharacter(DimensionId.Default, character));
        Assert.Equal(velocity, scene.Velocity(character));   // readable by the module that takes over
        Assert.Equal(0, scene.Physics.Statistics.Characters);
        WorldTransform left = scene.Transform(character);
        scene.Voxels.Tick(10);
        Assert.Equal(left, scene.Transform(character));   // nPhysics writes it no more

        scene.Physics.SetVelocity(body, Vector3.Zero, Vector3.Zero);
        scene.Physics.AddCharacter(DimensionId.Default, character, CharacterBody.Default);
        scene.Voxels.Tick(30);
        Assert.Equal(1, scene.Physics.Statistics.Characters);
        Assert.True(scene.State(character).IsGrounded);
        Assert.Equal(grid.Entity, scene.State(character).Support);
    }

    [Fact]
    public void AnEntityWithoutACharacterIsMovedByItsModuleOnly()
    {
        using var scene = new CharacterScene(Small);
        EntityId other = scene.Voxels.Probe.CreateEntity(WorldTransform.At(scene.Voxels.At(10.5, 3, 10.5)));
        scene.Voxels.Probe.World.Add(other, scene.Intent, new MovementIntent { Velocity = new Vector3(5, 0, 0), JumpSpeed = 3 });
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));

        for (int tick = 0; tick < 20; tick++)
        {
            WorldTransform written = WorldTransform.At(scene.Voxels.At(10.5 + (tick * 0.1), 3, 10.5));
            scene.World.Set(other, scene.Voxels.Probe.Entities.WorldTransform, written);
            scene.Voxels.Tick();
            Assert.Equal(written, scene.Transform(other));
        }

        Assert.True(scene.State(character).IsGrounded);
    }

    [Theory]
    [InlineData(1_000_000_016.0, -999_999_984.0)]
    [InlineData(-999_999_984.0, 1_000_000_016.0)]
    public void ACharacterWalksABillionBlocksAwayAsAtTheOrigin(double x, double z)
    {
        using var scene = new CharacterScene(Small, WorldPosition.FromBlocks(x, 16, z));
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.25, 0, 16.75));
        scene.Voxels.Tick();

        scene.Walk(character, new Vector3(3, 0, -2));
        scene.Voxels.Tick(30);

        Vector3 offset = scene.Offset(character);
        Assert.Equal(0f, offset.Y, 4);
        Assert.InRange(offset.X, 16.25 + 2.9, 16.25 + 3.05);
        Assert.InRange(offset.Z, 16.75 - 2.05, 16.75 - 1.9);
        Assert.True(scene.State(character).IsGrounded);
    }

    [Fact]
    public void MovingCharactersAllocatesNothingOnceStable()
    {
        using var scene = new CharacterScene(Small);
        Grid grid = scene.Platform(scene.Voxels.At(10, 4, 10), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f));
        EntityId walker = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));
        EntityId rider = scene.AddCharacter(grid.Transform.TransformPoint(new Vector3(0.5f, 1.2f, 0.5f)));
        scene.Voxels.Scene.Add(scene.Voxels.At(16.5, 1, 20), scene.Physics.CreateBox(new Vector3(4, 2, 1)), BodyKind.Static);
        for (int tick = 0; tick < 40; tick++)
        {
            Shuttle(scene, walker, rider, tick);
            scene.Voxels.Tick();
        }

        // The runtime may allocate now and then on this thread, such as while it compiles methods again: a round without allocation must come.
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int tick = 40 + (30 * round); tick < 70 + (30 * round); tick++)
            {
                Shuttle(scene, walker, rider, tick);
                scene.Voxels.Tick();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(grid.Entity, scene.State(rider).Support);
    }

    [Fact]
    public void TwoHundredCharactersWalkWithinABudget()
    {
        using var scene = new CharacterScene(Small);
        var characters = new List<EntityId>();
        for (int i = 0; i < 200; i++)
        {
            characters.Add(scene.AddCharacter(scene.Voxels.At(2.5 + (i % 20 * 1.5), 0, 2.5 + (i / 20 * 1.5))));
        }

        scene.Voxels.Tick(5);
        TimeSpan idle = TimeSpan.Zero;
        for (int tick = 0; tick < 30; tick++)
        {
            scene.Voxels.Tick();
            idle += scene.Physics.Statistics.LastStep;
        }

        TimeSpan walking = TimeSpan.Zero;
        for (int tick = 0; tick < 30; tick++)
        {
            foreach (EntityId character in characters)
            {
                scene.Walk(character, new Vector3(tick / 10 % 2 == 0 ? 2 : -2, 0, 1), jumpSpeed: tick % 10 == 0 ? 4 : 0);
            }

            scene.Voxels.Tick();
            walking += scene.Physics.Statistics.LastStep;
        }

        output.WriteLine($"200 characters: {idle.TotalMilliseconds / 30:F3} ms per step standing, {walking.TotalMilliseconds / 30:F3} ms walking and jumping ({walking.TotalMilliseconds / 30 / 200 * 1000:F1} µs per character).");
        Assert.All(characters, character => Assert.False(scene.State(character).IsFrozen));
        Assert.True(walking.TotalMilliseconds / 30 < 50, $"A step takes {walking.TotalMilliseconds / 30} ms.");
    }

    /// <summary>Keeps two characters walking back and forth, one jumping now and then.</summary>
    private static void Shuttle(CharacterScene scene, EntityId walker, EntityId rider, int tick)
    {
        float way = tick / 10 % 2 == 0 ? 2 : -2;
        scene.Walk(walker, new Vector3(way, 0, way), jumpSpeed: tick % 15 == 0 ? 4 : 0);
        scene.Walk(rider, new Vector3(0, 0, way / 2));
    }

    /// <summary>Blocks along Z at one X and one height, from <paramref name="first"/> to <paramref name="last"/>.</summary>
    private static (BlockCoordinate Position, uint State)[] Row(CharacterScene scene, int x, int y, uint state, int first = 10, int last = 22) =>
        [.. Enumerable.Range(first, last - first + 1).Select(z => (scene.Voxels.Block(x, y, z), state))];

    /// <summary>Blocks along X at one Z and one height.</summary>
    private static (BlockCoordinate Position, uint State)[] Column(CharacterScene scene, int z, int y, uint state, int first, int last) =>
        [.. Enumerable.Range(first, last - first + 1).Select(x => (scene.Voxels.Block(x, y, z), state))];
}

/// <summary>A voxel scene with helpers for characters: their components, their intents, and the events of the steps.</summary>
internal sealed class CharacterScene : IDisposable
{
    public CharacterScene(ChunkArea area, WorldPosition? center = null)
    {
        Voxels = new VoxelScene(area, center);
        Assert.True(Voxels.Probe.Entities.TryGetComponent(out ComponentType<MovementIntent> intent));
        Assert.True(Voxels.Probe.Entities.TryGetComponent(out ComponentType<CharacterState> state));
        Assert.True(Voxels.Probe.Entities.TryGetComponent(out ComponentType<BodyVelocity> velocity));
        Intent = intent;
        StateComponent = state;
        VelocityComponent = velocity;
        Physics.SubscribeStepEnd(Voxels.Probe.Context, Events);
    }

    public VoxelScene Voxels { get; }

    public IPhysicsSystem Physics => Voxels.Physics;

    public EntityWorld World => Voxels.Probe.World;

    public ComponentType<MovementIntent> Intent { get; }

    public ComponentType<CharacterState> StateComponent { get; }

    public ComponentType<BodyVelocity> VelocityComponent { get; }

    public CharacterEvents Events { get; } = new();

    public EntityId AddCharacter(in WorldPosition feet)
    {
        EntityId entity = Voxels.Probe.CreateEntity(WorldTransform.At(feet));
        Physics.AddCharacter(DimensionId.Default, entity, CharacterBody.Default);
        return entity;
    }

    public void Walk(EntityId character, Vector3 velocity, float jumpSpeed = 0) =>
        World.Set(character, Intent, new MovementIntent { Velocity = velocity, JumpSpeed = jumpSpeed });

    public CharacterState State(EntityId character) => World.Read(character, StateComponent);

    public WorldTransform Transform(EntityId character) => World.Read(character, Voxels.Probe.Entities.WorldTransform);

    public Vector3 Local(EntityId character) => World.Read(character, Voxels.Probe.Entities.LocalTransform).Position;

    public Vector3 Velocity(EntityId character) => World.Read(character, VelocityComponent).Linear;

    public Vector3 Angular(EntityId character) => World.Read(character, VelocityComponent).Angular;

    /// <summary>Gets the offset of the feet of a character from the corner of the chunk at the center of the scene, at y = 0.</summary>
    public Vector3 Offset(EntityId character) =>
        Transform(character).Position.RelativeTo(new WorldPosition(Voxels.Center.ChunkX, 0, Voxels.Center.ChunkZ, Vector3.Zero));

    /// <summary>Creates a grid with a platform of stone, 7 by 7 cells around its origin, at y = 0 in the grid: its top is 1 block above the origin.</summary>
    public Grid Platform(in WorldPosition origin, Quaternion rotation)
    {
        Grid grid = Voxels.Probe.Voxels.CreateGrid(Voxels.Probe.Context, DimensionId.Default, new GridOptions { Transform = new WorldTransform(origin, rotation, Vector3.One) });
        uint stone = Voxels.State("stone");
        for (int z = -3; z <= 3; z++)
        {
            for (int x = -3; x <= 3; x++)
            {
                var block = new BlockCoordinate(x, 0, z);
                grid.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(block)).Palette.Set(VoxelCoordinates.ToIndex(block), stone);
            }
        }

        return grid;
    }

    /// <summary>Makes a grid a kinematic body, centered on its origin.</summary>
    public BodyHandle MakeKinematic(Grid grid) =>
        Physics.AddBody(DimensionId.Default, grid.Entity, new BodyDescription { Kind = BodyKind.Kinematic, Shape = Physics.CreateBox(new Vector3(7, 1, 7)), Center = new Vector3(0, 0.5f, 0) });

    public void Dispose() => Voxels.Dispose();
}

/// <summary>Keeps the steps and the landings of the characters that the steps publish.</summary>
internal sealed class CharacterEvents : IPhysicsStepObserver
{
    public List<CharacterStep> Steps { get; } = new(1024);

    public List<CharacterLanding> Landings { get; } = new(64);

    public bool Keep { get; set; } = true;

    public void Clear()
    {
        Steps.Clear();
        Landings.Clear();
    }

    public void OnStepEnd(in PhysicsStepEnd step)
    {
        if (!Keep)
        {
            return;
        }

        foreach (CharacterStep walked in step.CharacterSteps)
        {
            if (Steps.Count < Steps.Capacity)
            {
                Steps.Add(walked);
            }
        }

        foreach (CharacterLanding landing in step.CharacterLandings)
        {
            if (Landings.Count < Landings.Capacity)
            {
                Landings.Add(landing);
            }
        }
    }
}
