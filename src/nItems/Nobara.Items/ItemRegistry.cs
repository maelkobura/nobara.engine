// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Items.Schemas;

namespace Nobara.Items;

/// <summary>The item registry of one context.</summary>
internal sealed class ItemRegistry : IItemRegistry
{
    private readonly List<ItemComponentType> _componentTypes = [];
    private readonly Dictionary<string, ItemComponentType> _componentsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemType> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemTag> _tagsById = new(StringComparer.Ordinal);
    private ItemType[] _items = [];
    private IReadOnlyList<ItemType> _itemsView = [];
    private IReadOnlyList<ItemTag> _tagsView = [];
    private Func<string, bool> _isModuleActive = _ => true;
    private IReadOnlyList<string> _distributionModules = [];

    public bool IsFrozen { get; private set; }

    public int DefaultMaxStack { get; private set; } = 64;

    public IReadOnlyList<ItemType> Items => Frozen(_itemsView);

    public IReadOnlyList<ItemTag> Tags => Frozen(_tagsView);

    public IReadOnlyList<ItemComponentType> ComponentTypes => _componentTypes;

    /// <summary>Gets the number of ticks per second used to convert durations.</summary>
    public double TickRate { get; private set; } = 30;

    public ItemType Get(string objectId) =>
        TryGet(objectId, out ItemType? item) ? item : throw new KeyNotFoundException($"No item has the identifier '{objectId}'.");

