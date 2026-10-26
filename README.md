# Nobara

Nobara is a modular voxel game engine written in C# on .NET. A game is an
assembly of modules on top of a small core.

## Status

Early development: license, CI, and documentation skeleton only. There is
no engine code and no release yet. Start with
[the architecture overview](docs/explanation/architecture.md).

## Building

Requirements: the .NET SDK pinned in `global.json`, Python 3.12 or later, and
Node.js (for the Markdown checks).

```sh
dotnet build Nobara.slnx
dotnet test Nobara.slnx
python .github/scripts/check_all.py
```

## Contributions

Outside contributions are not accepted. The repository is public to be read.

## License

[Mozilla Public License 2.0](LICENSE).
