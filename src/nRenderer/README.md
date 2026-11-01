# nRenderer

This page is the module sheet of nRenderer, the module that organizes the
rendering of a frame. It describes what the module provides to other modules
and the rules it enforces. Terms are defined in [the
glossary](../../docs/explanation/glossary.md); the reasons for the design are
in [the rendering layers](../../docs/explanation/rendering-layers.md).

## Purpose

nRenderer turns the rendering resources of every module into GPU objects: it
defines the resource types `Shader`, `Material`, `Mesh`, and `Texture`,
compiles shaders from GLSL to SPIR-V when a module is built, decodes images
and meshes when the game loads, places the textures in texture arrays, one
per size, and uploads everything through [nGraphics](../nGraphics/README.md).

This sheet covers the resources. The organization of a frame (render world,
extraction, render graph, views, debug draw) is not implemented yet.

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

Third-party libraries: `SharpGLTF.Core` 1.0.7, `StbImageSharp` 2.30.16.
<!-- nobara:generated:end -->

nGraphics provides the device that creates the GPU objects. Third-party
libraries: `StbImageSharp` (MIT or Unlicense) decodes PNG images and
`SharpGLTF.Core` (MIT) reads glTF files, when the game loads. The plugin
`Nobara.Renderer.Build` compiles GLSL with shaderc, through `Silk.NET.Shaderc`
(MIT) and its native library `Silk.NET.Shaderc.Native` (Apache 2.0); it runs
in the build and in hot reload only, and is never shipped with a game. No
other module sees the types of these libraries.

## Entry points

| Member | Use |
| --- | --- |
| `IRenderResources` | The service of nRenderer, resolved with `context.Resolve<IRenderResources>()`: the texture arrays, textures, shaders, materials, and meshes of every loaded module, by identifier. |
| `RenderTexture`, `TextureLocation`, `TextureArray` | A texture: its array and its layer, and the GPU texture and sampler of the array. |
| `RenderShader`, `ShaderProgram` | A shader: its GPU shaders, one program per variant, the textures, parameters, and buffers it reads, and the layout of the resource set of its materials. |
| `ShaderTextureBinding`, `ShaderParameterBlock`, `ShaderParameterInfo`, `ShaderBufferBinding` | What a shader reads, by name and binding. |
| `RenderMaterial`, `MaterialMode` | A material: its shader and variant, its textures, its parameters in a uniform buffer, its resource set, and its render mode. |
| `RenderMesh`, `Submesh` | A mesh: its vertex and index buffers, its parts with their materials, and its bounds. |
| `TextureFilter`, `TextureColorSpace`, `ShaderParameterType`, `TextureFallback` | The settings of the resources. |
| `RenderResourceException` | Resources that cannot be used together, such as a material that names a texture its shader does not declare. |
| `RendererModule` | The identifier of the module and the type prefixes `shader`, `material`, `mesh`, and `texture`. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Resources on the GPU

nRenderer creates the GPU objects when it starts (step 4), after nGraphics,
from the resources of every loaded module; a module that depends on
nRenderer uses them in its own `Start` or later. Any such module may provide
shaders, materials, meshes, and textures, and use those of the others.

```csharp
public void Start(ModuleContext context)
{
    IRenderResources resources = context.Resolve<IRenderResources>();
    RenderMaterial leaves = resources.GetMaterial("material:example.materials.leaves");
    RenderMesh crate = resources.GetMesh("mesh:example.meshes.crate");
    TextureLocation dirt = resources.GetTexture("texture://Example/Textures/Dirt").Location;
}
```

Objects are found by object identifier or by resource identifier; the lists
are sorted by object identifier. A problem in the resources, such as a
material that does not fit its shader, stops the start with a
`RenderResourceException` that lists every problem with its file and line.

### Textures

Textures of the same width, height, number of mip levels, filter, and color
space share a texture array (a 2D array texture of nGraphics), one layer per
texture, ordered by object identifier; the arrays are ordered by size, then
settings. A `TextureLocation` (array, layer) is therefore the same from one
start to the next while the set of textures does not change: a mesher may
write it into vertices. Each array has its sampler: nearest or linear
filtering, mip levels blended, repeating. Colors in sRGB use an sRGB format,
so shaders read linear values. nRenderer computes the mip levels on the CPU
when it loads the textures.

