// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Audio;

/// <summary>
/// A handle to one play of a sound event: change its volume, pitch, or
/// position, fade it, or stop it. The handle is a value: copy it freely.
/// </summary>
/// <remarks>
/// <para>
/// The handle is revoked when the sound ends, is stopped, loses its anchor, or
/// is replaced by a stronger instance of its event: <see cref="IsActive"/> is
/// then <see langword="false"/>, and every call does nothing. A sound may end
/// on its own at any frame, so a call on a revoked handle is not an error.
/// </para>
/// <para>
/// Calls take effect in the phase <c>Render</c> of the frame, after the ticks.
/// Use the handle on the loop thread of the client context. The default handle
/// is never active.
/// </para>
/// </remarks>
public readonly struct SoundInstance : IEquatable<SoundInstance>
{
    private readonly AudioMixer? _mixer;

    [HotPath]
    internal SoundInstance(AudioMixer mixer, int slot, int generation)
    {
        _mixer = mixer;
        Slot = slot;
        Generation = generation;
    }

    /// <summary>Gets whether the sound still plays, real or virtual.</summary>
    public bool IsActive
    {
        [HotPath]
        get => _mixer is not null && _mixer.IsLive(Slot, Generation);
    }

    /// <summary>Gets whether the sound has a voice of the device now; <see langword="false"/> while it is virtual or revoked.</summary>
    public bool IsReal
    {
        [HotPath]
        get => _mixer is not null && _mixer.IsLive(Slot, Generation) && _mixer.IsReal(Slot);
    }

    /// <summary>Gets the sound event, or <see langword="null"/> once revoked.</summary>
    public SoundEvent? Sound => IsActive ? _mixer!.SoundOf(Slot) : null;

    internal int Slot { get; }

    internal int Generation { get; }

    /// <summary>Tells whether two handles are equal.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they name the same play.</returns>
    public static bool operator ==(SoundInstance left, SoundInstance right) => left.Equals(right);

    /// <summary>Tells whether two handles differ.</summary>
    /// <param name="left">A handle.</param>
    /// <param name="right">Another handle.</param>
    /// <returns><see langword="true"/> when they name different plays.</returns>
    public static bool operator !=(SoundInstance left, SoundInstance right) => !left.Equals(right);

    /// <summary>Sets the volume of this play at once: the factor of the volume of its event that the play started with.</summary>
    /// <param name="volume">The factor, from 0.</param>
    [HotPath]
    public void SetVolume(float volume)
    {
        if (IsActive)
        {
            _mixer!.SetVolume(Slot, volume, 0);
        }
    }

    /// <summary>Moves the volume of this play to a value over a time.</summary>
    /// <param name="volume">The factor to reach, from 0.</param>
    /// <param name="seconds">How long the fade lasts, in seconds; 0 for at once.</param>
    [HotPath]
    public void FadeTo(float volume, float seconds)
    {
        if (IsActive)
        {
            _mixer!.SetVolume(Slot, volume, seconds);
        }
    }

    /// <summary>Sets the pitch of this play, a factor of the pitch drawn for it.</summary>
    /// <param name="pitch">The factor, greater than 0.</param>
    [HotPath]
    public void SetPitch(float pitch)
    {
        if (IsActive)
        {
            _mixer!.SetPitch(Slot, pitch);
        }
    }

    /// <summary>Moves a sound that plays at a position; a sound attached to an anchor or without position ignores it.</summary>
    /// <param name="position">The new position, interpolated from the previous one as the listener is.</param>
    [HotPath]
    public void SetPosition(in WorldPosition position)
    {
        if (IsActive)
        {
            _mixer!.SetPosition(Slot, position);
        }
    }

    /// <summary>Stops the sound, at once or with a fade.</summary>
    /// <param name="fadeSeconds">How long the fade lasts, in seconds; 0 to stop at the next frame.</param>
    public void Stop(float fadeSeconds = 0)
    {
        if (IsActive)
        {
            _mixer!.Stop(Slot, fadeSeconds);
        }
    }

    /// <inheritdoc/>
    public bool Equals(SoundInstance other) => ReferenceEquals(_mixer, other._mixer) && Slot == other.Slot && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SoundInstance other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Slot, Generation);
}

