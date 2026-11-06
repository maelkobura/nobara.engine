# nRenderer

This page is the module sheet of nRenderer, the module that organizes the
rendering of a frame. It describes what the module provides to other modules
and the rules it enforces. Terms are defined in [the
glossary](../../docs/explanation/glossary.md); the reasons for the design are
in [the rendering layers](../../docs/explanation/rendering-layers.md) and [the
frames of the renderer](../../docs/explanation/render-frames.md), and [GPU-driven chunk
geometry](../../docs/explanation/chunk-geometry.md).

## Purpose

nRenderer organizes the frames of the client, between
[nGraphics](../nGraphics/README.md), which talks to the GPU, and the render
pipelines, which decide what a frame looks like:

- it turns the rendering resources of every module into GPU objects: it
  defines the resource types `Shader`, `Material`, `Mesh`, and `Texture`,
  compiles shaders from GLSL to SPIR-V when a module is built, decodes images
  and meshes when the game loads, and places the textures in texture arrays,
  one per size;
- it keeps a render world, which modules fill through extractors, and
  interpolates its objects between the ticks of the simulation;
- it draws a view through the render graph that a pipeline declares, with the
  passes that modules insert at its insertion points, such as the user
  interface;
- it keeps the geometry of chunks on the GPU, in buffers shared by every
  chunk, culls it on the GPU against the view and the depth of the previous
  frame and on the CPU with a visibility graph, sorts its transparent faces,
  and draws it with a few indirect draws, for the passes of a pipeline;
- it draws debug lines, boxes, and spheres for any module, in development
  mode.

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
| `IRenderer` | The service of the frames, resolved with `context.Resolve<IRenderer>()`: the render world, the view, and the registration of extractors, pipelines, and insertions. |
| `RenderWorld`, `RenderObjectId`, `RenderObjectDescription`, `RenderItem` | The objects of the render world, and the parts of the visible objects that a view draws. |
| `IRenderExtractor`, `RenderExtraction` | The code of a module that copies its changes to the render world at every frame. |
| `Camera` | A camera with reversed depth. Positions and transforms are the `WorldPosition` and `WorldTransform` of the core, in `Nobara.Core.Mathematics`. |
| `RenderView`, `ViewRect` | The view: camera, target, viewport, pipeline, and render resolution, with the matrices and items of the frame. |
| `IRenderPipeline`, `RenderGraphBuilder`, `RenderPassBuilder` | A render pipeline and the declaration of its render graph. |
| `RenderGraphTexture`, `RenderGraphBuffer`, `RenderGraphTextureDescription`, `RenderTextureSize`, `RenderBufferAccess` | The resources of a render graph and how passes use them. |
| `IRenderPass`, `RenderPassContext` | The code of a pass, and what it receives when it runs. |
| `IRenderInsertion`, `RenderInsertionPoint`, `RenderInsertionPoints` | Passes that a module adds at a named point of every pipeline. |
| `RenderRegistration` | The handle of a registration; disposing it removes the registration. |
| `RenderGraphStatistics`, `RenderGraphException` | What the last build of the graph produced, and why a graph cannot run. |
| `IDebugDraw` | Debug drawing, resolved with `context.Resolve<IDebugDraw>()`. |
| `ChunkGeometry` | The geometry of chunks on the GPU (`IRenderer.Chunks`, `RenderExtraction.Chunks`): groups and sections, settings, statistics, and the culling, draw, and depth pyramid functions of a pipeline. |
| `ChunkGroupId`, `ChunkSectionId`, `SectionOrigin`, `ChunkSectionGeometry` | A group (a grid), a section (a chunk) and where it is in its group, and the geometry of a section. |
| `ChunkVertex`, `ChunkNormal`, `ChunkLight` | The vertex of chunk geometry, in 16 bytes, and its fields. |
| `ChunkCategory`, `SectionFace`, `SectionConnectivity` | The categories of faces, and which faces of a chunk see each other through it. |
| `ChunkGeometryStatistics` | What the chunk geometry holds, and what the GPU drew of it. |
| `IRenderResources` | The service of the resources, resolved with `context.Resolve<IRenderResources>()`: the texture arrays, textures, shaders, materials, and meshes of every loaded module, by identifier. |
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

### Frames

