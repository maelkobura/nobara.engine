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

## Loading chunks

A terrain grid has no end: nVoxel keeps in memory only the chunks that a
ticket holds. A ticket holds one chunk, or a cylindrical area of chunks
around a position that can move, at one of three levels: loaded, active, or
active at a reduced frequency. A chunk takes the highest level of its
tickets. Once no ticket holds a chunk, it stays in memory for
`voxel.unloadDelay` ticks, then nVoxel unloads it. A viewer, such as the local
player, holds a view area loaded and a smaller simulation area active.

A chunk that a ticket holds and that is not in memory is asked of the chunk
providers, in order: a save, a generator, later the network. The first that
accepts it writes its block states in a background job, into a buffer of its
own; the loop thread then applies the result at the `World` stage, as a
technical batch of cause `generation`, so subscribers of batch notifications
see generated blocks like any other change. Until then, queries report the
chunk as not loaded, never as air. A chunk that no provider accepts is air.

A multi-block may span two chunks. While the chunk of its origin is held, a
linked ticket holds the other chunks of its footprint loaded, so the
multi-block stays whole. A chunk held only by a linked ticket does not hold
further chunks, which rules out chains.

Grids other than terrain, such as a ship or a structure, keep every chunk they
have written until they are destroyed; their absent chunks are air.

## Block ticks and frozen dimensions

Active chunks tick at the `World` stage, after the batches. A scheduled tick
waits in the queue of its chunk until the clock of its dimension reaches it.
At each tick, nVoxel draws `voxel.randomTicksPerChunk` cells in each active
chunk; a drawn cell whose type subscribes to the `RandomTick` hook receives
it, and a chunk whose palette holds no such type costs one pass over its
palette. A chunk held at a reduced frequency ticks once every
`voxel.reducedTickInterval` ticks.

A dimension without an active chunk is frozen by default: its clock stops, so
its scheduled ticks and its entity systems wait, and resume when a chunk
becomes active again.

## Height maps and queries

For each grid without bounds, nVoxel keeps one height map per column of
chunks: the highest opaque block known in each column of blocks, updated when
chunks load and when batches apply. A bounded grid computes a height on
demand.

Raycasts, boxes, and spheres take their candidate grids from the spatial
index, then work in the frame of each grid. The offset between the query and
the grid is computed from whole chunk coordinates in double precision before
the rotation applies, so a query a billion blocks from the origin is as
precise as at the origin. A raycast walks the cells of each grid (DDA) and
tests a block that is not a full cube against its collision boxes.

See the [nVoxel module sheet](../../src/nVoxel/README.md), [entity worlds](entity-worlds.md),
and the [glossary](glossary.md).
