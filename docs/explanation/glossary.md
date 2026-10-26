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

### Business module

A module that implements a feature of a game, such as a world generator or a
mob. It depends on platform modules and on other business modules. See
[Architecture](architecture.md#overview).

### Chunk

A fixed-size section of a grid of voxels. The voxel module stores the chunks of a grid
and references them from the grid's entity.

### Component

A piece of data attached to an entity in the entity component system.

### Configuration key

A named setting declared by a module in its manifest, with a type, a
default, bounds, and a scope (`user` or `distribution`).

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

### Distribution

A game, as a set of modules declared in a project file and an engine file
and fixed when it is built. The result is a distribution manifest.

### Entity

An object of the game world in the entity component system, identified by
the components attached to it. A grid of voxels is an entity.

### Entity component system

The architecture that represents the game world as entities, components, and
systems. It is provided by a platform module.

### Event bus

A platform module that delivers decoupled notifications between modules. It
is not used on hot paths.

### Extension

A module that adds something to a parent module (a feature, a backend, a
transport, or a client-only part). It has the same level as its parent and
depends on it. See [Extensions](architecture.md#extensions).

### Grid

A set of voxels organized in chunks, such as a world dimension. A grid is an
entity. The word *grid* is used for every voxel grid, terrain included.

### Hot path

Code that runs often enough for allocations or slow constructs to matter,
marked with the `[HotPath]` attribute. Managed allocations, LINQ, capturing
closures, and boxing are forbidden there.

### Job

A unit of work that the core's job system runs on a worker thread. Modules
never create threads.

### Level

The classification of a module declared in its manifest: platform or
business. The core uses it only for load order and for the dependency rule.

### Loop thread

The thread that runs the loop of a context. There is one per context.

### Manifest

A file that declares what a unit contains. A module manifest declares its
name, version, level, side, dependencies, conflicts, reservations, and
configuration keys. A distribution manifest is compiled from the project
file and the engine file of a distribution.

### Module

A unit of the engine with a name, a version, a level, and declared
dependencies. A distribution is an assembly of modules. See
[Modules](architecture.md#modules).

### Module map

The file `obj/nobara/modules.props`, which associates module identifiers with
local projects. [Nobara.Sdk](#nobarasdk) turns a dependency into a project
reference when the map names a project for it. See [the SDK
page](../../tools/Nobara.Sdk/README.md#local-modules).

### nGame

The platform module that owns the simulation tick and game time. It splits
the simulation phase into ordered steps, schedules the systems of the tick,
and keeps pause, time scale, and tick rate.

### Nobara.Sdk

The MSBuild SDK that reads the manifest of a module and generates its version
and its references. See [the SDK page](../../tools/Nobara.Sdk/README.md).

### Optional dependency

A dependency on a module that the dependent module can work without. When
the target is active, it loads before the dependent module.

### Platform module

A module that provides a reusable foundation for other modules, such as
rendering, voxel storage, or physics. A platform module never depends on a
business module.

### Required dependency

A dependency without which a module cannot load. A missing required
dependency is always an error.

### Reservation

The declaration by a module that it holds an exclusive resource, identified
by an opaque name such as a main window. The core guarantees that each name
has a single holder.

### Service registry

The part of the core that maps a service type to the instance published by a
module and enforces that callers only obtain services of their declared
dependencies. There is one per context.

### Side

Where a module runs: client, server, or both.

### System

Logic that runs on entities with certain components during a tick.

### Tick

One step of the fixed-rate simulation. The core runs ticks with an
accumulator; [nGame](#ngame) orders the systems inside each tick.
