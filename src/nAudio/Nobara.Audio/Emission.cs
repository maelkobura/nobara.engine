// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Audio;

/// <summary>
/// The neutral parameters of a reverberation bus, in the units of the
/// physical world. A backend translates them to its own effect, such as EFX
/// for OpenAL Soft. <see cref="Default"/> is a generic room.
/// </summary>
/// <param name="DecayTime">How long the late reverberation takes to fall by 60 dB, in seconds, from 0.1 to 20.</param>
/// <param name="ReflectionsDelay">The delay of the first reflections after the direct sound, in seconds, from 0 to 0.3.</param>
/// <param name="ReflectionsGain">The gain of the first reflections, from 0 to 3.16.</param>
/// <param name="Density">The density of the echoes, from 0 (sparse, as in a small space) to 1.</param>
/// <param name="Diffusion">The diffusion of the echoes, from 0 (distinct echoes) to 1 (a smooth tail).</param>
/// <param name="Gain">The gain of the whole reverberation, from 0 to 1.</param>
[HotPath]
public readonly record struct ReverbParameters(float DecayTime, float ReflectionsDelay, float ReflectionsGain, float Density, float Diffusion, float Gain)
{
    /// <summary>The shortest decay, in seconds.</summary>
    public const float MinDecayTime = 0.1f;

    /// <summary>The longest decay, in seconds.</summary>
    public const float MaxDecayTime = 20f;

    /// <summary>The longest delay of the first reflections, in seconds.</summary>
    public const float MaxReflectionsDelay = 0.3f;

    /// <summary>The highest gain of the first reflections.</summary>
    public const float MaxReflectionsGain = 3.16f;

    /// <summary>Gets a generic room: a decay of 1.49 s, reflections after 7 ms at a gain of 0.05, full density and diffusion, a gain of 0.32.</summary>
    public static ReverbParameters Default => new(1.49f, 0.007f, 0.05f, 1f, 1f, 0.32f);

    /// <summary>Gets the parameters brought within their bounds; a value that is not a number takes the default.</summary>
    /// <returns>The parameters.</returns>
    [HotPath]
    public ReverbParameters Clamped()
    {
        ReverbParameters d = Default;
        return new ReverbParameters(
            Clamp(DecayTime, MinDecayTime, MaxDecayTime, d.DecayTime),
            Clamp(ReflectionsDelay, 0, MaxReflectionsDelay, d.ReflectionsDelay),
            Clamp(ReflectionsGain, 0, MaxReflectionsGain, d.ReflectionsGain),
            Clamp(Density, 0, 1, d.Density),
            Clamp(Diffusion, 0, 1, d.Diffusion),
            Clamp(Gain, 0, 1, d.Gain));
    }

    /// <summary>Gets the parameters a fraction of the way to others, each one linearly.</summary>
    /// <param name="from">The parameters at 0.</param>
    /// <param name="to">The parameters at 1.</param>
    /// <param name="amount">The fraction, from 0 to 1.</param>
    /// <returns>The parameters.</returns>
    [HotPath]
    public static ReverbParameters Lerp(in ReverbParameters from, in ReverbParameters to, float amount) => new(
        from.DecayTime + ((to.DecayTime - from.DecayTime) * amount),
        from.ReflectionsDelay + ((to.ReflectionsDelay - from.ReflectionsDelay) * amount),
        from.ReflectionsGain + ((to.ReflectionsGain - from.ReflectionsGain) * amount),
        from.Density + ((to.Density - from.Density) * amount),
        from.Diffusion + ((to.Diffusion - from.Diffusion) * amount),
        from.Gain + ((to.Gain - from.Gain) * amount));

    [HotPath]
    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsNaN(value) ? fallback : Math.Clamp(value, min, max);
}

/// <summary>
/// A reverberation bus: an effect that voices send part of their signal to,
/// with neutral parameters that a module changes while the game runs. Create
/// it with <see cref="IAudioSystem.CreateReverbBus"/>; dispose it to remove it.
/// </summary>
/// <remarks>
/// <para>
/// nAudio provides the bus and no preset: a module that knows the space around
/// the listener, such as an acoustic module, sets <see cref="Parameters"/>, and
/// its emission processor sets the send of each voice
/// (<see cref="EmissionParameters.Reverb"/>). A change of the parameters is
/// smoothed over a few tens of milliseconds.
/// </para>
/// <para>
/// The send is taken after the gain of the voice, buses included, so the
/// volume of a bus applies to the reverberation of its sounds. A bus is
/// unavailable (<see cref="IsAvailable"/>) when the device has no effects or
/// no free slot; voices that send to it then play dry.
/// </para>
/// </remarks>
[HotPath]
public sealed class ReverbBus : IDisposable
{
    private readonly Action<ReverbBus> _remove;
    private ReverbParameters _parameters = ReverbParameters.Default;

    internal ReverbBus(ModuleContext module, string name, Action<ReverbBus> remove)
    {
        Module = module;
        Name = name;
        _remove = remove;
        Current = _parameters;
    }

    /// <summary>Gets the name given at creation, for the debug window.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the parameters that the bus reaches, smoothed; set values are brought within their bounds.</summary>
    public ReverbParameters Parameters
    {
        get => _parameters;
        set
        {
            _parameters = value.Clamped();
            Changed = true;
        }
    }

