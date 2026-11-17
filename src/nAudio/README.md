# nAudio

This page is the module sheet of nAudio, the audio module. It describes what
the module provides to other modules and the rules it enforces. Terms are
defined in [the glossary](../../docs/explanation/glossary.md).

## Purpose

nAudio keeps the audio data of a game: the resource type `Sound`, the
[sound events](../../docs/explanation/glossary.md#sound-event) that modules
play, with their [variants](../../docs/explanation/glossary.md#variant) and
their settings, and the resource type `Bus`, the tree of
[buses](../../docs/explanation/glossary.md#bus) that mixes them. It
provides the default tree, numbers the sound events, and, on a client,
decodes their Ogg Vorbis files with NVorbis in background jobs, in memory or
in blocks for [streaming](../../docs/explanation/glossary.md#streaming). Its
build plugin converts WAV and FLAC sources to Ogg Vorbis when the module that
declares them is built.

nAudio does not play sounds yet: voices, mixing, spatialization, and the
playback API are not implemented, nor is any audio backend. It knows nothing
of blocks: the sounds of blocks are free properties that the modules that
play them read.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client and on the server. Requires an active backend on the client.
<!-- nobara:generated:end -->

A server loads the sound events and the buses, so that both sides number them
the same way, but decodes nothing and needs no backend.

## Dependencies

<!-- nobara:generated:begin dependencies -->
No module.

Third-party libraries: `NVorbis` 0.10.5.
<!-- nobara:generated:end -->

nAudio uses NVorbis (MIT), a managed Ogg Vorbis decoder, on the client. The
build plugin uses Nobara.AudioToolkit (MIT, with libogg and libvorbis under
the BSD 3-clause license) to read WAV and FLAC files and to encode Ogg
Vorbis; neither the toolkit nor its native library reaches a game.

## Entry points

| Member | Use |
| --- | --- |
| `ISoundRegistry` | The sound events (`Sounds`, `Get`, `TryGet`), the buses (`Buses`, `Master`, `GetBus`, `TryGetBus`), the decoded variants (`TryGetClip`, `CompleteDecoding`), and the streams (`OpenStream`). |
| `SoundEvent` | A sound event: its numeric `Index`, its bus, its volume and pitch (`SoundRange`), its spatial settings (`SoundSpatial`, `SoundRolloff`), its priority (`SoundPriority`), its limit of instances, whether it loops and streams, its variants, and the weighted draw `PickVariant`. |
| `SoundVariant` | One file of an event: its weight, its own volume and pitch factors, its channels, its sample rate, and its length. |
| `AudioBus` | A bus: its index, its parent and children, whether the player adjusts it, and the i18n key of its name. |
| `SoundClip` | A variant decoded in memory: interleaved 16-bit samples. |
| `SoundStream`, `SoundBlock` | A variant decoded in blocks while it plays, and one decoded block. |
| `AudioLoadException`, `AudioProblem` | Why the sounds or the buses cannot be loaded. |
| `AudioModule` | The identifier of the module, the type prefixes `sound` and `bus`, and the identifier of the master bus. |

Resolve the registry with `context.Resolve<ISoundRegistry>()`, from the
initialization of a module that depends on `nobara.audio`, and call it on the
loop thread of the context.

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

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

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

- Call the registry and the streams on the loop thread of the context, from
  the initialization of nAudio on.
- A stream takes and gives back its blocks without managed allocation of
  its own; NVorbis allocates while it decodes (see the known limitations).
- A server decodes nothing: `TryGetClip` is false and `OpenStream` throws.
- An event that changed by hot reload is a new `SoundEvent` object: keep
  its `Index`, not the object.
- nAudio keeps no static state.

## Known limitations

- Nothing plays: no voices, mixing, bus volumes, spatialization, playback
  API, emitters, or audio backend yet. A client distribution still needs an
  active backend of `nobara.audio`.
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
