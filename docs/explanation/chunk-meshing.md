# The meshing of chunks

This page explains how nVoxel.Renderer, the client extension of
[nVoxel](../../src/nVoxel/README.md), turns blocks into the [chunk
geometry](chunk-geometry.md) of nRenderer, and why. The API is described in
the module sheet.

## A mesher per shape

The shape of a block decides how it is meshed. nVoxel has two shapes, and
nVoxel.Renderer meshes both: cubes, whose faces it merges across the chunk,
and models, whose mesh it places in the cell. A module that adds a shape to
nVoxel adds its [mesher](glossary.md#mesher) too, from its client side: the
server never runs meshing code. A mesher of an instance-dependent shape
receives the block data of its block, so a block whose look depends on its
data, such as a block carved in small cells, needs no special case.

The look of a block is in free properties of its resource:
`RenderTexture` and its forms for each face, `RenderModel`, and `RenderTint`.
nBlocks applies the variants, so a lamp that is lit has a texture of its own
without any code; the meshing reads a table per state, built once at start.

## Greedy meshing of cubes

A chunk of 32³ blocks has up to 196,608 faces of cubes. Most of them are
hidden by a neighbor, and most visible ones lie next to an equal face in the
same plane. The mesher walks the chunk slice by slice, in each of the six
directions: it marks the visible faces of the slice, each with what makes it
look the way it does (texture, tint, category, and ambient occlusion), then
covers the slice with the largest rectangles of equal faces. This is
[greedy meshing](glossary.md#greedy-meshing): a flat floor of a chunk is one
quad instead of 1,024. The texture coordinates of a quad count in blocks, and
the texture repeats, so a merged face looks the same as the faces it replaces.

A face is hidden when the neighbor is opaque and covers that face. Two
transparent blocks of one type, such as panes of glass or cells of water,
hide the face between them. Cut-out blocks, such as leaves, keep the faces
between them: through the holes of one leaf, the next one shows.

## Ambient occlusion

[Ambient occlusion](glossary.md#ambient-occlusion) darkens the corners where
blocks meet, which gives depth to a world lit evenly. For each corner of a
face, the mesher looks at the two blocks beside the corner and the block in
the corner, in front of the face: two sides make the darkest corner, else
each block adds a step. The value goes in the vertex, and the GPU blends it
across the face. Faces whose corners differ are not merged, since a large
quad would spread the shadow of one corner over many blocks. The diagonal of
a quad joins its two lighter corners, so the shadow of a corner does not
streak along the face.

## Copy, then result

The mesher runs in background jobs, while the world goes on changing. A
remesh starts on the loop thread with a frozen copy of the chunk and of one
layer of cells from each of its 26 neighbors, the cells that hide its faces
and cast its shadows; the job reads only that copy. Its result comes back to
the loop thread, during the extraction of nRenderer, and is given to the
chunk geometry. If the chunk changed meanwhile, the result is discarded, and
the chunk is remeshed from a new copy.

A chunk is marked when a batch changes it, and its neighbors too when the
change touches its border; marking it again does nothing until its remesh
starts. So a chunk is remeshed at most once per frame, however many blocks
changed in it, and at most one remesh of a chunk runs at a time. The chunks
that a player changed go first, then the nearest the camera, so the world
fills in from the player outward. A change made by a player is shown at the
next frame: its remesh is waited for if it did not finish.

## Faces of the chunk

Each remesh also tells which faces of the chunk see each other through it,
for the [visibility graph](glossary.md#visibility-graph) of nRenderer: a
flood fill of the cells that do not hide all their faces, each region joining
the faces of the chunk it reaches. A chunk of solid stone joins none, and the
graph does not look through it.

## Loading

A world session starts once the chunks of the simulation area are loaded and
meshed. nVoxel.Renderer adds a loading task that waits for the loading task
of nVoxel and for the remeshes of those chunks, so the first frame of the
session shows the ground under the player. The rest of the view area keeps
loading and meshing in play.