    /// <summary>Gets whether the device runs the bus: it has effects and a free slot.</summary>
    public bool IsAvailable => Slot >= 0 && !IsRevoked;

    /// <summary>Gets whether the bus was removed.</summary>
    public bool IsRevoked { get; private set; }

    internal ModuleContext Module { get; }

    /// <summary>Gets or sets the slot of the device, or -1.</summary>
    internal int Slot { get; set; } = -1;

    /// <summary>Gets or sets the smoothed parameters last given to the device.</summary>
    internal ReverbParameters Current { get; set; }

    /// <summary>Gets or sets whether the parameters moved away from <see cref="Current"/>.</summary>
    internal bool Changed { get; set; } = true;

    /// <summary>Removes the bus: voices that sent to it play dry from the next frame.</summary>
    public void Dispose()
    {
        if (!IsRevoked)
        {
            IsRevoked = true;
            _remove(this);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>What an emission processor receives for one voice.</summary>
/// <remarks>The object is reused for every voice and every frame: do not keep it.</remarks>
public sealed class EmissionContext
{
    /// <summary>Gets the sound event of the voice.</summary>
    public SoundEvent Sound { get; internal set; } = null!;

    /// <summary>Gets the instance, to recognize the voice from frame to frame.</summary>
    public SoundInstance Instance { get; internal set; }

    /// <summary>Gets whether the voice plays at a position; otherwise the positions are those of the listener.</summary>
    public bool IsSpatial { get; internal set; }

    /// <summary>Gets the position of the voice, interpolated for the frame.</summary>
    public WorldPosition Position { get; internal set; }

    /// <summary>Gets the position of the listener, interpolated for the frame.</summary>
    public WorldPosition ListenerPosition { get; internal set; }

    /// <summary>Gets the position of the voice from the listener, in blocks.</summary>
    public Vector3 RelativePosition { get; internal set; }

    /// <summary>Gets the distance from the listener, in blocks.</summary>
    public float Distance { get; internal set; }

    /// <summary>Gets the real time since the previous frame, in seconds.</summary>
    public float DeltaTime { get; internal set; }
}

/// <summary>
/// What emission processors produce for one voice. Each processor of the chain
/// receives what the previous ones produced and changes it; nAudio smooths the
/// result before it reaches the device.
/// </summary>
public struct EmissionParameters
{
    /// <summary>Gets or sets the factor of the gain, from 0; 1 leaves the voice as it is.</summary>
    public float Gain;

    /// <summary>Gets or sets the cutoff of the low-pass filter, in hertz; <see cref="VoiceParameters.OpenCutoff"/> leaves the voice as it is.</summary>
    public float LowPassCutoff;

    /// <summary>Gets or sets the reverberation bus the voice sends to, or <see langword="null"/>.</summary>
    public ReverbBus? Reverb;

    /// <summary>Gets or sets the level of the send to <see cref="Reverb"/>, from 0 to 1.</summary>
    public float ReverbSend;

    /// <summary>Gets the parameters that leave a voice as it is.</summary>
    public static EmissionParameters Neutral => new() { Gain = 1, LowPassCutoff = VoiceParameters.OpenCutoff };
}

/// <summary>
/// A link of the chain of emission processors: code that a module registers to
/// change every voice from the world around it, such as occlusion by blocks or
/// the reverberation of a room. Register it with
/// <see cref="IAudioSystem.AddProcessor"/>.
/// </summary>
/// <remarks>
/// nAudio calls the chain at every frame, for every voice that plays, real or
/// virtual, on the loop thread, in the order of registration. A processor may
/// compute at a lower rate and give the same result between two computations:
/// nAudio smooths the parameters, so a step does not click. A processor that
/// needs a feature the device lacks is disabled, with a warning.
/// </remarks>
public interface IEmissionProcessor
{
    /// <summary>Gets the name of the processor, for warnings and the debug window.</summary>
    string Name { get; }

    /// <summary>Gets the features of the device the processor needs, such as <see cref="AudioFeatures.Filters"/>.</summary>
    AudioFeatures RequiredFeatures { get; }

    /// <summary>Changes the parameters of one voice.</summary>
    /// <param name="context">The voice and the listener.</param>
    /// <param name="parameters">What the previous processors produced; change it.</param>
    [LoopCallback]
    void Process(EmissionContext context, ref EmissionParameters parameters);
}

/// <summary>A registration with nAudio, such as an emission processor. Disposing it removes the registration.</summary>
public sealed class AudioRegistration : IDisposable
{
    private readonly Action<AudioRegistration> _remove;

    internal AudioRegistration(ModuleContext module, Action<AudioRegistration> remove)
    {
        Module = module;
        _remove = remove;
    }

    /// <summary>Gets whether the registration is still in effect.</summary>
    public bool IsActive => !Revoked;

    internal ModuleContext Module { get; }

    internal bool Revoked { get; private set; }

    /// <summary>Removes the registration.</summary>
    public void Dispose()
    {
        if (!Revoked)
        {
            Revoked = true;
            _remove(this);
        }
    }
}