    public bool TryGet(string objectId, [NotNullWhen(true)] out ItemType? item)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        RequireFrozen();
        return _byId.TryGetValue(objectId, out item);
    }

    [HotPath]
    public ItemType GetByIndex(int index)
    {
        RequireFrozen();
        ItemType[] items = _items;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, items.Length);
        return items[index];
    }

    public bool TryGetTag(string id, [NotNullWhen(true)] out ItemTag? tag)
    {
        ArgumentNullException.ThrowIfNull(id);
        RequireFrozen();
        if (!id.StartsWith('#') || id.Length < 2)
        {
            throw new ArgumentException($"'{id}' is not a tag identifier, such as '#example.meat' or '#item:example.meat'.", nameof(id));
        }

        string name = id[1..];
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (name[..colon] != ItemsModule.ResourceType)
            {
                throw new ArgumentException($"'{id}' is a tag of the registry '{name[..colon]}', not of the items.", nameof(id));
            }

            name = name[(colon + 1)..];
        }

        return _tagsById.TryGetValue(name, out tag);
    }

    public ItemComponentType<T> RegisterComponent<T>(ModuleContext module, string name, Func<ComponentValues, T> read, IEqualityComparer<T> equality)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(equality);
        if (IsFrozen)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' registers the item component '{name}' after the item registry froze; register component types while modules initialize.");
        }

        QualifiedName.Check(module.Module.Id, name, "item component");
        if (_componentsByName.TryGetValue(name, out ItemComponentType? existing))
        {
            throw new ArgumentException($"The item component '{name}' is already registered by module '{existing.ModuleId}'.", nameof(name));
        }

        var type = new ItemComponentType<T>(name, module.Module.Id, _componentTypes.Count, read, equality);
        _componentTypes.Add(type);
        _componentsByName[name] = type;
        return type;
    }

    public bool TryGetComponentType(string name, [NotNullWhen(true)] out ItemComponentType? type)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _componentsByName.TryGetValue(name, out type);
    }

    /// <summary>Tells whether a component type was registered in this registry.</summary>
    public bool Owns(ItemComponentType type) =>
        _componentsByName.TryGetValue(type.Name, out ItemComponentType? registered) && ReferenceEquals(registered, type);

    /// <summary>Builds the items, converts their components, resolves the tags, and freezes the registry.</summary>
    /// <exception cref="ItemLoadException">An item cannot be loaded.</exception>
    public void Freeze(
        IReadOnlyList<ItemDefinition> definitions,
        IReadOnlyList<ResourceTag> tags,
        int defaultMaxStack,
        double tickRate,
        IReadOnlyList<string> distributionModules,
        Func<string, bool> isModuleActive)
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException("The item registry is already frozen.");
        }

        DefaultMaxStack = defaultMaxStack;
        TickRate = tickRate;
        _distributionModules = distributionModules;
        _isModuleActive = isModuleActive;

        ItemDefinition[] sorted = definitions.OrderBy(d => d.Data.ObjectId, StringComparer.Ordinal).ToArray();
        var items = new ItemType[sorted.Length];
        var problems = new List<ItemProblem>();
        for (int i = 0; i < sorted.Length; i++)
        {
            var item = new ItemType(sorted[i].Data.ObjectId, i, sorted[i].Data);
            if (Build(sorted[i], problems) is { } state)
            {
                state.ApplyTo(item);
            }

            items[i] = item;
            _byId[item.ObjectId] = item;
        }

        if (problems.Count > 0)
        {
            _byId.Clear();
            throw new ItemLoadException(problems);
        }

        _items = items;
        _itemsView = Array.AsReadOnly(items);
        ApplyTags(tags);
        IsFrozen = true;
    }

    /// <summary>Applies reloaded resources in place; refuses what needs a restart.</summary>
    public void Reload(ResourceReload reload, IReadOnlyList<ItemDefinition> definitions, IReadOnlyList<ResourceTag> tags, double tickRate)
    {
        foreach (ResourceData added in reload.Added)
        {
            reload.Refuse(added.ObjectId, "a new item needs a restart of the context");
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed item needs a restart of the context");
        }

        TickRate = tickRate;
        var byId = definitions.ToDictionary(d => d.Data.ObjectId, StringComparer.Ordinal);
        foreach (ResourceData changed in reload.Changed)
        {
            if (!_byId.TryGetValue(changed.ObjectId, out ItemType? item) || !byId.TryGetValue(changed.ObjectId, out ItemDefinition? definition))
            {
                continue;
            }

            var problems = new List<ItemProblem>();
            ItemState? state = Build(definition, problems);
            if (state is null)
            {
                reload.Refuse(changed.ObjectId, string.Join("; ", problems.Select(p => p.ToString())));
                continue;
            }

            state.ApplyTo(item);
        }

        if (reload.ChangedTags.Count > 0)
        {
            ApplyTags(tags);
        }
    }

    private void ApplyTags(IReadOnlyList<ResourceTag> tags)
    {
        foreach (ResourceTag resolved in tags)
        {
            if (!_tagsById.TryGetValue(resolved.Name, out ItemTag? tag))
            {
                tag = new ItemTag(resolved.Name, _items.Length);
                _tagsById[resolved.Name] = tag;
            }

            var members = new List<ItemType>(resolved.Members.Count);
            foreach (string member in resolved.Members)
            {
                if (_byId.TryGetValue(member, out ItemType? item))
                {
                    members.Add(item);
                }
            }

            tag.SetMembers(members);
        }

        // A tag that lost every member stays known, empty.
        var current = tags.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (ItemTag tag in _tagsById.Values.Where(t => !current.Contains(t.Name)))
        {
            tag.SetMembers([]);
        }

        _tagsView = Array.AsReadOnly(_tagsById.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Reads the native fields and converts the components of an item; <see langword="null"/> when it has problems.</summary>
    private ItemState? Build(ItemDefinition definition, List<ItemProblem> problems)
    {
        int count = problems.Count;
        ResourceData data = definition.Data;
        Item content = definition.Content;
        ItemVisual visual = content.Visual?.KindCase switch
        {
            Visual.KindOneofCase.Model => new ItemVisual(ItemVisualKind.Model, content.Visual.Model),
            Visual.KindOneofCase.Sprite => new ItemVisual(ItemVisualKind.Sprite, content.Visual.Sprite),
            _ => ItemVisual.None,
        };

        var entries = new List<(ItemComponentType Type, object Value)>();
        foreach (GenericElement element in definition.Components)
        {
            string name = element.Attribute("ref")?.Value.Text ?? string.Empty;
            bool optional = element.Attribute("optional")?.Value.Text == "true";
            if (!_componentsByName.TryGetValue(name, out ItemComponentType? type))
            {
                string? owner = QualifiedName.FindOwner(name, _distributionModules);
                if (optional && owner is not null && !_isModuleActive(owner))
                {
                    continue;
                }

                string reason = owner is null
                    ? $"the component '{name}' names no module of the distribution"
                    : _isModuleActive(owner)
                        ? $"module '{owner}' registers no item component '{name}'"
                        : $"the component '{name}' belongs to module '{owner}', which is not active; mark it optional=\"true\" if the item works without it";
                problems.Add(new ItemProblem(data.ObjectId, reason + ".", Position(element.Source, data)));
                continue;
            }

            if (entries.Any(e => ReferenceEquals(e.Type, type)))
            {
                problems.Add(new ItemProblem(data.ObjectId, $"the component '{name}' is declared twice.", Position(element.Source, data)));
                continue;
            }

            var values = new ComponentValues(element, data.ObjectId, name, TickRate);
            try
            {
                object value = type.Read(values);
                values.CheckAllRead();
                entries.Add((type, value));
            }
            catch (ItemComponentException exception)
            {
                problems.Add(new ItemProblem(data.ObjectId, $"component '{name}': {exception.Message}", Position(exception.Position, data)));
            }
        }

        if (problems.Count > count)
        {
            return null;
        }

        string displayName = content.DisplayName.Length > 0
            ? content.DisplayName
            : ItemsModule.ResourceType + "." + data.ObjectId[(ItemsModule.ResourceType.Length + 1)..];
        return new ItemState(
            content.MaxStack > 0 ? content.MaxStack : DefaultMaxStack,
            displayName,
            visual,
            data,
            ItemComponents.Create(entries));
    }

    private static SourcePosition Position(SourcePosition position, ResourceData data) =>
        string.IsNullOrEmpty(position.File) ? data.Source : position;

    private IReadOnlyList<T> Frozen<T>(IReadOnlyList<T> values)
    {
        RequireFrozen();
        return values;
    }

    [HotPath]
    private void RequireFrozen()
    {
        if (!IsFrozen)
        {
            throw new InvalidOperationException("The items are read once nobara.items has started, after every module has initialized.");
        }
    }

    /// <summary>The values of an item built from its resource.</summary>
    private sealed record ItemState(int MaxStack, string DisplayName, ItemVisual Visual, ResourceData Data, ItemComponents Components)
    {
        public void ApplyTo(ItemType item)
        {
            item.MaxStack = MaxStack;
            item.DisplayName = DisplayName;
            item.Visual = Visual;
            item.Data = Data;
            item.Components = Components;
        }
    }
}
