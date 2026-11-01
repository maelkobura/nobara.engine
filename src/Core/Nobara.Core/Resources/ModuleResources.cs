// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;

namespace Nobara.Core.Resources;

/// <summary>
/// The resources as one module sees them in one context: the decoders it
/// registers, the decoded objects, the tags, and the free properties it reads.
/// </summary>
/// <remarks>
/// <para>
/// The core loads every resource once per process, before any context starts:
/// it detects duplicates between modules, applies the overrides, and resolves
/// the tags. The module that owns a resource type registers a decoder during
/// registration; the core decodes every resource of the type before the module
/// initializes, once per process, and every context shares the decoded objects.
/// </para>
/// <para>
/// A module reads the resources of the types defined by itself and by its
/// declared dependencies, like services. Decoders and free properties are
/// declared during registration; resources are read from initialization on.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public void Register(ModuleContext context)
/// {
///     context.Resources.RegisterDecoder("block", data => new BlockType(data.ObjectId, Block.Parser.ParseFrom(data.Content.Span)));
///     _weight = context.Resources.DeclareProperty("Weight", PropertyType.Float);
/// }
///
/// public void Initialize(ModuleContext context)
/// {
///     foreach (BlockType block in context.Resources.GetAll&lt;BlockType&gt;("block"))
///     {
///         // ...
///     }
/// }
/// </code>
/// </example>
public sealed class ModuleResources
{
    private readonly EngineContext _context;
    private readonly ContextResources _resources;
    private readonly ModuleRuntime _module;

    internal ModuleResources(EngineContext context, ContextResources resources, ModuleRuntime module)
    {
        _context = context;
        _resources = resources;
        _module = module;
    }

    /// <summary>Gets the protected namespaces of the distribution.</summary>
    public ProtectedNamespaces ProtectedNamespaces => _resources.Protection;

    /// <summary>
    /// Registers the decoder of a resource type that the module defines: the
    /// code that turns a <see cref="ResourceData"/> into an object of the module.
    /// </summary>
    /// <typeparam name="T">The type of the decoded objects.</typeparam>
    /// <param name="type">The type prefix: <c>block</c>.</param>
    /// <param name="decoder">The decoder. An exception it throws follows the error policy of the module.</param>
    /// <exception cref="InvalidOperationException">
    /// The step is not <see cref="LifecycleStep.Registration"/>, no schema
    /// defines the type, another module defines it, or it already has a decoder.
    /// </exception>
    public void RegisterDecoder<T>(string type, Func<ResourceData, T> decoder)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(decoder);
        RequireStep(registration: true, "Decoders are registered during registration");
        ResourceType definition = _resources.Catalog.Type(type)
            ?? throw new InvalidOperationException($"Module '{_module.Info.Id}' registers a decoder for '{type}', a type that no schema of the loaded modules defines.");
        if (!string.Equals(definition.OwnerId, _module.Info.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Module '{_module.Info.Id}' registers a decoder for '{type}', which module '{definition.OwnerId}' defines; only the module that defines a type decodes it.");
        }

        _resources.Register(_module, type, typeof(T), new ContextResources.Decoder<T>(_resources, type, decoder));
    }

    /// <summary>
    /// Registers what the module does when resources of a type that it defines
    /// change while the game runs, in development mode.
    /// </summary>
    /// <param name="type">The type prefix: <c>block</c>.</param>
    /// <param name="handler">
    /// Applies the changes, or refuses those it cannot apply with
    /// <see cref="ResourceReload.Refuse"/>. It runs on the loop thread of the
    /// context, at the synchronization point of a frame; an exception it throws
    /// follows the error policy of the module.
    /// </param>
    /// <remarks>
    /// A change of a type without a handler is not applied by its module: the
    /// core logs it as a warning. Reloads happen only when a development tool
    /// calls <see cref="Engine.ReloadResources(string, ReadOnlyMemory{byte})"/>; a release build never does.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The step is not <see cref="LifecycleStep.Registration"/>, no schema
    /// defines the type, another module defines it, or it already has a handler.
    /// </exception>
    public void RegisterReloadHandler(string type, Action<ResourceReload> handler)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(handler);
        RequireStep(registration: true, "Reload handlers are registered during registration");
        ResourceType definition = _resources.Catalog.Type(type)
            ?? throw new InvalidOperationException($"Module '{_module.Info.Id}' registers a reload handler for '{type}', a type that no schema of the loaded modules defines.");
        if (!string.Equals(definition.OwnerId, _module.Info.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Module '{_module.Info.Id}' registers a reload handler for '{type}', which module '{definition.OwnerId}' defines; only the module that defines a type reloads it.");
        }

        _resources.RegisterReloadHandler(_module, type, handler);
    }