nRenderer draws a frame in the phase `Render` of the client loop, once per
frame. It calls the extractors, which copy the changes of the frame to the
render world; begins the frame of the GPU; builds the render graph of the view
again when needed; computes the transforms of the visible objects seen from
the camera, with jobs when there are many; runs the passes of the graph; and
ends the frame. The client loop needs a phase `Render` that is not fixed; the
default loop has one.

Without a pipeline, nothing is drawn, and a warning says so once. While the
window is minimized, nothing is drawn either: extraction goes on, so the
render world stays up to date.

### Render world and extraction

The render world is the copy of the scene that nRenderer draws from: objects
made of a mesh, drawn with the materials of its slots or with one material,
at a transform. Objects stay from frame to frame. A module changes the world
only during extraction: it registers an extractor, which nRenderer calls at
every frame, in the order of registration, and which copies what changed.
Outside extraction, changing the world throws `InvalidOperationException`.

```csharp
public void Start(ModuleContext context)
{
    IRenderer renderer = context.Resolve<IRenderer>();
    _extractor = renderer.AddExtractor(context, new CrateExtractor(_crates));
}

private sealed class CrateExtractor(CrateSimulation crates) : IRenderExtractor
{
    public void Extract(RenderExtraction extraction)
    {
        foreach (Crate crate in crates.Spawned())
        {
            crate.RenderId = extraction.World.Create(new RenderObjectDescription(crate.Mesh, crate.Transform));
        }

        if (extraction.IsNewTick)
        {
            foreach (Crate crate in crates.Moved())
            {
                extraction.World.Move(crate.RenderId, crate.Transform);
            }
        }
    }
}
```

`Create` returns a `RenderObjectId`; `Move`, `Teleport`, `SetMesh`,
`SetVisible`, and `Destroy` change the object. An id of a destroyed object
names nothing, even when its slot is reused. An exception of an extractor is
charged to its module, under its error policy, and the other extractors run.

### Interpolation

The simulation runs at a fixed tick, the frames at their own rate. The world
keeps the last two states of each object: an object that `Move` changes is
drawn between its state before the last tick and its new state, by the
fraction of tick that the loop has accumulated (`RenderExtraction.Alpha`).
The frame shows the world one tick late, and motion stays smooth whatever the
frame rate. An object created, changed with `Teleport`, or not moved during
the last tick is drawn at its last transform. Calls to `Move` in the same
tick keep the state before the tick as the previous state. When the client
loop has no fixed phase, nothing is interpolated.

The camera of the view follows the same rules, with `MoveCamera` and
`TeleportCamera`. Use `Teleport` and `TeleportCamera` for what the frame moves
rather than the tick, such as a free camera driven by the mouse.

### Positions

Objects and cameras are placed with the `WorldPosition` and `WorldTransform`
of the core, which the other modules that place things in the world share. A
`WorldPosition` has whole chunk coordinates on 32 bits and a position in the
chunk, in blocks, in single precision; a chunk is 32 blocks along each edge.
Its precision is the same everywhere in the world. nRenderer never sends such
a position to the GPU: it subtracts the position of the camera on the CPU, in
double precision, so the matrices of the frame only hold offsets from the
camera (`WorldTransform.ToMatrix`, `WorldPosition.RelativeTo`).

### Views and cameras

A view is a camera, a target, a viewport in that target, and the pipeline
that draws it. nRenderer draws one view, `IRenderer.MainView`, to the screen
by default; `SetTarget` makes it draw to a texture, `SetScreenTarget` back to
the screen. On the screen, the view follows the size in pixels of the
window. `Pipeline` names the pipeline of the view; without a name, or with a
name that no module registers (with a warning), the first pipeline
registered draws it.

`RenderScale` sets the render resolution as a fraction of the viewport, from
0.1 to 4; the pipeline sets it. Transient textures follow the render
resolution or the viewport (`RenderTextureSize`).

A `Camera` looks along its local -Z axis, with +Y up. Its projection reverses
depth: the near plane has depth 1, the far plane, infinite by default, depth
0. Clear depth to `Camera.ClearDepth` and test with `Camera.DepthCompare`
(greater or equal). While the graph runs, the view gives the camera of the
frame, interpolated, its view and projection matrices, for the aspect ratio
of the viewport, and its items: one `RenderItem` per part of each visible
object, with its material and its matrix relative to the camera.

### Render graph

A pipeline declares the passes of a frame in `IRenderPipeline.Build`, in the
order they run, with what each one reads and writes:

