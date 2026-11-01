# Rendering layers

This page explains how rendering is split between three layers, and why.

## Three layers

| Layer | Question it answers | Module |
| --- | --- | --- |
| Rendering hardware interface ([RHI](glossary.md#rhi)) | How do we talk to the GPU? | [nGraphics](../../src/nGraphics/README.md) and its backends |
| Renderer | How is a frame organized? | nRenderer (not written yet) |
| Pipeline | What does the frame look like? | a render pipeline module (not written yet) |

The window is a fourth module, [nFrames](../../src/nFrames/README.md): it
creates the window and hands its surface to the graphics backend, but knows
nothing of the GPU.

## Why an RHI on the model of the explicit APIs

The first and only backend is OpenGL 4.6, yet nGraphics does not look like
OpenGL. It looks like Vulkan or Direct3D 12: pipelines are immutable
objects, resources are bound in sets that match a layout, commands are
recorded in command buffers and submitted, and the CPU paces frames in
flight with fences.

An RHI shaped after the state machine of OpenGL would be very hard to port to
an explicit API later: every draw would depend on state set anywhere before
it. The reverse is a known problem: the OpenGL backend records nothing on the
GPU side, and replays each command buffer when it is submitted, binding the
state of each pipeline as it goes. The cost is a slightly heavier layer from
the start.

The backend creates and changes every object through direct state access, and
binds an object only to use it. Shaders are SPIR-V compiled ahead of time, so
the same shaders can serve a Vulkan backend later.

## Why backends are extensions

nGraphics holds the neutral API and a registry; each graphics API is an
extension that registers with it, as the window backends do with nFrames.
Modules depend on nGraphics, never on a backend, and the backend is chosen at
run time from the configuration. A distribution ships only the backends its
targets need; one where nGraphics runs without a backend does not build. See
[Technical variants](architecture.md#technical-variants).

## Why capabilities are queried

Not every device can do everything: compute shaders, storage buffers,
indirect draws, and, one day, hardware ray tracing. nGraphics reports what
the device supports, and refuses what it does not, with a message that names
the missing feature. A pass or a pipeline declares the features it needs, so
the renderer can refuse it up front instead of failing in the middle of a
frame.

## Why the renderer sits between the RHI and the pipelines

Modules that draw, such as the meshing of voxels, the user interface, or
debug drawing, depend on the renderer, never on a pipeline. A pipeline can
then be replaced without touching them. The renderer, in turn, only sees the
neutral API of nGraphics, so a backend can be replaced without touching the
renderer.
