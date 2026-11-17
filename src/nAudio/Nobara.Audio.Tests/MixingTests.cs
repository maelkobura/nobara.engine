// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using System.Xml.Linq;
using Nobara.Audio.Tests.Support;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Audio.Tests;

/// <summary>Buses, the volumes of the player, the pause, the emission processors, the reverberation buses, and the emitters.</summary>
public sealed class MixingTests : IDisposable
{
    private readonly AudioHarness _harness = new();

    public MixingTests()
    {
        _harness.WithGame(AudioHarness.Compiled(_harness.CopyResources()));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void MultipliesTheVolumeOfABusByTheVolumesOfItsParents()
    {
        using Engine engine = Start(out _);
        IAudioSystem audio = _harness.System;
        ISoundRegistry sounds = _harness.Sounds;
        AudioBus machines = sounds.GetBus("bus:example.sounds.machines");
        audio.SetBusVolume(sounds.Master, 0.5f);
        audio.SetBusVolume(sounds.GetBus("bus:nobara.audio.effects"), 0.5f);
        audio.SetBusVolume(machines, 0.8f);

        Assert.Equal(0.8f, audio.GetBusVolume(machines));
        Assert.Equal(0.2f, audio.GetEffectiveVolume(machines), 1e-6f);
        Assert.Throws<InvalidOperationException>(() => audio.SetBusVolume(sounds.GetBus("bus:nobara.audio.effects.blocks"), 0.5f));

        // The wind plays on the machines: its gain is the effective volume of the bus.
        audio.Play(sounds.Get("sound:example.sounds.wind"));
        _harness.Frames(engine, 1);
        Assert.Equal(0.2f, _harness.Device.Playing.Single().Parameters.Gain, 1e-5f);
    }

    [Fact]
    public void WritesOnlyTheVolumesThatDifferAndReadsThemAtTheNextStart()
    {
        using (Engine engine = Start(out _))
        {
            IAudioSystem audio = _harness.System;
            audio.SetBusVolume(_harness.Sounds.GetBus("bus:nobara.audio.music"), 0.25f);
            audio.SetBusVolume(_harness.Sounds.GetBus("bus:nobara.audio.ui"), 1f);
            audio.SaveBusVolumes();

            XElement root = XDocument.Load(audio.VolumesFilePath).Root!;
            Assert.Equal("AudioVolumes", root.Name.LocalName);
            XElement entry = Assert.Single(root.Elements());
            Assert.Equal("bus:nobara.audio.music", entry.Attribute("id")!.Value);
            Assert.Equal("0.25", entry.Attribute("volume")!.Value);

            // A bus that no module defines any more is kept when the file is written again.
            root.Add(new XElement("Bus", new XAttribute("id", "bus:example.removed.radio"), new XAttribute("volume", "0.5")));
            root.Document!.Save(audio.VolumesFilePath);
        }

        using (Engine engine = _harness.Start())
        {
            IAudioSystem audio = _harness.System;
            Assert.Equal(0.25f, audio.GetBusVolume(_harness.Sounds.GetBus("bus:nobara.audio.music")));
            Assert.Equal(1f, audio.GetBusVolume(_harness.Sounds.Master));
            Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("bus:example.removed.radio"));
            audio.SaveBusVolumes();
            Assert.Contains("bus:example.removed.radio", File.ReadAllText(audio.VolumesFilePath));
        }
    }