/// <summary>How a persistent emitter plays its event.</summary>
/// <remarks>The default value loops.</remarks>
public readonly record struct EmitterRepeat
{
    private EmitterRepeat(float min, float max)
    {
        MinInterval = min;
        MaxInterval = max;
    }

    /// <summary>Gets an emitter that plays its event in a loop, whether the event loops or not.</summary>
    public static EmitterRepeat Loop => default;

    /// <summary>Gets whether the emitter loops.</summary>
    public bool IsLoop => MaxInterval <= 0;

    /// <summary>Gets the shortest time between two plays, in seconds; 0 for a loop.</summary>
    public float MinInterval { get; }

    /// <summary>Gets the longest time between two plays, in seconds; 0 for a loop.</summary>
    public float MaxInterval { get; }

    /// <summary>Gets an emitter that plays its event again after a random time, drawn between two bounds after each play.</summary>
    /// <param name="minSeconds">The shortest time, in seconds, from 0.</param>
    /// <param name="maxSeconds">The longest time, in seconds, greater than 0 and at least <paramref name="minSeconds"/>.</param>
    /// <returns>The repetition.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bounds are out of order or not positive.</exception>
    public static EmitterRepeat Every(float minSeconds, float maxSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSeconds, minSeconds);
        return new EmitterRepeat(minSeconds, maxSeconds);
    }
}

/// <summary>
/// A persistent emitter: a sound that a place or an object keeps playing, in a
/// loop or again and again, such as a machine, a waterfall, or a torch. Create
/// it once with <see cref="IAudioSystem.CreateEmitter(ModuleContext, SoundEvent, in WorldPosition, EmitterRepeat)"/>,
/// change it while it lives, and dispose it to remove it.
/// </summary>
/// <remarks>
/// <para>
/// Out of the range of its event, an emitter costs no voice of the device: a
/// looping emitter keeps a virtual voice that goes on in time, and a repeating
/// emitter skips the plays it would not be heard at. It is never created again
/// at each frame.
/// </para>
/// <para>
/// <see cref="IsActive"/> belongs to the logic of the game (a machine turned
/// off); <see cref="Suspend"/> and <see cref="Resume"/> to the life of what
/// holds the emitter (a chunk unloaded and loaded again). The emitter plays
/// when it is active and not suspended. Use it on the loop thread of the client
/// context.
/// </para>
/// </remarks>
public sealed class SoundEmitter : IDisposable
{
    private readonly Action<SoundEmitter> _remove;
    private float _volume = 1;
    private float _pitch = 1;

    internal SoundEmitter(ModuleContext module, SoundEvent sound, EmitterRepeat repeat, uint seed, Action<SoundEmitter> remove)
    {
        Module = module;
        Sound = sound;
        Repeat = repeat;
        Random = seed == 0 ? 1 : seed;
        _remove = remove;
    }

    /// <summary>Gets the sound event the emitter plays.</summary>
    public SoundEvent Sound { get; }

    /// <summary>Gets how the emitter plays its event.</summary>
    public EmitterRepeat Repeat { get; }

    /// <summary>Gets or sets whether the emitter plays: the state of what it belongs to.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Gets whether the emitter is suspended by <see cref="Suspend"/>.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>Gets whether the emitter was removed.</summary>
    public bool IsRevoked { get; private set; }

    /// <summary>Gets or sets the factor of the volume of its plays, from 0; applied from the next frame, to the sound that plays too.</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Max(0, value);
    }

    /// <summary>Gets or sets the factor of the pitch of its plays, greater than 0.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = value > 0 ? value : _pitch;
    }

    /// <summary>Gets the sound that plays now, or the default handle.</summary>
    public SoundInstance Playing { get; internal set; }

    internal ModuleContext Module { get; }

    internal ISoundAnchor? Anchor { get; init; }

    internal System.Numerics.Vector3 Offset { get; init; }

    internal WorldPosition Position { get; set; }

    /// <summary>Gets or sets the state of the random draws of the emitter.</summary>
    internal uint Random { get; set; }

    /// <summary>Gets or sets the time left before the next play of a repeating emitter, in seconds.</summary>
    internal float Countdown { get; set; } = -1;

    /// <summary>Moves an emitter placed at a position; an emitter attached to an anchor ignores it.</summary>
    /// <param name="position">The new position.</param>
    public void SetPosition(in WorldPosition position)
    {
        if (Anchor is null)
        {
            Position = position;
            if (Playing.IsActive)
            {
                Playing.SetPosition(position);
            }
        }
    }

    /// <summary>Suspends the emitter: its sound stops, and its timer waits, until <see cref="Resume"/>.</summary>
    public void Suspend() => IsSuspended = true;

    /// <summary>Resumes a suspended emitter.</summary>
    public void Resume() => IsSuspended = false;

    /// <summary>Removes the emitter; its sound stops at the next frame.</summary>
    public void Dispose()
    {
        if (!IsRevoked)
        {
            IsRevoked = true;
            _remove(this);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"emitter of {Sound}";
}
