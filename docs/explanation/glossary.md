# Glossary

This page defines the terms used across the Nobara documentation. Each term
has one meaning; the documentation does not use a second word for it.

## Terms

### Auxiliary project

A project that belongs to a module without being a module: it has no manifest
and its name ends with a reserved suffix (`.Build`, `.Generators`,
`.Analyzers`, `.Scripting`, `.TypeGen`, `.Tests`, or `.Benchmarks`). See
[the SDK page](../../tools/Nobara.Sdk/README.md#project-kinds).

### Backend

An extension of a base module that implements the module's neutral API with
a specific third-party library or platform API, such as a graphics API. The
base module selects a backend at run time. See [Technical
variants](architecture.md#technical-variants).

### Block hook

What happens to blocks, such as a block being broken or used, that modules
subscribe to by type of block, by tag, or for every type. It is distinct from
the event bus: the tables of subscribers are built per type, so a block whose
type has no subscriber costs nothing. See [the nBlocks module
sheet](../../src/nBlocks/README.md#block-hooks).

### Block state

One combination of the values of the state properties of a type of block,
such as `block:example.blocks.lamp[lit=true]`, numbered by an index that is
the same in every context that loads the same blocks. Not to be confused
with a [state](#state) of the application. See [the nBlocks module
sheet](../../src/nBlocks/README.md#block-states).

### Block type

A type of cell of a grid of voxels, defined by a `Block` resource and kept in
the block registry of nBlocks, such as `block:example.blocks.lamp`. The type
describes the blocks; the blocks placed in a world belong to the module that
stores them. See [the `Block` reference](../reference/formats/block.md).

### Build target

A way to build a game, declared in `build.nobara`: a platform, a side, and
the modules it leaves out. [`nobara build`](../reference/cli.md#nobara-build-target)
turns each target into an installed game and an archive.

### Business module

A module that implements a feature of a game, such as a world generator or a
mob. It depends on platform modules and on other business modules. See
[Architecture](architecture.md#overview).

### Chunk

A fixed-size section of a grid of voxels. The voxel module stores the chunks of a grid
and references them from the grid's entity.

### Command buffer

A list of GPU commands recorded by a module, then submitted to the
[graphics device](#graphics-device) in a frame: render passes, pipeline and
resource bindings, draws, dispatches, barriers. The OpenGL backend replays it
on the loop thread when it is submitted. See [the nGraphics module
sheet](../../src/nGraphics/README.md#command-buffers).

### Component

A piece of data attached to an entity in the entity component system.

### Configuration key

A named setting declared by a module in its manifest, with a type, a
default, bounds, and a scope (`user` or `distribution`).

### Content hash

The SHA-256 hash that the resource compiler computes for each resource: its
content, its free properties, its binary files, and the hashes of the
resources it references. See [the `.nof` reference](../reference/formats/nof.md#content-hash).

### Content registry

A platform module that holds the definitions of one kind of game content,
such as blocks or items. It is distinct from the service registry.

### Context

An isolated runtime instance of a game side: client or server. Each context
has its own loop thread and its own service registry, and contexts share no
service. A process can host both, for example a client with an integrated
server.

### Core

The minimal part of the engine that orchestrates every module: it loads
modules, runs the loop, schedules jobs, manages configuration, and provides
the service registry. The core is not a module and knows nothing about the
game world.

### Crash report

A file that the core writes to the logs folder before a context stops on an
error. It names the module, the lifecycle step, and the exception. See [the
core](../../src/Core/README.md#error-policies).

### Debug drawing

Lines, boxes, and spheres that any module draws in the world for a time, to
see what its code does: chunk borders, colliders, paths. On in [development
mode](#development-mode) only. See [the nRenderer module
sheet](../../src/nRenderer/README.md#debug-drawing).

### Debug tools

The windows of [nDebug](#ndebug) for the developer, such as the profiler and
the settings, with their menu bar. F1 opens and closes them; the key
`debug.tools` of the distribution decides whether they are available. See
[the nDebug module sheet](../../src/nDebug/README.md#tools-and-screens).

### Debug window

A window of [nDebug](#ndebug) that a module registers and draws with Dear
ImGui: a [debug tool](#debug-tools), or a screen of the game such as a menu.
See [the nDebug module sheet](../../src/nDebug/README.md#debug-windows).

### Decoder

The code that the module owning a resource type registers with the core to
turn each loaded resource of the type into an object of the module. The core
decodes each resource once per process. See [the core
page](../../src/Core/README.md#loading-resources).

### Depth prepass

A pass that draws the depth of the opaque geometry before any color, so that
the next pass shades each pixel once, for the nearest surface only.
[UNGP](#ungp) runs one, which can be turned off. See [the render
pipelines](render-pipelines.md#why-a-depth-prepass).

### Deferred event

An event that a module queues instead of emitting at once. The event bus hands
it to its handlers after the simulation, on the loop thread. Any thread can
queue one, jobs included. A deferred event cannot be cancelled.

### Development build

A build of a target for development, made with `nobara build --development`:
debug configuration, no ReadyToRun, no archive. Its executable accepts
`--hot-reload`. See [the command-line reference](../reference/cli.md#development-builds).

### Development mode

A way to run the engine with checks that cost time, chosen when the engine
starts (`EngineOptions.DevelopmentMode`). In this version it checks the data
that jobs and tick systems declare, it accepts reloaded resources, and it
turns on [debug drawing](#debug-drawing). See
[the core page](../../src/Core/README.md#declared-accesses).

### Dimension clock

The game time of one dimension: a raw count of game ticks on 64 bits, which
goes up by one at each tick unless the dimension is frozen, and which can
jump forward. It knows no day or season. See [the nGame module
sheet](../../src/nGame/README.md#game-time).

### Display mode

How the [window](#window) occupies the screen: windowed, borderless full
screen, or exclusive full screen at a chosen [video mode](#video-mode). The
player chooses it with the configuration keys of [nFrames](#nframes). See
[its module sheet](../../src/nFrames/README.md#configuration).

### Distribution

A game, as a set of modules declared in a project file and an engine file
and fixed when it is built. The result is a distribution manifest.

### Emergency hook

Code that a module registers to run before a context stops on an error, such
as an emergency save of the world.

### Entity

An object of the game world in the entity component system, identified by
the components attached to it. A grid of voxels is an entity.

### Entity component system

The architecture that represents the game world as entities, components, and
systems. It is provided by a platform module.

### Entry assembly

The assembly of a game that holds its `main()` for each side and embeds its
distribution manifest. `nobara generate` writes its project; it is never
written by hand. See [the command-line tool page](../../tools/Nobara.Cli/README.md#the-entry-assembly).

### Error policy

What the core does when a module throws: stop the context, disable the module
and every module that requires it, or log and continue. The distribution
chooses it for each module; required and platform modules always stop. See
[the core](../../src/Core/README.md#error-policies).

### Event

A `struct` that a module declares to tell other modules that something
happened or is about to happen, such as a player joining. Handlers receive it
by reference, and an event that implements `ICancellable` can be cancelled.
See [the nEvent module sheet](../../src/nEvent/README.md).

### Event bus

The part of a context that delivers events to their handlers, supplied by the
platform module nEvent. It is not used on hot paths. See [the nEvent module
sheet](../../src/nEvent/README.md).

### Event handler

The code that a module subscribes to the event bus for one type of event. A
handler has a priority, and the module receives a handle that removes it. A
handler that throws is logged with its module and the other handlers still
run. Not to be confused with a phase handler, which a module subscribes to a
phase of the loop.

### Event priority

The place of an event handler among the handlers of an event: `Lowest`,
`Low`, `Normal`, `High`, `Highest`, then `Monitor`, which only reads the final
event. Handlers run from the lowest priority to the highest, so the highest
has the last word. Handlers of the same priority run in the load order of
their modules, then in the order they subscribed.

### Extension

A module that adds something to a parent module (a feature, a backend, a
transport, or a client-only part). It has the same level as its parent and
depends on it. See [Extensions](architecture.md#extensions).

### Extractor

Code of a module that copies what it draws to the [render
world](#render-world) at every frame, before the frame is drawn: only what
changed since its previous call. See [the frames of the renderer](render-frames.md#why-a-render-world-filled-by-extraction).

### Fixed phase

A phase of the loop that runs once per tick due rather than once per frame,
so zero, one, or several times in a frame. See [the core
page](../../src/Core/README.md#loop).

### Frame

One pass of the loop of a context: the synchronization point, the start of
the frame, then each phase in order.

### Frame in flight

A frame that the GPU may still be running while the CPU records the next
one. The [graphics device](#graphics-device) keeps up to two by default and
waits, at the start of a frame, for the one that used the same slot, so the
CPU never rewrites data the GPU still reads. See [the nGraphics module
sheet](../../src/nGraphics/README.md#frames-in-flight).

### Free property

A value of a resource that its own type does not declare, written in its
`<Properties>` block for other modules to read, such as `Weight`. See [the
`.nof` reference](../reference/formats/nof.md#free-properties).

### Game time

The time of the simulated world, as opposed to real time: the tick number of
the context and the dimension clocks. It stops during a pause and follows the
time scale. See [the nGame module sheet](../../src/nGame/README.md#game-time).

### Generic representation

The compiled form of XML content whose schema the compiler does not know: an
element with its attributes, children, typed values, and positions. It holds
components, free properties, and module configurations.

### Global event

Something that happens to the whole application, such as a stop request,
which the active state of the state stack decides about first. When the state
does not handle it, its default action applies. See [the nContext module
sheet](../../src/nContext/README.md#global-events).

### Graphics device

The object of nGraphics that talks to the GPU through a
[backend](#backend): it creates GPU resources and pipelines, runs
[command buffers](#command-buffer), and paces the
[frames in flight](#frame-in-flight). See [the nGraphics module
sheet](../../src/nGraphics/README.md).

### Grid

A set of voxels organized in chunks, such as a world dimension. A grid is an
entity. The word *grid* is used for every voxel grid, terrain included.

### HDR

High dynamic range: colors stored as linear floating-point values that may
exceed 1, so that very bright light keeps its difference from bright light.
[UNGP](#ungp) draws the scene in HDR, then converts it to the colors of the
screen by [tonemapping](#tonemapping). See [the render
pipelines](render-pipelines.md#why-the-scene-is-drawn-in-hdr).

### Hot path

Code that runs often enough for allocations or slow constructs to matter,
marked with the `[HotPath]` attribute. Managed allocations, LINQ, capturing
closures, and boxing are forbidden there, and the engine analyzers enforce it.
See [the analyzer reference](../reference/analyzers.md#hot-path-rules).

### Hot reload

Reloading the resources of modules while a development build runs, from
their `.nof` files and the files they reference: the changed module is
compiled again and each context takes the result at its next frame. See [the hot reload page](../../tools/Nobara.HotReload/README.md).

### Input action

What the game reads instead of keys, such as "jump" or "move": a resource of
type `InputAction` that declares its kind of value (button, float, 1D axis,
or 2D axis), its default bindings, and its interaction. The player can change
its bindings. See [the `InputAction` reference](../reference/formats/input-action.md).

### Input binding

A key, an axis, a composite of keys, or a combination of keys that gives the
value of an input action. Keys are named by physical position, such as
`Key.W`. See [the `InputAction` reference](../reference/formats/input-action.md#bindings).

### Input interaction

How a press of the bindings of an input action becomes a press of the action:
press, release, hold, tap, double tap, or toggle. It is declared in the
resource, and the player can change it. See [the `InputAction`
reference](../reference/formats/input-action.md#interactions).

### Input layer

A reader of input in the stack of [nInput](#ninput), such as the debug
overlay, a screen, the heads-up display, or the player controller. Input goes
from the layer of highest priority down; each layer evaluates the actions from
what reaches it, and may consume an input or capture a device. See [the
nInput module sheet](../../src/nInput/README.md#layers).

### Input map

A group of input actions that the game turns on and off together, such as
"on foot" or "vehicle": a resource of type `InputMap`. An action of an
inactive map is not evaluated.

### Input sampler

What reads the input actions of a layer at the fixed tick: each sample
gathers what happened since the previous one, so that no press is lost
between two ticks and the movements of the mouse add up. See [the nInput
module sheet](../../src/nInput/README.md#reading-actions).

### Insertion point

A named place of a [render graph](#render-graph), declared by the [render
pipeline](#render-pipeline), where other modules add [render
passes](#render-pass): after the opaque geometry, before post-processing, or
the user interface. See [the nRenderer module sheet](../../src/nRenderer/README.md#insertion-points).

### Installed game

The folders that a build of a target produces: `bin/` for the runtime, the
core, and the entry assembly; `system/` and `game/` for the modules; `lib/`
for their libraries; the executable at the root. See [the command-line
reference](../reference/cli.md#the-installed-game).

### Interaction kind

A way of using an item that handlers are attached to, such as `Use`,
`UseOnBlock`, `UseOnEntity`, or `Attack`; a module may declare more. Which
input triggers which kind is the business of the game. See [the nItems module
sheet](../../src/nItems/README.md#usage).

### Isolation class

A class marked `[UsesOptionalDependency]` that holds all the code of a module
that uses one of its optional dependencies, so that the module loads when the
dependency is absent. See [the analyzer
reference](../reference/analyzers.md#optional-dependency-rules).

### Item

A type of object that can be held and stored, defined by an `Item` resource
and kept in the item registry of nItems, such as `item:example.food.steak`.
An item describes a type, never an instance: stacks of items belong to the
module that stores them. See [the `Item` reference](../reference/formats/item.md).

### Item component

A piece of data attached to an item, defined in code by a module and given
default values in the `.nof` file of the item, such as the nourishment of a
food. A stack may change the value for itself. Not to be confused with a
[component](#component) of an entity. See [the nItems module
sheet](../../src/nItems/README.md#item-components).

### Job

A unit of work that the core's job system runs on a worker thread: a struct
that implements `IJob`, or `IJobFor` for a parallel job that runs once per
index of a range. Modules never create threads. See [the core
page](../../src/Core/README.md#jobs).

### Job data

The name of a piece of data that jobs read or write, such as the storage of a
component. Jobs declare the job data they touch, so that the job system orders
conflicting jobs, and checks the declarations in development mode.

### Level

The classification of a module declared in its manifest: platform or
business. The core uses it only for load order and for the dependency rule.

### Lever

A control that the core grants to the module that holds the matching
reservation: the tick levers (pause, time scale, tick rate), the lever on
contexts, and the interception of stop requests. See [the core page](../../src/Core/README.md#reserved-phases-and-levers).

### Lifecycle

The ordered steps that a module goes through: discovery, loading,
registration, initialization, start, execution, stop, and release. See
[Lifecycle](architecture.md#lifecycle).

### Lock file

`project.nobara.lock`, which records every module of a game with the version
it resolved to. See [its reference](../reference/formats/project-nobara-lock.md).

### Loop callback

Code that a loop phase runs, marked with the `[LoopCallback]` attribute. It is
never `async`. See [NOB301](../reference/analyzers.md#nob301).

### Loop access

The data that code on the loop thread, such as a tick system, declares it
reads and writes, checked in development mode while the code runs. See [the
core page](../../src/Core/README.md#declared-accesses).

### Loop thread

The thread that runs the loop of a context. There is one per context.

### Manifest

A file that declares what a unit contains. A module manifest declares its
name, version, level, side, dependencies, conflicts, reservations, and
configuration keys. A distribution manifest is compiled from the project
file and the engine file of a distribution.

### Material

A resource of type `Material` that says how a surface is drawn: a
[shader](#shader) and its [variant](#shader-variant), the textures and
parameters it gives to the shader, and a render mode (opaque, cutout, or
transparent). See [the `Material` reference](../reference/formats/material.md).

### Material slot

A part of a [mesh](#mesh) drawn with one material, named after the material
of the glTF file. The `Mesh` resource gives a material to each slot. See [the
`Mesh` reference](../reference/formats/mesh.md#material-slots).

### Mesh

A resource of type `Mesh` that wraps a glTF binary file: static triangles in
one vertex buffer and one index buffer, split in [material
slots](#material-slot). See [the `Mesh` reference](../reference/formats/mesh.md).

### Module

A unit of the engine with a name, a version, a level, and declared
dependencies. A distribution is an assembly of modules. See
[Modules](architecture.md#modules).

### Module configuration

The `<ModuleConfig>` block of the engine file that targets one module, given
to that module in the generic representation for configuration that does not
fit in configuration keys.

### Module entry

The class of a module that implements `IModule`, named by the `[assembly:
ModuleEntry]` attribute and instantiated once per context.

### Module map

The file `obj/nobara/modules.props`, which associates module identifiers with
local projects; `nobara generate` writes it. [Nobara.Sdk](#nobarasdk) turns a
dependency into a project reference when the map names a project for it. See [the SDK
page](../../tools/Nobara.Sdk/README.md#local-modules).

### Module source

Where the core finds the modules of a distribution: one assembly per module in
the `system/` and `game/` folders, or, on platforms without dynamic loading,
modules compiled into the executable.

### nBlocks

The platform module of the block registry: the resource type `Block`, the
block types, their states and tags, the air, and the block hooks. See [its
module sheet](../../src/nBlocks/README.md).

### nContext

The platform module that drives the states of the application: it keeps the
state stack of each context, applies the transitions at the start of a frame,
and hands stop requests to the active state. See [its module
sheet](../../src/nContext/README.md).

### nDebug

The platform module of the debug user interface, on the client: Dear ImGui
drawn by [nRenderer](#nrenderer) over the frame, fed by the input layer of
highest priority, with the [debug windows](#debug-window) of the modules, the
window of the [profiler](#profiler), and the window of the settings. See [its
module sheet](../../src/nDebug/README.md).

### nEvent

The platform module that supplies the event bus of each context: typed events,
priorities, cancellation, synchronous and deferred delivery, and removable
handlers. See [its module sheet](../../src/nEvent/README.md).

### nFrames

The platform module of the window, on the client: the neutral API of the
window, its display modes and events, the raw input events, and the
registry of window backends. See [its module
sheet](../../src/nFrames/README.md).

### nGame

The platform module that owns the simulation tick and game time. It splits
the simulation phase into stages, runs the tick systems in a deterministic
order, keeps pause, time scale, tick rate, and dimension clocks, and runs the
scheduler. See [its module sheet](../../src/nGame/README.md).

### nGraphics

The platform module of the [RHI](#rhi), on the client: the neutral API of the
GPU, its capabilities, the [graphics device](#graphics-device), and the
registry of graphics backends. See [its module sheet](../../src/nGraphics/README.md).

### nInput

The platform module of input, on the client: the resource types
`InputAction` and `InputMap`, the evaluation of the actions from the raw
input of [nFrames](#nframes), the stack of input layers, text input, the mode
of the mouse, the sampling at the fixed tick, and the bindings of the player.
See [its module sheet](../../src/nInput/README.md).

### nItems

The platform module of the item registry: the resource type `Item`, the item
types and their tags, the types of item components, and the usage service
of items, with their handlers and prolonged uses. See [its module
sheet](../../src/nItems/README.md).

### nRenderer

The platform module, on the client, that organizes the rendering of a frame
on top of [nGraphics](#ngraphics): the [render world](#render-world), the
[render graph](#render-graph) of a [view](#view), and [debug
drawing](#debug-drawing). It also defines the resource types `Shader`,
`Material`, `Mesh`, and `Texture`, and creates their GPU objects. See [its
module sheet](../../src/nRenderer/README.md).

### Nobara.Sdk

The MSBuild SDK that reads the manifest of a module and generates its version
and its references. See [the SDK page](../../tools/Nobara.Sdk/README.md).

### Object identifier

The identifier of a defined object, derived from its type, namespace, and
name: `block:example.blocks.dirt`. See [the `.nof` reference](../reference/formats/nof.md#identifiers).

### Optional dependency

A dependency on a module that the dependent module can work without. When
the target is active, it loads before the dependent module.

### Override

A resource marked `override="true"` that changes the resource with the same
identifier, with simple fields and queries. See [the `.nof`
reference](../reference/formats/nof.md#overrides).

### Phase

A named part of the loop of a context, such as input, simulation, or
rendering, declared by the distribution in `engine.nobara`. Modules subscribe
handlers to the phases they need.

### Pipeline state

Everything fixed about a draw or a dispatch, gathered in one immutable
object: the shaders, the vertex layout, the resource layouts, and the
rasterizer, depth, and blend states. A pipeline is created once and bound by
a [command buffer](#command-buffer). See [the nGraphics module
sheet](../../src/nGraphics/README.md#pipelines-and-resource-sets).

### Platform module

A module that provides a reusable foundation for other modules, such as
rendering, voxel storage, or physics. A platform module never depends on a
business module.

### Profiler

The part of the core that measures, for each context, module, phase, and
job, the time spent, the managed memory allocated, and the pauses of the
garbage collector. Any module may read it, turn it on, and reset it; the
executable of a game turns it on with `--profile`. See [the core
page](../../src/Core/README.md#profiler).

### Prolonged use

A use of an item that lasts, such as eating or drawing a bow, started by a
handler and timed by nItems on the dimension clock of its actor: the pause
and a frozen dimension suspend it. An actor has one at a time. See [the
nItems module sheet](../../src/nItems/README.md#prolonged-uses).

### Protected namespace

A namespace that the distribution protects: a module loaded dynamically,
such as a mod, can neither define nor override a resource in it.

### Qualified property

A free property prefixed by the short name of a module, such as
`Motion.Weight`, which replaces the plain property for that module only.

### Raw input event

A key, mouse button, mouse movement, wheel movement, or typed text, as the
window backend received it, before any interpretation. [nFrames](#nframes)
hands the raw input events of each frame to the input module. See [its
module sheet](../../src/nFrames/README.md#raw-input).

### Reload handler

The code that the module that defines a resource type registers to apply
reloaded resources of that type, or to refuse what needs a restart. See [the
core page](../../src/Core/README.md#reloading-resources).

### Render graph

The [render passes](#render-pass) of a frame and the resources they read and
write, declared by a [render pipeline](#render-pipeline) for a
[view](#view). nRenderer removes the passes whose result nothing uses,
creates the [transient resources](#transient-resource), and inserts barriers.
See [the frames of the renderer](render-frames.md#why-a-render-graph).

### Render pass

One step of a [render graph](#render-graph): a raster pass, which draws to
the attachments it declares, or a compute pass. For a raster pass, nRenderer
begins and ends the render pass of the [command buffer](#command-buffer)
around its draws. See [the nRenderer module sheet](../../src/nRenderer/README.md#render-graph).

### Render pipeline

The code that decides what a frame looks like: it declares the [render
passes](#render-pass) of the [render graph](#render-graph) of a
[view](#view). A module registers it with nRenderer; modules that draw never
depend on one. [UNGP](#ungp) is the pipeline of the engine. Not to be
confused with a [pipeline state](#pipeline-state). See [the nRenderer module
sheet](../../src/nRenderer/README.md#pipelines) and [the render
pipelines](render-pipelines.md).

### Render resolution

The size in pixels at which a [render pipeline](#render-pipeline) draws the
scene of a [view](#view): a fraction of its viewport, the render scale. The
pipeline scales the image to the viewport at the end of the frame. See [the
render pipelines](render-pipelines.md#why-the-render-resolution-is-apart-from-the-window).

### Render world

The copy of the scene that nRenderer draws from: objects made of a mesh,
materials, and a transform, kept from frame to frame and changed by
[extractors](#extractor). It keeps the last two states of each object to
interpolate between ticks. See [the nRenderer module sheet](../../src/nRenderer/README.md#render-world-and-extraction).

### Required dependency

A dependency without which a module cannot load. A missing required
dependency is always an error.

### Reservation

The declaration by a module that it holds an exclusive resource, identified
by an opaque name such as a main window. The core guarantees that each name
has a single holder.

### Resource

An object or a file described in a `.nof` file, compiled into the assembly of
its module. See [the `.nof` reference](../reference/formats/nof.md).

### Resource identifier

The identifier of a resource in the resource system: `type://Namespace/Name`,
as in `texture://Example/Textures/Dirt`.

### Resource plugin

A class of a `.Build` project that adds to the resource compiler what the
schema of a resource type cannot declare: validation, derived resources,
binary processing, and schema converters. See [the resource compiler
page](../../tools/Nobara.ResourceCompiler/README.md#plugins).

### Resource type

A kind of resource, such as `Block`, defined by a message of a `.proto`
schema marked with `(nobara.resource_type)`. See [the schema options
reference](../reference/formats/proto-options.md).

### Reversed depth

A depth buffer where the near plane has depth 1 and the far plane depth 0.
With floating-point depth, it keeps precision over long distances. See [the
frames of the renderer](render-frames.md#why-positions-are-relative-to-the-camera).

### RHI

Rendering hardware interface: the layer that hides the graphics API behind a
neutral API modeled on the explicit APIs (immutable pipelines, command
buffers, bound resource sets). In Nobara it is [nGraphics](#ngraphics); see
[the rendering layers](rendering-layers.md).

### Scancode

The physical position of a key on the keyboard, whatever character the
layout gives it. Bindings use scancodes; the name shown to the player follows
the layout.

### Scheduled task

Code that the scheduler of nGame runs later: at a tick of a dimension clock,
after a delay, periodically, or after real time. Tasks are not saved. See
[the nGame module sheet](../../src/nGame/README.md#scheduler).

### Service

An object that a module publishes in the service registry of a context, under
a type, for the modules that depend on it.

### Service registry

The part of the core that maps a service type to the instance published by a
module and enforces that callers only obtain services of their declared
dependencies. There is one per context.

### Settings file

The file `config/settings.xml` in the data folders, where the core keeps the
values of `user` configuration keys that the player chose. See [the core
page](../../src/Core/README.md#configuration-keys).

### Shader

A resource of type `Shader`: GLSL sources, one per stage, compiled to
[SPIR-V](#spir-v) when the module is built, with its variants and the
textures, parameters, and buffers it reads. Distinct from the shader object
of nGraphics, one stage of one variant on the GPU. See [the `Shader`
reference](../reference/formats/shader.md).

### Shader variant

The sources of a [shader](#shader) compiled with more macros, such as
`ALPHA_CUTOUT`; a [material](#material) chooses one by name. See [the
`Shader` reference](../reference/formats/shader.md#variants).

### Side

Where a module runs: client, server, or both.

### SPIR-V

The binary format of the shaders that nGraphics loads. Shaders are written in
GLSL and compiled to SPIR-V ahead of time; the OpenGL backend loads it
through OpenGL 4.6. See [the nGraphics module sheet](../../src/nGraphics/README.md#shaders).

### Stage

A milestone of the simulation tick that orders the tick systems joined to
it: `Intents`, `Schedule`, `World`, `Entities`, `Physics`, `Late`, and the
stages that modules or the distribution insert. A stage holds no code. See
[the nGame module sheet](../../src/nGame/README.md#stages).

### Start-up point

`Engine.Run`, which every `main()` of a game calls: it loads the engine,
runs the loop of the main context, and returns the exit code of the
process.

### State

A step of the path of the application, such as a main menu, a loading screen,
the game, or a pause menu: a class that a module provides, declares in its
manifest under an identifier such as `state:example.menu`, and registers with
nContext. Not to be confused with the state of a module, its data, or with a
[block state](#block-state). See [the
nContext module sheet](../../src/nContext/README.md#states).

### State stack

The states of a context in a stack: the state at the top is the active state,
and the states below it are suspended or keep running. Transitions push, pop,
or replace states at the start of a frame, and the context stops when the
stack becomes empty. See [the nContext module
sheet](../../src/nContext/README.md#the-stack).

### Stop request

A request to stop a context that comes from outside the game: Ctrl+C, the
host of the engine, or the player closing the window, which a module
relays. The module that intercepts stop requests receives it instead, and
nContext hands it to the active state. See [the core
page](../../src/Core/README.md#reserved-phases-and-levers).

### Swapchain

The images shown in the window: the back buffer that a render pass draws to,
presented at the end of each frame. The [graphics device](#graphics-device)
creates it again when the window changes size or gets its surface back. See
[the nGraphics module sheet](../../src/nGraphics/README.md#swapchain).

### Synchronization point

The queue of each context whose callbacks run on its loop thread at the start
of each frame. Code on another thread hands its results to the loop through
it.

### System

Logic that runs on entities with certain components during a tick. Every
system of the entity component system is a tick system.

### Tag

A named group of objects of one registry, such as `#example.wood`, to which
several modules may add members. See [the `.nof` reference](../reference/formats/nof.md#tags).

### Text focus

The input layer that receives typed text, such as a chat field: while a
layer has it, the keyboard reaches no layer below it and triggers no input
action. See [the nInput module sheet](../../src/nInput/README.md#text-input).

### Texture array

A GPU texture made of layers of the same size, read by layer in the shader.
nRenderer places each texture resource in the array of its size and
settings, one layer per texture, instead of an atlas, so mip levels never
blend two textures; a texture is located by its array and its layer. See
[the `Texture` reference](../reference/formats/texture.md#texture-arrays).

### Tick

One step of the fixed-rate simulation. The core runs ticks with an
accumulator; [nGame](#ngame) runs the stages and tick systems inside each
tick. Ticks are numbered from 1 in each context.

### Tick system

Code that runs once per simulation tick, in a stage, registered with nGame
under a qualified name, with its constraints and its declared accesses. All
the code of the tick is made of tick systems. See [the nGame module
sheet](../../src/nGame/README.md#tick-systems).

### Tonemapping

The conversion of the [HDR](#hdr) colors of a scene to the range of the
target, at the end of a frame. The tonemapper of [UNGP](#ungp) keeps
ordinary colors and compresses bright ones toward white without cutting
them. See [the render pipelines](render-pipelines.md#why-this-tonemapping).

### Transient resource

A texture or a buffer of a [render graph](#render-graph) that lives during
one frame only. The graph creates it, and shares one GPU object between
transient resources that no pass uses at the same time. See [the nRenderer
module sheet](../../src/nRenderer/README.md#render-graph).

### UNGP

The Universal Nobara Graphics Pipeline: the [render
pipeline](#render-pipeline) of the engine, meant to run everywhere. It draws
the scene in [HDR](#hdr) at a render resolution apart from the window, with
passes that can be turned off. See [the UNGP module
sheet](../../src/UNGP/README.md).

### Video mode

A resolution in pixels and a refresh rate that a monitor can show. Exclusive
full screen uses one of the video modes of its monitor.

### View

What nRenderer draws a frame of: a camera, a target (the screen or a
texture), a viewport in that target, and the [render
pipeline](#render-pipeline) that draws it. See [the nRenderer module
sheet](../../src/nRenderer/README.md#views-and-cameras).

### Window

The surface of the operating system where the game shows its frames and
receives input. [nFrames](#nframes) creates one window per client, through a
[backend](#backend).

### Worker thread

A thread of the pool that the core creates for the job system, shared by
every context of a process. There is about one per processor, minus the loop
thread.

### World position

A position with the same precision everywhere in the world: whole chunk
coordinates on 32 bits and a position in the chunk, in blocks, in single
precision. See [the nRenderer module sheet](../../src/nRenderer/README.md#positions).
