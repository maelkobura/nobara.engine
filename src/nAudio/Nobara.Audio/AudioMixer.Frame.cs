// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Loop;
using Nobara.Core.Mathematics;

namespace Nobara.Audio;

/// <content>The work of each frame, in the phase <c>Render</c>.</content>
internal sealed partial class AudioMixer
{
    /// <inheritdoc/>
    public void Run(in PhaseTime time)
    {
        AudioDevice? device = _device;
        if (device is null)
        {
            return;
        }

        float delta = (float)time.Delta.TotalSeconds;
        bool newTick = time.Tick != _lastTick;
        _lastTick = time.Tick;
        _gamePaused = _pause?.Paused ?? false;

        // Voices ended since the previous frame free their voice of the device first.
        FlushStops(device);
        ComputeBuses();

        WorldPosition listenerPosition = default;
        Quaternion listenerRotation = Quaternion.Identity;
        Vector3 listenerVelocity = Vector3.Zero;
        bool hasListener = _listener is { IsPlaced: true };
        float tickSeconds = (float)_context.Loop.TickDuration.TotalSeconds;
        if (hasListener)
        {
            _listener!.Sample(time.Tick, (float)time.Alpha, _interpolates, tickSeconds, out listenerPosition, out listenerRotation, out listenerVelocity);
        }

        UpdateEmitters(delta, newTick, hasListener, listenerPosition);

        for (int slot = 0; slot < _highWater; slot++)
        {
            if (_voices[slot].Live)
            {
                Advance(slot, delta, newTick, time, tickSeconds, hasListener, listenerPosition);
            }
        }

        LimitInstances();

        // Voices ended in this frame stop before their voices of the device go to others.
        FlushStops(device);
        int realCount = Select(device);
        Apply(device);

        device.SetListener(new ListenerParameters(
            Vector3.Transform(-Vector3.UnitZ, listenerRotation),
            Vector3.Transform(Vector3.UnitY, listenerRotation),
            listenerVelocity,
            1f));
        SmoothReverbs(device, delta);
        FlushStops(device);
        device.Commit();

        int live = 0;
        for (int slot = 0; slot < _highWater; slot++)
        {
            if (_voices[slot].Live)
            {
                live++;
            }
        }

        Statistics = new AudioStatistics(realCount, live - realCount, _emitters.Count);
        if (!hasListener && !_warnedNoListener && live > 0)
        {
            _warnedNoListener = true;
            _context.Logger.Debug("No module placed the listener: spatial sounds are not heard.");
        }
    }

    private void FlushStops(AudioDevice device)
    {
        for (int i = 0; i < _pendingStops.Count; i++)
        {
            device.Stop(_pendingStops[i]);
        }

        _pendingStops.Clear();
    }

    [HotPath]
    private void ComputeBuses()
    {
        // Parents come before their children in the order of the tree.
        for (int i = 0; i < _busOrder.Length; i++)
        {
            int index = _busOrder[i];
            int parent = _busParent[index];
            _busEffective[index] = _busVolume[index] * (parent < 0 ? 1f : _busEffective[parent]);
        }
    }

    /// <summary>Reads an anchor; an exception of its code is charged to the module closest to it, and the anchor counts as gone.</summary>
    private bool ReadAnchor(ISoundAnchor anchor, out WorldTransform transform)
    {
        try
        {
            return anchor.TryGetTransform(out transform);
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            _context.ReportFault(exception);
            transform = default;
            return false;
        }
    }

    /// <summary>Gets whether the module of a registration still runs: a disabled module plays nothing.</summary>
    private static bool IsRunning(ModuleContext module) => module.IsModuleActive(module.Module.Id);

