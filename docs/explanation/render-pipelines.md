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
| Culling on the GPU | later |
| Depth prepass | yes, without normals |
| Light clusters, shadows | later |
| Opaque and cut-out geometry | yes, at full brightness |
| Sky | yes, one color |
| Transparent geometry | later |
| Fog, bloom, color grading | later |
| Tonemapping | yes |
| User interface | yes, through its insertion point |

Every surface is drawn at full brightness, with the color of its texture:
lighting comes with the lighting module. The passes of later versions
already have their place in the order, so adding one does not move the
others, nor the insertion points that other modules use.

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

## Why passes can be turned off

UNGP aims at the look of the best shader packs of voxel games, with soft
shadows, reflections, and volumetric clouds, but also at modest hardware.
Each effect is a pass that can be turned off, so a game, or a player, picks
its cost. Turning a pass off builds the render graph again: the passes that
remain read what is still written, and the graph checks that nothing reads
what no pass writes.
