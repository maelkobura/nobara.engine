// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Logging;

namespace Nobara.Audio;

/// <summary>
/// Where audio backends register. A backend module resolves it with
/// <c>context.Resolve&lt;IAudioBackends&gt;()</c> during its initialization and
/// registers its backend there.
/// </summary>
public interface IAudioBackends
{
    /// <summary>Registers a backend. nAudio chooses one when it starts, from <c>audio.backend</c>.</summary>
    /// <param name="module">The module of the backend.</param>
    /// <param name="backend">The backend.</param>
    /// <exception cref="InvalidOperationException">nAudio has started already, or a backend of the same name is registered.</exception>
    void Register(ModuleContext module, IAudioBackend backend);
}

/// <summary>An audio backend: the code of one audio library, behind the neutral API of nAudio.</summary>
public interface IAudioBackend
{
    /// <summary>Gets the name of the backend, which <c>audio.backend</c> uses, such as <c>openal</c>.</summary>
    string Name { get; }

    /// <summary>Opens the default output device.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The device; nAudio disposes it when it is released.</returns>
    /// <exception cref="NotSupportedException">No device can be opened; the message says why.</exception>
    AudioDevice Open(in AudioDeviceOptions options);
}

/// <summary>Whether the backend filters sounds by direction for headphones (HRTF).</summary>
public enum HrtfMode
{
    /// <summary>The backend decides, usually on when it detects headphones.</summary>
    Auto,

    /// <summary>Always on, when the backend can.</summary>
    On,

    /// <summary>Always off.</summary>
    Off,
}

/// <summary>What nAudio asks a backend for when it opens the device.</summary>
/// <param name="Hrtf">The mode of HRTF, from <c>audio.hrtf</c>.</param>
/// <param name="Logger">The journal of nAudio, for the library and its fallbacks.</param>
public readonly record struct AudioDeviceOptions(HrtfMode Hrtf, Logger Logger);

/// <summary>Optional features of an audio device. An emission processor declares those it needs, and is disabled when one is missing.</summary>
[Flags]
public enum AudioFeatures
{
    /// <summary>No feature.</summary>
    None = 0,

    /// <summary>Filtering by direction for headphones.</summary>
    Hrtf = 1,

    /// <summary>A low-pass filter per voice (<see cref="VoiceParameters.LowPassCutoff"/>).</summary>
    Filters = 2,

    /// <summary>Sends from a voice to an effect (<see cref="VoiceParameters.ReverbSlot"/>).</summary>
    AuxiliarySends = 4,

    /// <summary>Effects that voices send to, such as reverberation (<see cref="AudioDevice.SetReverb"/>).</summary>
    Effects = 8,
}

/// <summary>What an audio device supports: its optional features and its limits.</summary>
public sealed record AudioCapabilities
{
    /// <summary>Gets the optional features.</summary>
    public required AudioFeatures Features { get; init; }

    /// <summary>Gets the number of voices that the device mixes at once, at least 1.</summary>
    public required int MaxVoices { get; init; }

    /// <summary>Gets the number of reverberation slots, 0 without <see cref="AudioFeatures.Effects"/>.</summary>
    public required int MaxReverbSlots { get; init; }

    /// <summary>Gets whether the device has every feature of <paramref name="features"/>.</summary>
    /// <param name="features">The features.</param>
    /// <returns><see langword="true"/> when none is missing.</returns>
    [HotPath]
    public bool Supports(AudioFeatures features) => (Features & features) == features;
}

/// <summary>A buffer of samples on the device, created by <see cref="AudioDevice.CreateBuffer"/>.</summary>
/// <param name="Id">The identifier the backend gives it.</param>
public readonly record struct AudioBuffer(int Id);

/// <summary>The settings of one voice of the device, set at every frame where they change.</summary>
/// <remarks>
/// nAudio computes the gain itself, attenuation by distance, buses, fades, and
/// emission processors included: a backend applies <see cref="Gain"/> as it is,
/// with no distance model of its own. Positions are relative to the listener.
/// </remarks>
public struct VoiceParameters
{
    /// <summary>The cutoff of an open low-pass filter, in hertz: nothing is filtered.</summary>
    public const float OpenCutoff = 20000f;

    /// <summary>Gets or sets the gain, from 0, linear.</summary>
    public float Gain;

    /// <summary>Gets or sets the factor of the playback rate: 2 plays an octave higher.</summary>
    public float Pitch;

    /// <summary>Gets or sets whether the voice is placed in space; otherwise it plays as it is, as for the interface.</summary>
    public bool Spatial;

    /// <summary>Gets or sets the position from the listener, in blocks, along the axes of the world; never farther than the range of the sound.</summary>
    public Vector3 Position;

    /// <summary>Gets or sets the velocity, in blocks per second, along the axes of the world, for the Doppler effect.</summary>
    public Vector3 Velocity;

    /// <summary>Gets or sets the cutoff of the low-pass filter, in hertz; <see cref="OpenCutoff"/> when open. Ignored without <see cref="AudioFeatures.Filters"/>.</summary>
    public float LowPassCutoff;

    /// <summary>Gets or sets the reverberation slot the voice sends to, or -1. Ignored without <see cref="AudioFeatures.AuxiliarySends"/>.</summary>
    public int ReverbSlot;

