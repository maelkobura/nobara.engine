# Nobara

Nobara is a modular voxel game engine written in C# on .NET. A game is an
assembly of modules on top of a small core.

## Status

Early development: the build tools, the [core](src/Core/README.md), and the
first platform modules, [nEvent](src/nEvent/README.md),
[nGame](src/nGame/README.md), [nContext](src/nContext/README.md),
[nItems](src/nItems/README.md), [nBlocks](src/nBlocks/README.md),
[nFrames](src/nFrames/README.md), [nInput](src/nInput/README.md),
[nGraphics](src/nGraphics/README.md), and the resources of
[nRenderer](src/nRenderer/README.md), exist;
there is no release yet. Start with [the architecture
overview](docs/explanation/architecture.md).

## Building

Requirements: the .NET SDK pinned in `global.json`, Python 3.12 or later, and
Node.js (for the Markdown checks).

```sh
dotnet pack tools/Nobara.Sdk -c Release -o artifacts/packages
dotnet run --project tools/Nobara.Cli -- generate
dotnet build Nobara.slnx
dotnet test Nobara.slnx
python .github/scripts/check_all.py
```

Games are built with the command-line tool `nobara`, described in [the
command-line reference](docs/reference/cli.md).

## Contributions

Outside contributions are not accepted. The repository is public to be read.

## License

[Mozilla Public License 2.0](LICENSE).
