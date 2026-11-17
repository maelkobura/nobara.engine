// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Audio.Tests.Support;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Audio.Tests;

/// <summary>Real and virtual voices, the score of importance, the limits of instances, and the positions given to the device.</summary>
public sealed class VoiceTests : IDisposable
{
    private readonly AudioHarness _harness = new();

    public VoiceTests()
    {
        _harness.WithGame(AudioHarness.Compiled(_harness.CopyResources()));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void GivesTheVoicesOfTheDeviceToTheMostImportantSounds()
    {
        _harness.Backends[0] = new TestAudioBackend("test") { MaxVoices = 8 };
        using Engine engine = Start(out AudioListener listener);
        IAudioSystem audio = _harness.System;
        SoundEvent pop = _harness.Sounds.Get("sound:example.sounds.pop");
        SoundEvent step = _harness.Sounds.Get("sound:example.sounds.step");

        // 100 sounds at 1 to 100 blocks, in a shuffled order: the 8 nearest are the loudest.
        var random = new Random(7);
        var plays = new List<(int Distance, SoundInstance Instance)>();
        foreach (int distance in Enumerable.Range(1, 100).OrderBy(_ => random.Next()))
        {
            plays.Add((distance, audio.PlayAt(pop, At(distance, 0, 0), seed: 1)));
        }

        // A low priority sound next to the listener is not taken before them.
        SoundInstance low = audio.PlayAt(step, At(0.5, 0, 0), seed: 1);
        _harness.Frames(engine, 1);

        Assert.Equal(8, audio.RealVoiceLimit);
        Assert.Equal(new AudioStatistics(8, 93, 0), audio.Statistics);
        Assert.Equal(Enumerable.Range(1, 8), plays.Where(p => p.Instance.IsReal).Select(p => p.Distance).Order());
        Assert.False(low.IsReal);
        Assert.True(low.IsActive);
        Assert.Equal(8, _harness.Device.Playing.Count());

        // When the nearest stop, the next ones take their voices.
        foreach ((int distance, SoundInstance instance) in plays.Where(p => p.Distance <= 3))
        {
            instance.Stop();
        }

        _harness.Frames(engine, 1);
        Assert.Equal(Enumerable.Range(4, 8), plays.Where(p => p.Instance.IsReal).Select(p => p.Distance).Order());
        Assert.Equal(8, _harness.Device.Playing.Count());
        GC.KeepAlive(listener);
    }

    [Fact]
    public void KeepsALoopGoingWhileVirtualAndResumesItAtThePositionReached()
    {
        using Engine engine = Start(out AudioListener listener);
        SoundEvent hum = _harness.Sounds.Get("sound:example.sounds.hum");
        SoundVariant variant = hum.Variants[0];

        // Beyond its maximum distance of 64 blocks, the loop is virtual from its first frame.
        SoundInstance instance = _harness.System.PlayAt(hum, At(100, 0, 0), seed: 1);
        _harness.Frames(engine, 1);
        Assert.True(instance.IsActive);
        Assert.False(instance.IsReal);

        // 61 frames of 1/60 s: the loop went on for one second, and more than three times around its file.
        _harness.Frames(engine, 60);
        listener.Teleport(At(90, 0, 0), Quaternion.Identity);
        _harness.Frames(engine, 1);

        Assert.True(instance.IsReal);
        TestVoice voice = Assert.Single(_harness.Device.Playing);
        Assert.True(voice.Loop);
        long expected = (long)(61.0 / 60 * variant.SampleRate) % variant.Frames;
        Assert.InRange(voice.StartFrame, expected - 2, expected + 2);
        Assert.True(variant.Frames < variant.SampleRate, "the loop is shorter than the time it went on");

        // Out of range again, it gives its voice back and goes on.
        listener.Teleport(At(-100, 0, 0), Quaternion.Identity);
        _harness.Frames(engine, 1);
        Assert.False(instance.IsReal);
        Assert.True(instance.IsActive);
        Assert.Empty(_harness.Device.Playing);
    }

    [Fact]
    public void ReplacesTheWeakestInstanceOrIgnoresTheNewOne()
    {
        using Engine engine = Start(out _);
        IAudioSystem audio = _harness.System;
        SoundEvent step = _harness.Sounds.Get("sound:example.sounds.step");
        Assert.Equal(4, step.MaxInstances);
        SoundInstance[] near = [.. Enumerable.Range(0, 4).Select(i => audio.PlayAt(step, At(3 + i, 0, 0), seed: 1))];
        _harness.Frames(engine, 1);

        // A fifth instance, weaker than the four: it is ignored.
        SoundInstance far = audio.PlayAt(step, At(12, 0, 0), seed: 1);
        _harness.Frames(engine, 1);
        Assert.False(far.IsActive);
        Assert.All(near, i => Assert.True(i.IsActive));

        // A louder one replaces the weakest.
        SoundInstance nearest = audio.PlayAt(step, At(1, 0, 0), seed: 1);
        _harness.Frames(engine, 1);
        Assert.True(nearest.IsActive);
        Assert.False(near[3].IsActive);
        Assert.Equal(4, near.Count(i => i.IsActive) + 1);
    }

    [Fact]
    public void LimitsTheInstancesOfAnEventWithoutLimitByTheDistributionKey()
    {
        _harness.Setting(AudioSettings.MaxInstances, "3");
        using Engine engine = Start(out _);
        SoundEvent click = _harness.Sounds.Get("sound:example.sounds.click");
        Assert.Equal(0, click.MaxInstances);
        SoundInstance[] plays = [.. Enumerable.Range(0, 5).Select(_ => _harness.System.Play(click))];
        _harness.Frames(engine, 1);

        Assert.Equal(3, plays.Count(p => p.IsActive));
    }

    [Theory]
    [InlineData(1_000_000_000.0)]
    [InlineData(-1_000_000_000.0)]
    public void GivesTheDeviceSmallAndExactPositionsFarFromTheOrigin(double coordinate)
    {
        using Engine engine = Start(out AudioListener listener);
        listener.Teleport(WorldPosition.FromBlocks(coordinate, coordinate, coordinate), Quaternion.Identity);
        SoundEvent pop = _harness.Sounds.Get("sound:example.sounds.pop");
        SoundInstance near = _harness.System.PlayAt(pop, WorldPosition.FromBlocks(coordinate + 3.25, coordinate - 1.5, coordinate + 0.125));
        SoundInstance beyond = _harness.System.PlayAt(pop, WorldPosition.FromBlocks(coordinate + 500, coordinate, coordinate));
        _harness.Frames(engine, 1);

        Assert.True(near.IsReal);
        Assert.False(beyond.IsReal);
        TestVoice voice = Assert.Single(_harness.Device.Playing);
        Assert.Equal(new Vector3(3.25f, -1.5f, 0.125f), voice.Parameters.Position);
        Assert.True(voice.Parameters.Spatial);

        // No position given to the device ever goes beyond the range of its event.
        Assert.All(_harness.Device.Voices.Where(v => v.Playing), v => Assert.True(v.Parameters.Position.Length() <= pop.Spatial!.Value.MaxDistance));
    }

    [Fact]
    public void HearsPositionsBetweenTicksAsTheRenderWorldDoes()
    {
        using Engine engine = Start(out AudioListener listener);
        SoundEvent hum = _harness.Sounds.Get("sound:example.sounds.hum");
        SoundInstance instance = _harness.System.PlayAt(hum, At(10, 0, 0));
        RunToTick(engine);

        // A move during a tick is heard between the two positions, by the fraction of tick of the frame.
        instance.SetPosition(At(20, 0, 0));
        ulong tick = engine.MainContext.Tick;
        var heard = new List<float>();
        for (int i = 0; i < 10 && engine.MainContext.Tick == tick; i++)
        {
            _harness.Frames(engine, 1, 1.0 / 240);
            if (engine.MainContext.Tick == tick)
            {
                VoiceParameters parameters = _harness.Device.Playing.Single().Parameters;
                heard.Add(parameters.Position.X);

                // 10 blocks in a tick of 1/30 s.
                Assert.Equal(300f, parameters.Velocity.X, 0.01f);
            }
        }

        Assert.Contains(heard, x => x is > 10.01f and < 19.99f);
        Assert.Equal(heard.Order(), heard);
        GC.KeepAlive(listener);
    }

    [Fact]
    public void StopsASoundWhoseAnchorIsGone()
    {
        using Engine engine = Start(out _);
        var platform = new Platform(At(4, 0, 0));
        SoundEvent hum = _harness.Sounds.Get("sound:example.sounds.hum");
        SoundInstance instance = _harness.System.PlayAttached(hum, platform, new Vector3(0, 2, 0));
        _harness.Frames(engine, 1);
        Assert.True(instance.IsReal);
        Assert.Equal(new Vector3(4, 2, 0), _harness.Device.Playing.Single().Parameters.Position);

        platform.Destroyed = true;
        RunToTick(engine);

        Assert.False(instance.IsActive);
        Assert.Empty(_harness.Device.Playing);
    }

    [Fact]
    public void RevokesTheHandleOfAStoppedSoundAndIgnoresItsCalls()
    {
        using Engine engine = Start(out _);
        SoundEvent hum = _harness.Sounds.Get("sound:example.sounds.hum");
        SoundInstance instance = _harness.System.PlayAt(hum, At(2, 0, 0));
        SoundInstance copy = instance;
        _harness.Frames(engine, 1);

        // A fade lowers the gain over its time, then the sound ends.
        float before = _harness.Device.Playing.Single().Parameters.Gain;
        instance.Stop(fadeSeconds: 0.5f);
        _harness.Frames(engine, 15);
        Assert.True(instance.IsActive);
        Assert.InRange(_harness.Device.Playing.Single().Parameters.Gain, before * 0.3f, before * 0.7f);
        _harness.Frames(engine, 20);

        Assert.False(copy.IsActive);
        Assert.Null(copy.Sound);
        copy.SetVolume(1);
        copy.SetPitch(2);
        copy.SetPosition(At(1, 0, 0));
        copy.Stop();
        Assert.Empty(_harness.Device.Playing);
        Assert.False(default(SoundInstance).IsActive);

        // The slot is used again by the next play; the old handle stays revoked.
        SoundInstance next = _harness.System.PlayAt(hum, At(2, 0, 0));
        Assert.True(next.IsActive);
        Assert.False(copy.IsActive);
        Assert.NotEqual(copy, next);
    }

    [Fact]
    public void EndsASoundThatDoesNotLoopAtTheEndOfItsFile()
    {
        using Engine engine = Start(out _);
        SoundEvent pop = _harness.Sounds.Get("sound:example.sounds.pop");
        SoundInstance instance = _harness.System.PlayAt(pop, At(2, 0, 0), pitch: 1, seed: 1);
        double seconds = (double)pop.Variants[0].Frames / pop.Variants[0].SampleRate;
        _harness.Frames(engine, (int)(seconds * 60) - 2);
        Assert.True(instance.IsActive);
        _harness.Frames(engine, 4);
        Assert.False(instance.IsActive);
    }

    [Fact]
    public void RefusesASecondListener()
    {
        using Engine engine = Start(out AudioListener listener);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => _harness.System.ClaimListener(_harness.Probe.Context));
        Assert.Contains("holds it", error.Message);

        listener.Dispose();
        Assert.Null(_harness.System.Listener);
        using AudioListener second = _harness.System.ClaimListener(_harness.Probe.Context);
        Assert.Same(second, _harness.System.Listener);
    }

