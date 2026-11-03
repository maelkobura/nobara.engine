// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Core;
using Nobara.Core.Resources;

namespace Nobara.Entity;

/// <summary>
/// The content of an entity resource, decoded once per process and shared by
/// every context. Each context builds its own <see cref="EntityDefinition"/>
/// from it, with the component types and converters of that context.
/// </summary>
internal sealed class EntityResource
{
    private EntityResource(ResourceData data, GenericElement[] components)
    {
        Data = data;
        Components = components;
    }

    /// <summary>Gets the resource, overrides applied.</summary>
    public ResourceData Data { get; }

    /// <summary>Gets the components and their values, in the generic representation, in the order written.</summary>
    public IReadOnlyList<GenericElement> Components { get; }

    /// <summary>Decodes a resource of type <c>entity</c>.</summary>
    public static EntityResource Decode(ResourceData data)
    {
        Schemas.Entity content = Schemas.Entity.Parser.ParseFrom(data.Content.Span);
        var components = new GenericElement[content.Component.Count];
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = GenericElement.Parse(content.Component[i].Span);
        }

        return new EntityResource(data, components);
    }
}

/// <summary>
/// The entity definitions of one context: they are built from the decoded
/// resources once the modules have initialized, all at once in development
/// mode, and each on first use otherwise.
/// </summary>
internal sealed class EntityDefinitions
{
    /// <summary>The type prefix of the identifiers of entity definitions.</summary>
    public const string ResourceType = "entity";

    private readonly EntityWorlds _worlds;
    private readonly ModuleContext _module;
    private readonly List<(ModuleContext Module, EntityConverterHandle Handle)> _converters = [];
    private readonly Dictionary<string, Slot> _byId = new(StringComparer.Ordinal);
    private readonly List<EntityDefinition> _built = [];
    private bool _started;

    public EntityDefinitions(EntityWorlds worlds, ModuleContext module)
    {
        _worlds = worlds;
        _module = module;
    }

    /// <summary>Gets the definitions built so far, by index.</summary>
    public IReadOnlyList<EntityDefinition> Built => _built;

    private ComponentRegistry Registry => _worlds.Registry;

    public EntityConverterHandle AddConverter(ModuleContext module, EntityConverter converter)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(converter);
        if (_started)
        {
            throw new InvalidOperationException(
                $"Module '{module.Module.Id}' adds an entity converter after the entity module started; add converters while modules initialize.");
        }

