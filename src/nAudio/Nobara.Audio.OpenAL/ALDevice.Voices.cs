// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace Nobara.Audio.OpenAL;

/// <content>Buffers, sources, and the settings of a voice.</content>
internal sealed unsafe partial class ALDevice
{
    // The highest gain a source may take; the library clamps to 1 by default.
    private const float MaxSourceGain = 4f;

    private readonly Stack<uint> _freeBlocks = [];
    private readonly List<uint> _blocks = [];
    private readonly HashSet<uint> _buffers = [];
    private VoiceState[] _voices = [];
    private int _liveSources;
    private int _liveBuffers;
    private bool _hasSpatialize;

    /// <inheritdoc/>
    public override AudioBuffer CreateBuffer(ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        uint id = NewBuffer();
        _buffers.Add(id);
        Fill(id, samples, channels, sampleRate);
        return new AudioBuffer((int)id);
    }

    /// <inheritdoc/>
    public override void DestroyBuffer(AudioBuffer buffer)
    {
        if (_buffers.Remove((uint)buffer.Id))
        {
            _al.DeleteBuffer((uint)buffer.Id);
            _liveBuffers--;
        }
    }

    /// <inheritdoc/>
    public override void Play(int voice, AudioBuffer buffer, bool loop, int startFrame)
    {
        uint source = EnsureSource(voice);
        ref VoiceState state = ref _voices[voice];
        _al.SetSourceProperty(source, SourceInteger.Buffer, buffer.Id);
        _al.SetSourceProperty(source, SourceBoolean.Looping, loop);
        _al.SetSourceProperty(source, SourceInteger.SampleOffset, Math.Max(0, startFrame));
        _al.SourcePlay(source);
        state.Streaming = false;
        state.Started = true;
        state.Paused = false;
    }

    /// <inheritdoc/>
    public override void Queue(int voice, ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        uint source = EnsureSource(voice);
        ref VoiceState state = ref _voices[voice];
        if (!state.Streaming)
        {
            _al.SetSourceProperty(source, SourceInteger.Buffer, 0);
            _al.SetSourceProperty(source, SourceBoolean.Looping, false);
            state.Streaming = true;
        }

        uint block;
        if (_freeBlocks.Count > 0)
        {
            block = _freeBlocks.Pop();
        }
        else
        {
            block = NewBuffer();
            _blocks.Add(block);
        }

        Fill(block, samples, channels, sampleRate);
        _al.SourceQueueBuffers(source, 1, &block);

        // A source that ran out of blocks has stopped: it plays again from the block it was just given.
        _al.GetSourceProperty(source, GetSourceInteger.SourceState, out int sourceState);
        if (!state.Paused && (SourceState)sourceState != SourceState.Playing)
        {
            _al.SourcePlay(source);
            state.Started = true;
        }
    }

