# UNGP

This page is the module sheet of UNGP, the Universal Nobara Graphics
Pipeline. It describes what the module provides to other modules and the
rules it follows. Terms are defined in [the
glossary](../../docs/explanation/glossary.md); the reasons for the design are
in [the render pipelines](../../docs/explanation/render-pipelines.md).

## Purpose

UNGP is the render pipeline of the engine: it decides what a frame looks
like, by declaring to [nRenderer](../nRenderer/README.md) the passes of the
render graph of a view. It draws the scene in HDR at a render resolution
apart from the window, then tonemaps it to the target of the view. It
provides a standard shader and a default material, so that textured meshes
show without writing a shader.

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
| `IUngp` | The service of the pipeline, resolved with `context.Resolve<IUngp>()`: the passes that can be turned off, the sky color, the exposure, and the render scale. |
| `UngpModule` | The identifier of the module, the name of the pipeline (`ungp`), and the identifiers of the standard shader, of its variant `Cutout`, and of the default material. |
| `UngpSettings` | The names of the configuration keys, and how a color key reads. |
| `UngpPasses` | The names of the passes, as `IRenderer.LastGraph` lists them. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Registration

UNGP registers its pipeline with nRenderer under the name `ungp` when it
initializes. It requires no optional feature of the graphics device. A view
whose `Pipeline` names no other pipeline is drawn by UNGP when UNGP is the
first pipeline registered, which it is in a game that has no other.

### Passes

UNGP declares these passes, in this order:

| Pass | What it does | Can be turned off |
| --- | --- | --- |
| `nobara.ungp.depth-prepass` | Writes the depth of opaque and cut-out geometry, without color. | Yes: `IUngp.DepthPrepass` |
| `nobara.ungp.opaque` | Draws opaque, then cut-out geometry to the HDR scene, tested against the depth of the prepass, or writing depth when the prepass is off. | No |
| Insertion point `AfterOpaque` | Passes of other modules, with the scene color and depth. | |
| `nobara.ungp.sky` | Fills the pixels where no geometry is with the sky color. | Yes: `IUngp.Sky`; off, these pixels stay black |
| Insertion point `BeforePostProcess` | Passes of other modules, such as debug drawing, with the scene color and depth. | |
| `nobara.ungp.tonemap` | Reads the HDR scene, applies the exposure, tonemaps the colors, and draws them to the target of the view, scaled to its viewport. | Yes: `IUngp.Tonemapping`; off, the pass `nobara.ungp.output` clamps the colors instead |
| Insertion point `Ui` | Engines of user interface, on the target of the view. | |

Turning a pass on or off builds the render graph again at the next frame.
Every pass follows the reversed depth of nRenderer: depth is cleared to 0,
and the nearest fragments pass the test "greater or equal".

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

### Shaders of other modules

UNGP draws any material whose mode is `opaque` or `cutout`, with its own
shader, when that shader follows the same contract as the standard one:

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
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `ungp.renderScale` | The render resolution as a fraction of the viewport, from 0.25 to 2: under 1, the scene has fewer pixels and is scaled up; above 1, it has more and is scaled down. A change applies at the next frame. |
| `ungp.skyColor` | The color of the sky, written `#RRGGBB` in sRGB. A value that is not a color is reported with a warning and leaves the sky as it was. A change applies at the next frame. |
| `ungp.exposure` | The factor applied to the HDR colors before tonemapping, from 0.0625 to 16. A change applies at the next frame. |

`IUngp.RenderScale`, `IUngp.SkyColor`, and `IUngp.Exposure` change these
settings for the session, until the key changes again.

## Resources

UNGP provides three shaders and a material, in its own resources:

| Identifier | Content |
| --- | --- |
| `shader:nobara.ungp.standard` | The standard shader, with its variant `Cutout`. |
| `shader:nobara.ungp.sky` | The plain sky, drawn by the sky pass. |
| `shader:nobara.ungp.tonemap` | The tonemap pass, with its variant `Clamp` for the output without tonemapping. |
| `material:nobara.ungp.default` | The default material: the standard shader, white, opaque. |

The sky and tonemap shaders serve the passes of UNGP only. The tonemap shader
reads the scene from the sampled texture at binding 0, which the pass binds
itself: the `Shader` resource declares only its uniform block `Output`. In
development mode, a hot reload of the shaders of UNGP applies at the next
frame.

## Constraints

- Client only: a dedicated server runs without UNGP.
- Use `IUngp` on the loop thread of the client context.
- A frame allocates no managed memory once stable: the passes create their
  graphics pipelines and resource sets the first time they draw a material
  or a target size, then reuse them; the transforms of the objects are
  copied to a buffer that grows to the largest frame.
- UNGP keeps no static state.

## Known limitations

- No lighting: every surface is drawn at full brightness.
- Transparent materials are not drawn: each one is reported once with a
  warning, and its objects are left out.
- The sky is one color: no sun, moon, clouds, or gradient.
- No culling on the GPU or the CPU: every object of the view is drawn, by
  the prepass and by the opaque pass, even outside the field of view.
- The depth prepass writes no normals.
- No light clusters, shadows, fog, bloom, or color grading; their places in
  the order of the passes are kept, before the opaque pass for the first
  two, and before tonemapping for the others.
- The tonemap pass scales the image with linear filtering only; there is no
  upscaler.
- The exposure is fixed: it does not adapt to the brightness of the scene.
