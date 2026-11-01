# The `Material` resource

A `Material` resource defines a material of
[nRenderer](../../../src/nRenderer/README.md): a [shader](shader.md), the
textures and parameters it gives to the shader, and its render mode. Its
schema is `Schemas/renderer.proto` of the module `nobara.renderer`; a module
that writes materials declares a dependency on `nobara.renderer`. The common
structure of a resource is described in [the `.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Materials">
  <Material>
    <Name>Leaves</Name>                                 <!-- material:example.materials.leaves -->
    <Content>
      <Shader>shader:example.shaders.lit</Shader>
      <Variant>Cutout</Variant>
      <Mode>cutout</Mode>
      <Texture name="Albedo" texture="texture:example.textures.leaves" />
      <Parameter name="AlphaCutoff" value="0.25" />
    </Content>
  </Material>
  <Material>
    <Name>Stone</Name>
    <Content>
      <Shader>shader:example.shaders.lit</Shader>
      <Texture name="Albedo" texture="texture:example.textures.stone" />
      <Texture name="Normal" texture="texture:example.textures.stone_normal" />
      <Parameter name="Tint" value="0.9 0.9 1 1" />
    </Content>
  </Material>
</Nobara>
```

## Content

| Element | Required | Content |
| --- | --- | --- |
| `<Shader>` | Yes | The object identifier of a shader of the module or of its dependencies. |
| `<Variant>` | No | The name of a variant of the shader; the shader as written when absent. |
| `<Mode>` | No | `opaque` (default), `cutout`, or `transparent`. |
| `<Texture>` | No, repeated | A texture of the shader: attributes `name`, a texture of the shader, and `texture`, the object identifier of a [texture](texture.md). |
| `<Parameter>` | No, repeated | A parameter of the shader: attributes `name` and `value`, numbers separated by spaces. |

## Render mode

| Mode | Drawing |
| --- | --- |
| `opaque` | Every fragment is written. |
| `cutout` | Fragments below an alpha threshold are discarded, without sorting, as foliage needs. The shader does it, usually in a variant, with a parameter for the threshold. |
| `transparent` | Blended over what is behind, as water and glass need. |

The mode tells the pipeline in which pass to draw the material.

## Checks

The build checks the form: the mode, the identifiers, a texture or a
parameter given twice, and values that are numbers. The shader may come from
another module, so nRenderer checks the material against it when it creates
the GPU objects; a problem stops the start, or, during a hot reload, keeps
the previous form of the material:

- the variant exists in the shader;
- each texture is a texture of the shader, and each texture of the shader
  that is not optional is given;
- each parameter is a parameter of the shader, other than a `layer`, with
  as many values as its type, a whole number for an `int`.

A parameter that the material does not give takes the default of the shader.

## Errors

| Code | Cause |
| --- | --- |
| `NREN003` | An unknown render mode, a variant name of the wrong form, a texture or a parameter given twice, or a value that is not a list of numbers. |
| `NRC401` | A shader or a texture that no module of the build defines. |
