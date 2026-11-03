// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Entity;

/// <summary>
/// The entity worlds of a context, one per dimension, with the component
/// types and the entity systems they share. Resolve it with
/// <c>context.Resolve&lt;IEntityWorlds&gt;()</c>; there is one per context.
/// </summary>
/// <remarks>
/// <para>
/// The module that owns the dimensions creates and destroys the worlds: a
/// world belongs to a world session, from its <c>SessionStarting</c> at the
/// earliest to its <c>SessionEnded</c> at the latest. In development mode,
/// a world or an entity left after <c>SessionEnded</c> is reported as a leak
/// of the module that created the world.
/// </para>
/// <para>
/// Modules register their components at any time, and their systems while
/// they initialize. Use the service on the loop thread of the context.
/// </para>
/// </remarks>
public interface IEntityWorlds
{
    /// <summary>Gets the worlds, in the order of their dimensions: the order the systems go through them.</summary>
    IReadOnlyList<EntityWorld> Worlds { get; }

    /// <summary>Gets the component types registered so far, in the order of registration.</summary>
    IReadOnlyList<ComponentInfo> Components { get; }

    /// <summary>
    /// Gets the data of the job system that stands for the structure of the
    /// worlds: which entities exist and which components they have. Every
    /// entity system reads it; a system that changes the structure writes it.
    /// Another tick system that creates or destroys entities declares it as
    /// written.
    /// </summary>
    JobData Structure { get; }

    /// <summary>Gets component <c>nobara.entity.transform</c>: where an entity is in the world. Tracked; an entity created with it starts at the origin.</summary>
    ComponentType<WorldTransform> WorldTransform { get; }

    /// <summary>Gets component <c>nobara.entity.local_transform</c>: where a child is relative to its parent.</summary>
    ComponentType<LocalTransform> LocalTransform { get; }

    /// <summary>Gets component <c>nobara.entity.parent</c>: the link from a child to its parent.</summary>
    ComponentType<ParentLink> ParentLink { get; }

    /// <summary>
    /// Creates the world of a dimension. The dimension needs a clock in
    /// <see cref="IGameTime"/>: systems skip the world while the clock is
    /// frozen.
    /// </summary>
    /// <param name="owner">The module that owns the dimensions; it destroys the world, and a leak is charged to it.</param>
    /// <param name="dimension">The dimension.</param>
    /// <returns>The world.</returns>
    /// <exception cref="InvalidOperationException">No world session is starting or active, a system runs, or, on the client, a world exists already.</exception>
    /// <exception cref="ArgumentException">The dimension has a world already, or no clock.</exception>
    /// <remarks>On the client, one world exists at a time: the world of the dimension the player sees.</remarks>
    EntityWorld Create(ModuleContext owner, DimensionId dimension);

    /// <summary>Destroys the world of a dimension, with its entities.</summary>
    /// <param name="owner">The module that created the world.</param>
    /// <param name="dimension">The dimension.</param>
    /// <exception cref="ArgumentException">The dimension has no world, or another module created it.</exception>
    /// <exception cref="InvalidOperationException">A system runs.</exception>
    void Destroy(ModuleContext owner, DimensionId dimension);

    /// <summary>Gets the world of a dimension.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="world">The world, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the dimension has a world.</returns>
    bool TryGet(DimensionId dimension, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EntityWorld? world);

    /// <summary>
    /// Registers a component type: an unmanaged struct, under a name that
    /// files and other modules use. Registration works at any time, including
    /// from an assembly loaded after the start.
    /// </summary>
    /// <typeparam name="T">The C# type of the component: an unmanaged struct, registered once.</typeparam>
    /// <param name="module">The module that registers the component.</param>
    /// <param name="name">The qualified name: the identifier of the module, a dot, and a name, such as <c>example.farm.growth</c>.</param>
    /// <param name="options">Whether changes are tracked; <see langword="null"/> for none.</param>
    /// <param name="defaultValue">The value of the component on a new entity; <see langword="default"/> otherwise.</param>
    /// <returns>The handle, valid in every world of the context.</returns>
    /// <exception cref="ArgumentException">The name does not start with the identifier of the module, or the name or the type is registered already.</exception>
    ComponentType<T> RegisterComponent<T>(ModuleContext module, string name, ComponentOptions? options = null, T defaultValue = default)
        where T : unmanaged;

    /// <summary>
    /// Registers a component type whose initial value in an entity definition
    /// is read by a reader of the module, instead of by its public fields:
    /// for a value that a field cannot hold as written, such as an object
    /// identifier turned into an index.
    /// </summary>
    /// <typeparam name="T">The C# type of the component: an unmanaged struct, registered once.</typeparam>
    /// <param name="module">The module that registers the component.</param>
    /// <param name="name">The qualified name: the identifier of the module, a dot, and a name.</param>
    /// <param name="read">Reads the values that a definition writes for the component.</param>
    /// <param name="options">Whether changes are tracked; <see langword="null"/> for none.</param>
    /// <param name="defaultValue">The value of the component on a new entity; <see langword="default"/> otherwise.</param>
    /// <returns>The handle, valid in every world of the context.</returns>
    /// <exception cref="ArgumentException">The name does not start with the identifier of the module, or the name or the type is registered already.</exception>
    ComponentType<T> RegisterComponent<T>(ModuleContext module, string name, ComponentReader<T> read, ComponentOptions? options = null, T defaultValue = default)
        where T : unmanaged;

