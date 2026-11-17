// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Audio;

/// <summary>
/// The service that plays sounds: the device of the chosen backend, the
/// listener, the voices, the volumes of the buses, the emitters, and the
/// chain of emission processors. Resolve it with
/// <c>context.Resolve&lt;IAudioSystem&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Call it on the loop thread of the context, from initialization on. Plays
/// and changes made during the ticks take effect in the phase <c>Render</c> of
/// the frame, after them, with the positions interpolated for the frame:
/// nothing reaches the device elsewhere.
/// </para>
/// <para>
/// On a server, and on a client whose device could not be opened,
/// <see cref="IsActive"/> is <see langword="false"/>: plays return the default
/// <see cref="SoundInstance"/>, emitters stay silent, and nothing fails, so
/// that code shared by both sides needs no test.
/// </para>
/// </remarks>
public interface IAudioSystem
{
    /// <summary>Gets whether sounds are heard: the context is a client with an open device.</summary>
    bool IsActive { get; }

    /// <summary>Gets the name of the backend in use, such as <c>openal</c>, or <see langword="null"/> without device.</summary>
    string? BackendName { get; }

    /// <summary>Gets what the device supports, or <see langword="null"/> without device.</summary>
    AudioCapabilities? Capabilities { get; }

    /// <summary>Gets the most voices of the device used at once: <c>audio.realVoices</c>, within what the device has; 0 without device.</summary>
    int RealVoiceLimit { get; }

    /// <summary>Gets the counts of the last frame.</summary>
    AudioStatistics Statistics { get; }

    /// <summary>Gets the listener claimed by a module, or <see langword="null"/>.</summary>
    AudioListener? Listener { get; }

    /// <summary>Gets the persistent emitters, in the order of creation.</summary>
    IReadOnlyList<SoundEmitter> Emitters { get; }

    /// <summary>Gets the path of <c>config/audio.xml</c>, where <see cref="SaveBusVolumes"/> writes the volumes of the player.</summary>
    string VolumesFilePath { get; }

    /// <summary>Claims the listener, for the module that owns the camera.</summary>
    /// <param name="module">The module.</param>
    /// <returns>The listener; dispose it to give it back.</returns>
    /// <exception cref="InvalidOperationException">Another module holds the listener.</exception>
    AudioListener ClaimListener(ModuleContext module);

    /// <summary>Plays a sound without position, as for the interface: a spatial event plays as it is too.</summary>
    /// <param name="sound">The event.</param>
    /// <param name="volume">A factor of the volume of the event, from 0.</param>
    /// <param name="pitch">A factor of the pitch of the event, greater than 0.</param>
    /// <param name="seed">The seed of the draws of variant, volume, and pitch, or <see langword="null"/> to draw one.</param>
    /// <returns>The handle of the play; ignore it for a sound that plays to its end.</returns>
    SoundInstance Play(SoundEvent sound, float volume = 1, float pitch = 1, uint? seed = null);

    /// <summary>Plays a sound at a position of the world; an event that is not spatial ignores the position.</summary>
    /// <param name="sound">The event.</param>
    /// <param name="position">The position.</param>
    /// <param name="volume">A factor of the volume of the event, from 0.</param>
    /// <param name="pitch">A factor of the pitch of the event, greater than 0.</param>
    /// <param name="seed">The seed of the draws, or <see langword="null"/> to draw one.</param>
    /// <returns>The handle of the play.</returns>
    SoundInstance PlayAt(SoundEvent sound, in WorldPosition position, float volume = 1, float pitch = 1, uint? seed = null);

