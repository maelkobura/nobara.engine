// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Audio.Tests.Support;

/// <summary>An audio backend for the tests: it opens a <see cref="TestAudioDevice"/>, which records what nAudio asks of it.</summary>
internal sealed class TestAudioBackend(string name) : IAudioBackend
{
    public string Name { get; } = name;

    public int MaxVoices { get; init; } = 32;

    public AudioFeatures Features { get; init; } = AudioFeatures.Hrtf | AudioFeatures.Filters | AudioFeatures.AuxiliarySends | AudioFeatures.Effects;

    public int MaxReverbSlots { get; init; } = 4;

    /// <summary>Gets or sets whether opening fails, as without any output device.</summary>
    public bool Fails { get; init; }

    public TestAudioDevice? Device { get; private set; }

    public AudioDeviceOptions Options { get; private set; }

    public AudioDevice Open(in AudioDeviceOptions options)
    {
        if (Fails)
        {
            throw new NotSupportedException("no output device");
        }

        Options = options;
        Device = new TestAudioDevice(new AudioCapabilities { Features = Features, MaxVoices = MaxVoices, MaxReverbSlots = Features.HasFlag(AudioFeatures.Effects) ? MaxReverbSlots : 0 }, options.Hrtf);
        return Device;
    }
}

/// <summary>A device that plays nothing: it keeps the state of each voice and of each reverberation slot as nAudio set it.</summary>
internal sealed class TestAudioDevice(AudioCapabilities capabilities, HrtfMode hrtf) : AudioDevice
{
    private readonly Dictionary<int, int> _bufferFrames = [];
    private int _nextBuffer = 1;

    public override AudioCapabilities Capabilities { get; } = capabilities;

    public override string Description => "Test output";

    public override bool HrtfEnabled => Hrtf == HrtfMode.On;

    public HrtfMode Hrtf { get; private set; } = hrtf;

    public TestVoice[] Voices { get; } = Enumerable.Range(0, capabilities.MaxVoices).Select(_ => new TestVoice()).ToArray();

    public ReverbParameters?[] Reverbs { get; } = new ReverbParameters?[Math.Max(0, capabilities.MaxReverbSlots)];

    public ListenerParameters Listener { get; private set; }

    public int Commits { get; private set; }

    public int Buffers => _bufferFrames.Count;

    public bool Disposed { get; private set; }

    /// <summary>Gets the voices that play now, paused ones included.</summary>
    public IEnumerable<TestVoice> Playing => Voices.Where(v => v.Playing);

    public override AudioBuffer CreateBuffer(ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        var buffer = new AudioBuffer(_nextBuffer++);
        _bufferFrames.Add(buffer.Id, samples.Length / channels);
        return buffer;
    }

    public override void DestroyBuffer(AudioBuffer buffer) => _bufferFrames.Remove(buffer.Id);

    public override void Play(int voice, AudioBuffer buffer, bool loop, int startFrame)
    {
        if (Voices[voice].Playing)
        {
            Assert.Fail($"voice {voice} plays already");
        }

        Assert.True(_bufferFrames.ContainsKey(buffer.Id));
        TestVoice v = Voices[voice];
        (v.Playing, v.Buffer, v.Loop, v.StartFrame, v.Paused, v.Plays) = (true, buffer, loop, startFrame, false, v.Plays + 1);
    }

    public override void Queue(int voice, ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        TestVoice v = Voices[voice];
        v.Playing = true;
        v.Queued++;
        v.QueuedFrames += samples.Length / channels;
    }

    public override int GetQueuedBlocks(int voice) => Voices[voice].Queued;

    public override void Stop(int voice)
    {
        TestVoice v = Voices[voice];
        (v.Playing, v.Paused, v.Queued, v.QueuedFrames, v.Buffer) = (false, false, 0, 0, default);
        v.Stops++;
    }

    public override void Pause(int voice) => Voices[voice].Paused = true;

    public override void Resume(int voice) => Voices[voice].Paused = false;

    public override void SetVoice(int voice, in VoiceParameters parameters) => Voices[voice].Parameters = parameters;

    public override void SetListener(in ListenerParameters listener) => Listener = listener;

    public override void SetReverb(int slot, in ReverbParameters parameters) => Reverbs[slot] = parameters;

    public override void ClearReverb(int slot) => Reverbs[slot] = null;

    public override void SetHrtf(HrtfMode mode) => Hrtf = mode;

    public override void Commit() => Commits++;

    /// <summary>Plays the queued blocks of a voice to their end, as the library would.</summary>
    public void Drain(int voice) => Voices[voice].Queued = 0;

    protected override void Dispose(bool disposing) => Disposed = true;
}

/// <summary>A voice of a <see cref="TestAudioDevice"/>.</summary>
internal sealed class TestVoice
{
    public bool Playing { get; set; }

    public bool Paused { get; set; }

    public AudioBuffer Buffer { get; set; }

    public bool Loop { get; set; }

    public int StartFrame { get; set; }

    public int Plays { get; set; }

    public int Stops { get; set; }

    public int Queued { get; set; }

    public int QueuedFrames { get; set; }

    public VoiceParameters Parameters { get; set; }
}

/// <summary>A clock that the tests move: frames last what the test says.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
}