    private void UpdateEmitters(float delta, bool newTick, bool hasListener, in WorldPosition listener)
    {
        for (int i = 0; i < _emitters.Count; i++)
        {
            SoundEmitter emitter = _emitters[i];
            bool wanted = emitter.IsActive && !emitter.IsSuspended && IsRunning(emitter.Module);
            SoundInstance playing = emitter.Playing;
            if (!wanted)
            {
                if (playing.IsActive)
                {
                    playing.Stop();
                }

                emitter.Playing = default;
                continue;
            }

            if (playing.IsActive)
            {
                ref Voice voice = ref _voices[playing.Slot];
                voice.EmitterVolume = emitter.Volume;
                voice.PitchFactor = emitter.Pitch;
            }

            if (emitter.Repeat.IsLoop)
            {
                // A looping emitter whose anchor is gone tries again at the next tick.
                if (!playing.IsActive && (newTick || emitter.Anchor is null))
                {
                    emitter.Playing = StartEmitter(emitter, loop: true);
                }

                continue;
            }

            if (_gamePaused && _busFollowsPause[emitter.Sound.Bus.Index])
            {
                continue;
            }

            if (emitter.Countdown < 0)
            {
                emitter.Countdown = DrawInterval(emitter);
            }

            emitter.Countdown -= delta;
            if (emitter.Countdown > 0)
            {
                continue;
            }

            emitter.Countdown = DrawInterval(emitter);

            // Out of range, the play is skipped: a repeating emitter costs no voice there.
            if (emitter.Sound.Spatial is { } spatial && (!hasListener || !EmitterPosition(emitter, out WorldPosition at) || at.RelativeTo(listener).Length() > spatial.MaxDistance))
            {
                continue;
            }

            emitter.Playing = StartEmitter(emitter, loop: false);
        }
    }

    private SoundInstance StartEmitter(SoundEmitter emitter, bool loop)
    {
        SoundEvent sound = emitter.Sound;
        Placement placement = sound.Spatial is null ? Placement.Flat : emitter.Anchor is null ? Placement.Position : Placement.Anchor;
        SoundInstance instance = Start(sound, placement, emitter.Position, emitter.Anchor, emitter.Offset, 1, 1, Draw(emitter), loop, emitter);
        if (instance.IsActive)
        {
            _voices[instance.Slot].EmitterVolume = emitter.Volume;
            _voices[instance.Slot].PitchFactor = emitter.Pitch;
        }

        return instance;
    }

    private bool EmitterPosition(SoundEmitter emitter, out WorldPosition position)
    {
        if (emitter.Anchor is null)
        {
            position = emitter.Position;
            return true;
        }

        if (ReadAnchor(emitter.Anchor, out WorldTransform transform))
        {
            position = transform.TransformPoint(emitter.Offset);
            return true;
        }

        position = default;
        return false;
    }

    [HotPath]
    private static uint Draw(SoundEmitter emitter)
    {
        uint x = emitter.Random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        emitter.Random = x;
        return x;
    }

    [HotPath]
    private static float DrawInterval(SoundEmitter emitter)
    {
        EmitterRepeat repeat = emitter.Repeat;
        return repeat.MinInterval + ((repeat.MaxInterval - repeat.MinInterval) * (float)SoundEvent.Mix(Draw(emitter)));
    }