    /// <summary>Gets the decoded objects of a type, in load order of the modules that define them.</summary>
    /// <typeparam name="T">The type of the decoded objects, as the decoder declares it.</typeparam>
    /// <param name="type">The type prefix: <c>block</c>.</param>
    /// <returns>The objects.</returns>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>, the type is not readable by the module, has no decoder, or its decoder produces another type.</exception>
    public IReadOnlyList<T> GetAll<T>(string type)
        where T : class => Decoded<T>(type);

    /// <summary>Gets the decoded object of a resource.</summary>
    /// <typeparam name="T">The type of the decoded objects.</typeparam>
    /// <param name="objectId">The object identifier: <c>block:example.blocks.dirt</c>.</param>
    /// <param name="value">The object, or <see langword="null"/> when no resource has that identifier.</param>
    /// <returns><see langword="true"/> when the resource exists.</returns>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>, the type is not readable by the module, has no decoder, or its decoder produces another type.</exception>
    public bool TryGet<T>(string objectId, [NotNullWhen(true)] out T? value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(objectId);
        int colon = objectId.IndexOf(':', StringComparison.Ordinal);
        string type = colon > 0 ? objectId[..colon] : objectId;
        T[] all = Decoded<T>(type);
        int index = _resources.Catalog.IndexOf(objectId);
        value = index >= 0 && index < all.Length ? all[index] : null;
        return value is not null;
    }

    /// <summary>Gets the decoded object of a resource.</summary>
    /// <typeparam name="T">The type of the decoded objects.</typeparam>
    /// <param name="objectId">The object identifier: <c>block:example.blocks.dirt</c>.</param>
    /// <returns>The object.</returns>
    /// <exception cref="KeyNotFoundException">No resource has that identifier.</exception>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>, the type is not readable by the module, has no decoder, or its decoder produces another type.</exception>
    public T Get<T>(string objectId)
        where T : class =>
        TryGet(objectId, out T? value) ? value : throw new KeyNotFoundException($"No loaded module defines '{objectId}'.");

    /// <summary>Gets the loaded resources of a type, before decoding, in load order of the modules that define them.</summary>
    /// <param name="type">The type prefix: <c>block</c>.</param>
    /// <returns>The resources; empty when the type has none.</returns>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>, or the type is not readable by the module.</exception>
    public IReadOnlyList<ResourceData> GetData(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        RequireStep(registration: false, "Resources are read from initialization on");
        CheckAccess(type);
        return _resources.Catalog.OfType(type);
    }

    /// <summary>Finds a resource by its object identifier or its resource identifier.</summary>
    /// <param name="id">An object identifier (<c>block:example.blocks.dirt</c>) or a resource identifier (<c>block://Example/Blocks/Dirt</c>, without regard to case).</param>
    /// <returns>The resource, or <see langword="null"/> when no loaded module defines it.</returns>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>, or the type of the resource is not readable by the module.</exception>
    public ResourceData? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        RequireStep(registration: false, "Resources are read from initialization on");
        ResourceData? data = _resources.Catalog.Find(id);
        if (data is not null)
        {
            CheckAccess(data.Type);
        }

