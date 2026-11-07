# UNGP

This page is the module sheet of UNGP, the Universal Nobara Graphics
Pipeline. It describes what the module provides to other modules and the
rules it follows. Terms are defined in [the
glossary](../../docs/explanation/glossary.md); the reasons for the design are
in [the render pipelines](../../docs/explanation/render-pipelines.md).

## Purpose

UNGP is the render pipeline of the engine: it decides what a frame looks
like, by declaring to [nRenderer](../nRenderer/README.md) the passes of the
render graph of a view. It culls the chunk geometry of nRenderer on the GPU,
draws the scene in HDR at a render resolution apart from the window, opaque
and cut-out geometry first, then transparent geometry sorted from back to
front, and tonemaps it to the target of the view. It provides a standard
shader and a default material, so that textured meshes show without writing
a shader, and a chunk shader that draws the faces of blocks.

## Status

`experimental`

## Level

<!-- nobara:generated:begin level -->
Platform module, on the client.
<!-- nobara:generated:end -->

## Dependencies

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.graphics` | `^1.0` | Required |
| `nobara.renderer` | `^1.0` | Required |
<!-- nobara:generated:end -->

nRenderer runs the render graph that UNGP declares, and holds the shaders,
materials, and meshes it draws. nGraphics provides the device that creates
the GPU objects of the passes. Modules that draw depend on nRenderer, never
on UNGP; a game that uses the standard shader or the default material in its
resources depends on UNGP.

## Entry points

| Member | Use |
| --- | --- |
| `IUngp` | The service of the pipeline, resolved with `context.Resolve<IUngp>()`: the passes that can be turned off, occlusion culling, the sky color, the exposure, and the render scale. |
| `UngpModule` | The identifier of the module, the name of the pipeline (`ungp`), and the identifiers of the standard shader, of the chunk shader, of their variant `Cutout`, and of the default material. |
| `UngpSettings` | The names of the configuration keys, and how a color key reads. |
| `UngpPasses` | The names of the passes, as `IRenderer.LastGraph` lists them. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Registration

UNGP registers its pipeline with nRenderer under the name `ungp` when it
initializes. It requires the features of chunk geometry,
`ChunkGeometry.RequiredFeatures`: compute shaders, storage buffers, indirect
draws, and indirect draws with a count. On a device without one of them,
nRenderer refuses the pipeline when it starts, with an error that names the
missing features, and the game does not start. A view whose `Pipeline` names
no other pipeline is drawn by UNGP when UNGP is the first pipeline
registered, which it is in a game that has no other.

### Passes

UNGP declares these passes, in this order:

| Pass | What it does | Can be turned off |
| --- | --- | --- |
| `nobara.ungp.culling` | A compute pass: tests each chunk section against the view, then against the depth pyramid of the previous frame, and lists the draws of the visible ones. | No; `IUngp.OcclusionCulling` keeps only the test against the view |
| `nobara.ungp.depth-prepass` | Writes the depth of opaque and cut-out geometry, objects and chunks, without color. | Yes: `IUngp.DepthPrepass` |
| `nobara.ungp.opaque` | Draws opaque, then cut-out geometry, objects and chunks, to the HDR scene, tested against the depth of the prepass, or writing depth when the prepass is off. | No |
| `nobara.ungp.depth-pyramid` | A compute pass: builds the depth pyramid from the depth of the scene, for the culling of the next frame. | Yes: with `IUngp.OcclusionCulling` |
| Insertion point `AfterOpaque` | Passes of other modules, with the scene color and depth. | |
| `nobara.ungp.sky` | Fills the pixels where no geometry is with the sky color. | Yes: `IUngp.Sky`; off, these pixels stay black |
| `nobara.ungp.transparent` | Blends the transparent faces of chunks, then the transparent objects, over the scene, each from the farthest to the nearest; tested against the depth, without writing it. | No |
| Insertion point `BeforePostProcess` | Passes of other modules, such as debug drawing, with the scene color and depth. | |
| `nobara.ungp.tonemap` | Reads the HDR scene, applies the exposure, tonemaps the colors, and draws them to the target of the view, scaled to its viewport. | Yes: `IUngp.Tonemapping`; off, the pass `nobara.ungp.output` clamps the colors instead |
| Insertion point `Ui` | Engines of user interface, on the target of the view. | |

Turning a pass on or off builds the render graph again at the next frame.
Every pass follows the reversed depth of nRenderer: depth is cleared to 0,
and the nearest fragments pass the test "greater or equal". A pass of
another module that should draw before the transparent geometry inserts at
`AfterOpaque`; one that should draw after it inserts at `BeforePostProcess`.

### Culling

Only chunk geometry is culled: the objects of the render world are all
drawn. The culling pass leaves out the sections outside the view, then,
with `IUngp.OcclusionCulling` on (the default), the sections hidden behind
the depth pyramid of the previous frame, and those that the visibility graph
of nRenderer does not reach. Turning occlusion culling off removes the depth
pyramid pass and sets `ChunkGeometry.OcclusionCulling` and
`ChunkGeometry.VisibilityGraph` of `IRenderer.Chunks` to false; the image
does not change, only the number of sections drawn, which
`ChunkGeometry.Statistics` gives. The two settings of nRenderer can still be
set one by one afterward.

### Transparent geometry

The transparent pass draws, after the sky, the transparent faces of chunks,
from the farthest section to the nearest, each section sorted by
nRenderer, then the objects whose material has the mode `transparent`, from
the farthest to the nearest by the distance of their origin to the camera.
Colors are blended by the alpha of the fragment, and depth is tested but not
written, so a transparent surface behind another one still shows. The
standard shader writes the alpha of its texture times `Color`, so a
transparent material needs no variant:

```xml
<Material>
  <Name>Glass</Name>
  <Content>
    <Shader>shader:nobara.ungp.standard</Shader>
    <Mode>transparent</Mode>
    <Texture name="Albedo" texture="texture:example.blocks.glass" />
  </Content>
