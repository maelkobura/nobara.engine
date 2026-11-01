# The `Texture` resource

A `Texture` resource defines a texture of
[nRenderer](../../../src/nRenderer/README.md): a PNG file and how it is read.
It wraps the file, so the file has a resource identifier
(`texture://Example/Textures/Dirt`) and an object identifier
(`texture:example.textures.dirt`). Its schema is `Schemas/renderer.proto` of
the module `nobara.renderer`; a module that writes textures declares a
dependency on `nobara.renderer`. The common structure of a resource is
described in [the `.nof` reference](nof.md).

## Example

```xml
<Nobara version="1.0.0" namespace="Example/Textures">
  <Texture>
    <Name>Dirt</Name>                                   <!-- texture:example.textures.dirt -->
    <Content>
      <File>Textures/dirt.png</File>
    </Content>
  </Texture>
  <Texture>
    <Name>Stone_Normal</Name>
    <Content>
      <File>Textures/stone_normal.png</File>
      <Filter>linear</Filter>
      <ColorSpace>linear</ColorSpace>
    </Content>
  </Texture>
</Nobara>
```

## Content

| Element | Required | Content |
| --- | --- | --- |
| `<File>` | Yes | The `.png` file, relative to `Resources/`. It is embedded as it is and decoded when the game loads. |
| `<Filter>` | No | `nearest` (default), as pixel art needs, or `linear`. Mip levels are always blended. |
| `<MipLevels>` | No | The number of mip levels: 0 or absent for the full chain, 1 for none. |
| `<ColorSpace>` | No | `srgb` (default) for colors, or `linear` for data such as normals or roughness. |

The image is from 1 to 4096 texels on each side. It is read in RGBA, 8 bits
per channel; the first row of the file is the row 0 of the texture, as the
texture coordinates of glTF expect.

## Texture arrays

nRenderer places the textures in texture arrays, one layer per texture,
instead of an atlas, so that mip levels never blend two textures. Textures
of the same width, height, number of mip levels, filter, and color space
share an array; within it, they are ordered by object identifier. An array
that would exceed the layers the device supports is split.

The arrays are ordered by width, height, number of mip levels, filter, then
color space. The location of a texture, its array and its layer, is therefore
the same from one start of the game to the next as long as the set of
textures and their settings do not change, and modules can store it in data
such as vertices.

nRenderer computes the mip levels when it loads the texture, with a box
filter; in sRGB, colors are averaged as linear values.

## Errors

| Code | Cause |
| --- | --- |
| `NREN004` | A file that is not a `.png` file or not a PNG image, an image larger than 4096 texels on a side, or an unknown filter or color space. |
| `NRC312` | A file that is missing. |
