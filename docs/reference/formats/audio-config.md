# The `config/audio.xml` file

`audio.xml` holds the volumes of the buses that the player changed, as
[nAudio](../../../src/nAudio/README.md) writes them with
`IAudioSystem.SaveBusVolumes`. It lives in the configuration folder of the
game, next to `settings.xml`. It is readable XML: a player may correct it by
hand while the game is closed.

## Example

```xml
<?xml version="1.0" encoding="utf-8"?>
<AudioVolumes format="1">
  <Bus id="bus:nobara.audio.music" volume="0.25" />
  <Bus id="bus:example.sounds.machines" volume="0.8" />
</AudioVolumes>
```

## Content

The root element `<AudioVolumes>` takes the attribute `format`, `1`, and
holds one `<Bus>` element per adjustable bus whose volume differs from 1:

| Attribute | Content |
| --- | --- |
| `id` | The object identifier of the bus, such as `bus:nobara.audio.music`. Each bus appears once. |
| `volume` | The volume set by the player, from 0 to 1. |

Only the differences are written: a bus that the player did not change keeps
the default of 1, and the file stays empty of it.

## Reading

nAudio reads the file on the client when it initializes. A problem in an entry
ignores the entry, with a warning that gives its line:

- an element other than `<Bus id="…" volume="…"/>`;
- a second entry for the same bus;
- a bus that is not adjustable;
- a volume that is not a number from 0 to 1.

An entry for a bus that no loaded module defines is ignored with a warning,
and written again as it is when the file is saved: removing a module does not
lose the volume of its bus. A file that cannot be read, or whose root is not
`<AudioVolumes format="1">`, is ignored with a warning, and every volume is 1.

## Writing

`SaveBusVolumes` writes the whole file at once, through a temporary file
moved over the old one, so that a crash never leaves half a file.