</Material>
```

The transparent faces of a section are sorted again when the camera has
moved enough, as set by the keys `ungp.transparentResortAngle` and
`ungp.transparentResortDistance`, which UNGP gives to
`ChunkGeometry.TransparentResortAngle` and
`ChunkGeometry.TransparentResortDistance`.

### HDR scene and render resolution

The scene is drawn to a color texture in `Rgba16Float`, in linear values that
may exceed 1, with a depth texture in `Depth32Float`. Both follow the render
resolution of the view, which UNGP sets from `IUngp.RenderScale` (key
`ungp.renderScale`) as a fraction of the viewport. The tonemap pass reads the
scene with linear filtering and draws to the whole viewport, so a scale of
0.5 draws a quarter of the pixels and shows the image at the size of the
window.

### Tonemapping

The tonemap pass multiplies the colors by `IUngp.Exposure` (key
`ungp.exposure`), then keeps the colors whose largest component is under
0.76 as they are, and compresses brighter ones toward white without
clipping: the highlight compression of the Khronos PBR Neutral tone mapper.
A texture shown at full brightness keeps its color, and a value above 1
stays visible instead of being cut. The pass then writes the colors in sRGB:
it encodes them itself when the target stores 8-bit values without doing
it, as the screen does, and lets a target in `Rgba8UnormSrgb` encode them. A target in a floating-point
format receives linear values.

### Standard shader and default material

The standard shader, `shader:nobara.ungp.standard`, draws a mesh at full
brightness: the texture `Albedo` times the parameter `Color`. Its variant
`Cutout` discards the texels whose alpha is under the parameter
`AlphaCutoff`.

| Declaration | Kind | Default | Use |
| --- | --- | --- | --- |
| `Albedo` | optional texture | white | The color of the surface. |
| `Color` | `vec4` | `1 1 1 1` | A factor applied to the texture. |
| `AlphaCutoff` | `float` | `0.5` | The alpha under which the variant `Cutout` discards a texel. |
| `AlbedoLayer` | `layer` | | Written by nRenderer: the layer of the texture `Albedo`. |

The default material, `material:nobara.ungp.default`, is the standard shader,
white and opaque. A material of a game names the shader and its textures:

```xml
<Material>
  <Name>Leaves</Name>
  <Content>
    <Shader>shader:nobara.ungp.standard</Shader>
    <Variant>Cutout</Variant>
    <Mode>cutout</Mode>
    <Texture name="Albedo" texture="texture:example.blocks.leaves" />
  </Content>
