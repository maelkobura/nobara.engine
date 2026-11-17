// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace Nobara.Audio.OpenAL;

/// <content>EFX: the low-pass filter of a voice, the send to a reverberation slot, and the slots.</content>
internal sealed unsafe partial class ALDevice
{
    // The reference frequency of the low-pass filters of EFX, where GainHF applies.
    private const float FilterReference = 5000f;

    // A cutoff within this many hertz of the open one counts as open.
    private const float OpenMargin = 1f;

    // Below this level a send is detached: it would not be heard.
    private const float MinSend = 0.0005f;

    private EffectExtension? _efx;
    private int _sends;
    private EffectType _reverbType = EffectType.Reverb;
    private uint[] _slots = [];
    private uint[] _slotEffects = [];
    private bool[] _slotActive = [];
    private int _liveEffectObjects;

    /// <inheritdoc/>
    public override void SetReverb(int slot, in ReverbParameters parameters)
    {
        if (_efx is null || (uint)slot >= (uint)_slots.Length)
        {
            return;
        }

        uint effect = _slotEffects[slot];
        if (effect == 0)
        {
            effect = _efx.GenEffect();
            _liveEffectObjects++;
            _efx.SetEffectProperty(effect, EffectInteger.EffectType, (int)_reverbType);
            _slotEffects[slot] = effect;
        }

        bool eax = _reverbType == EffectType.EaxReverb;
        _efx.SetEffectProperty(effect, eax ? EffectFloat.EaxReverbDecayTime : EffectFloat.ReverbDecayTime, parameters.DecayTime);
        _efx.SetEffectProperty(effect, eax ? EffectFloat.EaxReverbReflectionsDelay : EffectFloat.ReverbReflectionsDelay, parameters.ReflectionsDelay);
        _efx.SetEffectProperty(effect, eax ? EffectFloat.EaxReverbReflectionsGain : EffectFloat.ReverbReflectionsGain, parameters.ReflectionsGain);
        _efx.SetEffectProperty(effect, eax ? EffectFloat.EaxReverbDensity : EffectFloat.ReverbDensity, parameters.Density);
        _efx.SetEffectProperty(effect, eax ? EffectFloat.EaxReverbDiffusion : EffectFloat.ReverbDiffusion, parameters.Diffusion);
        _efx.SetEffectProperty(effect, eax ? EffectFloat.EaxReverbGain : EffectFloat.ReverbGain, parameters.Gain);

        // The slot takes the parameters of its effect when the effect is set again.
        _efx.SetAuxiliaryEffectSlotProperty(_slots[slot], EffectSlotInteger.Effect, (int)effect);
        _slotActive[slot] = true;
    }

    /// <inheritdoc/>
    public override void ClearReverb(int slot)
    {
        if (_efx is null || (uint)slot >= (uint)_slots.Length)
        {
            return;
        }

        _efx.SetAuxiliaryEffectSlotProperty(_slots[slot], EffectSlotInteger.Effect, (int)EffectType.Null);
        _slotActive[slot] = false;
    }

    /// <summary>Gets the gain at the reference frequency of EFX that gives a one-pole low-pass filter its cutoff, relative to the open one.</summary>
    /// <param name="cutoff">The cutoff, in hertz.</param>
    /// <returns>The gain, from 0 to 1; 1 for an open filter.</returns>
    [HotPath]
    internal static float HighFrequencyGain(float cutoff)
    {
        if (cutoff >= VoiceParameters.OpenCutoff - OpenMargin)
        {
            return 1f;
        }

        float ratio = FilterReference / Math.Max(cutoff, 20f);
        float open = FilterReference / VoiceParameters.OpenCutoff;
        return Math.Clamp(MathF.Sqrt((1f + (open * open)) / (1f + (ratio * ratio))), 0f, 1f);
    }

    private bool SupportsLowPass()
    {
        _al.GetError();
        uint filter = _efx!.GenFilter();
        if (filter == 0)
        {
            return false;
        }

        _liveEffectObjects++;
        _efx.SetFilterProperty(filter, FilterInteger.FilterType, (int)FilterType.Lowpass);
        bool supported = _al.GetError() == AudioError.NoError;
        _efx.DeleteFilter(filter);
        _liveEffectObjects--;
        return supported;
    }