        var handle = new EntityConverterHandle(module.Module.Id, _converters.Count, converter);
        _converters.Add((module, handle));
        return handle;
    }

    /// <summary>Takes the decoded resources; in development mode, builds and checks every definition.</summary>
    /// <exception cref="EntityLoadException">In development mode, a definition cannot be loaded.</exception>
    public void Start(IReadOnlyList<EntityResource> resources)
    {
        foreach (EntityResource resource in resources.OrderBy(r => r.Data.ObjectId, StringComparer.Ordinal))
        {
            _byId[resource.Data.ObjectId] = new Slot(resource);
        }

        _started = true;
        if (!_module.DevelopmentMode)
        {
            return;
        }

        var problems = new List<EntityProblem>();
        foreach (Slot slot in _byId.Values)
        {
            BuildSlot(slot, problems);
        }

        if (problems.Count > 0)
        {
            throw new EntityLoadException(problems);
        }
    }

    public EntityDefinition Get(string objectId) =>
        TryGet(objectId, out EntityDefinition? definition)
            ? definition
            : throw new KeyNotFoundException($"No entity definition has the identifier '{objectId}'.");

    public bool TryGet(string objectId, [NotNullWhen(true)] out EntityDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        if (!_started)
        {
            throw new InvalidOperationException("Entity definitions are read once nobara.entity has started, after every module has initialized.");
        }

        definition = null;
        if (!_byId.TryGetValue(objectId, out Slot? slot))
        {
            return false;
        }

        if (slot.Definition is null && slot.Problems is null)
        {
            BuildSlot(slot, []);
        }

        if (slot.Problems is not null)
        {
            throw new EntityLoadException(slot.Problems);
        }

        definition = slot.Definition!;
        return true;
    }

    /// <summary>Applies reloaded definitions: values in place, components once no world exists; refuses the rest.</summary>
    public void Reload(ResourceReload reload, IReadOnlyList<EntityResource> resources)
    {
        if (!_started)
        {
            return;
        }

        var byId = resources.ToDictionary(r => r.Data.ObjectId, StringComparer.Ordinal);
        foreach (ResourceData added in reload.Added)
        {
            if (!byId.TryGetValue(added.ObjectId, out EntityResource? resource))
            {
                continue;
            }

            var slot = new Slot(resource);
            var problems = new List<EntityProblem>();
            if (BuildSlot(slot, problems) is null)
            {
                reload.Refuse(added.ObjectId, string.Join("; ", problems.Select(p => p.ToString())));
                continue;
            }

            _byId[added.ObjectId] = slot;
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed entity definition stays loaded until the context restarts");
        }

        foreach (ResourceData changed in reload.Changed)
        {
            if (!_byId.TryGetValue(changed.ObjectId, out Slot? slot) || !byId.TryGetValue(changed.ObjectId, out EntityResource? resource))
            {
                continue;
            }

            if (slot.Definition is null)
            {
                // Not built yet: it is built from the new resource on first use.
                slot.Resource = resource;
                slot.Problems = null;
                continue;
            }

            var problems = new List<EntityProblem>();
            DefinitionLayout? layout = Build(resource, problems);
            if (layout is null)
            {
                reload.Refuse(changed.ObjectId, string.Join("; ", problems.Select(p => p.ToString())));
                continue;
            }

            EntityDefinition definition = slot.Definition;
            slot.Resource = resource;
            if (layout.SameComponents(definition.Layout) || _worlds.SortedCount == 0)
            {
                definition.Layout = layout;
                definition.Resource = resource.Data;
                definition.Pending = null;
                definition.PendingResource = null;
                continue;
            }

            definition.Pending = layout;
            definition.PendingResource = resource.Data;
            reload.Refuse(
                changed.ObjectId,
                $"its components change ({layout.Difference(definition.Layout, Registry)}): the entities created once the world is reloaded have them; until then, they have the previous components and values");
        }
    }

    /// <summary>Applies the changes of components that waited for the worlds to go; no world exists.</summary>
    public void ApplyPending()
    {
        System.Diagnostics.Debug.Assert(_worlds.SortedCount == 0, "Components of definitions change only while no world exists.");
        foreach (EntityDefinition definition in _built)
        {
            if (definition.Pending is { } layout)
            {
                definition.Layout = layout;
                definition.Resource = definition.PendingResource!;
                definition.Pending = null;
                definition.PendingResource = null;
            }
        }
    }

    private EntityDefinition? BuildSlot(Slot slot, List<EntityProblem> problems)
    {
        int count = problems.Count;
        DefinitionLayout? layout = Build(slot.Resource, problems);
        if (layout is null)
        {
            slot.Problems = problems.Skip(count).ToArray();
            return null;
        }

        var definition = new EntityDefinition(_worlds, _built.Count, slot.Resource.Data, layout);
        _built.Add(definition);
        slot.Definition = definition;
        slot.Problems = null;
        return definition;
    }

    /// <summary>Resolves the components of a definition, reads their values, and runs the converters; <see langword="null"/> when it has problems.</summary>
    private DefinitionLayout? Build(EntityResource resource, List<EntityProblem> problems)
    {
        int count = problems.Count;
        ResourceData data = resource.Data;
        var components = new List<(int Index, byte[] Value)>();
        IReadOnlyList<string> distribution = _module.Distribution.Modules;
        foreach (GenericElement element in resource.Components)
        {
            string name = element.Attribute("ref")?.Value.Text ?? string.Empty;
            bool optional = element.Attribute("optional")?.Value.Text == "true";
            if (!Registry.TryGet(name, out ComponentType type))
            {
                string? owner = FindOwner(name, distribution);
                if (optional && owner is not null && !_module.IsModuleActive(owner))
                {
                    continue;
                }

                string reason = owner is null
                    ? $"the component '{name}' names no module of the distribution"
                    : _module.IsModuleActive(owner)
                        ? $"module '{owner}' registers no entity component '{name}'"
                        : $"the component '{name}' belongs to module '{owner}', which is not active; mark it optional=\"true\" if the entity works without it";
                problems.Add(new EntityProblem(data.ObjectId, reason + ".", Position(element.Source, data)));
                continue;
            }

            if (components.Exists(c => c.Index == type.Index))
            {
                problems.Add(new EntityProblem(data.ObjectId, $"the component '{name}' is declared twice.", Position(element.Source, data)));
                continue;
            }

            try
            {
                components.Add((type.Index, Registry.Codec(type).Read(element, data.ObjectId, name)));
            }
            catch (DefinitionValueException exception)
            {
                problems.Add(new EntityProblem(data.ObjectId, $"component '{name}': {exception.Message}", Position(exception.Position, data)));
            }
        }

        if (problems.Count > count)
        {
            return null;
        }

        Convert(resource, components, problems);
        return problems.Count > count ? null : new DefinitionLayout(Registry, components);
    }

    private void Convert(EntityResource resource, List<(int Index, byte[] Value)> components, List<EntityProblem> problems)
    {
        var conversion = new EntityConversion(Registry, resource.Data, components);
        foreach ((ModuleContext module, EntityConverterHandle handle) in _converters.OrderBy(c => c.Module.LoadOrder).ThenBy(c => c.Handle.Order))
        {
            if (handle.IsRevoked || !_module.IsModuleActive(module.Module.Id))
            {
                continue;
            }

            int errors = conversion.Errors.Count;
            conversion.Open(module.Module.Id);
            try
            {
                handle.Converter(conversion);
            }
            catch (Exception exception) when (exception is not EngineStopException)
            {
                // The policy of the module of the converter applies; what it set is discarded.
                conversion.Close(commit: false);
                module.ReportFault(exception);
                continue;
            }

            conversion.Close(commit: true);
            for (int i = errors; i < conversion.Errors.Count; i++)
            {
                problems.Add(new EntityProblem(resource.Data.ObjectId, $"the converter of module '{module.Module.Id}': {conversion.Errors[i]}", resource.Data.Source));
            }
        }
    }

    /// <summary>Finds the module of a distribution whose identifier, followed by a dot, is the longest prefix of a name.</summary>
    private static string? FindOwner(string name, IEnumerable<string> modules) =>
        modules
            .Where(m => name.Length > m.Length + 1 && name.StartsWith(m, StringComparison.Ordinal) && name[m.Length] == '.')
            .OrderByDescending(m => m.Length)
            .FirstOrDefault();

    private static SourcePosition Position(SourcePosition position, ResourceData data) =>
        string.IsNullOrEmpty(position.File) ? data.Source : position;

    /// <summary>A definition of the context: its resource, and what building it gave.</summary>
    private sealed class Slot(EntityResource resource)
    {
        public EntityResource Resource { get; set; } = resource;

        public EntityDefinition? Definition { get; set; }

        public IReadOnlyList<EntityProblem>? Problems { get; set; }
    }
}
