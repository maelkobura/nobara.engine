// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Audio.Tests.Support;
using Nobara.Core;
using Nobara.Core.Logging;

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
        engine.MainContext.RunFrame();
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
}
