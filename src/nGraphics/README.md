# nGraphics

This page is the module sheet of nGraphics, the rendering hardware interface,
and of its backend extension nGraphics.OpenGL. It describes what they provide
to other modules and the rules they enforce. Terms are defined in [the
glossary](../../docs/explanation/glossary.md); the reasons for the design are
in [the rendering layers](../../docs/explanation/rendering-layers.md).

## Purpose

nGraphics gives the client a GPU behind a neutral API on the model of the
explicit APIs: buffers, textures, samplers, SPIR-V shaders, immutable
pipelines, resource sets, command buffers, barriers, frames in flight, and the
swapchain of the window, with capabilities to query. A backend extension
implements it with a graphics API; nGraphics.OpenGL does it with OpenGL 4.6.

## Status

`experimental`

## Level

nGraphics:

<!-- nobara:generated:begin level -->
Platform module, on the client. Requires an active backend on the client.
<!-- nobara:generated:end -->

nGraphics.OpenGL:

<!-- nobara:generated:begin level nobara.graphics.opengl -->
Platform module, on the client. Backend of `nobara.graphics`.
<!-- nobara:generated:end -->

## Dependencies

nGraphics:

<!-- nobara:generated:begin dependencies -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.frames` | `^1.0` | Required |
<!-- nobara:generated:end -->

nGraphics.OpenGL:

<!-- nobara:generated:begin dependencies nobara.graphics.opengl -->
| Module | Versions | Kind |
| --- | --- | --- |
| `nobara.frames` | `^1.0` | Required |
| `nobara.graphics` | `^1.0` | Required |

Third-party libraries: `Silk.NET.OpenGL` 2.23.0.
<!-- nobara:generated:end -->

nGraphics uses the window of [nFrames](../nFrames/README.md): its size, its
events, and the OpenGL contexts it creates. Third-party libraries of
nGraphics.OpenGL: `Silk.NET.OpenGL` (MIT), with `Silk.NET.Core`,
`Silk.NET.Maths`, and `Ultz.Bcl.Half` (MIT). No other module sees their
types.

## Entry points

| Member | Use |
| --- | --- |
| `IGraphicsSystem` | The service of nGraphics, resolved with `context.Resolve<IGraphicsSystem>()`: the device and the name of the backend. |
| `GraphicsDevice` | Creates resources and pipelines, writes and reads them, runs command buffers, and paces frames. |
| `GraphicsCapabilities`, `GraphicsFeatures`, `GraphicsDeviceInfo` | What the device supports, and which GPU and driver it is. |
| `GpuBuffer`, `Texture`, `Sampler`, `Shader` | GPU resources, described by `BufferDescription`, `TextureDescription`, `SamplerDescription`, and `ShaderDescription`. |
| `ResourceLayout`, `ResourceSet`, `BoundResource` | The resources that shaders read, bound together. |
| `GraphicsPipeline`, `ComputePipeline` | Immutable pipelines, described by `GraphicsPipelineDescription` and `ComputePipelineDescription`. |
| `Framebuffer`, `RenderPassDescription` | The attachments a render pass draws to, and how it begins. |
| `CommandBuffer` | Records the commands of a frame. |
| `Swapchain`, `VSyncMode` | The back buffer of the window and its synchronization. |
| `PerFrame<T>` | One copy of a resource per frame in flight. |
| `GpuMemoryStatistics` | The blocks of GPU memory behind the buffers. |
| `GraphicsSettings` | The names of the configuration keys. |
| `IGraphicsBackends`, `IGraphicsBackend`, `GraphicsDeviceOptions`, `GpuMemoryBlock`, `Command`, `CommandKind` | The contract of a graphics backend. |

<!-- nobara:generated:begin states -->
States provided: none.
<!-- nobara:generated:end -->

### Device

nGraphics creates the device when it starts (step 4), after the window and
every backend. A module that needs it uses it in its own `Start` or later.

```csharp
public void Start(ModuleContext context)
{
    GraphicsDevice device = context.Resolve<IGraphicsSystem>().Device;
    device.Capabilities.Require(GraphicsFeatures.Compute, "Pass 'particles'");
}
```

Every `Create` method checks its description before the backend sees it,
and throws `ArgumentException` with the reason; a description that needs a
feature the device lacks throws `NotSupportedException`, which names the
feature. Disposing a resource retires it: the device destroys it once the
frames in flight that may use it are finished. `LiveObjectCount` and
`GetLiveObjects` list the resources not yet destroyed; nGraphics logs those
left when it is released.

### Shaders

Shaders are SPIR-V modules, compiled ahead of time from GLSL. Write them for
OpenGL: explicit `location` and `binding` qualifiers, and no `set`
qualifier. `CreateShader` throws `ShaderLoadException`, with the log of the
driver, when the driver refuses the module or does not find its entry point.

### Pipelines and resource sets

A resource layout lists the resources of a set: uniform buffers, read-only
or read-write storage buffers, sampled textures (a texture and its sampler,
one GLSL sampler), and storage textures. Binding numbers are counted per
class of resource, as in OpenGL: uniform buffers, storage buffers, sampled
textures, and storage textures each have their own numbers, shared by all
the layouts of a pipeline. Uniform ranges start at a multiple of
`UniformBufferAlignment`, storage ranges at a multiple of
`StorageBufferAlignment`.

A graphics pipeline holds the shaders, the vertex layout, the resource
layouts by slot, the topology, and the rasterizer, depth, and blend states,
and names the formats of the attachments it draws to. It can only be bound
in a render pass whose framebuffer has these formats.

### Command buffers

A command buffer records a frame: render passes, pipelines, resource sets,
vertex and index buffers, viewport, scissor, draws, indirect draws,
dispatches, barriers, buffer writes and copies, mipmap generation, and debug
groups and markers for capture tools. Each command is checked as it is
recorded. Bindings do not carry over from one command buffer to the next.

```csharp
CommandBuffer commands = device.CreateCommandBuffer("main");

