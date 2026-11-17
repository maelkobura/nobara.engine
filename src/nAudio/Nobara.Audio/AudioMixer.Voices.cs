// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Loop;
using Nobara.Core.Mathematics;

namespace Nobara.Audio;

/// <content>The voices: plays, handles, and the work of each frame.</content>
internal sealed partial class AudioMixer
{
    private enum Placement : byte
    {
        Flat,
        Position,
        Anchor,
    }

    // ---------------------------------------------------------------- plays

    public SoundInstance Play(SoundEvent sound, float volume = 1, float pitch = 1, uint? seed = null)
    {
        ArgumentNullException.ThrowIfNull(sound);
        return Start(sound, Placement.Flat, default, null, default, volume, pitch, seed, loop: sound.Loop, emitter: null);
    }

    public SoundInstance PlayAt(SoundEvent sound, in WorldPosition position, float volume = 1, float pitch = 1, uint? seed = null)
    {
        ArgumentNullException.ThrowIfNull(sound);
        return Start(sound, sound.Spatial is null ? Placement.Flat : Placement.Position, position, null, default, volume, pitch, seed, loop: sound.Loop, emitter: null);
    }

    public SoundInstance PlayAttached(SoundEvent sound, ISoundAnchor anchor, Vector3 offset = default, float volume = 1, float pitch = 1, uint? seed = null)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(anchor);
        return Start(sound, Placement.Anchor, default, anchor, offset, volume, pitch, seed, loop: sound.Loop, emitter: null);
    }

    [HotPath]
    internal bool IsLive(int slot, int generation) => slot < _highWater && _voices[slot].Live && _voices[slot].Generation == generation;

    [HotPath]
    internal bool IsReal(int slot) => _voices[slot].Real >= 0;

    internal SoundEvent SoundOf(int slot) => _voices[slot].Sound;

    /// <summary>Gets the voice of the device of a play, or -1, for the tests.</summary>
    internal int RealOf(SoundInstance instance) => instance.IsActive ? _voices[instance.Slot].Real : -1;

    /// <summary>Lists the live voices as the last frame left them, for the debug window.</summary>
    internal IEnumerable<VoiceView> Voices()
    {
        for (int slot = 0; slot < _highWater; slot++)
        {
            Voice voice = _voices[slot];
            if (voice.Live)
            {
                yield return new VoiceView(voice.Sound, voice.Real >= 0, voice.Gain, voice.Placement != Placement.Flat, voice.Relative.Length(), voice.Heard, voice.Paused);
            }
        }
    }

    [HotPath]
    internal void SetVolume(int slot, float volume, float seconds)
    {
        ref Voice voice = ref _voices[slot];
        voice.FadeTarget = Math.Max(0, float.IsNaN(volume) ? 1 : volume);
        voice.StopAtZero = false;
        if (seconds <= 0)
        {
            voice.Fade = voice.FadeTarget;
            voice.FadeRate = 0;
        }
        else
        {
            voice.FadeRate = Math.Abs(voice.FadeTarget - voice.Fade) / seconds;
        }
    }

    [HotPath]
    internal void SetPitch(int slot, float pitch)
    {
        if (pitch > 0)
        {
            _voices[slot].PitchFactor = pitch;
        }
    }

    [HotPath]
    internal void SetPosition(int slot, in WorldPosition position)
    {
        ref Voice voice = ref _voices[slot];
        if (voice.Placement != Placement.Position)
        {
            return;
        }

        ulong tick = _context.Loop.Tick;
        if (tick != voice.MovedTick)
        {
            voice.Previous = voice.Current;
            voice.MovedTick = tick;
        }

        voice.Current = position;
    }

    internal void Stop(int slot, float seconds)
    {
        ref Voice voice = ref _voices[slot];
        if (seconds <= 0)
        {
            End(slot);
            return;
        }

        voice.FadeTarget = 0;
        voice.FadeRate = voice.Fade / seconds;
        voice.StopAtZero = true;
    }

    private SoundInstance Start(SoundEvent sound, Placement placement, in WorldPosition position, ISoundAnchor? anchor, Vector3 offset, float volume, float pitch, uint? seed, bool loop, SoundEmitter? emitter)
    {
        if (_device is null || sound.Variants.Count == 0)
        {
            return default;
        }

        uint draw = seed ?? NextRandom();
        SoundVariant variant = sound.PickVariant(draw);
        int slot = Allocate();
        ref Voice voice = ref _voices[slot];
        voice.Live = true;
        voice.Generation++;
        voice.Sound = sound;
        voice.Variant = variant;
        voice.EventIndex = sound.Index;
        voice.Emitter = emitter;
        voice.Placement = placement;
        voice.Anchor = anchor;
        voice.Offset = offset;
        voice.Previous = voice.Current = position;
        voice.MovedTick = _context.Loop.Tick;
        voice.AnchorSampled = false;
        voice.Loop = loop;
        voice.Volume = sound.Volume.Lerp((float)SoundEvent.Mix(draw ^ 0x5bd1e995)) * variant.Volume;
        voice.EmitterVolume = 1;
        voice.Pitch = Math.Max(0.01f, sound.Pitch.Lerp((float)SoundEvent.Mix(draw ^ 0x1b873593)) * variant.Pitch * (pitch > 0 ? pitch : 1));
        voice.PitchFactor = 1;
        voice.Fade = voice.FadeTarget = float.IsNaN(volume) ? 1 : Math.Max(0, volume);
        voice.FadeRate = 0;
        voice.StopAtZero = false;
        voice.Frame = 0;
        voice.Started = false;
        voice.Real = -1;
        voice.DevicePaused = false;
        voice.Stream = null;
        voice.StreamEnded = false;
        voice.SmoothedReady = false;
        voice.Reverb = null;
        voice.Sequence = ++_sequence;
        voice.Score = 0;
        voice.Audible = false;
        _instances[sound.Index]++;
        if (_pendingCount == _pending.Length)
        {
            Array.Resize(ref _pending, _pending.Length * 2);
        }

        _pending[_pendingCount++] = slot;
        return new SoundInstance(this, slot, voice.Generation);
    }

    private int Allocate()
    {
        if (_freeCount == 0)
        {
            int old = _voices.Length;
            Array.Resize(ref _voices, old * 2);
            Array.Resize(ref _candidates, old * 2);
            Array.Resize(ref _free, old * 2);
            for (int i = (old * 2) - 1; i >= old; i--)
            {
                _free[_freeCount++] = i;
            }
        }

        int slot = _free[--_freeCount];
        _highWater = Math.Max(_highWater, slot + 1);
        return slot;
    }

    /// <summary>Ends a voice at once: the handle is revoked, and its voice of the device stops at the next frame.</summary>
    private void End(int slot)
    {
        ref Voice voice = ref _voices[slot];
        if (!voice.Live)
        {
            return;
        }

        if (voice.Real >= 0)
        {
            _realOwner[voice.Real] = -1;
            _pendingStops.Add(voice.Real);
            voice.Real = -1;
        }

        voice.Stream?.Dispose();
        voice.Stream = null;
        voice.Live = false;
        voice.Generation++;
        voice.Sound = null!;
        voice.Variant = null!;
        voice.Anchor = null;
        voice.Emitter = null;
        voice.Reverb = null;
        _instances[voice.EventIndex]--;
        _free[_freeCount++] = slot;
    }

    [HotPath]
    private uint NextRandom()
    {
        // xorshift32: the seeds of the plays that do not give one.
        uint x = _random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _random = x;
        return x;
    }

    /// <summary>One voice: a play of a sound event, real when it holds a voice of the device, virtual otherwise.</summary>
    private struct Voice
    {
        public bool Live;
        public int Generation;
        public SoundEvent Sound;
        public SoundVariant Variant;
        public int EventIndex;
        public SoundEmitter? Emitter;
        public Placement Placement;
        public ISoundAnchor? Anchor;
        public Vector3 Offset;
        public WorldPosition Previous;
        public WorldPosition Current;
        public ulong MovedTick;
        public bool AnchorSampled;
        public bool Loop;

        // The volume and pitch drawn for the play, with the factors of the variant (and of the call for the pitch),
        // and the volume of the emitter that plays it.
        public float Volume;
        public float EmitterVolume;
        public float Pitch;
        public float PitchFactor;

        // The volume of the instance, from the call: a factor that moves to its target at a rate per second.
        public float Fade;
        public float FadeTarget;
        public float FadeRate;
        public bool StopAtZero;

        // Where the play is in its file, in frames, counted from the start of the play.
        public double Frame;
        public bool Started;

        public int Real;
        public bool DevicePaused;
        public SoundStream? Stream;
        public bool StreamEnded;

        // The emission parameters, smoothed; the cutoff in octaves (log2 of hertz).
        public bool SmoothedReady;
        public float SmoothedGain;
        public float SmoothedCutoff;
        public float SmoothedSend;
        public ReverbBus? Reverb;

        // The state of the frame.
        public ulong Sequence;
        public bool Paused;
        public bool Audible;
        public float Gain;
        public float Score;
        public Vector3 Relative;
        public Vector3 Velocity;
        public WorldPosition Heard;
    }

    /// <summary>What the debug window shows of a voice.</summary>
    internal readonly record struct VoiceView(SoundEvent Sound, bool Real, float Gain, bool Spatial, float Distance, WorldPosition Position, bool Paused);

    /// <summary>A voice that wants a voice of the device, with what orders it.</summary>
    private struct Candidate
    {
        public int Slot;
        public int Priority;
        public float Score;
        public ulong Sequence;
    }
}