        return data;
    }

    /// <summary>Gets the resolved tags of a registry, sorted by identifier.</summary>
    /// <param name="registry">The type of the members: <c>block</c>.</param>
    /// <returns>The tags; empty when the registry has none.</returns>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>, or the type is not readable by the module.</exception>
    public IReadOnlyList<ResourceTag> GetTags(string registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        RequireStep(registration: false, "Tags are read from initialization on");
        CheckAccess(registry);
        return _resources.Catalog.Tags(registry);
    }

    /// <summary>Gets a resolved tag.</summary>
    /// <param name="id">The identifier in full form: <c>#block:example.wood</c>.</param>
    /// <param name="tag">The tag, or <see langword="null"/> when it has no member in any loaded module.</param>
    /// <returns><see langword="true"/> when the tag exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a tag identifier in full form.</exception>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>, or the type is not readable by the module.</exception>
    public bool TryGetTag(string id, [NotNullWhen(true)] out ResourceTag? tag)
    {
        ArgumentNullException.ThrowIfNull(id);
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        if (!id.StartsWith('#') || colon < 2)
        {
            throw new ArgumentException($"'{id}' is not a tag identifier in full form, such as '#block:example.wood'.", nameof(id));
        }

        RequireStep(registration: false, "Tags are read from initialization on");
        CheckAccess(id[1..colon]);
        tag = _resources.Catalog.Tag(id);
        return tag is not null;
    }

    /// <summary>Opens a binary file of a resource, embedded in the assembly of the module that defines the resource.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="path">The path of the file relative to the resources folder, as the content of the resource gives it: <c>Textures/dirt.png</c>.</param>
    /// <returns>A stream over the file; dispose it.</returns>
    /// <exception cref="FileNotFoundException">The module embeds no file at that path.</exception>
    /// <exception cref="InvalidOperationException">The type of the resource is not readable by the module.</exception>
    public Stream OpenFile(ResourceData resource, string path)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(path);
        CheckAccess(resource.Type);
        return _resources.Catalog.OpenFile(resource.ModuleId, path)
            ?? throw new FileNotFoundException($"Module '{resource.ModuleId}' embeds no file '{path}' for '{resource.ObjectId}'.", path);
    }

    /// <summary>
    /// Declares a free property that the module reads, with the type it
    /// expects. The core checks, once the modules are registered, that the
    /// modules that read a property agree on its type and that every value has
    /// that type.
    /// </summary>
    /// <param name="name">The name of the property, in its plain form: <c>Weight</c>.</param>
    /// <param name="type">The type the module expects.</param>
    /// <returns>The handle that reads the property, the qualified form for this module first.</returns>
    /// <exception cref="InvalidOperationException">The step is not <see cref="LifecycleStep.Registration"/>, or the module already declares the property.</exception>
    public FreeProperty DeclareProperty(string name, PropertyType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        RequireStep(registration: true, "Free properties are declared during registration");
        if (name.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' is a qualified name; declare the plain name of the property, such as 'Weight'.", nameof(name));
        }

        var property = new FreeProperty(name, type, _module.Info.Id, _module.Info.ShortName);
        _resources.Declare(_module, property);
        return property;
    }

    private T[] Decoded<T>(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        RequireStep(registration: false, "Resources are read from initialization on");
        CheckAccess(type);
        return _resources.Decoded<T>(type, _module);
    }

    private void CheckAccess(string type)
    {
        ResourceType? definition = _resources.Catalog.Type(type);
        if (definition is null)
        {
            throw new InvalidOperationException($"Module '{_module.Info.Id}' asked for resources of type '{type}', which no schema of the loaded modules defines.");
        }

        if (!string.Equals(definition.OwnerId, _module.Info.Id, StringComparison.Ordinal) && !_module.DependsOn(definition.OwnerId, requiredOnly: false))
        {
            throw new InvalidOperationException(
                $"Module '{_module.Info.Id}' asked for resources of type '{type}' of module '{definition.OwnerId}', which is not one of its declared dependencies.");
        }
    }

    private void RequireStep(bool registration, string rule)
    {
        if ((_context.Step == LifecycleStep.Registration) != registration)
        {
            throw new InvalidOperationException($"Module '{_module.Info.Id}' cannot do this during {EngineStopException.Describe(_context.Step)}. {rule}.");
        }
    }
}

/// <summary>The type that a module expects in a free property.</summary>
public enum PropertyType
{
    /// <summary><c>true</c> or <c>false</c>.</summary>
    Bool,

    /// <summary>A whole number.</summary>
    Int,

    /// <summary>A number; a whole number is accepted.</summary>
    Float,

    /// <summary>Text: any value without child elements.</summary>
    String,

    /// <summary>An object identifier such as <c>sound:example.blocks.dirt_break</c>.</summary>
    ObjectId,

    /// <summary>A resource identifier such as <c>texture://Example/Textures/Dirt</c>.</summary>
    ResourceId,