    /// <summary>Plays a sound that follows an anchor, such as an entity or a moving grid, and stops when the anchor is gone.</summary>
    /// <param name="sound">The event.</param>
    /// <param name="anchor">The anchor.</param>
    /// <param name="offset">The position in the frame of the anchor, in blocks.</param>
    /// <param name="volume">A factor of the volume of the event, from 0.</param>
    /// <param name="pitch">A factor of the pitch of the event, greater than 0.</param>
    /// <param name="seed">The seed of the draws, or <see langword="null"/> to draw one.</param>
    /// <returns>The handle of the play.</returns>
    SoundInstance PlayAttached(SoundEvent sound, ISoundAnchor anchor, Vector3 offset = default, float volume = 1, float pitch = 1, uint? seed = null);

    /// <summary>Creates a persistent emitter at a position.</summary>
    /// <param name="module">The module of the emitter.</param>
    /// <param name="sound">The event it plays.</param>
    /// <param name="position">The position.</param>
    /// <param name="repeat">A loop (the default), or plays at random intervals.</param>
    /// <returns>The emitter; dispose it to remove it.</returns>
    SoundEmitter CreateEmitter(ModuleContext module, SoundEvent sound, in WorldPosition position, EmitterRepeat repeat = default);

    /// <summary>Creates a persistent emitter that follows an anchor.</summary>
    /// <param name="module">The module of the emitter.</param>
    /// <param name="sound">The event it plays.</param>
    /// <param name="anchor">The anchor.</param>
    /// <param name="offset">The position in the frame of the anchor, in blocks.</param>
    /// <param name="repeat">A loop (the default), or plays at random intervals.</param>
    /// <returns>The emitter; dispose it to remove it.</returns>
    SoundEmitter CreateEmitter(ModuleContext module, SoundEvent sound, ISoundAnchor anchor, Vector3 offset, EmitterRepeat repeat = default);

    /// <summary>Gets the volume of a bus set by the player.</summary>
    /// <param name="bus">The bus.</param>
    /// <returns>The volume, from 0 to 1; 1 by default.</returns>
    float GetBusVolume(AudioBus bus);

    /// <summary>Sets the volume of an adjustable bus, from the next frame.</summary>
    /// <param name="bus">A bus marked adjustable.</param>
    /// <param name="volume">The volume, clamped from 0 to 1.</param>
    /// <exception cref="InvalidOperationException">The bus is not adjustable.</exception>
    void SetBusVolume(AudioBus bus, float volume);

    /// <summary>Gets the volume of a bus multiplied by the volumes of its parents.</summary>
    /// <param name="bus">The bus.</param>
    /// <returns>The effective volume.</returns>
    float GetEffectiveVolume(AudioBus bus);

    /// <summary>Gets whether the sounds of a bus are paused now: the game is paused, and the bus is not <c>ui</c>, <c>music</c>, or below them.</summary>
    /// <param name="bus">The bus.</param>
    /// <returns><see langword="true"/> when its sounds wait.</returns>
    bool IsPaused(AudioBus bus);

    /// <summary>Writes the volumes that differ from 1 to <c>config/audio.xml</c>, atomically.</summary>
    void SaveBusVolumes();

    /// <summary>Registers an emission processor at the end of the chain.</summary>
    /// <param name="module">The module of the processor, charged with its exceptions.</param>
    /// <param name="processor">The processor.</param>
    /// <returns>A handle that removes the processor.</returns>
    AudioRegistration AddProcessor(ModuleContext module, IEmissionProcessor processor);

    /// <summary>Creates a reverberation bus, with the default parameters.</summary>
    /// <param name="module">The module of the bus.</param>
    /// <param name="name">A name, for the debug window.</param>
    /// <returns>The bus; dispose it to remove it. It is unavailable, with a warning, when the device has no effects or no free slot.</returns>
    ReverbBus CreateReverbBus(ModuleContext module, string name);
}

/// <summary>The counts of voices and emitters at the last frame.</summary>
/// <param name="RealVoices">The sounds that had a voice of the device.</param>
/// <param name="VirtualVoices">The sounds that went on in time without one.</param>
/// <param name="Emitters">The persistent emitters.</param>
public readonly record struct AudioStatistics(int RealVoices, int VirtualVoices, int Emitters);
