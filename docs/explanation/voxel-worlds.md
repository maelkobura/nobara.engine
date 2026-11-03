# Voxel worlds

nVoxel stores each context's voxel state in dimensions. A dimension owns an
entity world from nEntity and a terrain grid. Other grids can represent
structures, moving platforms, or isolated finite areas.

## Grids and chunks

A grid is an ECS entity with a transform and a generation-checked handle to
voxel storage. The storage is a sparse table from chunk coordinates to resident
chunks. A grid can be unbounded or can reject block coordinates outside its
inclusive local bounds.

Each chunk contains `32 × 32 × 32` cells. A palette stores the 32-bit block
state indices, including air at index zero, using the narrowest packed index
width that fits the current entries. The chunk version increases when stored
state or metadata changes. Compaction can release unused palette and channel
storage.

## Coordinates

Block coordinates use signed 32-bit integers in grid-local space. A chunk
coordinate is the arithmetic shift by five; a local coordinate is the low five
bits. The row-major cell index is `x + 32 × (z + 32 × y)`.

World positions use the shared core type. nVoxel applies a grid transform when
converting between world space and grid-local block coordinates. Conversions
that exceed the signed 32-bit block range throw instead of wrapping.

## Metadata and background work

Rare block metadata is stored by `(local index, data type)` and declares
serialization plus optional quarter-turn rotation. Dense channels use a fixed
width and default value; storage is allocated when a value differs from that
default. The natural channel tracks whether a cell still matches generated
terrain.

Jobs declare the `JobData` for every chunk they access. Background consumers can
request a frozen chunk copy with selected border copies; the snapshot has no
live job access declaration, and the loop thread applies any result.

See the [nVoxel module sheet](../../src/nVoxel/README.md), [entity worlds](entity-worlds.md),
and the [glossary](glossary.md).