    /// <summary>Advances one voice in time, places it, and scores it.</summary>
    private void Advance(int slot, float delta, bool newTick, in PhaseTime time, float tickSeconds, bool hasListener, in WorldPosition listener)
    {
        ref Voice voice = ref _voices[slot];
        SoundEvent sound = voice.Sound;
        voice.Paused = _gamePaused && _busFollowsPause[sound.Bus.Index];

        if (voice.Placement == Placement.Anchor && (newTick || !voice.AnchorSampled))
        {
            if (!ReadAnchor(voice.Anchor!, out WorldTransform transform))
            {
                End(slot);
                return;
            }

            WorldPosition at = transform.TransformPoint(voice.Offset);
            voice.Previous = voice.AnchorSampled ? voice.Current : at;
            voice.Current = at;
            voice.MovedTick = time.Tick;
            voice.AnchorSampled = true;
        }

        // Time goes on from the first frame after the play, unless its bus waits for the game.
        if (voice.Started && !voice.Paused)
        {
            voice.Frame += delta * voice.Pitch * voice.PitchFactor * voice.Variant.SampleRate;
            if (voice.FadeRate > 0)
            {
                float step = voice.FadeRate * delta;
                voice.Fade = voice.Fade < voice.FadeTarget ? Math.Min(voice.FadeTarget, voice.Fade + step) : Math.Max(voice.FadeTarget, voice.Fade - step);
            }
        }

        voice.Started = true;
        if ((voice.StopAtZero && voice.Fade <= 0) || (!voice.Loop && voice.Stream is null && voice.Frame >= voice.Variant.Frames))
        {
            End(slot);
            return;
        }

        // The position heard, relative to the listener, and the attenuation by distance.
        bool spatial = voice.Placement != Placement.Flat;
        float distanceGain = 1;
        WorldPosition position = listener;
        voice.Relative = Vector3.Zero;
        voice.Velocity = Vector3.Zero;
        if (spatial)
        {
            bool moved = _interpolates && voice.MovedTick == time.Tick && time.Tick != 0;
            position = moved ? WorldPosition.Lerp(voice.Previous, voice.Current, (float)time.Alpha) : voice.Current;
            if (moved && tickSeconds > 0)
            {
                voice.Velocity = voice.Current.RelativeTo(voice.Previous) / tickSeconds;
            }

            voice.Heard = position;
            if (hasListener)
            {
                voice.Relative = position.RelativeTo(listener);
                distanceGain = Attenuation(sound.Spatial!.Value, voice.Relative.Length());
            }
            else
            {
                distanceGain = 0;
            }
        }

        // The chain of emission processors, then the smoothing of what it produced.
        EmissionParameters emission = EmissionParameters.Neutral;
        if (_processors.Count > 0 && distanceGain > 0)
        {
            _emission.Sound = sound;
            _emission.Instance = new SoundInstance(this, slot, voice.Generation);
            _emission.IsSpatial = spatial;
            _emission.Position = position;
            _emission.ListenerPosition = listener;
            _emission.RelativePosition = voice.Relative;
            _emission.Distance = voice.Relative.Length();
            _emission.DeltaTime = delta;
            for (int i = 0; i < _processors.Count; i++)
            {
                ProcessorEntry entry = _processors[i];
                if (!entry.Disabled && IsRunning(entry.Registration.Module))
                {
                    try
                    {
                        entry.Processor.Process(_emission, ref emission);
                    }
                    catch (Exception exception) when (exception is not EngineStopException)
                    {
                        entry.Registration.Module.ReportFault(exception);
                    }
                }
            }

            // A reverberation bus of a disabled module takes no send.
            if (emission.Reverb is { } reverb && !IsRunning(reverb.Module))
            {
                emission.Reverb = null;
            }
        }

        Smooth(ref voice, emission, delta);
        voice.Gain = voice.Volume * voice.EmitterVolume * voice.Fade * _busEffective[sound.Bus.Index] * distanceGain * voice.SmoothedGain;
        voice.Audible = voice.Gain >= AudibleGain;
        voice.Score = voice.Gain * (voice.Real >= 0 ? Hysteresis : 1f);
    }

    [HotPath]
    private static void Smooth(ref Voice voice, in EmissionParameters target, float delta)
    {
        float cutoff = MathF.Log2(Math.Clamp(target.LowPassCutoff, 10f, VoiceParameters.OpenCutoff));
        float gain = Math.Max(0, target.Gain);
        float send = target.Reverb is { IsAvailable: true } ? Math.Clamp(target.ReverbSend, 0, 1) : 0;
        if (!voice.SmoothedReady)
        {
            voice.SmoothedGain = gain;
            voice.SmoothedCutoff = cutoff;
            voice.SmoothedSend = send;
            voice.Reverb = target.Reverb;
            voice.SmoothedReady = true;
            return;
        }

        // A change of bus fades the send out of the old one before it rises in the new one.
        if (!ReferenceEquals(voice.Reverb, target.Reverb))
        {
            if (voice.SmoothedSend > 0.01f && voice.Reverb is { IsAvailable: true })
            {
                send = 0;
            }
            else
            {
                voice.Reverb = target.Reverb;
                voice.SmoothedSend = 0;
            }
        }

        float k = 1f - MathF.Exp(-delta / SmoothingSeconds);
        voice.SmoothedGain += (gain - voice.SmoothedGain) * k;
        voice.SmoothedCutoff += (cutoff - voice.SmoothedCutoff) * k;
        voice.SmoothedSend += (send - voice.SmoothedSend) * k;
    }

    /// <summary>Gets the gain of a spatial sound at a distance; 0 beyond its maximum distance, and fading over the last tenth of its range.</summary>
    [HotPath]
    internal static float Attenuation(in SoundSpatial spatial, float distance)
    {
        float reference = Math.Max(spatial.ReferenceDistance, 1e-3f);
        float max = Math.Max(spatial.MaxDistance, reference);
        if (distance >= max)
        {
            return 0;
        }

        if (distance <= reference)
        {
            return 1;
        }

        float gain = spatial.Rolloff switch
        {
            SoundRolloff.Linear => 1 - ((distance - reference) / (max - reference)),
            SoundRolloff.Exponential => (reference / distance) * (reference / distance),
            _ => reference / distance,
        };
        float edge = max * 0.9f;
        return distance > edge ? gain * ((max - distance) / (max - edge)) : gain;
    }