    /// <summary>A tag identifier such as <c>#example.wood</c>.</summary>
    TagId,

    /// <summary>A vector: child elements <c>X</c>, <c>Y</c>, and optionally <c>Z</c> and <c>W</c>.</summary>
    Vector,

    /// <summary>Any element, read in its generic form.</summary>
    Element,
}

/// <summary>
/// A free property that a module reads. Reading it on a resource gives the
/// value qualified by the short name of the module (<c>Motion.Weight</c>) when
/// the resource has one, and the plain value (<c>Weight</c>) otherwise.
/// </summary>
/// <remarks>
/// Read free properties once, when the resources are loaded, and convert them
/// into the structures of the module; never look them up by name in a hot path.
/// Every member is thread-safe.
/// </remarks>
public sealed class FreeProperty
{
    internal FreeProperty(string name, PropertyType type, string moduleId, string shortName)
    {
        Name = name;
        Type = type;
        ModuleId = moduleId;
        ShortName = shortName;
    }

    /// <summary>Gets the plain name of the property: <c>Weight</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the type the module expects.</summary>
    public PropertyType Type { get; }

    /// <summary>Gets the module that reads the property.</summary>
    public string ModuleId { get; }

    internal string ShortName { get; }

    /// <summary>Reads the property in its generic form.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="value">The value, or <see langword="null"/> when the resource does not have the property.</param>
    /// <returns><see langword="true"/> when the resource has the property; otherwise the module applies its own default.</returns>
    public bool TryGet(ResourceData resource, [NotNullWhen(true)] out GenericElement? value)
    {
        ArgumentNullException.ThrowIfNull(resource);
        value = null;
        foreach (ResourceProperty property in resource.Properties)
        {
            if (property.Name != Name)
            {
                continue;
            }

            if (property.Qualifier.Length == 0)
            {
                value ??= property.Value;
            }
            else if (ShortName.Length > 0 && property.Qualifier == ShortName)
            {
                value = property.Value;
                return true;
            }
        }

        return value is not null;
    }

    /// <summary>Reads a <see cref="PropertyType.Bool"/> property.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="value">The value, or <see langword="false"/> when the resource does not have the property.</param>
    /// <returns><see langword="true"/> when the resource has the property.</returns>
    /// <exception cref="InvalidOperationException">The property is not declared as <see cref="PropertyType.Bool"/>.</exception>
    public bool TryGetBool(ResourceData resource, out bool value)
    {
        Expect(PropertyType.Bool);
        value = false;
        return TryGet(resource, out GenericElement? element) && element.Value?.TryGetBoolean(out value) == true;
    }

    /// <summary>Reads an <see cref="PropertyType.Int"/> property.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="value">The value, or 0 when the resource does not have the property.</param>
    /// <returns><see langword="true"/> when the resource has the property.</returns>
    /// <exception cref="InvalidOperationException">The property is not declared as <see cref="PropertyType.Int"/>.</exception>
    public bool TryGetInt(ResourceData resource, out long value)
    {
        Expect(PropertyType.Int);
        value = 0;
        return TryGet(resource, out GenericElement? element) && element.Value?.TryGetInteger(out value) == true;
    }

    /// <summary>Reads a <see cref="PropertyType.Float"/> property.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="value">The value, or 0 when the resource does not have the property.</param>
    /// <returns><see langword="true"/> when the resource has the property.</returns>
    /// <exception cref="InvalidOperationException">The property is not declared as <see cref="PropertyType.Float"/>.</exception>
    public bool TryGetFloat(ResourceData resource, out double value)
    {
        Expect(PropertyType.Float);
        value = 0;
        return TryGet(resource, out GenericElement? element) && element.Value?.TryGetNumber(out value) == true;
    }

    /// <summary>Reads a <see cref="PropertyType.String"/>, <see cref="PropertyType.ObjectId"/>, <see cref="PropertyType.ResourceId"/>, or <see cref="PropertyType.TagId"/> property.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="value">The text, or <see langword="null"/> when the resource does not have the property.</param>
    /// <returns><see langword="true"/> when the resource has the property.</returns>
    /// <exception cref="InvalidOperationException">The property is not declared with one of those types.</exception>
    public bool TryGetString(ResourceData resource, [NotNullWhen(true)] out string? value)
    {
        if (Type is not (PropertyType.String or PropertyType.ObjectId or PropertyType.ResourceId or PropertyType.TagId))
        {
            throw new InvalidOperationException($"Module '{ModuleId}' declares '{Name}' as {Type}, not as text or an identifier.");
        }

        value = TryGet(resource, out GenericElement? element) ? element.Value?.Text : null;
        return value is not null;
    }

