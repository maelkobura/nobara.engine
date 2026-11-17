// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;

namespace Nobara.Audio.Tests.Samples;

/// <summary>
/// The sounds of a windmill in a fictional farming game, as the guide
/// "Play a sound" shows them: the listener, a sound of the interface, a sound
/// at a position, a sound on a moving cart, a controlled sound, and the
/// emitter of the mill.
/// </summary>
internal sealed class MillSounds
{
    private readonly IAudioSystem _audio;
    private readonly SoundEvent _click;
    private readonly SoundEvent _creak;
    private readonly SoundEvent _hum;
    private AudioListener? _listener;
    private SoundInstance _grinding;
    private SoundEmitter? _sails;

    // The services of nAudio, resolved in the initialization of the module.
    public MillSounds(ModuleContext context)
    {
        ISoundRegistry sounds = context.Resolve<ISoundRegistry>();
        _audio = context.Resolve<IAudioSystem>();
        _click = sounds.Get("sound:example.sounds.click");
        _creak = sounds.Get("sound:example.sounds.pop");
        _hum = sounds.Get("sound:example.sounds.hum");
    }

    public SoundInstance Grinding => _grinding;

    public SoundEmitter? Sails => _sails;

    // The module that owns the camera claims the listener once...
    public void ClaimListener(ModuleContext context, in WorldPosition eyes)
    {
        _listener = _audio.ClaimListener(context);
        _listener.Teleport(eyes, Quaternion.Identity);
    }

    // ...and moves it at every tick, where it moves the camera.
    public void MoveListener(in WorldPosition eyes, Quaternion orientation) => _listener?.Move(eyes, orientation);

    // A sound of the interface: no position.
    public void ButtonPressed() => _audio.Play(_click);

    // A sound at a position of the world, which plays to its end.
    public void DoorCreaked(in WorldPosition door) => _audio.PlayAt(_creak, door, volume: 0.8f);

    // A sound that follows a cart, one block above its origin, and stops when the cart is gone.
    public void CartRolled(ISoundAnchor cart) => _audio.PlayAttached(_hum, cart, new Vector3(0, 1, 0));

    // A controlled sound: it starts silent, fades in, and fades out when the work ends.
    public void StartGrinding(in WorldPosition millstone)
    {
        _grinding = _audio.PlayAt(_hum, millstone, volume: 0);
        _grinding.FadeTo(1, seconds: 2);
    }

    public void StopGrinding() => _grinding.Stop(fadeSeconds: 1.5f);

    // A persistent emitter: created once, changed while it lives, disposed with the mill.
    public void BuildMill(ModuleContext context, in WorldPosition sails) =>
        _sails = _audio.CreateEmitter(context, _hum, sails);

    public void WindChanged(float strength)
    {
        _sails!.Volume = strength;
        _sails.Pitch = 0.8f + (0.4f * strength);
    }

    public void MillUnloaded() => _sails!.Suspend();

    public void MillLoaded() => _sails!.Resume();

    public void MillDestroyed() => _sails!.Dispose();
}