    /// <summary>Applies the limits of instances to the plays of the frame: the weakest instance of an event over its limit ends, be it the new one.</summary>
    private void LimitInstances()
    {
        for (int p = 0; p < _pendingCount; p++)
        {
            int slot = _pending[p];
            if (!_voices[slot].Live)
            {
                continue;
            }

            SoundEvent sound = _voices[slot].Sound;
            int limit = sound.MaxInstances > 0 ? sound.MaxInstances : _defaultMaxInstances;
            while (limit > 0 && _instances[sound.Index] > limit)
            {
                int weakest = -1;
                for (int i = 0; i < _highWater; i++)
                {
                    ref Voice other = ref _voices[i];
                    if (other.Live && other.EventIndex == sound.Index
                        && (weakest < 0 || other.Gain < _voices[weakest].Gain || (other.Gain == _voices[weakest].Gain && other.Sequence > _voices[weakest].Sequence)))
                    {
                        weakest = i;
                    }
                }

                End(weakest);
            }
        }

        _pendingCount = 0;
    }

    /// <summary>Gives the voices of the device to the most important voices; returns how many have one.</summary>
    private int Select(AudioDevice device)
    {
        int count = 0;
        for (int slot = 0; slot < _highWater; slot++)
        {
            ref Voice voice = ref _voices[slot];
            if (voice.Live && voice.Audible && CanPlay(ref voice))
            {
                _candidates[count++] = new Candidate { Slot = slot, Priority = (int)voice.Sound.Priority, Score = voice.Score, Sequence = voice.Sequence };
            }
        }

        Span<Candidate> candidates = _candidates.AsSpan(0, count);
        SortCandidates(candidates);
        int real = Math.Min(count, _realLimit);
        Array.Clear(_realWanted);
        for (int i = 0; i < real; i++)
        {
            int owner = _voices[candidates[i].Slot].Real;
            if (owner >= 0)
            {
                _realWanted[owner] = true;
            }
        }

        // Voices that lose their voice of the device become virtual; they go on in time.
        for (int r = 0; r < _realOwner.Length; r++)
        {
            int slot = _realOwner[r];
            if (slot >= 0 && !_realWanted[r])
            {
                Demote(device, slot);
            }
        }

        int free = 0;
        for (int i = 0; i < real; i++)
        {
            int slot = candidates[i].Slot;
            if (_voices[slot].Real >= 0)
            {
                continue;
            }

            while (_realOwner[free] >= 0)
            {
                free++;
            }

            Promote(device, slot, free);
        }

        return real;
    }

    /// <summary>Sorts candidates, most important first, with a heap sort: in place, and with no comparer to box.</summary>
    [HotPath]
    private static void SortCandidates(Span<Candidate> items)
    {
        int count = items.Length;
        for (int i = (count / 2) - 1; i >= 0; i--)
        {
            SiftDown(items, i, count);
        }

        for (int end = count - 1; end > 0; end--)
        {
            (items[0], items[end]) = (items[end], items[0]);
            SiftDown(items, 0, end);
        }
    }

    [HotPath]
    private static void SiftDown(Span<Candidate> items, int root, int count)
    {
        while (true)
        {
            int child = (2 * root) + 1;
            if (child >= count)
            {
                return;
            }

            if (child + 1 < count && Compare(items[child + 1], items[child]) > 0)
            {
                child++;
            }

            if (Compare(items[child], items[root]) <= 0)
            {
                return;
            }

            (items[root], items[child]) = (items[child], items[root]);
            root = child;
        }
    }

    /// <summary>Orders candidates: the higher priority first, then the louder, then the older.</summary>
    [HotPath]
    private static int Compare(in Candidate x, in Candidate y)
    {
        if (x.Priority != y.Priority)
        {
            return y.Priority.CompareTo(x.Priority);
        }

        int byScore = y.Score.CompareTo(x.Score);
        return byScore != 0 ? byScore : x.Sequence.CompareTo(y.Sequence);
    }

    private bool CanPlay(ref Voice voice) => voice.Sound.Streamed || voice.Real >= 0 || _sounds.TryGetClip(voice.Variant, out _);

    private void Demote(AudioDevice device, int slot)
    {
        ref Voice voice = ref _voices[slot];
        device.Stop(voice.Real);
        _realOwner[voice.Real] = -1;
        voice.Real = -1;
        voice.DevicePaused = false;
        voice.Stream?.Dispose();
        voice.Stream = null;
    }