    /// <inheritdoc/>
    public override int GetQueuedBlocks(int voice)
    {
        uint source = _voices[voice].Source;
        if (source == 0 || !_voices[voice].Streaming)
        {
            return 0;
        }

        _al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
        for (; processed > 0; processed--)
        {
            uint block;
            _al.SourceUnqueueBuffers(source, 1, &block);
            _freeBlocks.Push(block);
        }

        _al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out int queued);
        return queued;
    }

    /// <inheritdoc/>
    public override void Stop(int voice)
    {
        ref VoiceState state = ref _voices[voice];
        if (state.Source == 0)
        {
            return;
        }

        _al.SourceStop(state.Source);
        DetachBuffers(ref state);
        state.Started = false;
        state.Paused = false;
    }

    /// <inheritdoc/>
    public override void Pause(int voice)
    {
        ref VoiceState state = ref _voices[voice];
        if (state.Source != 0 && state.Started)
        {
            _al.SourcePause(state.Source);
        }

        state.Paused = true;
    }

    /// <inheritdoc/>
    public override void Resume(int voice)
    {
        ref VoiceState state = ref _voices[voice];
        if (state.Paused && state.Source != 0 && state.Started)
        {
            _al.SourcePlay(state.Source);
        }

        state.Paused = false;
    }

    /// <inheritdoc/>
    [HotPath]
    public override void SetVoice(int voice, in VoiceParameters parameters)
    {
        uint source = EnsureSource(voice);
        ref VoiceState state = ref _voices[voice];
        bool first = !state.HasParameters;
        VoiceParameters last = state.Last;

        if (first || parameters.Gain != last.Gain)
        {
            _al.SetSourceProperty(source, SourceFloat.Gain, Math.Clamp(parameters.Gain, 0f, MaxSourceGain));
        }

        if (first || parameters.Pitch != last.Pitch)
        {
            _al.SetSourceProperty(source, SourceFloat.Pitch, Math.Max(parameters.Pitch, 0.01f));
        }

        if (first || parameters.Spatial != last.Spatial)
        {
            // A flat voice plays as it is, at the center: relative to the listener, at its position, and not placed in space.
            _al.SetSourceProperty(source, SourceBoolean.SourceRelative, !parameters.Spatial);
            if (_hasSpatialize)
            {
                _al.SetSourceProperty(source, (SourceInteger)SoftAlc.SourceSpatializeAttribute, parameters.Spatial ? SoftAlc.DontCare : 0);
            }
        }

        Vector3 position = parameters.Spatial ? parameters.Position : Vector3.Zero;
        Vector3 velocity = parameters.Spatial ? parameters.Velocity : Vector3.Zero;
        if (first || position != state.Position)
        {
            _al.SetSourceProperty(source, SourceVector3.Position, position.X, position.Y, position.Z);
            state.Position = position;
        }

        if (first || velocity != state.Velocity)
        {
            _al.SetSourceProperty(source, SourceVector3.Velocity, velocity.X, velocity.Y, velocity.Z);
            state.Velocity = velocity;
        }

        if (_capabilities.Supports(AudioFeatures.Filters))
        {
            SetLowPass(source, ref state, parameters.LowPassCutoff);
        }

        if (_capabilities.Supports(AudioFeatures.AuxiliarySends))
        {
            SetSend(source, ref state, parameters.ReverbSlot, parameters.ReverbSend);
        }

        state.Last = parameters;
        state.HasParameters = true;
    }

    /// <inheritdoc/>
    public override void SetListener(in ListenerParameters listener)
    {
        float* orientation = stackalloc float[6]
        {
            listener.Forward.X, listener.Forward.Y, listener.Forward.Z,
            listener.Up.X, listener.Up.Y, listener.Up.Z,
        };
        _al.SetListenerProperty(ListenerFloat.Gain, Math.Max(0f, listener.Gain));
        _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
        _al.SetListenerProperty(ListenerVector3.Velocity, listener.Velocity.X, listener.Velocity.Y, listener.Velocity.Z);
    }

    [HotPath]
    private uint NewBuffer()
    {
        uint id = _al.GenBuffer();
        _liveBuffers++;
        return id;
    }

    [HotPath]
    private void Fill(uint buffer, ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        BufferFormat format = channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;
        fixed (short* data = samples)
        {
            _al.BufferData(buffer, format, data, samples.Length * sizeof(short), sampleRate);
        }
    }

    [HotPath]
    private uint EnsureSource(int voice)
    {
        ref VoiceState state = ref _voices[voice];
        if (state.Source != 0)
        {
            return state.Source;
        }

        uint source = _al.GenSource();
        _liveSources++;
        state.Source = source;

        // The library applies no distance model: the gain it is given is final.
        _al.SetSourceProperty(source, SourceFloat.MaxGain, MaxSourceGain);
        _al.SetSourceProperty(source, SourceFloat.RolloffFactor, 0f);
        return source;
    }

    /// <summary>Forgets the buffer or the queue of a stopped source; blocks go back to the pool.</summary>
    [HotPath]
    private void DetachBuffers(ref VoiceState state)
    {
        if (state.Streaming)
        {
            _al.GetSourceProperty(state.Source, GetSourceInteger.BuffersQueued, out int queued);
            for (; queued > 0; queued--)
            {
                uint block;
                _al.SourceUnqueueBuffers(state.Source, 1, &block);
                _freeBlocks.Push(block);
            }

            state.Streaming = false;
            return;
        }

        _al.SetSourceProperty(state.Source, SourceInteger.Buffer, 0);
    }

    /// <summary>Deletes every object of the device; the context is current.</summary>
    private void ReleaseObjects()
    {
        for (int i = 0; i < _voices.Length; i++)
        {
            ref VoiceState state = ref _voices[i];
            if (state.Source != 0)
            {
                _al.SourceStop(state.Source);
                _al.SetSourceProperty(state.Source, SourceInteger.Buffer, 0);
                _al.DeleteSource(state.Source);
                _liveSources--;
                state.Source = 0;
            }
        }

        ReleaseEffects();
        foreach (uint block in _blocks)
        {
            _al.DeleteBuffer(block);
            _liveBuffers--;
        }

        _blocks.Clear();
        _freeBlocks.Clear();

        foreach (uint buffer in _buffers)
        {
            _al.DeleteBuffer(buffer);
            _liveBuffers--;
        }

        _buffers.Clear();
    }

    /// <summary>What the device keeps of a voice.</summary>
    private struct VoiceState
    {
        public uint Source;
        public bool Streaming;
        public bool Started;
        public bool Paused;
        public bool HasParameters;
        public VoiceParameters Last;
        public Vector3 Position;
        public Vector3 Velocity;
        public uint DirectFilter;
        public bool DirectAttached;
        public uint SendFilter;
        public float DirectGainHf;
        public int SendSlot;
        public float SendGain;
    }
}