if (device.BeginFrame())
{
    commands.Begin();
    commands.BeginRenderPass(device.Swapchain.Framebuffer, new RenderPassDescription { ClearColor = ColorF.Black });
    commands.SetPipeline(pipeline);
    commands.SetResourceSet(0, set);
    commands.SetVertexBuffer(0, vertices);
    commands.Draw(3);
    commands.EndRenderPass();
    commands.End();
    device.Submit(commands);
}

device.EndFrame();
```

A barrier makes the writes of earlier shaders, to storage buffers and storage
textures, visible to later commands: for example after a compute pass whose
output feeds draws, use `BarrierScope.VertexInput` or
`BarrierScope.StorageBuffer`.

### Frames in flight

Up to `FramesInFlight` frames run on the GPU while the CPU records the next
one. `BeginFrame` waits until the GPU finished the frame that used the same
slot, `FrameIndex`. Data rewritten every frame needs one copy per slot:
`PerFrame<T>` keeps them and gives the one of the current frame.

### Swapchain

The swapchain is the back buffer of the window. nGraphics follows the events
of the window: when its size in pixels changes, or its surface comes back
after being lost, the swapchain is created again at the start of the next
frame, by the same code. While the window is minimized or its surface is
lost, `BeginFrame` returns `false`: draw nothing to the swapchain, and
`EndFrame` presents nothing.

### Memory

Buffers are ranges of large blocks of GPU memory, 16 MiB each, shared by
buffers up to 4 MiB; a larger buffer, or one created with
`BufferDescription.Dedicated`, has a block of its own. A block left
empty is destroyed, except the last shared one. `Memory` gives the count of
blocks and the bytes used.

### Backends

A backend extension resolves `IGraphicsBackends` during its initialization
and registers an `IGraphicsBackend`. When nGraphics starts, it uses the
backend that `graphics.backend` names, or the first one registered when the
key is `auto` or names a backend that no module registers (with a warning).
A distribution where nGraphics runs without a backend does not build, and
does not start.

| Extension | Backend name | API |
| --- | --- | --- |
| nGraphics.OpenGL (`nobara.graphics.opengl`) | `opengl` | OpenGL 4.6 core, through Silk.NET |

nGraphics.OpenGL creates an OpenGL 4.6 core context on the window and needs a
driver that loads SPIR-V. It creates and changes every object through direct
state access. It replays each command buffer when it is submitted, ends each
frame with a fence, and draws the swapchain to the default framebuffer of
the window. Clip space has depths from 0 to 1, as in the explicit APIs, and
its origin at the bottom left: viewports, scissors, and texture rows count
from the bottom.

## Events

Emitted: none.

Listened to: the events of the window of nFrames, through a subscription to
its service, delivered synchronously at the start of each frame:
`Resized`, `ScaleChanged`, `ModeChanged`, `Minimized`, `Restored`,
`SurfaceLost`, and `SurfaceRecreated`.

## Configuration

<!-- nobara:generated:begin configuration -->
| Key | Scope | Type | Default | Values | Side |
| --- | --- | --- | --- | --- | --- |
| `graphics.backend` | user | string | `auto` |  | client |
| `graphics.framesInFlight` | distribution | int | `2` | 1 to 3 | client |
| `graphics.vsync` | user | enum | `on` | `off`, `on`, `adaptive` | client |
<!-- nobara:generated:end -->

| Key | Effect |
| --- | --- |
| `graphics.backend` | The name of the graphics backend, or `auto` for the first one registered. A change applies at the next start. |
| `graphics.vsync` | `off` (frames show at once and may tear), `on` (each frame waits for the vertical blank), or `adaptive` (waits, unless the frame is late; `on` when the driver lacks it, with a warning). A change applies at the next frame. |
| `graphics.framesInFlight` | The number of frames the GPU may run while the CPU records the next one, from 1 to 3. More frames hide the latency of the GPU but delay what the player sees. Read at start. |

## Resources

None.

## Constraints

- Client only: a dedicated server runs without nGraphics.
- Use the device, its resources, and command buffers on the loop thread of
  the client context: the OpenGL context is current there.
- `BeginFrame`, `Submit`, `EndFrame`, `UpdateBuffer`, and the recording of
  command buffers are hot paths: they allocate no managed memory once the
  command buffers have grown to the size of a frame. Give debug groups
  strings that live on, such as literals.
- `ReadBuffer` and `ReadTexture` wait for the GPU: for tests and tools, not
  for frames.
- Do not rewrite with `UpdateBuffer` a range that a frame in flight still
  reads: keep a copy per slot.

## Known limitations

- OpenGL is the only backend; there is no Vulkan backend and no dedicated
  render thread.
- No HDR output: the swapchain is 8-bit RGBA, not sRGB encoded.
- Buffers are sub-allocated; textures are not, since OpenGL places them
  itself. The debug name of a shared block is that of the block, not of its
  buffers.
- One blend state for all the color attachments of a pipeline; one clear
  color for all the color attachments of a render pass.
- No specialization constants, no push constants, no cube maps, no
  multisampling, no stencil test, and no texture-to-texture copies.
- Storage textures bind level 0 of every layer.
- An indexed indirect draw needs an index buffer created with
  `BufferDescription.Dedicated` and bound at offset 0: OpenGL counts the first
  index of indirect arguments from the start of the buffer object.
- The OpenGL backend keeps the UTF-8 bytes of each distinct debug group or
  marker name: names built at run time grow that cache.
- The OpenGL context is created without the debug flag; debug groups and
  object labels still reach capture tools.
- A device creates no GPU fence between submissions of the same frame: the
  order of OpenGL keeps them in sequence.
- Hardware ray tracing is a capability that no backend reports yet.
- The tests of nGraphics.OpenGL need an OpenGL 4.6 driver that loads SPIR-V,
  and a window: they are in the category `GPU`, which the public CI does not
  run.
