# Nobara

Nobara is a modular voxel game engine written in C# on .NET. A game is an
assembly of modules on top of a small core.

## Status

Early development. The repository currently holds the license, the
continuous integration, and the documentation skeleton. There is no engine
code yet and no released version.

## Building

Requirements: the .NET SDK pinned in `global.json`, Python 3.12 or later, and
Node.js (for the Markdown checks).

```sh
dotnet build Nobara.slnx
dotnet test Nobara.slnx
python .github/scripts/check_all.py
```

## Documentation

Start with [the architecture overview](docs/explanation/architecture.md) and
[the glossary](docs/explanation/glossary.md).

## Contributions

Outside contributions are not accepted: pull requests from other authors will
be closed. The repository is public to be read.

## License

[Mozilla Public License 2.0](LICENSE).