```csharp
public void Build(RenderGraphBuilder graph)
{
    RenderGraphTexture scene = graph.CreateTexture("scene", new RenderGraphTextureDescription(PixelFormat.Rgba16Float));
    RenderGraphTexture depth = graph.CreateTexture("depth", new RenderGraphTextureDescription(PixelFormat.Depth32Float));
    graph.AddRasterPass("opaque", _opaque).Color(scene, LoadAction.Clear).Depth(depth, LoadAction.Clear);
    graph.InsertionPoint(RenderInsertionPoints.BeforePostProcess, scene, depth);
    graph.AddRasterPass("tonemap", _tonemap).Read(scene).Color(graph.Target, LoadAction.DontCare);
    graph.InsertionPoint(RenderInsertionPoints.Ui, graph.Target);
}
```

A raster pass draws inside a render pass that nRenderer begins on the
attachments it declares (`Color`, `Depth`); a compute pass runs outside render
passes. `IRenderPass.Prepare` runs before the render pass begins, for writes
to buffers; `IRenderPass.Execute` records the draws or dispatches.

When the graph is built, nRenderer:

- removes the passes whose result nothing uses: a pass runs when it writes the
  target of the view, an imported resource, or what a later running pass
  reads, or when it calls `KeepAlive`;
- creates the transient textures and buffers, and shares one GPU object
  between transient resources of the same description that no pass uses at
  the same time; it keeps them from one build to the next;
- inserts a barrier before a pass that uses what an earlier pass wrote as
  storage, for the way this pass uses it;
- checks the graphics features that each pass requires (`Require`; a compute
  pass requires `Compute`).

A graph that cannot run is logged as an error, with the pass and its module,
and nothing is drawn until the graph is built again: a pass that reads what
no earlier pass writes, a pass that needs a feature the device lacks, or a
pass that draws to the screen with other attachments. The screen, the target
of a view on the screen, is drawn to with its own depth (`TargetDepth`) or
none, alone, and never read.

The graph is built for the first frame, then again only when the size,
target, viewport, render scale, or pipeline of the view changes, when a
pipeline or an insertion is registered or removed, or when a pipeline calls
`IRenderer.RebuildGraph`. `IRenderer.LastGraph` gives the passes that run,
the culled ones, and the transient resources of the last build.

### Pipelines

A pipeline module registers its pipeline with `IRenderer.RegisterPipeline`.
Its `RequiredFeatures` are checked when it is registered, or when nRenderer
starts for a pipeline registered before. Two pipelines cannot have the same
name. Modules that draw depend on nRenderer, never on a pipeline. The engine
provides one pipeline, [UNGP](../UNGP/README.md).

### Insertion points

A module adds passes at a named point of every pipeline with
`IRenderer.Insert`, such as an engine of user interface or debug drawing.
At each point, the insertions run in the order of their rank, then of their
registration; each one receives the textures of the point. A pipeline
declares each point once, with `InsertionPoint`:

| Point | Where | Textures |
| --- | --- | --- |
| `AfterOpaque` | after opaque and cut-out geometry, before the sky and transparent geometry | scene color and depth |
| `BeforePostProcess` | after all the geometry, before post-processing; debug drawing goes here | scene color and depth |
| `Ui` | after post-processing, on the target of the view; engines of user interface order themselves here by rank | target color, no depth |

An insertion at a point that the pipeline does not declare is not drawn, and
a warning says so once. An exception of an insertion while the graph is
built is charged under the error policy, and its passes are left out; as
everywhere, the core charges an exception to the module whose code is
closest to where it was thrown.

### Debug drawing

`IDebugDraw` draws lines, boxes, and spheres in the world, from any module,
for a duration in seconds of real time, and at least once: a duration of 0
draws the shape at the next frame only.

```csharp
IDebugDraw debug = context.Resolve<IDebugDraw>();
debug.Box(chunkCenter, new Vector3(16), new ColorF(0, 1, 0, 1), duration: 2);
```

Debug drawing is on in development mode only (`ModuleContext.DevelopmentMode`).
Off, every method returns at once, and nRenderer creates no pass and no GPU
object for it. On, nRenderer draws the lines at the insertion point
`BeforePostProcess`, tested against the depth of the scene, with alpha
blending.

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

### Chunk geometry

