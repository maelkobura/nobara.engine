# Give sounds to a block

This guide gives a block the sounds that play when it is placed and when it
is broken. It assumes a module with sound events, as [Add a
sound](add-a-sound.md) declares them, and with blocks, as in the [`Block`
reference](../reference/formats/block.md). The excerpts come from
`src/nVoxel/Nobara.Voxel.Sound.Tests/Resources/sounds.nof` and `blocks.nof`,
which the tests of nVoxel.Sound compile.

Prerequisites:

- a module with a dependency on `nobara.blocks`;
- on the client, a distribution with nAudio, a backend of nAudio, and
  nVoxel.Sound (`nobara.voxel.sound`).

A distribution without nVoxel.Sound ignores the properties below, and blocks
are silent.

## 1. Declare the sound events

Declare a sound event for each sound, on the bus of the sounds of blocks, and
spatial so that it is heard where the block is. A sound that is heard in
space must be mono:

```xml
  <Sound>
    <Name>StonePlace</Name>
    <Content>
      <Category>bus:nobara.audio.effects.blocks</Category>
      <Spatial refDistance="2" maxDistance="128"/>
      <Mono>true</Mono>
      <Variants>
        <Variant file="Sounds/step.wav"/>
      </Variants>
    </Content>
  </Sound>
```

Give an event several variants to vary the sound; nAudio draws one at every
play.

## 2. Name them in the block

Add the free properties `PlaceSound` and `BreakSound` to the `<Properties>`
of the block:

```xml
  <Block>
    <Name>Stone</Name>
    <Content>
      <Shape>shape:nobara.cube</Shape>
    </Content>
    <Properties>
      <PlaceSound>sound:example.world.stoneplace</PlaceSound>
      <BreakSound>sound:example.world.stonebreak</BreakSound>
    </Properties>
  </Block>
```

A property that a block leaves out is silence: a block that only breaks
loudly gives `BreakSound` alone. A sound that no module provides stops the
start, with the file and the line of the property.

## 3. Change the sound with the state

A variant of the block overrides a property for the states it concerns, as it
does for any free property:

```xml
  <Block>
    <Name>Lamp</Name>
    <Content>
      <Shape>shape:nobara.cube</Shape>
      <States>
        <Bool name="lit" default="false"/>
      </States>
      <Variants>
        <Variant when="lit=true">
          <Properties>
            <BreakSound>sound:example.world.lampbreak</BreakSound>
          </Properties>
        </Variant>
      </Variants>
    </Content>
    <Properties>
      <BreakSound>sound:example.world.stonebreak</BreakSound>
    </Properties>
  </Block>
```

The sound of a block that breaks is the one of the state it had; the one of a
block that is placed is the one of the state it gets. To change a sound for
nVoxel.Sound only, write the property with the short name of the module,
`<VoxelSound.BreakSound>`.

## What the player hears

The sound plays at the middle of the block, with the settings of its event. A
block of several cells makes one sound, at its origin. On a grid that moves,
the sound follows the grid. When a batch places or breaks many blocks at
once, blocks of one type that share a sound event and a zone of 4 × 4 × 4 blocks make one
sound, and a tick starts at most 16 of them: see [the module
sheet](../../src/nVoxel/README.md#resources).
