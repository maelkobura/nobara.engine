# Render pipelines

This page explains what a render pipeline is, what UNGP, the pipeline of the
engine, draws in its first version, and why it is built this way.

## What a pipeline decides

[nRenderer](../../src/nRenderer/README.md) organizes a frame: it keeps the
[render world](glossary.md#render-world), runs the
[render graph](glossary.md#render-graph), and lets modules add passes at
[insertion points](glossary.md#insertion-point). It does not decide what
the frame looks like. A [render pipeline](glossary.md#render-pipeline) does:
which passes run, in which order, in which formats, and with which effects.
The split between the three layers is explained in [the rendering
layers](rendering-layers.md).

Modules that draw depend on nRenderer, never on a pipeline. A pipeline can
then be replaced, or another one added for other hardware, without touching
them. [UNGP](glossary.md#ungp), the Universal Nobara Graphics Pipeline, is
the first: it is meant to run everywhere, as the universal pipeline of Unity
does, and other pipelines, such as one built on hardware ray tracing, may
come later.

## What UNGP draws in its first version

| Pass | Version |
| --- | --- |
| Culling on the GPU | yes, chunk geometry only |
| Depth prepass | yes, without normals |
| Light clusters, shadows | later |
| Opaque and cut-out geometry | yes: objects at full brightness, chunks at the light of their vertices |
| Sky | yes, one color |
| Transparent geometry | yes, sorted from back to front |
| Fog, bloom, color grading | later |
| Tonemapping | yes |
| User interface | yes, through its insertion point |

Objects are drawn at full brightness, with the color of their texture, and
chunk faces with the light, the ambient occlusion, and the tint that the
mesher writes in their vertices: lighting comes with the lighting module.
The passes of later versions already have their place in the order, so
adding one does not move the others, nor the insertion points that other
modules use.

## Why the scene is drawn in HDR

The scene is drawn in a floating-point texture, with linear values that may
exceed 1, and is converted to the colors of the screen only at the end, by
[tonemapping](glossary.md#tonemapping). In
[HDR](glossary.md#hdr), light adds up without being cut: a torch next to
the sun, a reflection on water, or bloom later on keep the difference
between bright and very bright. Effects that read the scene, such as fog,
bloom, or exposure, work on these values, and only the last pass decides how
they fit on the screen.

The texture is in half-precision floating point, four channels: enough
precision for colors, and half the memory of single precision.

## Why this tonemapping

A tonemapper maps HDR values to the range of the screen. Common ones, such
as the ACES curves, change every color, darker or more saturated, to give a
film look. In the first version of UNGP, a surface shows the color of its
texture at full brightness, and the colors chosen by the artist should stay
as they are.

UNGP therefore keeps the colors whose largest component is under 0.76
unchanged, and compresses only brighter ones toward white, without cutting
them: this is the highlight compression of the Khronos PBR Neutral tone
mapper, made to keep base colors faithful. The full Khronos curve also
darkens the darkest colors slightly, to make up for the light that
physically based materials reflect; UNGP leaves that part out while it has
no such materials.

## Why the render resolution is apart from the window

The scene is drawn at a render resolution, a fraction of the viewport, and
the tonemap pass scales it to the viewport. A lower resolution trades
sharpness for speed on a weak GPU; a higher one makes the image smoother.
Keeping the two apart from the start also leaves room for an upscaler,
which draws fewer pixels and reconstructs the others.

## Why a depth prepass

The prepass draws the depth of the opaque geometry first, without color.
The opaque pass then shades each pixel once, for the nearest surface only,
instead of shading surfaces that a nearer one later hides. The prepass costs
a second drawing of the geometry, cheap without color; it pays off when
shading is expensive, and later gives the depth that culling on the GPU and
the clusters of lights need. It can be turned off, and the opaque pass then
writes the depth itself.

## Why culling runs in a pass of its own

The culling pass runs first, on the GPU: it tests the box of each chunk
section against the view, then against a depth pyramid built from the depth
of the previous frame, and writes the list of the draws of the visible
sections, which the prepass, the opaque pass, and the transparent pass then
read. The CPU never waits for the result. Turning occlusion culling off
keeps the test against the view, since the passes draw only what the list
holds, and since it never changes the image: it only removes the depth
pyramid and the visibility graph, which hide sections that other geometry
covers. How chunk geometry is culled is explained in [GPU-driven chunk
geometry](chunk-geometry.md#occlusion-culling).

## Why transparent geometry comes after the sky

A transparent surface blends its color with what lies behind it, so what
lies behind must be drawn first: the opaque geometry, then the sky. The
transparent pass then draws from the farthest surface to the nearest, and
tests the depth without writing it, so that a pane of glass behind another
one still shows through it. Chunk faces are sorted by nRenderer, section by
section; objects are sorted by UNGP, by the distance of their origin. The
order is exact for faces that do not cross, which is the common case of
glass and water.

## Why chunk faces are shaded by their direction

Before lighting exists, faces that point in different directions would show
the same color, and the shape of the terrain would be hard to read. The
chunk shader darkens each face by its direction, the top brightest and the
bottom darkest, as voxel games commonly do, and by the ambient occlusion
that the mesher computes at each corner. These factors describe how bright
the face looks on the screen, so the shader converts them to linear values
before it multiplies the linear colors of the scene.

## Why passes can be turned off

UNGP aims at the look of the best shader packs of voxel games, with soft
shadows, reflections, and volumetric clouds, but also at modest hardware.
Each effect is a pass that can be turned off, so a game, or a player, picks
its cost. Turning a pass off builds the render graph again: the passes that
remain read what is still written, and the graph checks that nothing reads
what no pass writes.