    /// <summary>Reads a <see cref="PropertyType.Vector"/> property.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="value">The components, or <see langword="null"/> when the resource does not have the property.</param>
    /// <returns><see langword="true"/> when the resource has the property.</returns>
    /// <exception cref="InvalidOperationException">The property is not declared as <see cref="PropertyType.Vector"/>.</exception>
    public bool TryGetVector(ResourceData resource, [NotNullWhen(true)] out IReadOnlyList<double>? value)
    {
        Expect(PropertyType.Vector);
        value = TryGet(resource, out GenericElement? element) && element.Vector.Count > 0 ? element.Vector : null;
        return value is not null;
    }

    /// <summary>Tells whether a value has the type of the property.</summary>
    internal static bool Accepts(PropertyType type, GenericElement value) => type switch
    {
        PropertyType.Bool => value.Value?.Kind == GenericValueKind.Boolean,
        PropertyType.Int => value.Value?.Kind == GenericValueKind.Integer,
        PropertyType.Float => value.Value?.Kind is GenericValueKind.Integer or GenericValueKind.Number,
        PropertyType.String => value.Value is not null,
        PropertyType.ObjectId => value.Value?.Kind == GenericValueKind.ObjectId,
        PropertyType.ResourceId => value.Value?.Kind == GenericValueKind.ResourceId,
        PropertyType.TagId => value.Value?.Kind == GenericValueKind.TagId,
        PropertyType.Vector => value.Vector.Count > 0,
        _ => true,
    };

    private void Expect(PropertyType type)
    {
        if (Type != type)
        {
            throw new InvalidOperationException($"Module '{ModuleId}' declares '{Name}' as {Type}, not as {type}.");
        }
    }
}

/// <summary>The decoders and the free properties of one context.</summary>
internal sealed class ContextResources
{
    private readonly Dictionary<string, (ModuleRuntime Owner, Type Result, IDecoder Decoder)> _decoders = new(StringComparer.Ordinal);
    private readonly List<(ModuleRuntime Module, FreeProperty Property)> _properties = [];
    private readonly Dictionary<string, (ModuleRuntime Owner, Action<ResourceReload> Handler)> _reloadHandlers = new(StringComparer.Ordinal);

    public ContextResources(ResourceCatalog catalog, ProtectedNamespaces protection)
    {
        Catalog = catalog;
        Protection = protection;
    }

    /// <summary>Gets the catalog that the context reads; a reload replaces it at the synchronization point.</summary>
    public ResourceCatalog Catalog { get; set; }

    /// <summary>Gets the version of the resources of the engine that the context reads: 0 until the first reload.</summary>
    public int Version { get; set; }

    public ProtectedNamespaces Protection { get; }

    public void Register(ModuleRuntime owner, string type, Type result, IDecoder decoder)
    {
        if (_decoders.TryGetValue(type, out var existing))
        {
            throw new InvalidOperationException($"Module '{owner.Info.Id}' registers a decoder for '{type}', which already has one, registered by module '{existing.Owner.Info.Id}'.");
        }

        _decoders[type] = (owner, result, decoder);
    }

    public void RegisterReloadHandler(ModuleRuntime owner, string type, Action<ResourceReload> handler)
    {
        if (!_reloadHandlers.TryAdd(type, (owner, handler)))
        {
            throw new InvalidOperationException($"Module '{owner.Info.Id}' registers a second reload handler for '{type}'.");
        }
    }

    /// <summary>Gets the handler of a type, or <see langword="null"/>.</summary>
    public (ModuleRuntime Owner, Action<ResourceReload> Handler)? ReloadHandler(string type) =>
        _reloadHandlers.TryGetValue(type, out var entry) && !entry.Owner.Disabled ? entry : null;

    public void Declare(ModuleRuntime module, FreeProperty property)
    {
        if (_properties.Any(p => p.Module == module && p.Property.Name == property.Name))
        {
            throw new InvalidOperationException($"Module '{module.Info.Id}' declares the free property '{property.Name}' twice.");
        }

        _properties.Add((module, property));
    }

