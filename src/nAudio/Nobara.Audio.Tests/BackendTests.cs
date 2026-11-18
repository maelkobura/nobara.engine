// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.Tests.Support;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Core.Mathematics;

namespace Nobara.Audio.Tests;

/// <summary>The registry of backends, the choice of the backend, and a context without device.</summary>
public sealed class BackendTests : IDisposable
{
    private readonly AudioHarness _harness = new();
    private readonly string _resources;

    public BackendTests()
    {
        _resources = _harness.CopyResources();
        _harness.WithGame(AudioHarness.Compiled(_resources));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TakesTheFirstBackendRegisteredByDefault()
    {
        _harness.Backends.Add(new TestAudioBackend("other"));
        using Engine engine = _harness.Start();

        IAudioSystem audio = _harness.System;
        Assert.True(audio.IsActive);
        Assert.Equal("test", audio.BackendName);
        Assert.NotNull(_harness.Backends[0].Device);
        Assert.Null(_harness.Backends[1].Device);
        Assert.Equal(HrtfMode.Auto, _harness.Backends[0].Options.Hrtf);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Audio device opened by backend 'test': Test output"));
    }

    [Fact]
    public void TakesTheBackendTheKeyNamesWithoutCase()
    {
        _harness.Backends.Add(new TestAudioBackend("other"));
        _harness.Setting(AudioSettings.Backend, "OTHER").Setting(AudioSettings.Hrtf, "off");
        using Engine engine = _harness.Start();

        Assert.Equal("other", _harness.System.BackendName);
        Assert.Null(_harness.Backends[0].Device);
        Assert.Equal(HrtfMode.Off, _harness.Backends[1].Options.Hrtf);
    }

    [Fact]
    public void FallsBackToTheFirstBackendWithAWarningForAnUnknownName()
    {
        _harness.Backends.Add(new TestAudioBackend("other"));
        _harness.Setting(AudioSettings.Backend, "xaudio");
        using Engine engine = _harness.Start();

        Assert.Equal("test", _harness.System.BackendName);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'xaudio'") && e.Message.Contains("test, other"));
    }

    [Fact]
    public void RefusesTwoBackendsOfTheSameName()
    {
        _harness.Backends.Add(new TestAudioBackend("TEST"));

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Start());

        Assert.Contains("both register an audio backend named 'TEST'", stop.ToString());
    }

    [Fact]
    public void AppliesAChangeOfHrtfAtOnce()
    {
        using Engine engine = _harness.Start();
        _harness.Probe.Context.Settings.Set(AudioSettings.Hrtf, "on");
        _harness.Frames(engine, 1);

        Assert.Equal(HrtfMode.On, _harness.Device.Hrtf);
    }

    [Fact]
    public void RunsWithoutSoundWhenNoDeviceOpens()
    {
        _harness.Backends.Clear();
        _harness.Backends.Add(new TestAudioBackend("test") { Fails = true });
        using Engine engine = _harness.Start();

        IAudioSystem audio = _harness.System;
        Assert.False(audio.IsActive);
        Assert.False(audio.Play(_harness.Sounds.Get("sound:example.sounds.click")).IsActive);
        _harness.Frames(engine, 3);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("no output device"));
    }

    [Fact]
    public void StartsOnAServerWithoutBackendAndPlaysNothing()
    {
        using Engine engine = _harness.Start(Side.Server);

        IAudioSystem audio = _harness.System;
        Assert.False(audio.IsActive);
        Assert.Null(audio.BackendName);
        SoundEvent pop = _harness.Sounds.Get("sound:example.sounds.pop");
        SoundInstance instance = audio.PlayAt(pop, default);
        Assert.False(instance.IsActive);
        instance.SetVolume(0.5f);
        instance.Stop();
        using SoundEmitter emitter = audio.CreateEmitter(_harness.Probe.Context, pop, default);
        audio.SaveBusVolumes();
        engine.MainContext.RunFrame();
        Assert.False(File.Exists(audio.VolumesFilePath));
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void ClosesTheDeviceWhenTheContextStops()
    {
        Engine engine = _harness.Start();
        TestAudioDevice device = _harness.Device;
        _harness.System.Play(_harness.Sounds.Get("sound:example.sounds.click"));
        _harness.Sounds.CompleteDecoding();
        _harness.Frames(engine, 2);
        Assert.Equal(1, device.Buffers);

        engine.Dispose();

        Assert.True(device.Disposed);
        Assert.Equal(0, device.Buffers);
    }

    [Fact]
    public void OpensTheDefaultOutputAgainWhenTheDeviceIsLostAndTheLoopsGoOn()
    {
        using Engine engine = _harness.Start();
        _harness.Sounds.CompleteDecoding();
        using AudioListener listener = _harness.System.ClaimListener(_harness.Probe.Context);
        listener.Teleport(default, System.Numerics.Quaternion.Identity);
        SoundEvent hum = _harness.Sounds.Get("sound:example.sounds.hum");
        SoundInstance instance = _harness.System.PlayAt(hum, WorldPosition.FromBlocks(1, 0, 0), seed: 1);
        _harness.Frames(engine, 3);
        TestAudioDevice first = _harness.Device;
        Assert.True(instance.IsReal);
        using ReverbBus room = _harness.System.CreateReverbBus(_harness.Probe.Context, "room");
        _harness.Frames(engine, 1);
        Assert.NotNull(first.Reverbs[0]);

        first.Lost = true;
        _harness.Frames(engine, 1);

        TestAudioDevice second = _harness.Device;
        Assert.NotSame(first, second);
        Assert.True(first.Disposed);
        Assert.Equal(2, _harness.Backends[0].Opened);
        Assert.True(_harness.System.IsActive);
        Assert.True(instance.IsActive);
        Assert.True(instance.IsReal);
        TestVoice voice = Assert.Single(second.Playing);
        Assert.True(voice.Loop);
        Assert.NotNull(second.Reverbs[0]);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("is lost"));
    }

    [Fact]
    public void TriesAgainEverySecondWhileNoOutputExists()
    {
        using Engine engine = _harness.Start();
        TestAudioDevice first = _harness.Device;
        first.Lost = true;
        _harness.Backends[0].Fails = true;

        _harness.Frames(engine, 1);
        Assert.False(_harness.System.IsActive);
        Assert.False(_harness.System.Play(_harness.Sounds.Get("sound:example.sounds.click")).IsActive);
        _harness.Frames(engine, 30);
        Assert.Equal(1, _harness.Backends[0].Opened);

        _harness.Backends[0].Fails = false;
        _harness.Frames(engine, 40);

        Assert.True(_harness.System.IsActive);
        Assert.Equal(2, _harness.Backends[0].Opened);
        Assert.Single(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("No audio output is available"));
    }
}
