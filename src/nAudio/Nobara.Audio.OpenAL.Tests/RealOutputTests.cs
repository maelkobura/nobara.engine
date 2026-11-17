// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.OpenAL.Tests.Support;
using Nobara.Core;
using Nobara.Core.Logging;

namespace Nobara.Audio.OpenAL.Tests;

/// <summary>The module of the backend on the default output of the machine.</summary>
public sealed class RealOutputTests
{
    /// <summary>Runs where an output device exists: the CI has none and skips it.</summary>
    [Fact]
    [Trait("Category", "Audio")]
    public void OpensTheDefaultOutputThroughTheModule()
    {
        using var harness = new OpenALHarness(realModule: true);
        using Engine engine = harness.Start();

        IAudioSystem audio = harness.System;
        Assert.True(audio.IsActive, "no audio output device is available on this machine");
        Assert.Equal("openal", audio.BackendName);
        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Audio device opened by backend 'openal': OpenAL Soft"));
        Assert.DoesNotContain(harness.Log.Entries, e => e.Level >= LogLevel.Warning);
        engine.MainContext.RunFrame();
    }

    /// <summary>Runs everywhere: with or without an output, the game starts, and it says why it has no sound.</summary>
    [Fact]
    public void StartsWithTheModuleWhetherOrNotAnOutputExists()
    {
        using var harness = new OpenALHarness(realModule: true);
        using Engine engine = harness.Start();

        IAudioSystem audio = harness.System;
        if (audio.IsActive)
        {
            Assert.Equal("openal", audio.BackendName);
        }
        else
        {
            Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("'openal' cannot open a device, so the game runs without sound"));
        }

        engine.MainContext.RunFrame();
    }
}
