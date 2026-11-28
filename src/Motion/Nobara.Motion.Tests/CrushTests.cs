// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Motion.Tests.Support;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion.Tests;

/// <summary>A kinematic piston that crushes a character against a wall, and the handlers that decide what happens.</summary>
[Collection(Serial.Name)]
public sealed class CrushTests
{
    [Fact]
    public void APistonThatCrushesACharacterStopsUntilTheWayIsFree()
    {
        using var scene = new MotionScene();
        (Grid piston, EntityId character) = Setup(scene);

        scene.Motion.SetVelocity(piston, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(90);

        Assert.Equal(18f - 0.3f, Feet(scene, character).X, 0.01f);   // pushed to the wall of the terrain
        float held = PistonFace(scene, piston);
        Assert.InRange(held, 17.3f, 17.4f + 1e-3f);
        Assert.True(scene.Motion.GetState(piston).LinearVelocity.X > 1.9f);   // still driven
        Assert.True(((MotionSystem)scene.Motion).Crushes > 0);

        scene.Physics.RemoveCharacter(DimensionId.Default, character);
        scene.Probe.World.Destroy(character);   // the way is free
        scene.Tick(15);

        Assert.InRange(PistonFace(scene, piston), held + 0.9f, held + 1.1f);
    }

    [Fact]
    public void AHandlerThatMovesTheCharacterLetsThePistonGoOn()
    {
        using var scene = new MotionScene();
        (Grid piston, EntityId character) = Setup(scene);
        var crushes = new List<CharacterCrush>();
        using CrushHandlerHandle handle = scene.Motion.AddCrushHandler(scene.Probe.Context, new Handler(crush =>
        {
            crushes.Add(crush);
            return CrushResponse.MoveTo(scene.At(16.5, 3, 16.5));   // on top of the piston
        }));

        scene.Motion.SetVelocity(piston, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(90);

        CharacterCrush crush = Assert.Single(crushes);
        Assert.Same(piston, crush.Grid);
        Assert.Equal(character, crush.Character);
        Assert.Equal(1f, crush.Direction.X, 0.01f);
        Assert.Equal(17f, PistonFace(scene, piston) - 2, 0.1f);   // never held back: 13 + 6 blocks
        Assert.Equal(3f, Feet(scene, character).Y, 0.05f);   // stands on the piston, which carries it
        Assert.Equal(piston.Entity, scene.Probe.World.Read(character, Component<CharacterState>(scene)).Support);
    }

    [Fact]
    public void TheHandlersAreAskedFromTheHighestPriorityAndTheFirstAnswerWins()
    {
        using var scene = new MotionScene();
        (Grid piston, _) = Setup(scene);
        var asked = new List<string>();
        using CrushHandlerHandle low = scene.Motion.AddCrushHandler(scene.Probe.Context, new Handler(_ =>
        {
            asked.Add("low");
            return CrushResponse.Pass;
        }), priority: -1);
        using CrushHandlerHandle declines = scene.Motion.AddCrushHandler(scene.Probe.Context, new Handler(_ =>
        {
            asked.Add("declines");
            return null;
        }), priority: 5);
        using CrushHandlerHandle high = scene.Motion.AddCrushHandler(scene.Probe.Context, new Handler(_ =>
        {
            asked.Add("high");
            return null;
        }), priority: 10);
        using CrushHandlerHandle later = scene.Motion.AddCrushHandler(scene.Probe.Context, new Handler(_ =>
        {
            asked.Add("same priority, added later");
            return null;
        }), priority: 5);

        scene.Motion.SetVelocity(piston, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(90);

        Assert.Equal(["high", "declines", "same priority, added later", "low"], asked.Take(4));
        Assert.Equal(19f, PistonFace(scene, piston), 0.1f);   // the lowest handler lets it pass through the character
    }

    [Fact]
    public void ARemovedHandlerIsNotAskedAndTheDefaultBlocks()
    {
        using var scene = new MotionScene();
        (Grid piston, _) = Setup(scene);
        int asked = 0;
        CrushHandlerHandle handle = scene.Motion.AddCrushHandler(scene.Probe.Context, new Handler(_ =>
        {
            asked++;
            return CrushResponse.Pass;
        }));
        handle.Dispose();

        scene.Motion.SetVelocity(piston, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(90);

        Assert.Equal(0, asked);
        Assert.InRange(PistonFace(scene, piston), 17.3f, 17.4f + 1e-3f);
    }

    [Fact]
    public void APistonHeldByACrushAllocatesNothing()
    {
        using var scene = new MotionScene();
        (Grid piston, _) = Setup(scene);
        scene.Motion.SetVelocity(piston, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(90);
        float held = PistonFace(scene, piston);

        long before = GC.GetAllocatedBytesForCurrentThread();
        scene.Tick(30);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(held, PistonFace(scene, piston), 1e-4f);
    }

    /// <summary>
    /// Builds a wall of the terrain at X = 18, a kinematic piston of 2 by 3 by
    /// 5 blocks whose face is at X = 13, and a character in between at
    /// X = 14.5.
    /// </summary>
    private static (Grid Piston, EntityId Character) Setup(MotionScene scene)
    {
        uint stone = scene.State("stone");
        BlockCoordinate corner = VoxelCoordinates.ToBlock(scene.At(0, 0, 0));
        scene.Place(scene.Terrain, stone, [.. MotionScene.Box(18, 0, 13, 18, 2, 19).Select(cell => new BlockCoordinate(corner.X + cell.X, cell.Y, corner.Z + cell.Z))]);
        Grid piston = scene.Grid(scene.At(11, 0, 14), stone, MotionScene.Box(0, 0, 0, 1, 2, 4));
        scene.Motion.Add(piston, GridMotionMode.Kinematic);
        EntityId character = scene.Probe.CreateEntity(WorldTransform.At(scene.At(14.5, 0, 16.5)));
        scene.Physics.AddCharacter(DimensionId.Default, character, CharacterBody.Default);
        scene.Tick(2);
        return (piston, character);
    }

    /// <summary>Gets where the face of the piston is along X, from the corner of the chunk at the center of the scene.</summary>
    private static float PistonFace(MotionScene scene, Grid piston) => piston.Transform.Position.RelativeTo(scene.At(0, 0, 0)).X + 2;

    private static Vector3 Feet(MotionScene scene, EntityId character) =>
        scene.Probe.World.Read(character, scene.Probe.Entities.WorldTransform).Position.RelativeTo(scene.At(0, 0, 0));

    private static ComponentType<T> Component<T>(MotionScene scene)
        where T : unmanaged
    {
        Assert.True(scene.Probe.Entities.TryGetComponent(out ComponentType<T> type));
        return type;
    }

    private sealed class Handler(Func<CharacterCrush, CrushResponse?> answer) : ICrushHandler
    {
        public bool TryResolve(in CharacterCrush crush, out CrushResponse response)
        {
            CrushResponse? decided = answer(crush);
            response = decided.GetValueOrDefault();
            return decided.HasValue;
        }
    }
}
