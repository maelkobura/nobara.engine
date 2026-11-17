# The `Sound` resource

A `Sound` resource defines a sound event of
[nAudio](../../../src/nAudio/README.md): what plays when a module asks for
`sound:example.sounds.step`, with its variants and its settings. It wraps
its files, so each event has a resource identifier
(`sound://Example/Sounds/Step`) and an object identifier
(`sound:example.sounds.step`). Its schema is `Schemas/audio.proto` of the
module `nobara.audio`; a module that writes sounds declares a dependency on
`nobara.audio`. The common structure of a resource is described in
[the `.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Sounds">
  <Sound>
    <Name>Step</Name>                                   <!-- sound:example.sounds.step -->
    <Content>
      <Category>bus:nobara.audio.effects.players</Category>
      <Volume min="0.9" max="1.0"/>
      <Pitch min="0.9" max="1.1"/>
      <Spatial refDistance="2" maxDistance="16" rolloff="Linear"/>
      <Priority>Low</Priority>
      <MaxInstances>4</MaxInstances>
      <Variants>
        <Variant file="Sounds/step.wav"/>
        <Variant file="Sounds/bell.flac" weight="3" volume="0.5"/>
      </Variants>
      <Mono>true</Mono>
    </Content>
  </Sound>
</Nobara>
```

## Content

| Element | Required | Content |
| --- | --- | --- |
| `<Category>` | Yes | The bus the event plays on: a [`Bus`](bus.md) identifier, such as `bus:nobara.audio.effects.blocks`. |
| `<Volume>` | No | A factor of the volume: `value="0.8"` for a fixed value, or `min` and `max` for a range drawn at each play. 1 when absent. |
| `<Pitch>` | No | A factor of the pitch, written like the volume, greater than 0. 1 when absent. |
| `<Spatial>` | No | Present when the event plays at a position in the world; see below. |
| `<Priority>` | No | `Low`, `Normal` (default), or `High`: what keeps a real voice when voices run short. |
| `<MaxInstances>` | No | The most instances of the event that play at once; 0 or absent for the limit of the distribution, `audio.maxInstances`. Over the limit, the weakest instance ends, be it the new one. |
| `<Loop>` | No | `true` when the event loops. |
| `<Stream>` | No | `true` to decode the event in blocks while it plays, for music and long ambiences, instead of once when it loads. |
| `<Quality>` | No | The Vorbis quality of the WAV and FLAC sources, from `-0.1` to `1`; 0.4 when absent, about 128 kbit/s for stereo at 44.1 kHz. |
| `<Mono>` | No | `true` to mix stereo sources to mono at build time. |
| `<Variants>` | Yes | One `<Variant>` or more. |

### `<Spatial>`

| Attribute | Default | Content |
| --- | --- | --- |
| `refDistance` | 1 | The distance under which the event plays at full volume, greater than 0. |
| `maxDistance` | 32 | The distance beyond which it is not heard, greater than `refDistance`. |
| `rolloff` | `Inverse` | How the volume falls between them: `Inverse`, `Linear`, or `Exponential`. |

### `<Variant>`

| Attribute | Required | Content |
| --- | --- | --- |
| `file` | Yes | An `.ogg`, `.wav`, or `.flac` file, relative to `Resources/`. |
| `weight` | No | The weight of the variant in the draw, greater than 0; 1 when absent. |
| `volume` | No | A factor applied to the volume of the event; 1 when absent. |
| `pitch` | No | A factor applied to the pitch of the event, greater than 0; 1 when absent. |
| `quality` | No | The Vorbis quality of this source, instead of `<Quality>`. |

## Files

The embedded files are always Ogg Vorbis, mono or stereo:

- an `.ogg` file is embedded as it is, unless `<Mono>` asks for mono and the
  file is stereo: it is then decoded, mixed, and encoded again;
- a `.wav` file (integer PCM of 8, 16, 24, or 32 bits, or 32-bit float) or a
  `.flac` file is decoded and encoded with libvorbis at the quality of the
  variant or of the event, after the mix to mono that `<Mono>` asks for.

The file keeps its path: `Sounds/step.wav` is embedded at that path, as Ogg
Vorbis. A file used by several events is converted once, in mono when one of
them asks for it, at the highest quality they ask for. A file with more
than two channels is an error.

Every embedded file is decoded whole at build time: a damaged file, or one
shorter than it declares, is the error `NAUD002`.

A backend places only mono sounds in the world: a spatial event that uses a
stereo file gets the warning `NAUD003`, which suggests `<Mono>`.

## Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `NAUD001` | Error | A setting is not valid: a range, a distance, a weight, or a pitch. |
| `NAUD002` | Error | A file is not in a format the build reads, or is malformed. |
| `NAUD003` | Warning | A spatial event uses a stereo file. |
| `NAUD005` | Error | A file has more than two channels. |

An unknown `<Category>` and a missing file are reported by the compiler at
their line, like every reference.
