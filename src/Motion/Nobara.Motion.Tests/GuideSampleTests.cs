// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Motion.Tests.Samples;
using Nobara.Motion.Tests.Support;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion.Tests;

/// <summary>The samples of the guides "Move a grid" and "Link two grids" do what the guides say.</summary>
[Collection(Serial.Name)]
public sealed class GuideSampleTests
{
    [Fact]
    public void AnAirshipFloatsAndGoesForwardAndALiftRises()
    {
        AirshipMotion? sample = null;
        using var scene = new MotionScene(configure: probe => sample = new AirshipMotion(probe.Context));
        Grid airship = sample!.Launch(scene.At(4, 12, 14), scene.State("wood"));
        Grid lift = sample.BuildLift(scene.At(24, 2, 24), scene.State("wood"));

        scene.Tick(60);

        GridMotionState state = scene.Motion.GetState(airship);
        Assert.InRange(scene.Height(airship), 11.9, 12.1);   // no gravity
        Assert.True(state.LinearVelocity.X > 1, $"The airship goes at {state.LinearVelocity}.");
        Assert.True(MathF.Abs(state.AngularVelocity.Y) > 0.01f, "An engine off the center of mass turns the airship.");
        Assert.InRange(scene.Height(lift), 2 + (2 * AirshipMotion.LiftSpeed) - 0.2, 2 + (2 * AirshipMotion.LiftSpeed) + 0.2);
    }

    [Fact]
    public void TheSailsTurnOnTheTerrainAndTheBoomOnTheMovingFerry()
    {
        HarborJoints? sample = null;
        using var scene = new MotionScene(configure: probe => sample = new HarborJoints(probe.Context));
        uint wood = scene.State("wood");
        Grid sails = sample!.BuildSails(scene.At(8, 10, 8), wood);
        (Grid ferry, Grid boom) = sample.BuildFerry(scene.At(16, 3, 20), wood);
        scene.Tick(30);

        scene.Motion.SetVelocity(ferry, new Vector3(1, 0, 0), Vector3.Zero);
        float drift = 0;
        for (int tick = 0; tick < 60; tick++)
        {
            scene.Tick();
            drift = MathF.Max(drift, boom.Transform.TransformPoint(new Vector3(0.5f, 0, 0.5f)).RelativeTo(ferry.Transform.TransformPoint(new Vector3(1.5f, 1.1f, 1.5f))).Length());
        }

        Assert.Equal(HarborJoints.SailSpeed, scene.Motion.GetState(sails).AngularVelocity.Z, 0.02f);
        Assert.Equal(HarborJoints.BoomSpeed, scene.Motion.GetState(boom).AngularVelocity.Y, 0.02f);
        Assert.True(drift < 0.05f, $"The foot of the boom left the deck by {drift} blocks.");
        sample.Dispose();
    }

    [Fact]
    public void TheGateSlidesOpenAndShut()
    {
        HarborJoints? sample = null;
        using var scene = new MotionScene(configure: probe => sample = new HarborJoints(probe.Context));
        Grid gate = sample!.BuildGate(scene.At(10, 0.5, 10), scene.State("wood"));
        scene.Tick(5);
        double start = gate.Transform.Position.RelativeTo(scene.At(0, 0, 0)).Z;

        sample.OpenGate();
        scene.Tick(60);
        double open = gate.Transform.Position.RelativeTo(scene.At(0, 0, 0)).Z;
        sample.CloseGate();
        scene.Tick(60);

        Assert.InRange(open - start, 1.9, 2.1);
        Assert.InRange(gate.Transform.Position.RelativeTo(scene.At(0, 0, 0)).Z - start, -0.1, 0.1);
        Assert.InRange(scene.Height(gate), 0.45, 0.55);   // held up by the slider
        sample.Dispose();
    }

    [Fact]
    public void ACharacterCaughtByTheFerryLandsOnItsDeck()
    {
        HarborJoints? sample = null;
        using var scene = new MotionScene(configure: probe => sample = new HarborJoints(probe.Context));
        uint wood = scene.State("wood");
        BlockCoordinate corner = VoxelCoordinates.ToBlock(scene.At(0, 0, 0));
        scene.Place(scene.Terrain, wood, [.. MotionScene.Box(24, 0, 18, 24, 0, 24).Select(cell => new BlockCoordinate(corner.X + cell.X, cell.Y, corner.Z + cell.Z))]);
        (Grid ferry, _) = sample!.BuildFerry(scene.At(14, 0, 20), wood);
        EntityId character = scene.Probe.CreateEntity(WorldTransform.At(scene.At(21.5, 0, 21.5)));
        scene.Physics.AddCharacter(DimensionId.Default, character, CharacterBody.Default);
        scene.Tick(2);

        scene.Motion.SetVelocity(ferry, new Vector3(2, 0, 0), Vector3.Zero);
        scene.Tick(75);   // the ferry pushes the character into a low wall of the terrain, which the ferry goes through

        Assert.True(scene.Probe.Entities.TryGetComponent(out ComponentType<CharacterState> state));
        Assert.Equal(ferry.Entity, scene.Probe.World.Read(character, state).Support);
        sample.Dispose();
    }
}