`ChunkGeometry` keeps the geometry of chunks on the GPU, for a mesher. A
**group** is a grid: a `WorldTransform`, moved with `MoveGroup` (interpolated
between ticks, like an object) or `TeleportGroup`. A **section** is the
geometry of one chunk of a group, at the whole coordinates of the chunk
(`SectionOrigin`, in chunks of 32 blocks): vertices, and three lists of
triangles, opaque, cut-out, and transparent. Like the render world, it
changes during extraction only.

```csharp
public void Extract(RenderExtraction extraction)
{
    ChunkGeometry chunks = extraction.Chunks;
    _terrain ??= chunks.CreateGroup(WorldTransform.Identity);
    foreach (MeshedChunk chunk in _mesher.Finished())
    {
        var geometry = new ChunkSectionGeometry(chunk.Vertices, chunk.Opaque, chunk.Cutout, chunk.Transparent)
        {
            Visibility = chunk.Connectivity,
        };
        if (chunk.Section.IsValid)
        {
            chunks.ReplaceSection(chunk.Section, geometry);
        }
        else
        {
            chunk.Section = chunks.AddSection(_terrain.Value, chunk.Origin, geometry);
        }
    }
}
```

The geometry is copied, then sent to the GPU within `UploadBudget` bytes per
frame (16 MiB by default), in order; a replaced section is drawn with its
previous geometry until then (`IsUploaded`). A group has one section per
origin. `RemoveSection` and `DestroyGroup` stop drawing at once.

Every section shares one vertex buffer and one index buffer. A full buffer
doubles; ranges freed in the middle are reused once no frame in flight reads
them, and when the free space is fragmented beyond
`DefragmentationThreshold`, sections are moved down, within
`DefragmentationBudget` bytes per frame.

#### Vertices

A `ChunkVertex` is 16 bytes: its position in blocks from the corner of the
chunk (from -16 to 48, steps of 1/1024), its texture coordinates (0 to 64, the
same steps), its `TextureLocation` (arrays 0 to 31, layers 0 to 2047), its
`ChunkNormal`, its `ChunkLight` (sky, and red, green, and blue of blocks, 0 to
15.75 in quarters; `ChunkLight.Full` before the light is computed), and its
ambient occlusion (0 to 3). Values outside their range are clamped. The four
words are documented on the type.

#### Culling

Each frame, the CPU lists the candidate sections of the visible groups. With
`VisibilityGraph` (on by default), it searches, from the chunk of the camera,
through the faces of chunks, crossing a section only between faces that its
`SectionConnectivity` joins (`ChunkSectionGeometry.Visibility`,
`SectionConnectivity.All` by default) and never back toward the camera;
chunks without a section are open, and a camera outside the box of the
sections of a group sees them all. The culling pass then tests each candidate
on the GPU against the view and, with `OcclusionCulling` (on by default),
against the depth pyramid of the previous frame. On the first frame, after a
frame without a depth pyramid, or for a section behind the previous camera,
only the view culls. When the render resolution changes, the culling of the
next frame still uses the pyramid of the previous one, with the camera it was
built for, and the pyramid is built again at the new size. The figures of the GPU (`Statistics.Drawn`,
`FrustumCulled`, `OcclusionCulled`) are read without waiting, a few frames
late (`CulledFrame`).

#### Transparent faces

Each section keeps its transparent triangles on the CPU and sorts them from
the farthest to the nearest, in jobs, when the direction from the section to
the camera has turned by `TransparentResortAngle` degrees (5 by default),
and the camera has moved by at least `TransparentResortDistance` blocks (0.5
by default). Sections are drawn from the farthest to the nearest. Cut-out
faces are not sorted.

#### Drawing chunk geometry in a pipeline

A pipeline declares the passes, in the order of the frame; each needs
`ChunkGeometry.RequiredFeatures` (compute, storage buffers, indirect draws,
and their counts in buffers):

```csharp
public void Build(RenderGraphBuilder graph)
{
    ChunkGeometry chunks = _renderer.Chunks;
    chunks.AddCullingPass(graph);
    graph.AddRasterPass("opaque", _opaque).Color(scene, LoadAction.Clear).Depth(depth, LoadAction.Clear);
    chunks.AddDepthPyramidPass(graph, depth);
}
```

