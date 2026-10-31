# Configuration keys

This page lists the configuration keys that the modules of this repository
declare, with the module sheet that gives their type, default, bounds, and
effect. How keys are declared, adjusted by a distribution, and stored for the
player is described in [the core page](../../src/Core/README.md#configuration-keys).

## Scopes

| Scope | Who changes the value | Where the value lives |
| --- | --- | --- |
| `user` | The player, within the limits of the distribution | `config/settings.xml` in the data folders |
| `distribution` | The distribution only, in `engine.nobara` | The distribution manifest |

## Keys

| Key | Scope | Module |
| --- | --- | --- |
| `blocks.maxStatesPerType` | distribution | [nBlocks](../../src/nBlocks/README.md#configuration) |
| `frames.backend` | user | [nFrames](../../src/nFrames/README.md#configuration) |
| `frames.backgroundFps` | user | [nFrames](../../src/nFrames/README.md#configuration) |
| `frames.fullscreenHeight` | user | [nFrames](../../src/nFrames/README.md#configuration) |
| `frames.fullscreenRefreshRate` | user | [nFrames](../../src/nFrames/README.md#configuration) |
| `frames.fullscreenWidth` | user | [nFrames](../../src/nFrames/README.md#configuration) |
| `frames.mode` | user | [nFrames](../../src/nFrames/README.md#configuration) |
| `frames.monitor` | user | [nFrames](../../src/nFrames/README.md#configuration) |
| `items.defaultMaxStack` | distribution | [nItems](../../src/nItems/README.md#configuration) |
