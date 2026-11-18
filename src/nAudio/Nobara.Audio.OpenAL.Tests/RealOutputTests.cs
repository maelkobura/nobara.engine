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

    /// <summary>
    /// A manual check at the headphones. With <c>NOBARA_AUDIO_ORBIT=1</c> it
    /// turns a sound around the listener for 60 seconds on the default output
    /// with HRTF on: 20 seconds in the horizontal plane (ahead, right, behind,
    /// left), then 20 seconds in the vertical plane (ahead, above, behind,
    /// below), then 20 seconds back in the horizontal plane. Unplug the
    /// headphones, or change the default output, while it plays: the sound
    /// goes on, on the new output. Without the variable the test does nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "Audio")]
    public void TurnsASoundAroundTheListenerOnTheDefaultOutput()
    {
        if (Environment.GetEnvironmentVariable("NOBARA_AUDIO_ORBIT") != "1")
        {
            return;
        }

        using var harness = new OpenALHarness(defaultOutput: true).Setting("audio.hrtf", "on");
        using Engine engine = harness.Start();
        ALDevice? device = harness.Backend.Opened;
        Assert.NotNull(device);
        Assert.True(device.HrtfEnabled, "HRTF is not available on this output");

        // A burst of noise-like tone with harmonics, so that direction is easy to hear.
        short[] burst = new short[Signals.Rate / 2];
        for (int i = 0; i < burst.Length; i++)
        {
            double t = (double)i / Signals.Rate;
            double envelope = Math.Exp(-8 * (t % 0.25));
            double sample = Math.Sin(2 * Math.PI * 600 * t) + (0.5 * Math.Sin(2 * Math.PI * 1800 * t)) + (0.25 * Math.Sin(2 * Math.PI * 5400 * t));
            burst[i] = (short)(sample * envelope * 0.3 * short.MaxValue);
        }

        ALDevice? current = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 60)
        {
            // The frame polls the device: after a loss, the backend has a new one, and the sound starts again on it.
            engine.MainContext.RunFrame();
            ALDevice opened = harness.Backend.Opened!;
            if (!ReferenceEquals(opened, current))
            {
                current = opened;
                current.SetVoice(0, Voice(0, 0, -3));
                current.Play(0, current.CreateBuffer(burst, 1, Signals.Rate), loop: true, startFrame: 0);
            }

            double turn = watch.Elapsed.TotalSeconds % 20 / 20 * 2 * Math.PI;
            bool vertical = watch.Elapsed.TotalSeconds is >= 20 and < 40;
            float x = vertical ? 0f : (float)Math.Sin(turn) * 3f;
            float y = vertical ? (float)Math.Sin(turn) * 3f : 0f;
            float z = -(float)Math.Cos(turn) * 3f;
            current.SetVoice(0, Voice(x, y, z));
            current.SetListener(new ListenerParameters(-System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.Zero, 1f));
            current.Commit();
            Thread.Sleep(20);
        }
    }

    private static VoiceParameters Voice(float x, float y, float z) => new()
    {
        Gain = 1f,
        Pitch = 1f,
        Spatial = true,
        Position = new System.Numerics.Vector3(x, y, z),
        LowPassCutoff = VoiceParameters.OpenCutoff,
        ReverbSlot = -1,
    };
}
