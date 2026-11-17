// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace Nobara.Audio.OpenAL;

/// <content>What the library holds, read back for the tests.</content>
internal sealed unsafe partial class ALDevice
{
    /// <summary>Reads the state of a voice from the library.</summary>
    /// <param name="voice">The voice.</param>
    /// <returns>Its state.</returns>
    internal SourceReading Inspect(int voice)
    {
        uint source = _voices[voice].Source;
        if (source == 0)
        {
            return default;
        }

        _al.GetSourceProperty(source, SourceFloat.Gain, out float gain);
        _al.GetSourceProperty(source, SourceFloat.Pitch, out float pitch);
        _al.GetSourceProperty(source, SourceBoolean.SourceRelative, out bool relative);
        _al.GetSourceProperty(source, SourceBoolean.Looping, out bool looping);
        _al.GetSourceProperty(source, SourceVector3.Position, out Vector3 position);
        _al.GetSourceProperty(source, SourceVector3.Velocity, out Vector3 velocity);
        _al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
        _al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out int queued);
        var reading = new SourceReading
        {
            Exists = true,
            Gain = gain,
            Pitch = pitch,
            Relative = relative,
            Looping = looping,
            Position = position,
            Velocity = velocity,
            Playing = (SourceState)state == SourceState.Playing,
            Paused = (SourceState)state == SourceState.Paused,
            Queued = queued,
            DirectGainHf = 1f,
        };
        if (_efx is null)
        {
            return reading;
        }

        // The library copies the settings of a filter into the source and does not give them back: read the filters this device attached.
        VoiceState held = _voices[voice];
        if (held.DirectAttached)
        {
            _efx.GetFilterProperty(held.DirectFilter, FilterFloat.LowpassGainHF, out float gainHf);
            reading = reading with { DirectFilter = held.DirectFilter, DirectGainHf = gainHf };
        }

        if (held.SendFilter != 0 && held.SendGain >= MinSend)
        {
            _efx.GetFilterProperty(held.SendFilter, FilterFloat.LowpassGain, out float sendGain);
            reading = reading with { SendSlotId = _slots[held.SendSlot], SendGain = sendGain };
        }

        return reading;
    }

    /// <summary>Gets the library name of a reverberation slot, for comparing with <see cref="SourceReading.SendSlotId"/>.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The identifier of the slot.</returns>
    internal uint SlotId(int slot) => _slots[slot];

    /// <summary>Reads, from the effect that a slot has loaded, the reverberation it plays.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The parameters, or <see langword="null"/> when the slot has no effect.</returns>
    internal ReverbParameters? InspectReverb(int slot)
    {
        if (!_slotActive[slot])
        {
            return null;
        }

        bool eax = _reverbType == EffectType.EaxReverb;
        uint id = _slotEffects[slot];
        float Read(EffectFloat eaxName, EffectFloat name)
        {
            _efx!.GetEffectProperty(id, eax ? eaxName : name, out float value);
            return value;
        }

        return new ReverbParameters(
            Read(EffectFloat.EaxReverbDecayTime, EffectFloat.ReverbDecayTime),
            Read(EffectFloat.EaxReverbReflectionsDelay, EffectFloat.ReverbReflectionsDelay),
            Read(EffectFloat.EaxReverbReflectionsGain, EffectFloat.ReverbReflectionsGain),
            Read(EffectFloat.EaxReverbDensity, EffectFloat.ReverbDensity),
            Read(EffectFloat.EaxReverbDiffusion, EffectFloat.ReverbDiffusion),
            Read(EffectFloat.EaxReverbGain, EffectFloat.ReverbGain));
    }

    /// <summary>Gets whether the library uses its EAX reverberation rather than the standard one.</summary>
    internal bool UsesEaxReverb => _reverbType == EffectType.EaxReverb;
}

/// <summary>The state of a voice, as the library holds it.</summary>
internal readonly record struct SourceReading
{
    public bool Exists { get; init; }

    public float Gain { get; init; }

    public float Pitch { get; init; }

    public bool Relative { get; init; }

    public bool Looping { get; init; }

    public Vector3 Position { get; init; }

    public Vector3 Velocity { get; init; }

    public bool Playing { get; init; }

    public bool Paused { get; init; }

    public int Queued { get; init; }

    public uint DirectFilter { get; init; }

    public float DirectGainHf { get; init; }

    public uint SendSlotId { get; init; }

    public float SendGain { get; init; }
}
