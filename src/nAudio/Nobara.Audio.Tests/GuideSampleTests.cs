// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Audio.Tests.Samples;
using Nobara.Audio.Tests.Support;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Audio.Tests;

/// <summary>The sample of the guide "Play a sound" does what the guide says.</summary>
public sealed class GuideSampleTests : IDisposable
{
    private readonly AudioHarness _harness = new();

    public GuideSampleTests()
    {
        _harness.WithGame(AudioHarness.Compiled(_harness.CopyResources()));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void PlaysTheSoundsOfTheMill()
    {
        using Engine engine = _harness.Start();
        _harness.Sounds.CompleteDecoding();
        ModuleContext context = _harness.Probe.Context;
        var mill = new MillSounds(context);
        mill.ClaimListener(context, default);

        mill.ButtonPressed();
        mill.DoorCreaked(WorldPosition.FromBlocks(3, 0, 0));
        mill.CartRolled(new Cart());
        mill.StartGrinding(WorldPosition.FromBlocks(0, 0, 4));
        mill.BuildMill(context, WorldPosition.FromBlocks(0, 10, 0));
        _harness.Frames(engine, 1);
        Assert.Equal(new AudioStatistics(4, 1, 1), _harness.System.Statistics);

        // Silent at its start, the grinding is virtual until its fade makes it heard.
        Assert.True(mill.Grinding.IsActive);
        Assert.False(mill.Grinding.IsReal);

        mill.MoveListener(WorldPosition.FromBlocks(1, 0, 0), Quaternion.Identity);
        mill.WindChanged(0.5f);
        _harness.Frames(engine, 120);
        Assert.True(mill.Grinding.IsReal);
        Assert.Equal(1f, mill.Sails!.Pitch);

        mill.StopGrinding();
        mill.MillUnloaded();
        _harness.Frames(engine, 120);
        Assert.False(mill.Grinding.IsActive);
        Assert.False(mill.Sails.Playing.IsActive);
        mill.MillLoaded();
        _harness.Frames(engine, 1);
        Assert.True(mill.Sails.Playing.IsActive);
        mill.MillDestroyed();
        Assert.Empty(_harness.System.Emitters);
    }

    private sealed class Cart : ISoundAnchor
    {
        public bool TryGetTransform(out WorldTransform transform)
        {
            transform = WorldTransform.At(WorldPosition.FromBlocks(-2, 0, 0));
            return true;
        }
    }
}