    /// <summary>Registers a component type known only by its <see cref="Type"/>, such as a type found in an assembly loaded at run time.</summary>
    /// <param name="module">The module that registers the component.</param>
    /// <param name="name">The qualified name.</param>
    /// <param name="type">The C# type: an unmanaged struct.</param>
    /// <param name="options">Whether changes are tracked; <see langword="null"/> for none.</param>
    /// <returns>The handle, without the C# type; <see cref="TryGetComponent{T}"/> gives the typed one.</returns>
    /// <exception cref="ArgumentException">The type is not an unmanaged struct, the name does not start with the identifier of the module, or the name or the type is registered already.</exception>
    ComponentType RegisterComponent(ModuleContext module, string name, Type type, ComponentOptions? options = null);

    /// <summary>Finds a component type by its qualified name.</summary>
    /// <param name="name">The qualified name.</param>
    /// <param name="component">The handle, or the default value.</param>
    /// <returns><see langword="true"/> when a component has the name.</returns>
    bool TryGetComponent(string name, out ComponentType component);

    /// <summary>Finds a component type by its C# type.</summary>
    /// <typeparam name="T">The C# type.</typeparam>
    /// <param name="component">The handle, or the default value.</param>
    /// <returns><see langword="true"/> when the type is registered.</returns>
    bool TryGetComponent<T>(out ComponentType<T> component)
        where T : unmanaged;

    /// <summary>Gets what is known of a component type.</summary>
    /// <param name="component">The handle.</param>
    /// <returns>The name, type, size, module, and data of the component.</returns>
    /// <exception cref="ArgumentException">The handle is not valid in this context.</exception>
    ComponentInfo GetComponent(ComponentType component);

    /// <summary>
    /// Gets an entity definition of a <c>.nof</c> file, whose entities
    /// <see cref="EntityWorld.Instantiate(EntityDefinition)"/> creates. In
    /// development mode every definition is loaded when the entity module
    /// starts; otherwise each one is loaded the first time it is asked for.
    /// </summary>
    /// <param name="objectId">The object identifier: <c>entity:example.animals.pig</c>.</param>
    /// <returns>The definition; the same object every time.</returns>
    /// <exception cref="KeyNotFoundException">No definition has the identifier.</exception>
    /// <exception cref="EntityLoadException">The definition cannot be loaded.</exception>
    /// <exception cref="InvalidOperationException">The entity module has not started: modules initialize.</exception>
    /// <remarks>Loading allocates: get definitions once, outside hot path code.</remarks>
    EntityDefinition GetDefinition(string objectId);

    /// <summary>Gets an entity definition, if one has the identifier.</summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="definition">The definition, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a definition has the identifier.</returns>
    /// <exception cref="EntityLoadException">The definition cannot be loaded.</exception>
    /// <exception cref="InvalidOperationException">The entity module has not started: modules initialize.</exception>
    bool TryGetDefinition(string objectId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EntityDefinition? definition);

    /// <summary>
    /// Adds a converter: it reads the free properties of each entity
    /// definition when the definition is loaded, and adds or fills components
    /// of the module. Converters run in the load order of their modules.
    /// </summary>
    /// <param name="module">The module that adds the converter; an exception in it follows its error policy.</param>
    /// <param name="converter">The converter.</param>
    /// <returns>The handle; dispose it to remove the converter for the definitions loaded later.</returns>
    /// <exception cref="InvalidOperationException">The entity module has started: add converters while modules initialize.</exception>
    EntityConverterHandle AddConverter(ModuleContext module, EntityConverter converter);

    /// <summary>
    /// Registers an entity system: one tick system of nGame, under the same
    /// name, that runs the system once per world, in the order of the
    /// dimensions, and skips frozen dimensions.
    /// </summary>
    /// <param name="module">The module that registers the system; an exception in it follows its error policy.</param>
    /// <param name="name">The qualified name of the system, unique among the stages and tick systems.</param>
    /// <param name="system">The code to run.</param>
    /// <param name="options">The stage, constraints, query, accesses, and sides of the system.</param>
    /// <returns>The handle; dispose it to remove the system.</returns>
    /// <exception cref="ArgumentException">The name is not qualified by the module or is taken, or a component handle is not valid.</exception>
    /// <exception cref="InvalidOperationException">The order of the tick is resolved already: register systems while the module initializes.</exception>
    EntitySystemHandle AddSystem(ModuleContext module, string name, IEntitySystem system, EntitySystemOptions? options = null);
}