</Material>
```

A cut-out material sets both the mode `cutout`, which places it in the
opaque pass after opaque geometry, seen from both sides, and the variant
`Cutout`, which discards its transparent texels. Opaque geometry hides its
back faces.

### Chunk shader

The chunk shader, `shader:nobara.ungp.chunk`, draws the chunk geometry of
nRenderer in every pass. It decodes the chunk vertex, reads the texture of
the face from the texture arrays of nRenderer, and multiplies it by the
shade of the vertex: its tint, then its brightness, the product of its
light, of its ambient occlusion, and of the shade of its face.

| Value | Brightness |
| --- | --- |
| Light | The light of the vertex over 15, at most 1, with the daylight multiplier at 1. |
| Ambient occlusion 0, 1, 2, 3 | 1, 0.8, 0.6, 0.4 |
| Face toward +Y, -Y, ±Z, ±X, none | 1, 0.5, 0.8, 0.6, 1 |

The brightness is a value as the screen shows it, so the shader raises it to
the power 2.2 before it multiplies the linear color of the texture: a face
at 0.8 looks 0.8 times as bright on the screen. Opaque faces hide their back
faces; cut-out faces are drawn from both sides, and the variant `Cutout`
discards the texels whose alpha is under 0.5; transparent faces are blended
by the alpha of their texture.

The shader samples the texture arrays 0 to 15 of `IRenderResources`. A game
whose textures fill more arrays, through more sizes or settings, gets a
warning once, and the faces whose texture is in a later array are drawn
white.

The chunk shader follows this contract, which UNGP binds:

- it includes `<nobara/renderer/chunk.glsl>`, which reads the vertex at
  location 0, the section at location 1 per instance, and the storage
  buffers of the groups and of the tints at bindings 7 and 8
  (`ChunkGeometry.DrawLayout`, resource set 1);
- it declares the uniform block `View` (`<Uniform name="View">`), at a
  binding of its choice, which holds `mat4 ViewProjection`;
- it declares `uniform sampler2DArray BlockTextures[16]` at binding 0, which
  takes the bindings 0 to 15; the `Shader` resource does not declare these
  textures, which the pass binds itself;
- it declares `invariant gl_Position`, so that the depth prepass and the
  opaque pass compute the same depth;
- its variant `Cutout`, when it has one, draws cut-out faces.

### Shaders of other modules

UNGP draws any material, whatever its mode, with its own shader, when that
shader follows the same contract as the standard one:

- it reads the vertices of the mesh at locations 0 to 3
  (`RenderMesh.VertexLayout`);
- it reads the transform of the object seen from the camera as a `mat4` at
  locations 4 to 7, one per instance;
- it declares the uniform block `View` (`<Uniform name="View">`), at a
  binding of its choice, which holds `mat4 ViewProjection`;
- it declares `invariant gl_Position`, so that the depth prepass and the
  opaque pass compute the same depth.

```glsl
layout(location = 0) in vec3 inPosition;
layout(location = 4) in mat4 inModel;
layout(std140, binding = 0) uniform View { mat4 ViewProjection; };
invariant gl_Position;

