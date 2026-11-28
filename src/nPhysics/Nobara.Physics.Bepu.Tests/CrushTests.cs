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

/// <summary>Kinematic grids that push characters, and crush them against what does not move.</summary>
[Collection(Serial.Name)]
public sealed class CrushTests
{
    private static readonly ChunkArea Small = new(1, 1);

    [Fact]
    public void AKinematicGridPushesACharacterInItsWay()
    {
        using var scene = new CharacterScene(Small);
        (Grid wall, BodyHandle body) = Wall(scene, scene.Voxels.At(12, 0, 16));
        EntityId character = scene.AddCharacter(scene.Voxels.At(14.5, 0, 16.5));
        scene.Voxels.Tick();

        scene.Physics.SetVelocity(body, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Voxels.Tick(60);

        float face = WallX(scene, wall) + 1;
        Assert.Equal(16f, WallX(scene, wall), 0.05f);                 // never held back
        Assert.Equal(face + 0.3f, scene.Offset(character).X, 0.01f);   // just in front of the wall
        Assert.Equal(0f, scene.Offset(character).Y, 0.01f);
        Assert.True(scene.State(character).IsGrounded);
    }

    [Theory]
    [InlineData(16.0, 16.0)]
    [InlineData(1_000_000_016.0, -999_999_984.0)]
    public void AGridThatCrushesACharacterAgainstAWallStopsAndGoesOnOnceTheWayIsFree(double x, double z)
    {
        using var scene = new CharacterScene(Small, WorldPosition.FromBlocks(x, 16, z));
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Enumerable.Range(0, 3).SelectMany(y => Enumerable.Range(13, 7).Select(z => (scene.Voxels.Block(18, y, z), stone)))]);
        (Grid wall, BodyHandle body) = Wall(scene, scene.Voxels.At(12, 0, 16));
        EntityId character = scene.AddCharacter(scene.Voxels.At(14.5, 0, 16.5));
        scene.Voxels.Tick();

        scene.Physics.SetVelocity(body, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Voxels.Tick(90);

        Assert.Equal(18f - 0.3f, scene.Offset(character).X, 0.01f);   // against the wall of the terrain
        Assert.InRange(WallX(scene, wall) + 1, 17.4f - 0.1f, 17.4f + 1e-3f);   // held at the step before it went into the character
        Assert.Equal(2f, scene.Physics.GetState(body).LinearVelocity.X, 1e-4f);   // still driven

        scene.World.Set(character, scene.Voxels.Probe.Entities.WorldTransform, WorldTransform.At(scene.Voxels.At(14.5, 4, 12)));   // the character leaves the way
        scene.Voxels.Tick(15);

        Assert.InRange(WallX(scene, wall) + 1, 18.3f, 18.5f);   // goes on, through the terrain as a kinematic body
    }