    /// <summary>Gets or sets the level of the send to <see cref="ReverbSlot"/>, from 0 to 1.</summary>
    public float ReverbSend;
}

/// <summary>The listener as the device receives it: always at the origin, since positions are relative to it.</summary>
/// <param name="Forward">The direction it faces, of length 1, along the axes of the world.</param>
/// <param name="Up">Its up direction, of length 1.</param>
/// <param name="Velocity">Its velocity, in blocks per second, for the Doppler effect.</param>
/// <param name="Gain">The gain of the whole output.</param>
public readonly record struct ListenerParameters(Vector3 Forward, Vector3 Up, Vector3 Velocity, float Gain);

/// <summary>
/// An output device of a backend: real voices, buffers, the listener, and the
/// reverberation slots. nAudio calls it on the loop thread of the client
/// context, during the phase <c>Render</c>, and nothing else does.
/// </summary>
/// <remarks>
/// Voices are numbered from 0 to <see cref="AudioCapabilities.MaxVoices"/>
/// excluded; nAudio uses at most as many as <c>audio.realVoices</c> allows. A
/// voice plays one buffer, looping or not, or a queue of blocks for a stream.
/// </remarks>
public abstract class AudioDevice : IDisposable
{
    /// <summary>Gets what the device supports.</summary>
    public abstract AudioCapabilities Capabilities { get; }

    /// <summary>Gets the name of the device, for logs and bug reports.</summary>
    public abstract string Description { get; }

    /// <summary>Gets whether the device filters by direction now.</summary>
    public abstract bool HrtfEnabled { get; }

    /// <summary>
    /// Gets whether the device cannot play any more, because it was unplugged,
    /// or is not the default output any more. nAudio asks once per frame; when
    /// it is <see langword="true"/>, nAudio disposes the device, makes every
    /// voice virtual, and opens the default output of the backend again.
    /// </summary>
    public virtual bool IsLost => false;

    /// <summary>Copies samples to a buffer of the device.</summary>
    /// <param name="samples">Interleaved 16-bit samples.</param>
    /// <param name="channels">1 or 2.</param>
    /// <param name="sampleRate">The frames per second.</param>
    /// <returns>The buffer.</returns>
    public abstract AudioBuffer CreateBuffer(ReadOnlySpan<short> samples, int channels, int sampleRate);

    /// <summary>Destroys a buffer that no voice plays.</summary>
    /// <param name="buffer">The buffer.</param>
    public abstract void DestroyBuffer(AudioBuffer buffer);

    /// <summary>Plays a buffer on a voice, from a frame, after the parameters of the voice are set.</summary>
    /// <param name="voice">The voice, stopped.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="loop">Whether it loops.</param>
    /// <param name="startFrame">The frame to start from, within the buffer.</param>
    public abstract void Play(int voice, AudioBuffer buffer, bool loop, int startFrame);

    /// <summary>Queues a block of a stream on a voice, which plays it after the blocks already queued, and starts playing if it was not.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="samples">Interleaved 16-bit samples; copied before the call returns.</param>
    /// <param name="channels">1 or 2.</param>
    /// <param name="sampleRate">The frames per second.</param>
    [HotPath]
    public abstract void Queue(int voice, ReadOnlySpan<short> samples, int channels, int sampleRate);

    /// <summary>Gets the number of blocks queued on a voice that it has not finished playing.</summary>
    /// <param name="voice">The voice.</param>
    /// <returns>The number of blocks.</returns>
    [HotPath]
    public abstract int GetQueuedBlocks(int voice);

    /// <summary>Stops a voice and forgets its buffer or its queue.</summary>
    /// <param name="voice">The voice.</param>
    [HotPath]
    public abstract void Stop(int voice);

    /// <summary>Pauses a voice where it is.</summary>
    /// <param name="voice">The voice.</param>
    [HotPath]
    public abstract void Pause(int voice);

    /// <summary>Resumes a paused voice.</summary>
    /// <param name="voice">The voice.</param>
    [HotPath]
    public abstract void Resume(int voice);

    /// <summary>Sets the parameters of a voice.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="parameters">The parameters.</param>
    [HotPath]
    public abstract void SetVoice(int voice, in VoiceParameters parameters);

    /// <summary>Sets the listener.</summary>
    /// <param name="listener">The listener.</param>
    [HotPath]
    public abstract void SetListener(in ListenerParameters listener);

    /// <summary>Turns a reverberation slot on, or changes its parameters.</summary>
    /// <param name="slot">The slot, from 0 to <see cref="AudioCapabilities.MaxReverbSlots"/> excluded.</param>
    /// <param name="parameters">The parameters, within their bounds.</param>
    [HotPath]
    public abstract void SetReverb(int slot, in ReverbParameters parameters);

    /// <summary>Turns a reverberation slot off.</summary>
    /// <param name="slot">The slot.</param>
    public abstract void ClearReverb(int slot);

    /// <summary>Changes the mode of HRTF; the device applies it as soon as it can.</summary>
    /// <param name="mode">The mode.</param>
    public abstract void SetHrtf(HrtfMode mode);

    /// <summary>Ends the changes of a frame, for a backend that applies them together.</summary>
    [HotPath]
    public virtual void Commit()
    {
    }

    /// <summary>Closes the device.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes the device.</summary>
    /// <param name="disposing"><see langword="true"/> when called by <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }
}
