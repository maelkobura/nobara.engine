# nAudio

This page is the module sheet of nAudio, the audio module, and of its backend
extension nAudio.OpenAL. It describes what they provide to other modules and
the rules they enforce. Terms are defined in [the
glossary](../../docs/explanation/glossary.md).

## Purpose

nAudio keeps the audio data of a game and plays it. It defines the resource
type `Sound`, the [sound events](../../docs/explanation/glossary.md#sound-event)
that modules play, with their [variants](../../docs/explanation/glossary.md#variant)
and their settings, and the resource type `Bus`, the tree of
[buses](../../docs/explanation/glossary.md#bus) that mixes them. It provides
the default tree, numbers the sound events, and, on a client, decodes their
Ogg Vorbis files with NVorbis in background jobs, in memory or in blocks for
[streaming](../../docs/explanation/glossary.md#streaming). Its build plugin
converts WAV and FLAC sources to Ogg Vorbis when the module that declares them
is built.

On a client, nAudio plays sounds through a neutral API, on the device of an
audio [backend](../../docs/explanation/glossary.md#backend) that an extension
registers: [voices](../../docs/explanation/glossary.md#voice), real or
[virtual](../../docs/explanation/glossary.md#virtual-voice), the volumes of
the buses, the [listener](../../docs/explanation/glossary.md#listener), sounds
at a position or attached to a
[sound anchor](../../docs/explanation/glossary.md#sound-anchor),
[persistent emitters](../../docs/explanation/glossary.md#persistent-emitter),
and the chain of [emission processors](../../docs/explanation/glossary.md#emission-processor)
with [reverberation buses](../../docs/explanation/glossary.md#reverberation-bus).
The layers are described in [the voices of nAudio](../../docs/explanation/audio-voices.md).

nAudio knows nothing of blocks: the sounds of blocks are free properties that
the modules that play them read.

## Status

`experimental`

## Level

nAudio:

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server. Requires an active backend on the client.
<!-- nobara:generated:end -->

nAudio.OpenAL:

<!-- nobara:generated:begin level nobara.audio.openal -->
Platform module, on the client. Backend of `nobara.audio`.
<!-- nobara:generated:end -->

A server loads the sound events and the buses, so that both sides number them
the same way, but decodes nothing, needs no backend, and plays nothing: its
`IAudioSystem` is inactive.

## Dependencies

nAudio:

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.debug` | `^1.0` | Optional |
| `nobara.game` | `^1.0` | Optional |
| `nobara.graphics` | `^1.0` | Optional |
| `nobara.renderer` | `^1.0` | Optional |

Third-party libraries: `NVorbis` 0.10.5.
<!-- nobara:generated:end -->

nAudio.OpenAL:

<!-- nobara:generated:begin dependencies nobara.audio.openal -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.audio` | `^1.0` | Required |

Third-party libraries: `Silk.NET.OpenAL` 2.23.0, `Silk.NET.OpenAL.Extensions.Creative` 2.23.0, `Silk.NET.OpenAL.Soft.Native` 1.23.1.
<!-- nobara:generated:end -->

nAudio uses NVorbis (MIT), a managed Ogg Vorbis decoder, on the client. The
build plugin uses Nobara.AudioToolkit (MIT, with libogg and libvorbis under
the BSD 3-clause license) to read WAV and FLAC files and to encode Ogg
Vorbis; neither the toolkit nor its native library reaches a game.

nAudio.OpenAL uses `Silk.NET.OpenAL` and `Silk.NET.OpenAL.Extensions.Creative`
(MIT, with `Silk.NET.Core`), the bindings of OpenAL and of its EFX extension,
and OpenAL Soft 1.23.1, which `Silk.NET.OpenAL.Soft.Native` ships as a native
library for each platform (`soft_oal.dll` on Windows). OpenAL Soft is under
the LGPL 2.0 or later: the engine links it dynamically and does not modify
it, so a game keeps its own license, and a game that ships the library
ships its license text with it. No other module sees the types of these
libraries.

Each optional dependency is used by one isolation class:

- nGame (`GamePause`): the pause of the game pauses the sounds of the world;
  without nGame, nothing pauses.
- nDebug (`AudioDebugWindow`): the "Audio" tool window.
- nRenderer and nGraphics, with nDebug: the same window draws the sounds in
  the world with the debug drawing of nRenderer, while the window shows and
  the engine runs in development mode.

## Entry points

| Member | Use |
| --- | --- |
| `ISoundRegistry` | The sound events (`Sounds`, `Get`, `TryGet`), the buses (`Buses`, `Master`, `GetBus`, `TryGetBus`), the decoded variants (`TryGetClip`, `CompleteDecoding`), and the streams (`OpenStream`, from the start or from a frame). |
| `IAudioSystem` | Playback: `Play`, `PlayAt`, `PlayAttached`, the listener (`ClaimListener`), the emitters (`CreateEmitter`), the volumes of the buses (`GetBusVolume`, `SetBusVolume`, `GetEffectiveVolume`, `IsPaused`, `SaveBusVolumes`), the processors (`AddProcessor`), the reverberation buses (`CreateReverbBus`), the device (`IsActive`, `BackendName`, `Capabilities`, `RealVoiceLimit`), and the counts of the last frame (`Statistics`). |
| `SoundInstance` | The handle of one play, a value: `IsActive`, `IsReal`, `SetVolume`, `FadeTo`, `SetPitch`, `SetPosition`, `Stop`. |
| `AudioListener` | The listener: `Move`, `Teleport`; dispose it to give it back. |
| `ISoundAnchor` | What a sound or an emitter follows: an entity, a moving grid. |
| `SoundEmitter`, `EmitterRepeat` | A persistent emitter, in a loop or at random intervals: `Volume`, `Pitch`, `IsActive`, `SetPosition`, `Suspend`, `Resume`. |
| `IEmissionProcessor`, `EmissionContext`, `EmissionParameters`, `AudioRegistration` | The chain of emission processors. |
| `ReverbBus`, `ReverbParameters` | A reverberation bus and its neutral parameters. |
| `IAudioBackends`, `IAudioBackend`, `AudioDevice`, `AudioDeviceOptions`, `AudioCapabilities`, `AudioFeatures`, `AudioBuffer`, `VoiceParameters`, `ListenerParameters`, `HrtfMode` | The interface of a backend, for the extensions that provide one. |
| `AudioSettings` | The names of the configuration keys. |
| `SoundEvent` | A sound event: its numeric `Index`, its bus, its volume and pitch (`SoundRange`), its spatial settings (`SoundSpatial`, `SoundRolloff`), its priority (`SoundPriority`), its limit of instances, whether it loops and streams, its variants, and the weighted draw `PickVariant`. |
| `SoundVariant` | One file of an event: its weight, its own volume and pitch factors, its channels, its sample rate, and its length. |
| `AudioBus` | A bus: its index, its parent and children, whether the player adjusts it, and the i18n key of its name. |
| `SoundClip` | A variant decoded in memory: interleaved 16-bit samples. |
| `SoundStream`, `SoundBlock` | A variant decoded in blocks while it plays, and one decoded block. |
| `AudioLoadException`, `AudioProblem` | Why the sounds or the buses cannot be loaded. |
| `AudioModule` | The identifier of the module, the type prefixes `sound` and `bus`, and the identifier of the master bus. |

Resolve the services with `context.Resolve<ISoundRegistry>()` and
`context.Resolve<IAudioSystem>()`, from the initialization of a module that
depends on `nobara.audio`, and call them on the loop thread of the context.
The guide [Play a sound](../../docs/guides/play-a-sound.md) walks through
them.

### Sound events

nAudio reads every sound event when it initializes, checks it again with its
overrides applied, and numbers the events by identifier, in ordinal order.
The same modules give the same `Index` on every side and in every load: use
it in memory and in messages between sides, never on disk.

`PickVariant(seed)` draws a variant by weight; the same seed always gives
the same variant, so that two sides that share a seed play the same file.

```csharp
ISoundRegistry sounds = context.Resolve<ISoundRegistry>();
SoundEvent step = sounds.Get("sound:example.sounds.step");
SoundVariant variant = step.PickVariant(seed);
float volume = step.Volume.Lerp(random01) * variant.Volume;
```

### The tree of buses

The volume of a bus multiplies the volume of its children. nAudio provides
the default tree:

```text
bus:nobara.audio.master
├── bus:nobara.audio.music
├── bus:nobara.audio.ambient
├── bus:nobara.audio.effects
│   ├── bus:nobara.audio.effects.blocks
│   ├── bus:nobara.audio.effects.entities
│   └── bus:nobara.audio.effects.players
└── bus:nobara.audio.ui
```

The master bus and its direct children are adjustable by the player; the
children of `effects` follow it. A module adds a bus under an existing one
with a `Bus` resource. Every bus but the master has a parent; a parent that
no loaded module defines, or a cycle, stops the context with an
`AudioLoadException`. The master bus has index 0, and the others follow by
identifier.

### Decoding

On a client, every variant of an event that does not stream starts decoding
in memory, in a background job, when nAudio initializes; nothing waits for
it. `TryGetClip` gives the samples once they are ready, and
`CompleteDecoding` waits for every decoding in progress. A file that cannot
be decoded is logged as an error, and its variant has no clip.

An event marked to stream is decoded while it plays. `OpenStream` opens a
`SoundStream` that decodes ahead into three blocks of
`SoundStream.BlockFrames` frames, rented from a pool when it opens, one
background job at a time. Take the blocks in order, read them, and give them
back:

```csharp
using SoundStream stream = sounds.OpenStream(music.Variants[0], loop: true);
if (stream.TryTake(out SoundBlock block))
{
    ReadOnlySpan<short> samples = stream.GetSamples(block);
    // give the samples to the backend
    stream.Return(block);
}
```

A looping stream goes back to the start of the file inside a block, with no
gap. A stream that does not loop marks its last block with `IsLast`.
Samples are interleaved 16-bit integers, left then right for stereo.

### Playing sounds

A play returns a `SoundInstance`, a handle that the caller may ignore. The
play starts in the phase `Render` of the frame, after the ticks: nAudio draws
the variant, the volume, and the pitch from a seed, gives the play a voice,
and computes its gain from the volume of the event, the volume of the play,
the buses, the attenuation by distance, and the emission processors.

- `Play` plays without position, for the interface. A spatial event plays as
  it is too.
- `PlayAt` plays at a position; `SoundInstance.SetPosition` moves it.
- `PlayAttached` follows an anchor, at an offset in the frame of the anchor,
  and stops when the anchor has no transform any more.

A sound that does not loop ends at the end of its file. `Stop` ends a play at
once or with a fade; `FadeTo` moves its volume over a time. A handle is
revoked when its play ends, by itself or not: every call on it then does
nothing.

### Voices

Every play is a voice. The device mixes a limited number of them, at most
`audio.realVoices`: at each frame, nAudio gives them to the voices that
matter most, by priority of the event, then by gain, then by age. A voice
already real counts 1.25 times its gain, so that two close voices do not
swap at each frame. The other voices are virtual: they go on in time without
being heard, and a looping one resumes at the point it reached when it is
heard again. A voice whose gain is under 0.001 (about -60 dB), out of the
range of its event, or not decoded yet, stays virtual.

The most instances of an event that play at once is its `<MaxInstances>`, or
`audio.maxInstances` when the event gives none. Over the limit, the weakest
instance ends, be it the new one: a louder play replaces the weakest, and a
weaker one is ignored.

### Positions and the listener

The module that owns the camera claims the listener and moves it at every
tick, with the rules of the render world: the first `Move` of a tick makes
the pose heard until then the previous pose, and nAudio hears the listener
and the sounds between their two poses, by the fraction of tick of the frame.
`Teleport` is not interpolated. Velocities, for the Doppler effect, come from
the moves of the last tick.

Every position is subtracted from the position of the listener in double
precision before anything reaches the device: a device receives positions
relative to the listener, never farther than the range of the sound, however
far the listener is from the origin of the world. Without a listener, no
spatial sound is heard.

The gain of a spatial sound is 1 up to its reference distance, then falls
with its rolloff: `Inverse` (reference / distance), `Linear` (to 0 at the
maximum distance), or `Exponential` (the square of reference / distance). Over
the last tenth of its range, the gain fades to 0, so that a sound does not
cut at its maximum distance. nAudio computes the gain itself; a backend
applies no distance model of its own.

### Buses and pause

The volume of an adjustable bus, from 0 to 1, multiplies the volumes of the
buses below it. `SetBusVolume` changes it from the next frame;
`SaveBusVolumes` writes the volumes that differ from 1 to
[`config/audio.xml`](../../docs/reference/formats/audio-config.md), which
nAudio reads when it initializes.

While nGame pauses the game, the sounds of every bus but `ui`, `music`, and
the buses below them pause where they are, and their emitters wait.

### Emitters

A persistent emitter plays its event at a position or on an anchor, in a loop
or again after a random time between two bounds. It is created once and
changed while it lives. A looping emitter keeps one voice, virtual when it is
not heard; a repeating emitter skips the plays it would not be heard at.
`IsActive` follows the state of what it belongs to; `Suspend` and `Resume`
follow the life of what holds it, such as a chunk that unloads and loads
again.

### Emission processors and reverberation

A module adds an emission processor to change every voice from the world
around it: a factor of its gain, the cutoff of a low-pass filter, and the
level of a send to a reverberation bus. The chain runs at every frame for
every voice, in the order of registration; nAudio smooths what it produces
with a time constant of 50 ms before it reaches the device. A processor that
needs a feature the device lacks is disabled, with a warning. The chain is
empty in nAudio itself.

A reverberation bus is an effect with neutral parameters (decay, delay and
gain of the first reflections, density, diffusion, gain), changed while the
game runs and smoothed in the same way. It is unavailable, with a warning,
when the device has no effects or no free slot. nAudio provides no preset:
the module that knows the space around the listener sets them. The send of a
voice is taken after its gain, so the volumes of the buses apply to its
reverberation too.

### Backends

A backend module registers its `IAudioBackend` with `IAudioBackends` during
its initialization. When it starts, nAudio takes the backend that
`audio.backend` names, or the first one registered, and opens its device. A
client without any backend stops at start; a device that cannot open is
logged as an error, and the game runs without sound. A backend receives only
neutral types: final gains, positions relative to the listener, cutoffs in
hertz, and reverberation parameters in seconds and factors.

A device that stops playing, because the headphones were unplugged or the
default output changed, says so with `AudioDevice.IsLost`, which nAudio reads
once per frame. nAudio then closes the device, makes every voice virtual,
and opens the default output of the backend again; the voices take a voice of
the new device at the same frame, and a loop resumes where it was. While no
output exists, nAudio tries again every second, and the sounds wait.

| Extension | Backend name | Library |
| --- | --- | --- |
| nAudio.OpenAL (`nobara.audio.openal`) | `openal` | OpenAL Soft 1.23.1, through Silk.NET |

### nAudio.OpenAL

The guide [Choose and configure the audio backend](../../docs/guides/choose-the-audio-backend.md)
walks through the distribution and the keys.

nAudio.OpenAL runs on the client only. It opens the default output device
with one context and maps what nAudio asks for onto OpenAL:

- A real voice is a source, created the first time the voice is used. A sound
  in memory is a buffer; a stream is a queue of blocks, taken from a pool and
  given back to it once played, so a stream creates no buffer after its first
  blocks. A source that runs out of blocks stops, and plays again from the
  next block it is given.
- The device applies no distance model and no rolloff: it plays the gain it
  is given. The listener stays at the origin, and a spatial voice sits at its
  position relative to the listener, on the axes of the world, one block being
  one meter for the Doppler effect. A voice that is not spatial plays at the
  center, not placed in space.
- With EFX, the low-pass filter of a voice is the filter of the source, and a
  reverberation bus is an effect slot with a reverberation effect (EAX
  reverberation when the library has it, the standard reverberation
  otherwise). The level of the send of a voice is the gain of a filter on the
  send, and the neutral parameters of the bus are the parameters of the
  effect, one for one. EFX filters a low-pass at a fixed frequency of 5 kHz, so
  the device converts a cutoff to the attenuation of a one-pole filter at that
  frequency: it is an approximation, exact at 20 kHz, where the filter is
  removed.
- `Capabilities` reports `Hrtf` when the library has it, `Filters` and
  `AuxiliarySends` with EFX (one send per voice), `Effects` when at least one
  of the 4 reverberation slots exists, and the number of voices the library
  offers (256 by default).
- `audio.hrtf` is the HRTF attribute of the context: `auto` leaves the
  decision to the library, which turns it on when it detects headphones, `on`
  forces it, `off` disables it. A change of the key resets the device, and
  `AudioDevice.HrtfEnabled` reads the effective state; nAudio logs it when the
  device opens. HRTF needs a stereo output.
- The library mixes on a thread of its own, the only thread besides those of
  the engine: it reads what the device sets and writes to the output, and
  nothing else of the engine runs on it. Every call to the device is made on
  the loop thread. The library reports a change of the default output from
  that thread; the callback posts a flag to the loop thread, through the
  synchronization point of the context, and nAudio reads it in the phase
  `Render`.
- When the library cannot be loaded, or finds no output, opening fails with a
  `NotSupportedException` that says why; nAudio logs it as an error and the
  game runs without sound.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `audio.backend` | user | string | `auto` |  | client |
| `audio.hrtf` | user | enum | `auto` | `auto`, `on`, `off` | client |
| `audio.maxInstances` | distribution | int | `32` | 0 to … | client |
| `audio.realVoices` | distribution | int | `64` | 1 to 4096 | client |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `audio.backend` | The name of the audio backend, or `auto` for the first one registered. An unknown name takes the first one, with a warning. A change applies at the next start. |
| `audio.hrtf` | `auto` (the backend decides, usually on with headphones), `on`, or `off`: whether the backend filters sounds by direction. A change applies at once. |
| `audio.realVoices` | The most voices of the device used at once, within what the device has. Read at start. |
| `audio.maxInstances` | The most instances of an event that gives no `<MaxInstances>`; 0 for no limit. Read at start. |

## Resources

The resource types `Sound` and `Bus`, described in their references:
[`Sound`](../../docs/reference/formats/sound.md) and
[`Bus`](../../docs/reference/formats/bus.md). The guide
[Add a sound](../../docs/guides/add-a-sound.md) walks through a module that
declares sounds.

The plugin `Nobara.Audio.Build` checks what the schema cannot (`NAUD001` to
`NAUD005`): the ranges of volume and pitch, the distances of a spatial sound,
the weights of the variants, the format of each file, and the form of a bus.
It converts every WAV and FLAC source to Ogg Vorbis with libvorbis, mixes a
stereo source to mono when a sound asks for it, and embeds an Ogg source as
it is. It warns when a spatial sound uses a stereo file, which a backend
cannot place. Every embedded file is decoded whole by the decoder of the
module, so that a file that the game cannot play fails the build. The resource
compiler keeps the converted files in its cache: an unchanged source, used by
unchanged sounds, is not encoded again.

In development mode, with [hot reload](../../tools/Nobara.HotReload/README.md),
a changed sound, or a changed file, is converted again and replaces its
event at the next frame, with the same `Index`; a client decodes its
variants again. A new or removed sound, and any change of a bus, are refused
with a warning and take effect at the next start.

## Constraints

- Call the services, the handles, and the streams on the loop thread of the
  context, from the initialization of nAudio on. Nothing reaches the device
  outside the phase `Render`.
- An exception thrown by an emission processor is charged to the module that
  registered it, and one thrown by an anchor to the module of its code; the
  sound of a failed anchor ends, and the other voices go on. The processors,
  emitters, and reverberation buses of a disabled module no longer run.
- Once its voices, buffers, and emitters exist, a frame of nAudio allocates no
  managed memory (checked by a test); a play may grow the pool of voices
  once, and a voice that becomes real opens its stream or creates its buffer.
- A stream takes and gives back its blocks without managed allocation of
  its own; NVorbis allocates while it decodes (see the known limitations).
- A server decodes nothing: `TryGetClip` is false and `OpenStream` throws.
- An event that changed by hot reload is a new `SoundEvent` object: keep
  its `Index`, not the object.
- nAudio keeps no static state.
- nAudio.OpenAL makes one OpenAL context current per process: two clients in
  the same process, each with its own device, are not supported.

## Known limitations

- No backend ships in this module: a client distribution needs a backend
  extension of `nobara.audio`.
- One listener per client: no split screen.
- nAudio.OpenAL is tested on Windows x64. It applies the changes of a frame
  to the library one by one, not as one batch: a voice may hear its new gain
  a few milliseconds before its new position.
- nAudio.OpenAL has no output to test in the CI: its tests render the
  loopback device of OpenAL Soft, and the test of the default output has the
  category `Audio`, which the CI skips.
- A headphone that is unplugged is detected by the library within a fraction
  of a second; during that time the voices of nAudio go on, so sounds resume
  where they would be, and the part in between is not heard.
- While no output exists after a loss, a sound that is already playing waits
  where it is, and a sound played in the meantime is ignored.
- The server sends no sound to its clients, and a client does not play
  ahead the sounds of its own actions for a server: both come with the
  network.
- An anchor that teleports is interpolated across the jump, during one tick.
  When several ticks run in a frame, the previous position is that of the
  previous frame, not of the tick before the last one, as in the render world.
- The buffers of the variants that hot reload replaces stay on the device
  until the game stops.
- A play whose variant is still decoding stays virtual until the decoding
  ends.
- NVorbis 0.10.5 allocates managed memory while it decodes, about 5 bytes per
  decoded sample, and about 70 KB each time a looping stream goes back to its
  start. The allocations are short-lived and made by background jobs.
- NVorbis 0.10.5 decodes a few frames past the length that some files
  declare; nAudio stops at the declared length.
- Mixing an Ogg source to mono encodes it again, with one more lossy pass.
- The encoder ships for win-x64 only: the build of a module with WAV or FLAC
  sounds runs on Windows x64.
- A file referenced by several sounds is converted once: in mono when one of
  them asks for it, at the highest quality that they ask for.
- The stereo warning names the file and the sound, not the line of the
  `.nof` file.