    [Fact]
    public void AResolverThatMovesTheCharacterLetsTheGridGoOn()
    {
        using var scene = new CharacterScene(Small);
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Enumerable.Range(0, 3).SelectMany(y => Enumerable.Range(13, 7).Select(z => (scene.Voxels.Block(18, y, z), stone)))]);
        (Grid wall, BodyHandle body) = Wall(scene, scene.Voxels.At(12, 0, 16));
        EntityId character = scene.AddCharacter(scene.Voxels.At(14.5, 0, 16.5));
        var resolver = new Resolver(crush => CrushResponse.MoveTo(scene.Voxels.At(16.5, 0, 12.5)));
        using (scene.Physics.SetCrushResolver(scene.Voxels.Probe.Context, resolver))
        {
            scene.Voxels.Tick();
            scene.Physics.SetVelocity(body, new Vector3(2, 0, 0), Vector3.Zero);
            scene.Voxels.Tick(90);
        }

        CharacterCrush first = Assert.Single(resolver.Crushes);
        Assert.Equal(character, first.Character);
        Assert.Same(wall, first.Grid);
        Assert.Equal(body, first.Body);
        Assert.Equal(1f, first.Direction.X, 0.01f);
        Assert.Equal(DimensionId.Default, first.Dimension);
        Assert.Equal(new Vector3(16.5f, 0, 12.5f), scene.Offset(character), new Vector3Comparer(0.01f));
        Assert.Equal(18f, WallX(scene, wall), 0.05f);   // not held back
    }

    [Fact]
    public void AResolverThatLetsTheGridPassLeavesTheCharacterInIt()
    {
        using var scene = new CharacterScene(Small);
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Enumerable.Range(0, 3).SelectMany(y => Enumerable.Range(13, 7).Select(z => (scene.Voxels.Block(18, y, z), stone)))]);
        (Grid wall, BodyHandle body) = Wall(scene, scene.Voxels.At(12, 0, 16));
        EntityId character = scene.AddCharacter(scene.Voxels.At(14.5, 0, 16.5));
        using CrushResolverHandle handle = scene.Physics.SetCrushResolver(scene.Voxels.Probe.Context, new Resolver(_ => CrushResponse.Pass));
        scene.Voxels.Tick();

        scene.Physics.SetVelocity(body, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Voxels.Tick(90);

        Assert.Equal(18f, WallX(scene, wall), 0.05f);
        Assert.Equal(18f - 0.3f, scene.Offset(character).X, 0.01f);   // pushed to the terrain, then left where it was
    }

    [Fact]
    public void AGridThatRisesUnderACharacterLiftsItAndCarriesIt()
    {
        using var scene = new CharacterScene(Small);
        Grid lift = scene.Platform(scene.Voxels.At(16, -1.5, 16), Quaternion.Identity);   // its top half a block under the ground
        BodyHandle body = scene.MakeKinematic(lift);
        EntityId character = scene.AddCharacter(scene.Voxels.At(16.5, 0, 16.5));
        scene.Voxels.Tick(2);
        Assert.Equal(EntityId.Null, scene.State(character).Support);   // on the terrain

        scene.Physics.SetVelocity(body, new Vector3(0, 1, 0), Vector3.Zero);
        scene.Voxels.Tick(30);

        double top = lift.Transform.Position.RelativeTo(scene.Voxels.At(0, 0, 0)).Y + 1;
        Assert.Equal(top, scene.Offset(character).Y, 0.02);
        Assert.Equal(lift.Entity, scene.State(character).Support);
        Assert.True(scene.State(character).IsGrounded);
    }

    [Fact]
    public void ASecondResolverIsRefusedUntilTheFirstIsRemoved()
    {
        using var scene = new CharacterScene(Small);
        var resolver = new Resolver(_ => CrushResponse.Block);
        CrushResolverHandle first = scene.Physics.SetCrushResolver(scene.Voxels.Probe.Context, resolver);

        Assert.Throws<InvalidOperationException>(() => scene.Physics.SetCrushResolver(scene.Voxels.Probe.Context, resolver));
        first.Dispose();
        scene.Physics.SetCrushResolver(scene.Voxels.Probe.Context, resolver).Dispose();
    }

    [Fact]
    public void AGridHeldByACrushAllocatesNothing()
    {
        using var scene = new CharacterScene(Small);
        uint stone = scene.Voxels.State("stone");
        scene.Voxels.Place([.. Enumerable.Range(0, 3).SelectMany(y => Enumerable.Range(13, 7).Select(z => (scene.Voxels.Block(18, y, z), stone)))]);
        (Grid wall, BodyHandle body) = Wall(scene, scene.Voxels.At(12, 0, 16));
        scene.AddCharacter(scene.Voxels.At(14.5, 0, 16.5));
        scene.Voxels.Tick();
        scene.Physics.SetVelocity(body, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Voxels.Tick(90);
        float held = WallX(scene, wall);

        // The runtime may allocate now and then on this thread, such as while it compiles methods again: a round without allocation must come.
        long allocated = -1;
        for (int round = 0; round < 10 && allocated != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            scene.Voxels.Tick(30);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(held, WallX(scene, wall), 1e-4f);
    }

    /// <summary>Creates a kinematic grid with a wall of stone 1 block thick, 3 high, and 5 long along Z, whose cells start at its origin along X.</summary>
    private static (Grid Grid, BodyHandle Body) Wall(CharacterScene scene, in WorldPosition origin)
    {
        Grid grid = scene.Voxels.Probe.Voxels.CreateGrid(scene.Voxels.Probe.Context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(origin) });
        uint stone = scene.Voxels.State("stone");
        for (int y = 0; y < 3; y++)
        {
            for (int z = -2; z <= 2; z++)
            {
                var block = new BlockCoordinate(0, y, z);
                grid.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(block)).Palette.Set(VoxelCoordinates.ToIndex(VoxelCoordinates.ToLocal(block)), stone);
            }
        }

        BodyHandle body = scene.Physics.AddBody(DimensionId.Default, grid.Entity, new BodyDescription
        {
            Kind = BodyKind.Kinematic,
            Shape = scene.Physics.CreateBox(new Vector3(1, 3, 5)),
            Center = new Vector3(0.5f, 1.5f, 0.5f),
        });
        return (grid, body);
    }

    /// <summary>Gets where the cells of a wall start along X, from the corner of the chunk at the center of the scene.</summary>
    private static float WallX(CharacterScene scene, Grid wall) => wall.Transform.Position.RelativeTo(scene.Voxels.At(0, 0, 0)).X;

    private sealed class Resolver(Func<CharacterCrush, CrushResponse> answer) : ICrushResolver
    {
        public List<CharacterCrush> Crushes { get; } = [];

        public CrushResponse Resolve(in CharacterCrush crush)
        {
            Crushes.Add(crush);
            return answer(crush);
        }
    }

    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.Distance(x, y) <= tolerance;

        public int GetHashCode(Vector3 obj) => 0;
    }
}
