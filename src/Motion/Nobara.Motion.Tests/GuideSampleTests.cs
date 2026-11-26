// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Motion.Tests.Samples;
using Nobara.Motion.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Motion.Tests;

/// <summary>The sample of the guide "Move a grid" does what the guide says.</summary>
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
}
