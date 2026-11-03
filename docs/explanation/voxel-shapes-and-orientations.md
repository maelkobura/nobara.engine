# Voxel shapes and orientations

This page explains how nVoxel represents block collision, contact, and orientation without depending on a physics or rendering backend.

## Shape registry

nVoxel owns one shape registry per context. nVoxel provides the native
`shape:nobara.cube` and `shape:nobara.model` shapes. A platform module can
register another shape during initialization. nVoxel closes the registry at
startup, validates every `shape:` identifier in the block registry, and stops
the context with the block and source file when one is unknown.

A shape returns a neutral collision made of axis-aligned boxes inside the
unit block cube. An empty list is a non-colliding shape. The result also has
a face-opacity mask and one 4 × 4 contact mask for each face. nVoxel applies
the block's orientation to these values, so a shape implementation describes
only its reference orientation.

The native model shape uses a full unit-block collision because nVoxel does
not read render meshes. A client renderer can use a more detailed visual mesh,
and a platform module can register a shape with a more detailed server-side
collision when needed.

## Instance-dependent shapes

A shape can request the block-data view for the cell being queried. nVoxel
uses the same `IBlockShape` contract for type-only and instance-dependent
shapes. Type-only results are computed once per block state. Instance-dependent
results are evaluated with the data view supplied by the caller.

The result can additionally provide an instance mass, a fluid capacity, or a
normalization state. Consumers for those optional values are added by later
modules; nVoxel only carries the values through the shape contract.

## Orientations

An orientation is a block state property. The four horizontal orientations,
the six facing orientations, and the 24 full cube rotations use the same
right-handed axes as nBlocks: X points east, Y points up, and Z points south.
The reference orientation faces north. A quarter turn is clockwise when seen
from above.

The shape receives a state with its orientation reset to the reference value.
nVoxel then rotates the collision boxes and face mask. This keeps orientation
logic in one place and makes a shape usable by physics, lighting, integrity,
and rendering consumers.

## Multi-blocks

A multi-block type stores its footprint in the block resource. Every cell
stores the same type and orientation plus a `part` index. nVoxel rotates the
footprint with the orientation and calculates the origin by subtracting the
rotated offset from the cell coordinate. Only part zero is the origin; later
batch and persistence modules attach shared state and block data to that cell.

The footprint is bounded to eight cells along each axis by nBlocks. This keeps
neighbor queries and cross-chunk shape lookups bounded.