    /// <summary>Chooses the reverberation effect and creates the slots; returns how many exist.</summary>
    private int CreateSlots()
    {
        _al.GetError();
        uint probe = _efx!.GenEffect();
        if (probe == 0)
        {
            return 0;
        }

        _liveEffectObjects++;
        _reverbType = EffectType.EaxReverb;
        _efx.SetEffectProperty(probe, EffectInteger.EffectType, (int)_reverbType);
        if (_al.GetError() != AudioError.NoError)
        {
            _reverbType = EffectType.Reverb;
            _efx.SetEffectProperty(probe, EffectInteger.EffectType, (int)_reverbType);
            if (_al.GetError() != AudioError.NoError)
            {
                _efx.DeleteEffect(probe);
                _liveEffectObjects--;
                return 0;
            }
        }

        _efx.DeleteEffect(probe);
        _liveEffectObjects--;

        var slots = new List<uint>(MaxSlots);
        for (int i = 0; i < MaxSlots; i++)
        {
            uint slot = _efx.GenAuxiliaryEffectSlot();
            if (slot == 0 || _al.GetError() != AudioError.NoError)
            {
                break;
            }

            _liveEffectObjects++;
            slots.Add(slot);
        }

        _slots = [.. slots];
        _slotEffects = new uint[_slots.Length];
        _slotActive = new bool[_slots.Length];
        return _slots.Length;
    }

    [HotPath]
    private void SetLowPass(uint source, ref VoiceState state, float cutoff)
    {
        float gain = HighFrequencyGain(cutoff);
        if (!state.DirectAttached && gain >= 1f)
        {
            return;
        }

        if (state.DirectAttached && MathF.Abs(gain - state.DirectGainHf) < 0.001f)
        {
            return;
        }

        if (gain >= 1f)
        {
            _efx!.SetSourceProperty(source, EFXSourceInteger.DirectFilter, 0);
            state.DirectGainHf = 1f;
            state.DirectAttached = false;
            return;
        }

        if (state.DirectFilter == 0)
        {
            state.DirectFilter = NewLowPass();
        }

        _efx!.SetFilterProperty(state.DirectFilter, FilterFloat.LowpassGain, 1f);
        _efx.SetFilterProperty(state.DirectFilter, FilterFloat.LowpassGainHF, gain);

        // A source takes the settings of a filter when the filter is attached again.
        _efx.SetSourceProperty(source, EFXSourceInteger.DirectFilter, (int)state.DirectFilter);
        state.DirectGainHf = gain;
        state.DirectAttached = true;
    }

    [HotPath]
    private void SetSend(uint source, ref VoiceState state, int slot, float level)
    {
        bool wanted = (uint)slot < (uint)_slots.Length && level >= MinSend;
        bool attached = state.SendFilter != 0 && state.SendGain >= MinSend;
        if (!wanted)
        {
            if (attached)
            {
                _efx!.SetSourceProperty(source, EFXSourceInteger3.AuxiliarySendFilter, 0, 0, 0);
                state.SendGain = 0f;
            }

            return;
        }

        level = Math.Min(level, 1f);
        if (attached && state.SendSlot == slot && MathF.Abs(level - state.SendGain) < 0.001f)
        {
            return;
        }

        if (state.SendFilter == 0)
        {
            state.SendFilter = NewLowPass();
        }

        // The level of the send is the gain of the filter on it; the send is not filtered by frequency.
        _efx!.SetFilterProperty(state.SendFilter, FilterFloat.LowpassGain, level);
        _efx.SetFilterProperty(state.SendFilter, FilterFloat.LowpassGainHF, 1f);
        _efx.SetSourceProperty(source, EFXSourceInteger3.AuxiliarySendFilter, (int)_slots[slot], 0, (int)state.SendFilter);
        state.SendSlot = slot;
        state.SendGain = level;
    }

    [HotPath]
    private uint NewLowPass()
    {
        uint filter = _efx!.GenFilter();
        _liveEffectObjects++;
        _efx.SetFilterProperty(filter, FilterInteger.FilterType, (int)FilterType.Lowpass);
        return filter;
    }

    /// <summary>Deletes the filters, effects, and slots; the sources are gone already.</summary>
    private void ReleaseEffects()
    {
        if (_efx is null)
        {
            return;
        }

        for (int i = 0; i < _voices.Length; i++)
        {
            ref VoiceState state = ref _voices[i];
            if (state.DirectFilter != 0)
            {
                _efx.DeleteFilter(state.DirectFilter);
                _liveEffectObjects--;
                state.DirectFilter = 0;
            }

            if (state.SendFilter != 0)
            {
                _efx.DeleteFilter(state.SendFilter);
                _liveEffectObjects--;
                state.SendFilter = 0;
            }
        }

        for (int i = 0; i < _slots.Length; i++)
        {
            _efx.SetAuxiliaryEffectSlotProperty(_slots[i], EffectSlotInteger.Effect, (int)EffectType.Null);
            _efx.DeleteAuxiliaryEffectSlot(_slots[i]);
            _liveEffectObjects--;
            if (_slotEffects[i] != 0)
            {
                _efx.DeleteEffect(_slotEffects[i]);
                _liveEffectObjects--;
            }
        }

        _slots = [];
        _slotEffects = [];
        _slotActive = [];
        _efx.Dispose();
        _efx = null;
    }
}
