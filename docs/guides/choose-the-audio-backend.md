# Choose and configure the audio backend

This guide gives a client game its sound: it adds the OpenAL backend to the
distribution, sets the keys that the player and the distribution may change,
and explains what happens when the output changes or does not exist. It
assumes a game that already declares or plays sounds, as in [Add a
sound](add-a-sound.md) and [Play a sound](play-a-sound.md).

## 1. Add the backend

nAudio plays nothing by itself: a client distribution needs a backend
extension of `nobara.audio`, and a distribution where nAudio runs on a client
without one does not build. Add the OpenAL extension to the modules of the
game, in `project.nobara`:

```xml
<Package id="nobara.audio.openal" version="^1.0" />
```

The extension runs on the client only, so a dedicated server does not load
it. nAudio still loads the sound events there, so that both sides number them
the same way. See [the `build.nobara` reference](../reference/formats/build-nobara.md)
for the targets a distribution leaves modules out of.

The extension ships OpenAL Soft as a native library in `lib/` of the game,
where the backend finds it. It is under the LGPL: the engine links it dynamically and does not change it, and a
game that ships it ships its license text too.

## 2. Choose the backend by name

`audio.backend` names the backend to use. Its default, `auto`, takes the first
one registered, which is `openal` when it is the only backend. A name that no
module registers takes the first backend, with a warning in the journal. The
key is a `user` key: the player changes it in `config/settings.xml`, and a
change applies at the next start of the game.

## 3. Set the HRTF

`audio.hrtf` decides whether the output filters sounds by direction, so that
a player with headphones places them ahead, behind, above, and below:

| Value | Effect |
| --- | --- |
| `auto` | The library turns HRTF on when it detects headphones. This is the default. |
| `on` | HRTF is always on, when the output is stereo. |
| `off` | HRTF is always off; sounds are only panned. |

A distribution changes the default, or fixes the value, in `engine.nobara`
with `<Default>` or `<Lock>`, as for any `user` key; the player changes it
otherwise, and the change applies at once. The debug window of nAudio, with
nDebug, shows whether HRTF is in effect, and the journal records it each time
the device opens.

## 4. Limit the voices

OpenAL Soft mixes 256 sounds at once, which is more than a voxel game needs.
`audio.realVoices` caps the number of voices that nAudio gives to the device,
64 by default; with HRTF every real voice costs mixing time, so raise it only
when the sounds of a scene are lost. The other voices are virtual: they go on
in time, without being heard. `audio.maxInstances` caps the instances of one
event that gives no limit of its own. Both are `distribution` keys, set with
`<Set>` in `engine.nobara`.

## 5. Know what happens when the output changes

The player may unplug the headphones, or choose another default output in the
system, while the game runs. nAudio notices it within a fraction of a second,
closes the device, and opens the new default output without stopping the
game. Every sound goes on: a looping sound resumes where it was, and a sound
that does not loop ends at the end of its file. The journal records the
change.

When no output exists, at start or after a change, the game runs without
sound. At start, it logs an error and does not look for an output again. After
a change, nAudio tries again every second, and the sounds that were playing wait for
the output to come back; a sound played in the meantime is ignored.

## 6. Check the sound at the headphones

The HRTF is heard, not measured: put on headphones, play a sound that turns
around the listener, and check that it passes ahead, behind, above, and below.
The tests of nAudio.OpenAL check the rest with the loopback device of OpenAL
Soft, which renders sound in memory and needs no output. The test that opens
the real default output has the category `Audio`: run it on a machine that
has one with

```text
dotnet test Nobara.slnx --filter "Category=Audio"
```

The CI has no output and skips that category.