    private void Promote(AudioDevice device, int slot, int real)
    {
        ref Voice voice = ref _voices[slot];
        long length = Math.Max(1, voice.Variant.Frames);
        long frame = (long)voice.Frame;
        frame = voice.Loop ? frame % length : Math.Min(frame, length - 1);
        _realOwner[real] = slot;
        voice.Real = real;
        voice.DevicePaused = false;
        device.SetVoice(real, Parameters(ref voice));
        if (voice.Sound.Streamed)
        {
            voice.Stream = _sounds.OpenStream(voice.Variant, voice.Loop, frame);
            voice.StreamEnded = false;
            return;
        }

        _sounds.TryGetClip(voice.Variant, out SoundClip? clip);
        if (!_buffers.TryGetValue(clip!, out AudioBuffer buffer))
        {
            buffer = device.CreateBuffer(clip!.Samples, clip.Channels, clip.SampleRate);
            _buffers.Add(clip, buffer);
        }

        device.Play(real, buffer, voice.Loop, (int)frame);
    }

    /// <summary>Sends the parameters of the real voices, pauses them with the game, and feeds the streams.</summary>
    private void Apply(AudioDevice device)
    {
        for (int r = 0; r < _realOwner.Length; r++)
        {
            int slot = _realOwner[r];
            if (slot < 0)
            {
                continue;
            }

            ref Voice voice = ref _voices[slot];
            device.SetVoice(r, Parameters(ref voice));
            if (voice.Paused != voice.DevicePaused)
            {
                if (voice.Paused)
                {
                    device.Pause(r);
                }
                else
                {
                    device.Resume(r);
                }

                voice.DevicePaused = voice.Paused;
            }

            if (voice.Stream is { } stream)
            {
                Feed(device, slot, r, stream);
            }
        }
    }

    private void Feed(AudioDevice device, int slot, int real, SoundStream stream)
    {
        ref Voice voice = ref _voices[slot];
        int queued = device.GetQueuedBlocks(real);
        while (!voice.StreamEnded && queued < QueuedBlocks && stream.TryTake(out SoundBlock block))
        {
            if (block.Frames > 0)
            {
                device.Queue(real, stream.GetSamples(block), voice.Variant.Channels, voice.Variant.SampleRate);
                queued++;
            }

            voice.StreamEnded = block.IsLast;
            stream.Return(block);
        }

        if (stream.Error is not null || (voice.StreamEnded && queued == 0))
        {
            End(slot);
        }
    }

    [HotPath]
    private static VoiceParameters Parameters(ref Voice voice) => new()
    {
        Gain = voice.Gain,
        Pitch = voice.Pitch * voice.PitchFactor,
        Spatial = voice.Placement != Placement.Flat,
        Position = voice.Relative,
        Velocity = voice.Velocity,
        LowPassCutoff = MathF.Pow(2, voice.SmoothedCutoff),
        ReverbSlot = voice.Reverb is { IsAvailable: true } reverb ? reverb.Slot : -1,
        ReverbSend = voice.SmoothedSend,
    };

    private void SmoothReverbs(AudioDevice device, float delta)
    {
        float k = 1f - MathF.Exp(-delta / SmoothingSeconds);
        for (int i = 0; i < _reverbs.Count; i++)
        {
            ReverbBus reverb = _reverbs[i];
            if (reverb.Slot < 0 || !reverb.Changed || !IsRunning(reverb.Module))
            {
                continue;
            }

            ReverbParameters target = reverb.Parameters;
            ReverbParameters next = ReverbParameters.Lerp(reverb.Current, target, k);
            bool reached = Close(next, target);
            reverb.Current = reached ? target : next;
            reverb.Changed = !reached;
            device.SetReverb(reverb.Slot, reverb.Current);
        }
    }

    [HotPath]
    private static bool Close(in ReverbParameters a, in ReverbParameters b) =>
        Math.Abs(a.DecayTime - b.DecayTime) < 1e-3f && Math.Abs(a.ReflectionsDelay - b.ReflectionsDelay) < 1e-5f
        && Math.Abs(a.ReflectionsGain - b.ReflectionsGain) < 1e-4f && Math.Abs(a.Density - b.Density) < 1e-4f
        && Math.Abs(a.Diffusion - b.Diffusion) < 1e-4f && Math.Abs(a.Gain - b.Gain) < 1e-4f;
}