### Shaders

A shader resource holds SPIR-V for each stage and each variant, compiled by
the build. `RenderShader.Programs` has the shader as written first, then each
variant in the order declared. `MaterialLayout` is the resource layout of a
material of the shader: its textures, by binding, as sampled textures, then
its parameter block as a uniform buffer, all visible to every stage of the
shader. The uniform and storage buffers that the shader declares are bound by
the pipeline: `Uniforms` and `Storage` give their bindings by name.

### Materials

A material resolves its shader, its variant, its textures, and its
parameters when nRenderer starts. The parameter block is written in the
std140 layout: the defaults of the shader, the values of the material, and,
for each `layer` parameter, the layer of the texture, or -1 when the
material leaves out an optional texture. `ResourceSet` binds, for each
texture of the shader, the array of the texture of the material, or a
texture of one texel given by the fallback of the shader, then the uniform
buffer of the parameters. `Mode` tells the pipeline in which pass to draw.

### Meshes

A mesh has one vertex buffer and one index buffer of 32-bit indices. Its
vertices follow `RenderMesh.VertexLayout`: position, normal, texture
coordinates, and tangent, 48 bytes. `Submeshes` has one part per material
slot, with the range of its indices and its material.

## Events

None.

## Configuration

<!-- nobara:generated:begin configuration -->
None.
<!-- nobara:generated:end -->

## Resources

The resource types `Shader`, `Material`, `Mesh`, and `Texture`, described
in their references: [`Shader`](../../docs/reference/formats/shader.md),
[`Material`](../../docs/reference/formats/material.md),
[`Mesh`](../../docs/reference/formats/mesh.md), and
[`Texture`](../../docs/reference/formats/texture.md). The plugin
`Nobara.Renderer.Build` compiles the GLSL sources of shaders to SPIR-V with
shaderc, and checks what the schema cannot: the stages, variants, bindings,
and parameters of a shader, the form of a material, the PNG files and the
settings of textures, and the glTF files and material slots of meshes
(`NREN001` to `NREN007`). An error of the GLSL compiler names the file and
the line of the source. A material is checked against its shader when
nRenderer starts, since the shader may come from another module.

PNG and GLB files are embedded as they are, and decoded once per process
when the game loads; every context shares the decoded data and creates its
own GPU objects.

In development mode, with [hot reload](../../tools/Nobara.HotReload/README.md),
a changed `.nof` file, GLSL source, PNG file, or GLB file is compiled again,
and nRenderer creates the GPU objects again at the next frame; each object
keeps its identity and increments its `Version`:

- a shader gets new GPU shaders and a new layout, and its materials are
  built again against it; when one of them does not fit the new shader, the
  shader keeps its previous version;
- a material is built again, or keeps its previous form when it does not fit
  its shader;
- a mesh is uploaded again;
- a texture is written again in its layer.

A GLSL source that does not compile is logged with its file and line, and
the game keeps the previous version. New shaders, materials, and meshes are
added. A removed resource, a new texture, and a texture whose size or
settings change, which would move it to another array, are refused with a
warning and need a restart.

## Constraints

- Client only: a dedicated server runs without nRenderer.
- Use the service and the objects on the loop thread of the client context.
- The GPU objects are created at the start and on hot reload. Looking up an
  object by resource identifier allocates (the identifier is compared
  without regard to case): resolve objects at load, not in hot paths.
- nRenderer keeps no static state; the native library of shaderc is never
  loaded by the game.

## Known limitations

- Only the resources are implemented: no render world, no extraction, no
  render graph, no view, and no debug draw. Nothing is drawn yet.
- The `transparent` mode is declared only: no pass draws or sorts
  transparent materials.
- GLSL `#include` is not supported.
- Textures are PNG files only, uploaded uncompressed in RGBA 8 bits; there
  is no animated texture.
- glTF animations, skins, morph targets, and the materials of the file are
  ignored; a mesh is static triangles.
- An override of a `Shader` that changes its sources or its variants
  compiles nothing: the SPIR-V comes from the module that defines the shader.
- A mesh keeps a reference to its materials: a material created again by a
  hot reload keeps its identity, so meshes see it.