In the raster pass, bind a graphics pipeline whose vertex buffers are
`chunks.VertexLayouts` and whose resource layouts hold `chunks.DrawLayout`,
then call `chunks.Draw(context, ChunkCategory.Opaque, slot)` with the slot of
that layout: a few indirect draws for every visible section. The depth
pyramid reads the depth texture: it is sampled, at the render resolution.
Without a culling pass in the frame, `Draw` draws nothing, with a warning
once.

#### Shaders of chunk geometry

A vertex shader includes the shader library of nRenderer:

```glsl
#version 460
#include <nobara/renderer/chunk.glsl>

layout(std140, binding = 0) uniform View { mat4 ViewProjection; };

void main()
{
    NobaraChunkVertex v = nobara_chunkVertex();
    gl_Position = ViewProjection * vec4(nobara_chunkPosition(nobara_ChunkSection, v.position), 1.0);
}
```

The library declares the inputs at locations 0 (the vertex) and 1 (its
section, per instance) and the storage buffer of the groups at binding 7
(`ChunkGeometry.GroupsBinding`). `nobara_decodeChunkVertex` decodes a vertex,
`nobara_chunkPosition` gives a position relative to the camera, computed from
whole chunk coordinates so that it is as precise a billion blocks from the
origin as at the origin, `nobara_chunkNormal` turns a normal with its group,
and `nobara_chunkLight` combines the light of a vertex with a day and night
multiplier.

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
- The client loop has a phase `Render` that runs once per frame.
- Use the services and the objects on the loop thread of the client context,
  debug drawing included.
- A frame allocates no managed memory once stable: the render world, the
  view, the graph, and debug drawing reuse their storage, which grows to the
  largest frame. Building the render graph allocates, so it happens only when
  something changes. Extractors and passes of other modules keep the frame
  without allocation only if they allocate nothing themselves.
- The render world and the camera change during extraction only.
- The GPU objects are created at the start and on hot reload. Looking up an
  object by resource identifier allocates (the identifier is compared
  without regard to case): resolve objects at load, not in hot paths.
- nRenderer keeps no static state; the native library of shaderc is never
  loaded by the game.

## Known limitations

- One view: nothing draws a second view, split screen, or a view to a
  texture with its own rate. The render scale is the only form of render
  resolution.
- Objects of the render world are neither culled nor sorted: every visible
  object is an item of the view, and its transform seen from the camera is
  computed at every frame. Only chunk geometry is culled and sorted.
- From 256 objects on, the transforms are computed by jobs. When the machine
  is busy, the first wait of the loop thread for a job may allocate once in
  the job system of the core.
- No dedicated render thread: everything runs on the loop thread of the
  client context.
- When several ticks run in one frame, a moved object is interpolated from
  its state at the previous extraction, not at the tick before the last.
- The render graph runs its passes in the order they are declared; it does
  not reorder them. Transient resources share a GPU object only when their
  descriptions are identical: memory is not aliased between resources of
  different sizes or formats.
- Barriers cover writes as storage made earlier in the same frame; a pass
  that reads what an imported resource received as storage in an earlier
  frame inserts its own.
- Every color attachment of a raster pass begins with the same load action
  and clear color, as nGraphics allows.
- Debug drawing has no text.
- The `transparent` mode of a material is declared only: no pass draws or
  sorts transparent objects. Transparent faces of chunk geometry are sorted
  and drawn by `ChunkGeometry.Draw`.
- GLSL `#include` reads only the files of the shader library of nRenderer.
- Chunk geometry: the shared buffers never shrink. Transparent faces are
  sorted by the centers of their triangles, which does not order faces that
  cross. The visibility graph covers the box of the sections of a group, one
  chunk wider: a group spread over a huge box is slow to search (turn it off
  with `VisibilityGraph`). The depth pyramid of the previous frame may hide,
  for one frame, a section that the camera uncovers. No debug window shows
  the statistics: nDebug depends on nRenderer, so nRenderer cannot have a
  window of its own; read them with `ChunkGeometry.Statistics`.
- Textures are PNG files only, uploaded uncompressed in RGBA 8 bits; there
  is no animated texture.
- glTF animations, skins, morph targets, and the materials of the file are
  ignored; a mesh is static triangles.
- An override of a `Shader` that changes its sources or its variants
  compiles nothing: the SPIR-V comes from the module that defines the shader.
- A mesh keeps a reference to its materials: a material created again by a
  hot reload keeps its identity, so meshes see it.