void main()
{
    gl_Position = ViewProjection * (inModel * vec4(inPosition, 1.0));
}
```

A material whose shader lacks a vertex stage or the block `View` is left
out, with a warning once per material.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `ungp.exposure` | distribution | float | `1` | 0.0625 to 16 | client |
| `ungp.renderScale` | user | float | `1` | 0.25 to 2 | client |
| `ungp.skyColor` | distribution | string | `#87CEEB` |  | client |
| `ungp.transparentResortAngle` | distribution | float | `5` | 0 to 90 | client |
| `ungp.transparentResortDistance` | distribution | float | `0.5` | 0 to 16 | client |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `ungp.renderScale` | The render resolution as a fraction of the viewport, from 0.25 to 2: under 1, the scene has fewer pixels and is scaled up; above 1, it has more and is scaled down. A change applies at the next frame. |
| `ungp.skyColor` | The color of the sky, written `#RRGGBB` in sRGB. A value that is not a color is reported with a warning and leaves the sky as it was. A change applies at the next frame. |
| `ungp.exposure` | The factor applied to the HDR colors before tonemapping, from 0.0625 to 16. A change applies at the next frame. |
| `ungp.transparentResortAngle` | The angle, in degrees from 0 to 90, that the direction from a chunk section to the camera turns before the transparent faces of the section are sorted again; 5 by default. Lower, transparent faces stay in order when the camera moves fast, at the cost of more sorting. A change applies at the next frame. |
| `ungp.transparentResortDistance` | The distance, in blocks from 0 to 16, that the camera moves at least before the transparent faces of a chunk section are sorted again, for a camera near or inside the section; 0.5 by default. A change applies at the next frame. |

`IUngp.RenderScale`, `IUngp.SkyColor`, and `IUngp.Exposure` change these
settings for the session, until the key changes again.

## Resources

UNGP provides four shaders and a material, in its own resources:

| Identifier | Content |
| --- | --- |
| `shader:nobara.ungp.standard` | The standard shader, with its variant `Cutout`. |
| `shader:nobara.ungp.chunk` | The chunk shader, with its variant `Cutout`, drawn by the passes for chunk geometry. |
| `shader:nobara.ungp.sky` | The plain sky, drawn by the sky pass. |
| `shader:nobara.ungp.tonemap` | The tonemap pass, with its variant `Clamp` for the output without tonemapping. |
| `material:nobara.ungp.default` | The default material: the standard shader, white, opaque. |

The chunk, sky, and tonemap shaders serve the passes of UNGP only. The
tonemap shader reads the scene from the sampled texture at binding 0, and the
chunk shader the texture arrays at bindings 0 to 15, which the passes bind
themselves: the `Shader` resource declares only their uniform blocks. In
development mode, a hot reload of the shaders of UNGP applies at the next
frame.

## Constraints

- Client only: a dedicated server runs without UNGP.
- Use `IUngp` on the loop thread of the client context.
- A frame allocates no managed memory once stable: the passes create their
  graphics pipelines and resource sets the first time they draw a material,
  a category of chunk faces, or a target size, then reuse them; the
  transforms of the objects are copied to a buffer that grows to the largest
  frame, and the transparent objects are sorted in arrays that grow the
  same way.
- UNGP keeps no static state.

## Known limitations

- No lighting: objects are drawn at full brightness; chunk faces at the
  light of their vertices, with a daylight multiplier fixed at 1.
- The sky is one color: no sun, moon, clouds, or gradient.
- Only chunk geometry is culled: every object of the view is drawn, by the
  prepass and by the opaque pass, even outside the field of view.
- Transparent objects are sorted by the distance of their origin: a large
  object, or two that cross, can be drawn out of order. The transparent
  faces of chunks are all drawn before the transparent objects, so a
  transparent object behind a transparent face of a chunk does not show
  through it.
- The chunk shader samples the first 16 texture arrays only.
- The chunk shader cannot be replaced by another one; a material does not
  apply to chunk geometry.
- The depth prepass writes no normals.
- No light clusters, shadows, fog, bloom, or color grading; their places in
  the order of the passes are kept, before the opaque pass for the first
  two, and before tonemapping for the others. Without fog, chunks end
  sharply at the edge of the loaded area.
- The tonemap pass scales the image with linear filtering only; there is no
  upscaler.
- The exposure is fixed: it does not adapt to the brightness of the scene.
