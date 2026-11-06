# GPU-driven chunk geometry

This page explains how [nRenderer](../../src/nRenderer/README.md) stores,
culls, and draws the geometry of chunks, and why. nRenderer knows neither
voxels nor blocks: a mesher gives it, per chunk, vertices and triangles, and
nRenderer keeps them on the GPU. The API is described in the module sheet.

## Groups and sections

The geometry is organized like the world: a [chunk group](glossary.md#chunk-group)
per grid, with the transform of the grid, and a [chunk
section](glossary.md#chunk-section) per chunk that has geometry, placed by the
whole coordinates of its chunk in the group. A group moves like an object of
the render world, interpolated between ticks by the same rules, so a moving
grid needs no change of its sections. The terrain is one more group, at the
origin.

Each section has three categories of faces, drawn in different passes:
opaque faces, cut-out faces (alpha tested, such as leaves), and transparent
faces (water, glass), which must be drawn from back to front.

## Shared buffers and indirect draws

Drawing thousands of chunks one draw call each costs the CPU more than the
GPU. nRenderer stores the vertices of every section in one vertex buffer and
their indices in one index buffer. A section takes a range of each, found
first fit; a removed or replaced section gives its ranges back once the
[frames in flight](glossary.md#frame-in-flight) that may read them are done.
A full buffer doubles, its content copied on the GPU, in order with the
commands of the frame.

With one pair of buffers, a whole category is one [multi-draw
indirect](glossary.md#multi-draw-indirect): each draw reads its first index,
index count, and base vertex from a GPU buffer, and its instance is the
section, whose chunk and group a vertex shader reads per instance. Opaque and
cut-out faces are each one indirect draw whose number of draws is also read
from a buffer; transparent faces are one more.

Ranges freed in the middle of a buffer leave holes. When the free space is
fragmented, sections are moved down to holes below them by copies on the GPU,
within a budget of bytes per frame, so a world that keeps remeshing does not
make the buffers grow.

Geometry is copied when a module gives it and sent to the GPU within a budget
of bytes per frame, in order; a replaced section is drawn with its previous
geometry until the new one is there.

## A vertex in 16 bytes

A chunk vertex holds its position in the chunk on 16 bits per axis, in steps
of 1/1024 block from -16 to 48 blocks, so models may overhang their chunk;
its texture coordinates on 16 bits each, up to 64, so a merged face may repeat
its texture over a chunk; its texture array and layer; its normal as one of
the six directions or none; its sky light and the red, green, and blue light
of blocks, from 0 to 15.75 in quarters, which smoothed light needs; and its
ambient occlusion on 2 bits. Positions are local to the chunk: no large
coordinate is ever stored in a vertex. The shader library of nRenderer
decodes it, so every shader reads the format the same way.

## Precision far from the origin

The chunk of a section and the chunk of the camera are whole numbers. The CPU
places the camera in each group once per frame, in double precision: a whole
chunk and a position in it. A vertex shader subtracts the chunk of the camera
from the chunk of the section in integers, then adds the position of the
vertex in the chunk minus the position of the camera in its chunk, and only
then applies the rotation and scale of the group. Every value that reaches a
float is small, near the camera, wherever the camera is: a scene a billion
blocks away is drawn exactly as at the origin, pixel for pixel.

## Culling on the GPU

Each frame, the CPU lists the candidate sections. A compute shader tests each
one: its box, transformed by its group, against the planes of the view, then
against the [depth pyramid](glossary.md#depth-pyramid) of the previous frame.
A visible section appends its draws to the lists of the frame; the indirect
draws read them. The CPU never waits for the result: the figures of the
culling come back a few frames later.

## Occlusion culling

Two techniques complement each other.

The **depth pyramid** is built from the depth of the frame, after the opaque
geometry, and used by the culling of the next frame: a box is projected with
the camera of that frame, and is hidden when it is behind the farthest depth
of the values it covers. It needs nothing from the mesher and hides anything
behind anything, entities included. Its limits come from its delay: on the
first frame, after a frame without it, or for a box behind the camera of the
previous frame, it hides nothing; and a section that the camera uncovers
during one frame may miss that frame.

The **[visibility graph](glossary.md#visibility-graph)** runs on the CPU,
before the GPU. A mesher gives, with each section, which faces of the chunk
see each other through it. From the chunk of the camera, a search crosses
sections only between connected faces, and never turns back toward the
camera; chunks without a section are open. The result depends only on the
chunk of the camera and on the sections, so it is kept until one of them
changes. Sections it does not reach, such as those beyond the walls of a cave,
are not even sent to the GPU, and it has no delay.

Measured underground (3,176 solid sections, the camera in a tunnel), the
pyramid alone draws 244 sections out of 3,176 candidates; the graph alone
sends 91 candidates and draws 83; both together draw 73. Both are on by
default, and each can be turned off.

## Sorted transparency

Transparent faces are blended, so they must be drawn from back to front.
nRenderer keeps the transparent triangles of each section on the CPU and
sorts them by the distance of their centers to the camera, in jobs, then
sends the sorted indices. Sorting at every frame would cost a lot for nothing:
a section is sorted again only when the direction from it to the camera has
turned by a few degrees, or, near the camera, when the camera has moved by
some distance. Sections themselves are drawn from the farthest to the
nearest, an order the CPU computes at every frame. Cut-out faces are not
sorted: they are opaque where they are drawn.