    /// <summary>Decodes the resources of the types whose decoders a module registered.</summary>
    public void DecodeOwned(ModuleRuntime module)
    {
        foreach ((ModuleRuntime owner, Type _, IDecoder decoder) in _decoders.Values)
        {
            if (owner == module)
            {
                decoder.DecodeAll();
            }
        }
    }

    public T[] Decoded<T>(string type, ModuleRuntime requester)
    {
        if (!_decoders.TryGetValue(type, out var entry) || entry.Owner.Disabled)
        {
            throw new InvalidOperationException($"Module '{requester.Info.Id}' asked for the decoded resources of '{type}', which no active module decodes in this context.");
        }

        if (entry.Result != typeof(T))
        {
            throw new InvalidOperationException($"Module '{requester.Info.Id}' asked for the resources of '{type}' as '{typeof(T).FullName}', but module '{entry.Owner.Info.Id}' decodes them as '{entry.Result.FullName}'.");
        }

        return ((Decoder<T>)entry.Decoder).All();
    }

    /// <summary>Removes the decoders and properties of a disabled module.</summary>
    public void Withdraw(ModuleRuntime module)
    {
        foreach (string type in _decoders.Where(d => d.Value.Owner == module).Select(d => d.Key).ToList())
        {
            _decoders.Remove(type);
        }

        foreach (string type in _reloadHandlers.Where(d => d.Value.Owner == module).Select(d => d.Key).ToList())
        {
            _reloadHandlers.Remove(type);
        }

        _properties.RemoveAll(p => p.Module == module);
    }

    /// <summary>Checks that the readers of each free property agree on its type, and that every value has it.</summary>
    /// <param name="catalog">The catalog to check, <see cref="Catalog"/> when <see langword="null"/>.</param>
    public List<ResourceProblem> CheckProperties(ResourceCatalog? catalog = null)
    {
        var problems = new List<ResourceProblem>();
        var active = _properties.Where(p => !p.Module.Disabled).ToList();
        foreach (IGrouping<string, (ModuleRuntime Module, FreeProperty Property)> group in active.GroupBy(p => p.Property.Name, StringComparer.Ordinal))
        {
            var first = group.First();
            foreach (var other in group.Where(p => p.Property.Type != first.Property.Type).Take(1))
            {
                problems.Add(new ResourceProblem(
                    ResourceProblemKind.PropertyTypeConflict,
                    other.Module.Info.Id,
                    string.Empty,
                    $"Modules '{first.Module.Info.Id}' and '{other.Module.Info.Id}' read the free property '{group.Key}' as {first.Property.Type} and {other.Property.Type}.",
                    default));
            }
        }

        if (problems.Count > 0 || active.Count == 0)
        {
            return problems;
        }

        foreach (ResourceData resource in (catalog ?? Catalog).All)
        {
            foreach (ResourceProperty property in resource.Properties)
            {
                foreach (var (module, declared) in active.Where(p => p.Property.Name == property.Name
                    && (property.Qualifier.Length == 0 || p.Property.ShortName == property.Qualifier)))
                {
                    if (!FreeProperty.Accepts(declared.Type, property.Value))
                    {
                        string name = property.Qualifier.Length == 0 ? property.Name : property.Qualifier + "." + property.Name;
                        problems.Add(new ResourceProblem(
                            ResourceProblemKind.InvalidProperty,
                            resource.ModuleId,
                            resource.ObjectId,
                            $"The free property '{name}' of '{resource.ObjectId}' is '{property.Value.Value?.Text ?? "<" + property.Value.Name + ">"}', but module '{module.Info.Id}' reads it as {declared.Type}.",
                            !string.IsNullOrEmpty(property.Value.Source.File) ? property.Value.Source : resource.Source));
                    }
                }
            }
        }

        return problems;
    }

    internal interface IDecoder
    {
        void DecodeAll();
    }

    // Reads the catalog of the context each time, so a reload decodes the new resources.
    internal sealed class Decoder<T>(ContextResources resources, string type, Func<ResourceData, T> decode) : IDecoder
    {
        public void DecodeAll() => All();

        public T[] All() => resources.Catalog.Decoded(type, decode);
    }
}