    [Fact]
    public void HearsNoSpatialSoundWithoutListener()
    {
        using Engine engine = _harness.Start();
        _harness.Sounds.CompleteDecoding();
        SoundInstance spatial = _harness.System.PlayAt(_harness.Sounds.Get("sound:example.sounds.hum"), At(1, 0, 0));
        SoundInstance flat = _harness.System.Play(_harness.Sounds.Get("sound:example.sounds.click"));
        _harness.Frames(engine, 1);

        Assert.False(spatial.IsReal);
        Assert.True(spatial.IsActive);
        Assert.True(flat.IsReal);
        Assert.False(_harness.Device.Playing.Single().Parameters.Spatial);
    }

    [Fact]
    public void StreamsAStreamedSoundIntoTheQueueOfItsVoice()
    {
        using Engine engine = Start(out _);
        SoundEvent music = _harness.Sounds.Get("sound:example.sounds.music");
        SoundInstance instance = _harness.System.Play(music);
        for (int i = 0; i < 400 && _harness.Device.Playing.SingleOrDefault()?.Queued is null or < 3; i++)
        {
            _harness.Frames(engine, 1);
            Thread.Sleep(5); // the blocks decode in background jobs
        }

        TestVoice voice = _harness.Device.Playing.Single();
        Assert.True(instance.IsReal);
        Assert.Equal(3, voice.Queued);
        Assert.Equal(3 * SoundStream.BlockFrames, voice.QueuedFrames);

        // The library plays the blocks; the stream fills the queue again.
        _harness.Device.Drain(Array.IndexOf(_harness.Device.Voices, voice));
        for (int i = 0; i < 400 && voice.Queued < 3; i++)
        {
            _harness.Frames(engine, 1);
            Thread.Sleep(5);
        }

        Assert.Equal(3, voice.Queued);
        instance.Stop();
        _harness.Frames(engine, 1);
        Assert.Empty(_harness.Device.Playing);
    }

    private Engine Start(out AudioListener listener)
    {
        Engine engine = _harness.Start();
        _harness.Sounds.CompleteDecoding();
        listener = _harness.System.ClaimListener(_harness.Probe.Context);
        listener.Teleport(default, Quaternion.Identity);
        return engine;
    }

    private void RunToTick(Engine engine)
    {
        ulong tick = engine.MainContext.Tick;
        for (int i = 0; i < 10 && engine.MainContext.Tick == tick; i++)
        {
            _harness.Frames(engine, 1);
        }
    }

    private static WorldPosition At(double x, double y, double z) => WorldPosition.FromBlocks(x, y, z);

    /// <summary>An anchor that the test destroys.</summary>
    private sealed class Platform(WorldPosition position) : ISoundAnchor
    {
        public bool Destroyed { get; set; }

        public bool TryGetTransform(out WorldTransform transform)
        {
            transform = WorldTransform.At(position);
            return !Destroyed;
        }
    }
}
