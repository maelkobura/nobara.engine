# The `Mesh` resource

A `Mesh` resource defines a mesh of [nRenderer](../../../src/nRenderer/README.md):
a glTF binary file (`.glb`) and the [material](material.md) of each of its
parts. Its schema is `Schemas/renderer.proto` of the module
`nobara.renderer`; a module that writes meshes declares a dependency on
`nobara.renderer`. The common structure of a resource is described in [the
`.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Meshes">
  <Mesh>
    <Name>Crate</Name>                                  <!-- mesh:example.meshes.crate -->
    <Content>
      <File>Meshes/crate.glb</File>
      <Material slot="Wood" material="material:example.materials.wood" />
      <Material slot="Leaves" material="material:example.materials.leaves" />
    </Content>
  </Mesh>
</Nobara>
```

## Content

| Element | Required | Content |
| --- | --- | --- |
| `<File>` | Yes | The `.glb` file, relative to `Resources/`. It is embedded as it is and read when the game loads. |
| `<Material>` | No, repeated | The material of a slot: attributes `slot` and `material`, an object identifier. |

## What is read from the file

The file is read with SharpGLTF. nRenderer takes the triangles of every
mesh of the default scene of the file (the first scene when none is the
default), with the transforms of their nodes applied, and puts them in one
vertex buffer and one index buffer.

| Attribute | Use | When absent |
| --- | --- | --- |
| `POSITION` | Position | The primitive is refused. |
| `NORMAL` | Normal | Averaged from the triangles. |
| `TEXCOORD_0` | Texture coordinates | (0, 0). |
| `TANGENT` | Tangent, with the sign of the bitangent | Computed from the texture coordinates. |

Only triangles are read; points, lines, and strips are refused.

## Material slots

The primitives are grouped by glTF material, in the order the materials
first appear: each group is one part of the mesh, drawn with one material.
The slot of a part is the name of its glTF material, `#` followed by the
index of a material without a name, or `default` for primitives without
material. The `.nof` gives a material of the engine to each slot it names;
the materials of the file, their colors and textures, are ignored. A slot
without material is drawn as the pipeline chooses.

## Errors

| Code | Cause |
| --- | --- |
| `NREN005` | A file that is not a `.glb` file or that cannot be read, a scene without triangles, a primitive that is not triangles or has no positions, a slot that the file does not have, or a slot given twice. |
| `NRC401` | A material that no module of the build defines. |
