// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Audio;

/// <summary>
/// The ears of the player: one per client, claimed by the module that owns the
/// camera with <see cref="IAudioSystem.ClaimListener"/>, which moves it at
/// every tick. Dispose it to give it back.
/// </summary>
/// <remarks>
/// <para>
/// The listener follows the rules of the camera of the render world: the
/// first <see cref="Move"/> of a tick makes the pose heard until then the
/// previous pose, and nAudio hears the listener between its two poses, by the
/// fraction of tick of the frame, so that sound stays on the image.
/// <see cref="Teleport"/> is not interpolated. The listener faces -Z of its
/// orientation, with +Y up.
/// </para>
/// <para>
/// Every position is heard from the listener: nAudio subtracts its position
/// from the position of each sound in double precision before anything reaches
/// the device, so sounds stay exact however far they are from the origin of
/// the world. Without a listener, no spatial sound is heard.
/// </para>
/// </remarks>
public sealed class AudioListener : IDisposable
{
    private readonly Action<AudioListener> _release;
    private readonly Func<ulong> _tick;
    private WorldPosition _previous;
    private WorldPosition _current;
    private Quaternion _previousRotation = Quaternion.Identity;
    private Quaternion _currentRotation = Quaternion.Identity;
    private ulong _movedTick;
    private bool _placed;

    internal AudioListener(ModuleContext module, Func<ulong> tick, Action<AudioListener> release)
    {
        Module = module;
        _tick = tick;
        _release = release;
    }

    /// <summary>Gets whether the listener was placed once, by <see cref="Move"/> or <see cref="Teleport"/>.</summary>
    public bool IsPlaced => _placed;

    /// <summary>Gets whether the listener was given back.</summary>
    public bool IsReleased { get; private set; }

    /// <summary>Gets the position given last.</summary>
    public WorldPosition Position => _current;

    /// <summary>Gets the orientation given last.</summary>
    public Quaternion Orientation => _currentRotation;

    internal ModuleContext Module { get; }

    /// <summary>Moves the listener, interpolated from where it was.</summary>
    /// <param name="position">Its position, such as the eyes of the player.</param>
    /// <param name="orientation">Its orientation: it faces -Z, with +Y up.</param>
    [HotPath]
    public void Move(in WorldPosition position, Quaternion orientation)
    {
        if (!_placed)
        {
            Teleport(position, orientation);
            return;
        }

        ulong tick = _tick();
        if (tick != _movedTick)
        {
            _previous = _current;
            _previousRotation = _currentRotation;
            _movedTick = tick;
        }

        _current = position;
        _currentRotation = orientation;
    }

    /// <summary>Places the listener, with no interpolation and no velocity.</summary>
    /// <param name="position">Its position.</param>
    /// <param name="orientation">Its orientation: it faces -Z, with +Y up.</param>
    [HotPath]
    public void Teleport(in WorldPosition position, Quaternion orientation)
    {
        _previous = _current = position;
        _previousRotation = _currentRotation = orientation;
        _movedTick = _tick();
        _placed = true;
    }

    /// <summary>Gives the listener back: spatial sounds are not heard until another module claims it.</summary>
    public void Dispose()
    {
        if (!IsReleased)
        {
            IsReleased = true;
            _release(this);
        }
    }

    /// <summary>Gets the pose heard at a frame.</summary>
    /// <param name="tick">The last tick that ran.</param>
    /// <param name="alpha">The fraction of tick of the frame.</param>
    /// <param name="interpolates">Whether the loop has a fixed phase.</param>
    /// <param name="tickSeconds">The duration of a tick, in seconds.</param>
    /// <param name="position">The position.</param>
    /// <param name="rotation">The orientation.</param>
    /// <param name="velocity">The velocity, in blocks per second.</param>
    [HotPath]
    internal void Sample(ulong tick, float alpha, bool interpolates, float tickSeconds, out WorldPosition position, out Quaternion rotation, out Vector3 velocity)
    {
        if (interpolates && _movedTick == tick && tick != 0)
        {
            position = WorldPosition.Lerp(_previous, _current, alpha);
            rotation = Quaternion.Slerp(_previousRotation, _currentRotation, alpha);
            velocity = tickSeconds > 0 ? _current.RelativeTo(_previous) / tickSeconds : Vector3.Zero;
            return;
        }

        position = _current;
        rotation = _currentRotation;
        velocity = Vector3.Zero;
    }
}

/// <summary>
/// What a sound or an emitter follows: an entity, a moving grid, or anything
/// that has a place in the world for a time. A module implements it over its
/// own objects and passes it to <see cref="IAudioSystem.PlayAttached"/> or
/// <see cref="IAudioSystem.CreateEmitter(ModuleContext, SoundEvent, ISoundAnchor, Vector3, EmitterRepeat)"/>.
/// </summary>
/// <remarks>
/// <para>
/// nAudio reads the anchor once per frame that follows a tick, and hears the
/// sound between the two last transforms it read, by the fraction of tick of
/// the frame, as the render world does. The offset given with the sound is in
/// the frame of the anchor: a sound on a moving grid is placed in the
/// coordinates of the grid and turns with it.
/// </para>
/// <para>
/// When the anchor has no transform any more, because its entity was
/// destroyed or its grid unloaded, a sound attached to it stops, and an
/// emitter is silent until the anchor has a transform again. Keep one anchor
/// per object rather than creating one per sound.
/// </para>
/// </remarks>
public interface ISoundAnchor
{
    /// <summary>Gets the transform of the anchor in the world now.</summary>
    /// <param name="transform">The transform.</param>
    /// <returns><see langword="false"/> when the object is gone.</returns>
    [LoopCallback]
    bool TryGetTransform(out WorldTransform transform);
}