    [Fact]
    public void PausesTheBusesOfTheWorldWithTheGameButNotTheInterface()
    {
        using Engine engine = Start(out _);
        IAudioSystem audio = _harness.System;
        ISoundRegistry sounds = _harness.Sounds;
        SoundInstance hum = audio.PlayAt(sounds.Get("sound:example.sounds.hum"), WorldPosition.FromBlocks(2, 0, 0));
        SoundInstance click = audio.Play(sounds.Get("sound:example.sounds.click"));
        _harness.Frames(engine, 1);
        TestVoice humVoice = Voice(hum);

        _harness.Probe.Context.Resolve<IGameTime>().Paused = true;
        _harness.Frames(engine, 1);

        Assert.True(audio.IsPaused(sounds.GetBus("bus:nobara.audio.effects.blocks")));
        Assert.False(audio.IsPaused(sounds.GetBus("bus:nobara.audio.ui")));
        Assert.False(audio.IsPaused(sounds.GetBus("bus:nobara.audio.music")));
        Assert.True(humVoice.Paused);
        Assert.False(Voice(click).Paused);

        // A paused loop does not move in time: when it resumes, it is where it stopped.
        _harness.Frames(engine, 120);
        Assert.True(hum.IsActive);
        Assert.False(click.IsActive);
        _harness.Probe.Context.Resolve<IGameTime>().Paused = false;
        _harness.Frames(engine, 1);
        Assert.False(humVoice.Paused);
        Assert.Equal(1, humVoice.Plays);
    }

    [Fact]
    public void SmoothsTheParametersOfAProcessorOnTheWayToTheDevice()
    {
        using Engine engine = Start(out _);
        IAudioSystem audio = _harness.System;
        ReverbBus room = audio.CreateReverbBus(_harness.Probe.Context, "room");
        var processor = new TestProcessor { Gain = 0.5f, Cutoff = 4000, Reverb = room, Send = 0.5f };
        using AudioRegistration registration = audio.AddProcessor(_harness.Probe.Context, processor);
        SoundInstance hum = audio.PlayAt(_harness.Sounds.Get("sound:example.sounds.hum"), WorldPosition.FromBlocks(1, 0, 0));
        _harness.Frames(engine, 1);

        // The first frame of a voice takes the parameters as they are.
        VoiceParameters first = Voice(hum).Parameters;
        Assert.Equal(0.5f, first.Gain, 1e-4f);
        Assert.Equal(4000f, first.LowPassCutoff, 1f);
        Assert.Equal(room.Slot, first.ReverbSlot);
        Assert.Equal(0.5f, first.ReverbSend, 1e-4f);
        Assert.Same(hum.Sound, processor.Seen);

        // A step of the processor reaches the device over a few tens of milliseconds.
        (processor.Gain, processor.Cutoff, processor.Send) = (1f, 250f, 0f);
        _harness.Frames(engine, 1);
        VoiceParameters step = Voice(hum).Parameters;
        Assert.InRange(step.Gain, 0.55f, 0.95f);
        Assert.InRange(step.LowPassCutoff, 300f, 3900f);
        Assert.InRange(step.ReverbSend, 0.05f, 0.45f);
        _harness.Frames(engine, 30);
        VoiceParameters settled = Voice(hum).Parameters;
        Assert.Equal(1f, settled.Gain, 1e-3f);
        Assert.Equal(250f, settled.LowPassCutoff, 1f);
        Assert.Equal(0f, settled.ReverbSend, 1e-3f);

        // Removed, the processor leaves the voices as they are.
        registration.Dispose();
        processor.Gain = 0;
        _harness.Frames(engine, 30);
        Assert.Equal(1f, Voice(hum).Parameters.Gain, 1e-3f);
    }

