# Play a sound

This guide plays the sounds of a windmill in a fictional farming game: a
sound of the interface, sounds in the world, a sound that follows a cart, a
sound that fades in and out, and the emitter of the mill. It assumes a module
that declares sound events, as [Add a sound](add-a-sound.md) does. The code
excerpts come from `src/nAudio/Nobara.Audio.Tests/Samples/MillSounds.cs` and
`EntitySoundAnchor.cs`, which the tests of nAudio compile and run.

Prerequisites:

- a module with a dependency on `nobara.audio`;
- on the client, a distribution with a backend extension of nAudio.

On a server, every call below works and plays nothing, so code shared by both
sides needs no test of the side.

## 1. Resolve the services

From the initialization of the module, resolve the registry of sounds and the
audio system, and keep the events the module plays:

```csharp
ISoundRegistry sounds = context.Resolve<ISoundRegistry>();
_audio = context.Resolve<IAudioSystem>();
_click = sounds.Get("sound:example.sounds.click");
_creak = sounds.Get("sound:example.sounds.pop");
_hum = sounds.Get("sound:example.sounds.hum");
```

Call the audio system on the loop thread of the client. A play made during a
tick starts in the phase `Render` of the same frame.

## 2. Place the listener

The module that owns the camera claims the listener once, and moves it at
every tick, where it moves the camera:

```csharp
public void ClaimListener(ModuleContext context, in WorldPosition eyes)
{
    _listener = _audio.ClaimListener(context);
    _listener.Teleport(eyes, Quaternion.Identity);
}

public void MoveListener(in WorldPosition eyes, Quaternion orientation) => _listener?.Move(eyes, orientation);
```

Only one module holds the listener; it faces -Z of its orientation, with +Y
up. `Move` is interpolated between ticks, `Teleport` is not. Without a
listener, sounds without position play, and spatial sounds are not heard.

## 3. Play sounds

A sound of the interface has no position:

```csharp
public void ButtonPressed() => _audio.Play(_click);
```

A sound of the world plays at a position, and to its end:

```csharp
public void DoorCreaked(in WorldPosition door) => _audio.PlayAt(_creak, door, volume: 0.8f);
```

The volume and the pitch of the call are factors of those of the event. Each
play draws its variant, volume, and pitch; pass `seed` to draw the same ones
again.

## 4. Follow an object

A sound attached to an anchor follows it, at an offset in its frame, and stops
when the anchor is gone:

```csharp
public void CartRolled(ISoundAnchor cart) => _audio.PlayAttached(_hum, cart, new Vector3(0, 1, 0));
```

An anchor is any object that gives its transform while it exists. A module
that depends on nEntity writes one over an entity, and keeps one per entity
that makes sounds:

```csharp
internal sealed class EntitySoundAnchor(EntityWorld world, EntityId entity, ComponentType<WorldTransform> transform) : ISoundAnchor
{
    public bool TryGetTransform(out WorldTransform value)
    {
        if (!world.IsAlive(entity))
        {
            value = default;
            return false;
        }

        value = world.Read(entity, transform);
        return true;
    }
}
```

## 5. Control a sound

Every play returns a `SoundInstance`. Keep it to change the sound while it
plays: here a millstone starts silent, fades in over 2 seconds, and fades out
when the work ends.

```csharp
public void StartGrinding(in WorldPosition millstone)
{
    _grinding = _audio.PlayAt(_hum, millstone, volume: 0);
    _grinding.FadeTo(1, seconds: 2);
}

public void StopGrinding() => _grinding.Stop(fadeSeconds: 1.5f);
```

The handle is revoked when the sound ends, by itself or not; a call on a
revoked handle does nothing, so there is no need to test `IsActive` first.

## 6. Keep a sound going

A persistent emitter plays its event as long as it lives, in a loop by
default, or at random intervals with `EmitterRepeat.Every`. Create it once,
change it while it lives, and suspend it with what holds it:

```csharp
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
```

Far from the listener, the emitter costs no voice of the device: its loop
goes on in time without being heard.

## 7. Check what plays

In development mode, the "Audio" window of the debug tools lists the voices,
real and virtual, with their gains, the volumes of the buses, which it can
change, and the emitters. With nRenderer, it can draw the sounds in the world:
a small sphere at each sound, and its range.

See [the module sheet of nAudio](../../src/nAudio/README.md) for the rules of
the voices, the buses, and the pause, and [the voices of
nAudio](../explanation/audio-voices.md) for why they work that way.
