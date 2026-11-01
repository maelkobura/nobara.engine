# The `Shader` resource

A `Shader` resource defines a shader of [nRenderer](../../../src/nRenderer/README.md):
GLSL sources, one per stage, compiled to SPIR-V when the module is built, its
variants, and the resources it reads. Its schema is `Schemas/renderer.proto`
of the module `nobara.renderer`; a module that writes shaders declares a
dependency on `nobara.renderer`, which loads the plugin that compiles them.
The common structure of a resource is described in [the `.nof`
reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Shaders">
  <Shader>
    <Name>Lit</Name>                                    <!-- shader:example.shaders.lit -->
    <Content>
      <Vertex>Shaders/lit.vert</Vertex>
      <Fragment>Shaders/lit.frag</Fragment>
      <Variant name="Cutout">
        <Define>ALPHA_CUTOUT</Define>
      </Variant>
      <Texture name="Albedo" binding="0" />
      <Texture name="Normal" binding="1" optional="true" fallback="normal" />
      <Parameters binding="1">
        <Parameter name="Tint" type="vec4" default="1 1 1 1" />
        <Parameter name="AlphaCutoff" type="float" default="0.5" />
        <Parameter name="AlbedoLayer" type="layer" texture="Albedo" />
        <Parameter name="NormalLayer" type="layer" texture="Normal" />
      </Parameters>
      <Uniform name="Camera" binding="0" />
    </Content>
  </Shader>
</Nobara>
```

```glsl
#version 460

layout(binding = 0) uniform sampler2DArray Albedo;
layout(binding = 1) uniform sampler2DArray Normal;

layout(std140, binding = 1) uniform Material
{
    vec4 Tint;
    float AlphaCutoff;
    int AlbedoLayer;
    int NormalLayer;
};

layout(location = 0) in vec2 vTexCoord;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 color = texture(Albedo, vec3(vTexCoord, AlbedoLayer)) * Tint;
#ifdef ALPHA_CUTOUT
    if (color.a < AlphaCutoff)
    {
        discard;
    }
#endif
    outColor = color;
}
```

## Content

| Element | Required | Content |
| --- | --- | --- |
| `<Vertex>` | With `<Fragment>` | The GLSL source of the vertex stage, relative to `Resources/`. |
| `<Fragment>` | With `<Vertex>` | The GLSL source of the fragment stage. |
| `<Compute>` | Alone | The GLSL source of a compute shader, without the other stages. |
| `<Variant>` | No, repeated | A variant: attribute `name`, and `<Define>` elements, each `NAME` or `NAME=VALUE`. |
| `<Texture>` | No, repeated | A texture that materials give: attributes `name`, `binding`, `optional` (`false` when absent), and `fallback`. |
| `<Parameters>` | No | The uniform block of the parameters of a material: attribute `binding`, and `<Parameter>` elements. |
| `<Uniform>` | No, repeated | A uniform block that the pipeline binds: attributes `name` and `binding`. |
| `<Storage>` | No, repeated | A storage buffer that the pipeline binds: attributes `name` and `binding`. |

A shader is either a vertex and fragment shader or a compute shader. Names
of variants, textures, parameters, and buffers have the form of GLSL
identifiers, and are unique in the shader.

## Sources

Each source is one GLSL stage written for OpenGL: it starts with
`#version 460`, its entry point is `main`, and its resources have explicit
`binding` qualifiers without `set`, and its inputs and outputs explicit
`location` qualifiers. Binding numbers are counted per class of resource:
samplers, uniform blocks, and storage buffers each have their own numbers
(see [the nGraphics module sheet](../../../src/nGraphics/README.md#shaders)).

The build compiles each source to SPIR-V with shaderc, once as written and
once for each set of macros of the variants of the shaders that use it, and
embeds the result in place of the source. A source can be shared by several
shaders, as the same stage. An error of the GLSL compiler fails the build
with the file and the line of the source; a variant that does not compile
is named in the message.

## Variants

A variant is the same sources compiled with more macros, as if each source
began with `#define NAME VALUE` (`1` when the value is absent). Macros whose
name starts with `GL_` are reserved. A material chooses a variant by name;
nRenderer creates the GPU shaders of every variant when it starts.

## Textures

A texture of a shader is a `sampler2DArray` at the binding given, read with
the layer of the texture of the material (see `layer` below). An optional
texture may be left out by a material; nRenderer then binds a texture of one
texel given by `fallback`:

| `fallback` | Texel |
| --- | --- |
| `white` (default) | Opaque white. |
| `black` | Opaque black. |
| `normal` | A flat normal in tangent space: (0.5, 0.5, 1). |

Materials use these names, by convention, for the maps of physically based
shading: `Albedo`, `Normal`, `Roughness`, and `Emission`.

## Parameters

`<Parameters>` describes the uniform block of a material, in the std140
layout and in the order of the GLSL source. Each `<Parameter>` has a `name`,
a `type`, and an optional `default`: numbers separated by spaces, as many as
the type has; zeros when absent.

| `type` | GLSL | Values |
| --- | --- | --- |
| `float` | `float` | 1 |
| `vec2` | `vec2` | 2 |
| `vec3` | `vec3` | 3 |
| `vec4` | `vec4` | 4 |
| `int` | `int` | 1, a whole number |
| `layer` | `int` | None: nRenderer writes the layer of the texture named by `texture`, or -1 when the material leaves it out. |

## Errors

| Code | Cause |
| --- | --- |
| `NREN001` | Stages missing or mixed, a variant, texture, parameter, or buffer that is invalid or declared twice, two textures or two uniform blocks on one binding, a macro of the wrong form, an unknown fallback or parameter type, a default with the wrong number of values, or a layer that names no texture of the shader. |
| `NREN002` | The GLSL source does not compile; at the file and the line of the source. |
| `NREN006` | A GLSL source used as two stages. |
| `NREN007` | Warning of the GLSL compiler, at its line. |