    [Fact]
    public void DisablesAProcessorThatNeedsAMissingFeature()
    {
        _harness.Backends[0] = new TestAudioBackend("test") { Features = AudioFeatures.None };
        using Engine engine = Start(out _);
        var processor = new TestProcessor { Gain = 0.1f, Required = AudioFeatures.Filters };
        using AudioRegistration registration = _harness.System.AddProcessor(_harness.Probe.Context, processor);
        using ReverbBus room = _harness.System.CreateReverbBus(_harness.Probe.Context, "room");
        SoundInstance hum = _harness.System.PlayAt(_harness.Sounds.Get("sound:example.sounds.hum"), WorldPosition.FromBlocks(1, 0, 0));
        _harness.Frames(engine, 1);

        Assert.Null(processor.Seen);
        Assert.Equal(1f, Voice(hum).Parameters.Gain, 1e-4f);
        Assert.False(room.IsAvailable);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("needs the audio features Filters"));
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'room'") && e.Message.Contains("no effects"));
    }

    [Fact]
    public void SmoothsTheParametersOfAReverberationBusAndFreesItsSlot()
    {
        using Engine engine = Start(out _);
        ReverbBus hall = _harness.System.CreateReverbBus(_harness.Probe.Context, "hall");
        _harness.Frames(engine, 1);
        Assert.True(hall.IsAvailable);
        Assert.Equal(ReverbParameters.Default, _harness.Device.Reverbs[hall.Slot]);

        hall.Parameters = ReverbParameters.Default with { DecayTime = 9.49f, Gain = 2f };
        Assert.Equal(1f, hall.Parameters.Gain);
        _harness.Frames(engine, 1);
        ReverbParameters moving = _harness.Device.Reverbs[hall.Slot]!.Value;
        Assert.InRange(moving.DecayTime, 1.6f, 9.4f);
        _harness.Frames(engine, 60);
        Assert.Equal(9.49f, _harness.Device.Reverbs[hall.Slot]!.Value.DecayTime);

        int slot = hall.Slot;
        hall.Dispose();
        Assert.Null(_harness.Device.Reverbs[slot]);
        Assert.False(hall.IsAvailable);
    }

    [Fact]
    public void PlaysARepeatingEmitterAtRandomIntervalsInRangeOnly()
    {
        using Engine engine = Start(out AudioListener listener);
        SoundEvent pop = _harness.Sounds.Get("sound:example.sounds.pop");
        using SoundEmitter emitter = _harness.System.CreateEmitter(_harness.Probe.Context, pop, WorldPosition.FromBlocks(3, 0, 0), EmitterRepeat.Every(0.5f, 1f));

        // Ten seconds: between 10 and 20 plays, each one 0.5 to 1 s after the previous.
        var starts = new List<int>();
        SoundInstance last = default;
        for (int frame = 0; frame < 600; frame++)
        {
            _harness.Frames(engine, 1);
            if (emitter.Playing.IsActive && emitter.Playing != last)
            {
                last = emitter.Playing;
                starts.Add(frame);
            }
        }

        Assert.InRange(starts.Count, 10, 20);
        Assert.All(starts.Zip(starts.Skip(1), (a, b) => b - a), gap => Assert.InRange(gap, 29, 61));

        // Out of range, it plays nothing, and costs no voice.
        listener.Teleport(WorldPosition.FromBlocks(10_000, 0, 0), Quaternion.Identity);
        _harness.Frames(engine, 60);
        SoundInstance before = emitter.Playing;
        _harness.Frames(engine, 300);
        Assert.Equal(before, emitter.Playing);
        Assert.Equal(1, _harness.System.Statistics.Emitters);

        // Suspended, it waits; resumed, it plays again.
        listener.Teleport(default, Quaternion.Identity);
        emitter.Suspend();
        for (int i = 0; i < 120; i++)
        {
            _harness.Frames(engine, 1);
            Assert.False(emitter.Playing.IsActive);
        }

        emitter.Resume();
        _harness.Frames(engine, 120);
        Assert.NotEqual(default, emitter.Playing);
    }

    [Fact]
    public void KeepsALoopingEmitterWithoutCreatingItAgainAndFollowsItsParameters()
    {
        using Engine engine = Start(out _);
        SoundEvent hum = _harness.Sounds.Get("sound:example.sounds.hum");
        SoundEmitter emitter = _harness.System.CreateEmitter(_harness.Probe.Context, hum, WorldPosition.FromBlocks(2, 0, 0));
        _harness.Frames(engine, 1);
        SoundInstance playing = emitter.Playing;
        Assert.True(playing.IsReal);

        emitter.Volume = 0.5f;
        emitter.Pitch = 1.5f;
        _harness.Frames(engine, 60);
        Assert.Equal(playing, emitter.Playing);
        VoiceParameters parameters = Voice(playing).Parameters;
        Assert.Equal(1.5f, parameters.Pitch, 1e-5f);
        Assert.Equal(1, Voice(playing).Plays);

        emitter.IsActive = false;
        _harness.Frames(engine, 1);
        Assert.False(playing.IsActive);
        emitter.IsActive = true;
        _harness.Frames(engine, 1);
        Assert.True(emitter.Playing.IsReal);

        emitter.Dispose();
        _harness.Frames(engine, 1);
        Assert.Empty(_harness.Device.Playing);
        Assert.Empty(_harness.System.Emitters);
    }

    [Fact]
    public void AllocatesNothingAtEachFrameOnceSettled()
    {
        _harness.Backends[0] = new TestAudioBackend("test") { MaxVoices = 8 };
        using Engine engine = Start(out AudioListener listener);
        IAudioSystem audio = _harness.System;
        ISoundRegistry sounds = _harness.Sounds;
        ReverbBus room = audio.CreateReverbBus(_harness.Probe.Context, "room");
        var processor = new TestProcessor { Gain = 0.9f, Cutoff = 8000, Reverb = room, Send = 0.3f };
        using AudioRegistration registration = audio.AddProcessor(_harness.Probe.Context, processor);
        SoundEvent hum = sounds.Get("sound:example.sounds.hum");
        var anchor = new MovingAnchor();
        for (int i = 0; i < 20; i++)
        {
            audio.PlayAt(hum, WorldPosition.FromBlocks(i * 4, 0, 0));
        }

        audio.PlayAttached(hum, anchor);
        using SoundEmitter loop = audio.CreateEmitter(_harness.Probe.Context, hum, WorldPosition.FromBlocks(5, 0, 0));
        using SoundEmitter repeat = audio.CreateEmitter(_harness.Probe.Context, sounds.Get("sound:example.sounds.pop"), WorldPosition.FromBlocks(6, 0, 0), EmitterRepeat.Every(10, 20));
        _harness.Frames(engine, 120);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++)
        {
            anchor.Step();
            listener.Move(WorldPosition.FromBlocks(i * 0.1, 0, 0), Quaternion.Identity);
            room.Parameters = ReverbParameters.Default with { DecayTime = 1 + (i % 3) };
            _harness.Frames(engine, 1);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(8, audio.Statistics.RealVoices);
        Assert.True(audio.Statistics.VirtualVoices >= 14);
        Assert.Equal(0, allocated);
    }

    private Engine Start(out AudioListener listener)
    {
        Engine engine = _harness.Start();
        _harness.Sounds.CompleteDecoding();
        listener = _harness.System.ClaimListener(_harness.Probe.Context);
        listener.Teleport(default, Quaternion.Identity);
        return engine;
    }

    private TestVoice Voice(SoundInstance instance)
    {
        Assert.True(instance.IsReal);
        return _harness.Device.Voices[((AudioMixer)_harness.System).RealOf(instance)];
    }

    /// <summary>A processor whose result the test sets.</summary>
    private sealed class TestProcessor : IEmissionProcessor
    {
        public string Name => "test";

        public AudioFeatures Required { get; init; } = AudioFeatures.Filters | AudioFeatures.AuxiliarySends;

        public AudioFeatures RequiredFeatures => Required;

        public float Gain { get; set; } = 1;

        public float Cutoff { get; set; } = VoiceParameters.OpenCutoff;

        public ReverbBus? Reverb { get; set; }

        public float Send { get; set; }

        public SoundEvent? Seen { get; private set; }

        public void Process(EmissionContext context, ref EmissionParameters parameters)
        {
            Seen = context.Sound;
            parameters.Gain *= Gain;
            parameters.LowPassCutoff = Math.Min(parameters.LowPassCutoff, Cutoff);
            parameters.Reverb = Reverb;
            parameters.ReverbSend = Send;
        }
    }

    /// <summary>An anchor that moves at each step.</summary>
    private sealed class MovingAnchor : ISoundAnchor
    {
        private float _x;

        public void Step() => _x += 0.05f;

        public bool TryGetTransform(out WorldTransform transform)
        {
            transform = WorldTransform.At(WorldPosition.FromBlocks(_x, 1, 0));
            return true;
        }
    }
}
