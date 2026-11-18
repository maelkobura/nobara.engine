// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Audio.OpenAL.Tests.Support;
using Nobara.Core;

namespace Nobara.Audio.OpenAL.Tests;

/// <summary>The device of OpenAL Soft behind the interface of nAudio, on its loopback device.</summary>
public sealed class DeviceTests : IDisposable
{
    private static readonly ListenerParameters Facing = new(-Vector3.UnitZ, Vector3.UnitY, Vector3.Zero, 1f);

    private readonly OpenALHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ReportsTheCapabilitiesOfTheLibrary()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;

        AudioCapabilities capabilities = device.Capabilities;
        Assert.Equal(AudioFeatures.Hrtf | AudioFeatures.Filters | AudioFeatures.AuxiliarySends | AudioFeatures.Effects, capabilities.Features);
        Assert.InRange(capabilities.MaxVoices, 64, 4096);
        Assert.Equal(ALDevice.MaxSlots, capabilities.MaxReverbSlots);
        Assert.True(_harness.System.IsActive);
        Assert.Equal("openal", _harness.System.BackendName);
        Assert.Equal(capabilities, _harness.System.Capabilities);
    }

    [Fact]
    public void PlaysASoundInMemoryToTheEnd()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(0.1), 1, Signals.Rate);
        device.SetVoice(3, Parameters());

        device.Play(3, buffer, loop: false, startFrame: 0);
        float[] first = Signals.Render(device, 0.05);
        Assert.True(device.Inspect(3).Playing);
        Signals.Render(device, 0.2);

        Assert.True(Signals.Energy(first, 0) > 0.001);
        Assert.False(device.Inspect(3).Playing);
        device.Stop(3);
        device.DestroyBuffer(buffer);
        device.Commit();
    }

    [Fact]
    public void LoopsASoundFromTheFrameItIsGivenAndStopsItOnRequest()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(0.1), 1, Signals.Rate);
        device.SetVoice(0, Parameters());

        device.Play(0, buffer, loop: true, startFrame: 2000);
        Signals.Render(device, 0.5);

        SourceReading playing = device.Inspect(0);
        Assert.True(playing.Playing);
        Assert.True(playing.Looping);
        device.Stop(0);
        Assert.False(device.Inspect(0).Playing);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void PlaysAStreamBlockByBlockAndStartsAgainAfterARunOut()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        short[] block = Signals.Sine(0.05);
        device.SetVoice(1, Parameters());

        for (int i = 0; i < 3; i++)
        {
            device.Queue(1, block, 1, Signals.Rate);
        }

        Assert.Equal(3, device.GetQueuedBlocks(1));
        float[] heard = Signals.Render(device, 0.07);

        // The block that plays is not finished: it counts with the one that waits.
        Assert.Equal(2, device.GetQueuedBlocks(1));
        Signals.Render(device, 0.2);
        Assert.Equal(0, device.GetQueuedBlocks(1));
        Assert.True(Signals.Energy(heard, 0) > 0.001);

        // The source ran out of blocks and stopped: the next block plays.
        device.Queue(1, block, 1, Signals.Rate);
        float[] again = Signals.Render(device, 0.03);
        Assert.True(Signals.Energy(again, 0) > 0.001);
        device.Stop(1);
        Assert.Equal(0, device.GetQueuedBlocks(1));
    }

    [Fact]
    public void PausesAndResumesAVoice()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1), 1, Signals.Rate);
        device.SetVoice(0, Parameters());
        device.Play(0, buffer, loop: false, startFrame: 0);

        device.Pause(0);
        float[] silent = Signals.Render(device, 0.05);
        Assert.True(device.Inspect(0).Paused);
        device.Resume(0);
        float[] heard = Signals.Render(device, 0.05);

        Assert.Equal(0, Signals.Energy(silent, 0), 8);
        Assert.True(Signals.Energy(heard, 0) > 0.001);
        device.Stop(0);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void PlacesASpatialSoundOnItsSideOfTheListener()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetHrtf(HrtfMode.Off);
        device.SetListener(Facing);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1), 1, Signals.Rate);
        device.SetVoice(0, Parameters() with { Position = new Vector3(-4, 0, 0) });
        device.SetVoice(1, Parameters() with { Position = new Vector3(4, 0, 0) });

        device.Play(0, buffer, loop: true, startFrame: 0);
        float[] left = Signals.Render(device, 0.1);
        device.Stop(0);
        device.Play(1, buffer, loop: true, startFrame: 0);
        float[] right = Signals.Render(device, 0.1);

        Assert.True(Signals.Energy(left, 0) > 4 * Signals.Energy(left, 1), "a sound on the left is louder in the left ear");
        Assert.True(Signals.Energy(right, 1) > 4 * Signals.Energy(right, 0), "a sound on the right is louder in the right ear");

        // The listener turns to its left: a sound that was ahead is now at its right.
        device.SetListener(new ListenerParameters(-Vector3.UnitX, Vector3.UnitY, Vector3.Zero, 1f));
        device.SetVoice(1, Parameters() with { Position = new Vector3(0, 0, -4) });
        float[] turned = Signals.Render(device, 0.1);
        Assert.True(Signals.Energy(turned, 1) > 4 * Signals.Energy(turned, 0), "a sound ahead is on the right of a listener who faces left");
        device.Stop(1);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void PlaysAFlatSoundAtTheCenterWhateverItsPosition()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1), 1, Signals.Rate);
        device.SetVoice(0, Parameters() with { Spatial = false, Position = new Vector3(-9, 0, 0) });

        device.Play(0, buffer, loop: true, startFrame: 0);
        float[] frames = Signals.Render(device, 0.1);

        double left = Signals.Energy(frames, 0);
        double right = Signals.Energy(frames, 1);
        Assert.True(left > 0.001);
        Assert.InRange(left / right, 0.9, 1.1);
        SourceReading reading = device.Inspect(0);
        Assert.True(reading.Relative);
        Assert.Equal(Vector3.Zero, reading.Position);
        device.Stop(0);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void GivesTheLibraryTheGainPitchPositionAndVelocityAsTheyAre()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1), 1, Signals.Rate);

        device.SetVoice(5, Parameters() with { Gain = 0.25f, Pitch = 1.5f, Position = new Vector3(1, 2, -3), Velocity = new Vector3(4, 5, 6) });
        device.Play(5, buffer, loop: false, startFrame: 0);

        SourceReading reading = device.Inspect(5);
        Assert.Equal(0.25f, reading.Gain);
        Assert.Equal(1.5f, reading.Pitch);
        Assert.False(reading.Relative);
        Assert.Equal(new Vector3(1, 2, -3), reading.Position);
        Assert.Equal(new Vector3(4, 5, 6), reading.Velocity);
        device.Stop(5);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void AppliesALowPassFilterAndRemovesItWhenTheCutoffOpens()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1), 1, Signals.Rate);
        device.SetVoice(0, Parameters() with { LowPassCutoff = 2000f });
        device.Play(0, buffer, loop: true, startFrame: 0);

        SourceReading filtered = device.Inspect(0);
        Assert.NotEqual(0u, filtered.DirectFilter);
        Assert.Equal(ALDevice.HighFrequencyGain(2000f), filtered.DirectGainHf, 3);
        Assert.InRange(filtered.DirectGainHf, 0.1f, 0.5f);

        device.SetVoice(0, Parameters() with { LowPassCutoff = VoiceParameters.OpenCutoff });
        Assert.Equal(0u, device.Inspect(0).DirectFilter);
        device.Stop(0);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void TakesAHighToneDownWithTheLowPassFilter()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1, frequency: 8000), 1, Signals.Rate);
        device.SetVoice(0, Parameters() with { Spatial = false });
        device.Play(0, buffer, loop: true, startFrame: 0);
        float[] open = Signals.Render(device, 0.1);

        device.SetVoice(0, Parameters() with { Spatial = false, LowPassCutoff = 500f });
        Signals.Render(device, 0.05);
        float[] closed = Signals.Render(device, 0.1);

        Assert.True(Signals.Energy(closed, 0) < Signals.Energy(open, 0) / 8);
        device.Stop(0);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void ConvertsTheCutoffToAGainThatFallsWithIt()
    {
        Assert.Equal(1f, ALDevice.HighFrequencyGain(VoiceParameters.OpenCutoff));
        float previous = 1f;
        foreach (float cutoff in new[] { 10000f, 5000f, 2000f, 800f, 200f, 20f })
        {
            float gain = ALDevice.HighFrequencyGain(cutoff);
            Assert.InRange(gain, 0f, previous);
            previous = gain;
        }

        Assert.True(previous < 0.05f);
    }

    [Fact]
    public void SendsAVoiceToAReverberationSlotAtTheGivenLevel()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        var room = new ReverbParameters(3.2f, 0.02f, 0.4f, 0.7f, 0.6f, 0.5f);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1), 1, Signals.Rate);

        device.SetReverb(2, room);
        device.SetVoice(0, Parameters() with { ReverbSlot = 2, ReverbSend = 0.4f });
        device.Play(0, buffer, loop: true, startFrame: 0);

        SourceReading sending = device.Inspect(0);
        Assert.Equal(device.SlotId(2), sending.SendSlotId);
        Assert.Equal(0.4f, sending.SendGain, 3);
        ReverbParameters? loaded = device.InspectReverb(2);
        Assert.NotNull(loaded);
        Assert.Equal(room.DecayTime, loaded.Value.DecayTime, 3);
        Assert.Equal(room.ReflectionsDelay, loaded.Value.ReflectionsDelay, 3);
        Assert.Equal(room.ReflectionsGain, loaded.Value.ReflectionsGain, 3);
        Assert.Equal(room.Density, loaded.Value.Density, 3);
        Assert.Equal(room.Diffusion, loaded.Value.Diffusion, 3);
        Assert.Equal(room.Gain, loaded.Value.Gain, 3);

        // The level changes, then the voice leaves the slot, and the slot is cleared.
        device.SetVoice(0, Parameters() with { ReverbSlot = 2, ReverbSend = 0.9f });
        Assert.Equal(0.9f, device.Inspect(0).SendGain, 3);
        device.SetVoice(0, Parameters() with { ReverbSlot = -1 });
        Assert.Equal(0u, device.Inspect(0).SendSlotId);
        device.ClearReverb(2);
        Assert.Null(device.InspectReverb(2));
        device.Stop(0);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void RendersTheTailOfAReverberationAfterTheSoundEnded()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        AudioBuffer click = device.CreateBuffer(Signals.Sine(0.05), 1, Signals.Rate);
        device.SetReverb(0, new ReverbParameters(2f, 0.01f, 0.5f, 1f, 1f, 1f));

        // Dry: nothing is heard after the click.
        device.SetVoice(0, Parameters() with { Spatial = false });
        device.Play(0, click, loop: false, startFrame: 0);
        Signals.Render(device, 0.1);
        float[] dryTail = Signals.Render(device, 0.2);
        device.Stop(0);

        // Wet: the slot keeps ringing.
        device.SetVoice(0, Parameters() with { Spatial = false, ReverbSlot = 0, ReverbSend = 1f });
        device.Play(0, click, loop: false, startFrame: 0);
        Signals.Render(device, 0.1);
        float[] wetTail = Signals.Render(device, 0.2);

        Assert.True(Signals.Energy(wetTail, 0) > 1e-5, "the reverberation rings after the sound");
        Assert.True(Signals.Energy(wetTail, 0) > 100 * Signals.Energy(dryTail, 0));
        device.Stop(0);
        device.DestroyBuffer(click);
    }

    [Fact]
    public void AppliesTheModeOfHrtfWhenItChanges()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;

        device.SetHrtf(HrtfMode.On);
        Assert.True(device.HrtfEnabled);
        device.SetHrtf(HrtfMode.Off);
        Assert.False(device.HrtfEnabled);
        device.SetHrtf(HrtfMode.On);
        Assert.True(device.HrtfEnabled);
    }

    [Fact]
    public void OpensWithHrtfForcedOnOrOff()
    {
        using (var on = new OpenALHarness().Setting("audio.hrtf", "on"))
        {
            using Engine engine = on.Start();
            Assert.True(on.Backend.Opened!.HrtfEnabled);
            Assert.True(on.System.IsActive);
        }

        using (var off = new OpenALHarness().Setting("audio.hrtf", "off"))
        {
            using Engine engine = off.Start();
            Assert.False(off.Backend.Opened!.HrtfEnabled);
        }
    }

    [Fact]
    public void FiltersByDirectionWithHrtf()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        device.SetListener(Facing);
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(1, frequency: 3000), 1, Signals.Rate);
        device.SetHrtf(HrtfMode.On);
        device.SetVoice(0, Parameters() with { Position = new Vector3(0, 0, -4) });
        device.Play(0, buffer, loop: true, startFrame: 0);
        float[] ahead = Signals.Render(device, 0.1);
        device.Stop(0);
        device.SetVoice(0, Parameters() with { Position = new Vector3(0, 0, 4) });
        device.Play(0, buffer, loop: true, startFrame: 0);
        float[] behind = Signals.Render(device, 0.1);

        // A source ahead and one behind are shadowed differently by the head and the ears: with HRTF the levels differ.
        double aheadLevel = Signals.Energy(ahead, 0) + Signals.Energy(ahead, 1);
        double behindLevel = Signals.Energy(behind, 0) + Signals.Energy(behind, 1);
        Assert.True(aheadLevel > 0 && behindLevel > 0);
        Assert.True(Math.Abs(aheadLevel - behindLevel) > 0.05 * aheadLevel, $"ahead {aheadLevel}, behind {behindLevel}");
        device.Stop(0);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void CreatesVoicesOneAtATimeAndFreesEverythingWhenClosed()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        AudioBuffer buffer = device.CreateBuffer(Signals.Sine(0.1), 1, Signals.Rate);
        short[] block = Signals.Sine(0.02);
        device.SetReverb(0, ReverbParameters.Default);
        device.SetReverb(1, ReverbParameters.Default);
        Assert.Equal(0, device.LiveSources);

        for (int voice = 0; voice < 20; voice++)
        {
            device.SetVoice(voice, Parameters() with { LowPassCutoff = 1000f, ReverbSlot = voice % 2, ReverbSend = 0.5f });
            if (voice % 2 == 0)
            {
                device.Play(voice, buffer, loop: false, startFrame: 0);
            }
            else
            {
                device.Queue(voice, block, 1, Signals.Rate);
            }
        }

        Assert.Equal(20, device.LiveSources);
        Assert.True(device.LiveBuffers >= 11);
        Assert.True(device.LiveEffectObjects > 0);
        device.Commit();

        device.Dispose();

        Assert.Equal(0, device.LiveSources);
        Assert.Equal(0, device.LiveBuffers);
        Assert.Equal(0, device.LiveEffectObjects);
    }

    [Fact]
    public void ReusesTheBlocksOfAStreamInsteadOfCreatingBuffers()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        short[] block = Signals.Sine(0.05);
        device.SetVoice(0, Parameters());

        for (int round = 0; round < 20; round++)
        {
            device.Queue(0, block, 1, Signals.Rate);
            Signals.Render(device, 0.06);
            device.GetQueuedBlocks(0);
        }

        Assert.InRange(device.LiveBuffers, 1, 3);
        device.Stop(0);
    }

    [Fact]
    public void IsLostWhenTheDefaultOutputChanges()
    {
        using Engine engine = _harness.Start();
        ALDevice device = _harness.Backend.Opened!;
        Assert.False(device.IsLost);

        device.NotifyDefaultChanged();

        Assert.True(device.IsLost);
    }

    [Fact]
    public void OpensTheOutputAgainWhenTheDeviceIsLostAndKeepsTheModuleActive()
    {
        using Engine engine = _harness.Start();
        ALDevice first = _harness.Backend.Opened!;
        first.NotifyDefaultChanged();

        engine.MainContext.RunFrame();

        ALDevice second = _harness.Backend.Opened!;
        Assert.NotSame(first, second);
        Assert.Equal(0, first.LiveSources);
        Assert.True(_harness.System.IsActive);
        Assert.False(second.IsLost);
        Assert.Contains(_harness.Log.Entries, e => e.Message.Contains("is lost"));
    }

    private static VoiceParameters Parameters() => new()
    {
        Gain = 1f,
        Pitch = 1f,
        Spatial = true,
        LowPassCutoff = VoiceParameters.OpenCutoff,
        ReverbSlot = -1,
    };
}
