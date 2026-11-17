# Add a sound

This guide declares the sounds of a module: sound events with WAV, FLAC, and
Ogg Vorbis sources, and a bus of its own. It assumes a module built with
`Nobara.Sdk` and the [`.nof` format](../reference/formats/nof.md). The
resource excerpts come from `src/nAudio/Nobara.Audio.Tests/Resources/game.nof`,
which the tests of nAudio compile.

## 1. Depend on nAudio

Declare the dependency in `module.nobara`; the build then loads the plugin of
nAudio, which checks the sounds and converts their sources.

```xml
<Dependencies>
  <Module id="nobara.audio" version="^1.0" />
</Dependencies>
```

## 2. Add the files

Put the files under `Resources/`, such as `Resources/Sounds/step.wav`. The
build reads:

- `.ogg` (Ogg Vorbis), embedded as it is;
- `.wav` (integer PCM of 8, 16, 24, or 32 bits, or 32-bit float) and
  `.flac`, converted to Ogg Vorbis.

A sound is mono or stereo. A sound placed in the world must be mono: a
backend cannot place a stereo sound.

## 3. Declare a bus, if needed

Every sound plays on a bus. Use one of the default tree, such as
`bus:nobara.audio.effects.blocks`, or add one under an existing bus:

```xml
<Bus>
  <Name>Machines</Name>
  <Content>
    <Parent>bus:nobara.audio.effects</Parent>
    <Adjustable>true</Adjustable>
    <DisplayName>audio.bus.machines</DisplayName>
  </Content>
</Bus>
```

An adjustable bus shows in the settings of the player, under the name of its
i18n key.

## 4. Declare the sound events

A sound for the interface, from an Ogg file:

```xml
<Sound>
  <Name>Click</Name>
  <Content>
    <Category>bus:nobara.audio.ui</Category>
    <Volume value="0.8"/>
    <Variants>
      <Variant file="Sounds/click.ogg"/>
    </Variants>
  </Content>
</Sound>
```

A sound placed in the world, drawn from two variants, the second three times
as often. `<Mono>` mixes the stereo FLAC file to mono at build time:

```xml
<Sound>
  <Name>Step</Name>
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
```

A long sound, decoded in blocks while it plays:

```xml
<Sound>
  <Name>Music</Name>
  <Content>
    <Category>bus:nobara.audio.music</Category>
    <Loop>true</Loop>
    <Stream>true</Stream>
    <Priority>High</Priority>
    <Variants>
      <Variant file="Sounds/music.ogg"/>
    </Variants>
  </Content>
</Sound>
```

Every element is described in [the `Sound` reference](../reference/formats/sound.md).

## 5. Build

`dotnet build` compiles the sounds. The WAV and FLAC sources are encoded at
quality 0.4, or at the quality of `<Quality>`; an unchanged source is not
encoded again. Check the diagnostics:

- `NAUD003`: a spatial sound uses a stereo file; add `<Mono/>` (or `<Mono>true</Mono>`);
- `NAUD002`: a file is not in a format the build reads;
- an unknown bus or a missing file is reported at its line.

## 6. Read the sounds

From its initialization, a module that depends on nAudio finds the events in
the registry, `ISoundRegistry`, by identifier; their `Index` is the same on
every side. See [the sound events](../../src/nAudio/README.md#sound-events)
in the module sheet. nAudio does not play sounds yet; see the known limitations of
[its module sheet](../../src/nAudio/README.md#known-limitations).
