// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Nobara.Core;
using Nobara.Core.Resources;
using Nobara.Items;

namespace Nobara.Blocks;

/// <summary>The block registry of one context.</summary>
internal sealed class BlockRegistry : IBlockRegistry
{
    /// <summary>The object identifier of the air.</summary>
    public const string AirId = "block:nobara.air";

    private readonly Dictionary<string, BlockType> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockTag> _tagsById = new(StringComparer.Ordinal);
    private BlockType[] _types = [];
    private IReadOnlyList<BlockType> _typesView = [];
    private IReadOnlyList<BlockTag> _tagsView = [];
    private int[] _typeOfState = [];
    private BlockOpacity[] _opacity = [];
    private BlockCollision[] _collision = [];
    private float[] _hardness = [];
    private uint[] _rotated = [];
    private ItemComponentType<PlaceableBlock>? _placeable;

    public bool IsFrozen { get; private set; }

    public int MaxStatesPerType { get; private set; } = BlockLayout.DefaultMaxStates;

    public IReadOnlyList<BlockType> Types => Frozen(_typesView);

    public BlockType Air => Frozen(_typesView)[0];

    public int StateCount => _typeOfState.Length;

    public IReadOnlyList<BlockTag> Tags => Frozen(_tagsView);

    public ItemComponentType<PlaceableBlock> Placeable =>
        _placeable ?? throw new InvalidOperationException("The component nobara.blocks.placeable is registered while nobara.blocks initializes.");

    /// <summary>Gets the types by index, without the check of <see cref="Types"/>.</summary>
    internal BlockType[] TypeArray => _types;

    /// <summary>Gets the type index of each state.</summary>
    internal int[] TypeOfState
    {
        [HotPath]
        get => _typeOfState;
    }

    /// <summary>Registers the item component that places a block.</summary>
    public void RegisterPlaceable(ModuleContext module, IItemRegistry items) =>
        _placeable = items.RegisterComponent(module, PlaceableBlock.ComponentName, values => PlaceableBlock.Read(values, this), EqualityComparer<PlaceableBlock>.Default);

    public BlockType Get(string objectId) =>
        TryGet(objectId, out BlockType? type) ? type : throw new KeyNotFoundException($"No block has the identifier '{objectId}'.");

    public bool TryGet(string objectId, [NotNullWhen(true)] out BlockType? type)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        RequireFrozen();
        return _byId.TryGetValue(objectId, out type);
    }

    public bool TryGetTag(string id, [NotNullWhen(true)] out BlockTag? tag)
    {
        ArgumentNullException.ThrowIfNull(id);
        RequireFrozen();
        if (!id.StartsWith('#') || id.Length < 2)
        {
            throw new ArgumentException($"'{id}' is not a tag identifier, such as '#example.wood' or '#block:example.wood'.", nameof(id));
        }

        string name = id[1..];
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (name[..colon] != BlocksModule.ResourceType)
            {
                throw new ArgumentException($"'{id}' is a tag of the registry '{name[..colon]}', not of the blocks.", nameof(id));
            }

            name = name[(colon + 1)..];
        }

        return _tagsById.TryGetValue(name, out tag);
    }

    [HotPath]
    public BlockType TypeOf(uint state) => _types[_typeOfState[Check(state)]];

    [HotPath]
    public BlockOpacity GetOpacity(uint state) => _opacity[Check(state)];

    [HotPath]
    public BlockCollision GetCollision(uint state) => _collision[Check(state)];

    [HotPath]
    public float GetHardness(uint state) => _hardness[Check(state)];

    [HotPath]
    public int GetValue(uint state, StateProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        uint local = Local(state, property);
        return (int)(local / property.Stride % (uint)property.Count);
    }

    [HotPath]
    public uint With(uint state, StateProperty property, int value)
    {
        ArgumentNullException.ThrowIfNull(property);
        if ((uint)value >= (uint)property.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"'{property.Name}' has values from 0 to {property.Count - 1}.");
        }

        uint local = Local(state, property);
        uint current = local / property.Stride % (uint)property.Count;
        return state - (current * property.Stride) + ((uint)value * property.Stride);
    }

    [HotPath]
    public uint Rotate(uint state) => _rotated[Check(state)];

    [HotPath]
    public bool HasTag(uint state, BlockTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.ContainsType(_typeOfState[Check(state)]);
    }

    public string Describe(uint state)
    {
        BlockType type = TypeOf(state);
        if (type.Properties.Count == 0)
        {
            return type.ObjectId;
        }

        var text = new StringBuilder(type.ObjectId).Append('[');
        for (int i = 0; i < type.Properties.Count; i++)
        {
            StateProperty property = type.Properties[i];
            text.Append(i == 0 ? string.Empty : ",").Append(property.Name).Append('=').Append(property.Values[GetValue(state, property)]);
        }

        return text.Append(']').ToString();
    }

    public bool TryParseState(string id, out uint state)
    {
        ArgumentNullException.ThrowIfNull(id);
        state = 0;
        int open = id.IndexOf('[', StringComparison.Ordinal);
        string typeId = open < 0 ? id : id[..open];
        if (!TryGet(typeId, out BlockType? type))
        {
            return false;
        }

        state = type.DefaultState;
        if (open < 0)
        {
            return true;
        }

        string inner = id.EndsWith(']') ? id[(open + 1)..^1] : string.Empty;
        if (!id.EndsWith(']') || inner.Length == 0)
        {
            return id.EndsWith(']');
        }

        (int Property, int Value)[]? values = type.Layout.ParseCondition(inner, out _);
        if (values is null)
        {
            state = 0;
            return false;
        }

        foreach ((int property, int value) in values)
        {
            state = With(state, type.Properties[property], value);
        }

        return true;
    }

    public GenericElement?[] ResolveProperty(ModuleContext reader, FreeProperty property)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(property);
        RequireFrozen();
        if (property.ModuleId != reader.Module.Id)
        {
            throw new ArgumentException($"The free property '{property.Name}' is declared by module '{property.ModuleId}', not by '{reader.Module.Id}'.", nameof(property));
        }

        string shortName = reader.Module.ShortName;
        var values = new GenericElement?[StateCount];
        var problems = new List<BlockProblem>();
        foreach (BlockType type in _types)
        {
            property.TryGet(type.Data, out GenericElement? resourceValue);
            var variantValues = new GenericElement?[type.Layout.Variants.Count];
            for (int v = 0; v < variantValues.Length; v++)
            {
                variantValues[v] = Find(type.Layout.Variants[v].Properties, property.Name, shortName);
                if (variantValues[v] is { } found && !Accepts(property.Type, found))
                {
                    problems.Add(new BlockProblem(
                        type.ObjectId,
                        $"<Variant> #{v + 1}: the free property '{property.Name}' is '{found.Value?.Text ?? "<" + found.Name + ">"}', but module '{reader.Module.Id}' reads it as {property.Type}.",
                        Position(found.Source, type.Data)));
                }
            }

            int[] current = new int[type.Properties.Count];
            for (int local = 0; local < type.StateCount; local++)
            {
                Decompose(type, (uint)local, current);
                GenericElement? value = resourceValue;
                for (int v = 0; v < variantValues.Length; v++)
                {
                    if (variantValues[v] is not null && Matches(type.Layout.Variants[v], current))
                    {
                        value = variantValues[v];
                    }
                }

                values[type.FirstState + (uint)local] = value;
            }
        }

        return problems.Count > 0 ? throw new BlockLoadException(problems) : values;
    }

    /// <summary>Builds the types, their states, and the tags, and freezes the registry.</summary>
    /// <exception cref="BlockLoadException">A block cannot be loaded.</exception>
    public void Freeze(IReadOnlyList<BlockDefinition> definitions, IReadOnlyList<ResourceTag> tags, int maxStatesPerType)
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException("The block registry is already frozen.");
        }

        MaxStatesPerType = maxStatesPerType;
        var problems = new List<BlockProblem>();
        BlockDefinition? air = definitions.FirstOrDefault(d => d.Data.ObjectId == AirId);
        if (air is null)
        {
            throw new BlockLoadException([new BlockProblem(AirId, "the air is missing: the resources of nobara.blocks are not loaded.", default)]);
        }

        var sorted = new List<BlockDefinition> { air };
        sorted.AddRange(definitions.Where(d => d.Data.ObjectId != AirId).OrderBy(d => d.Data.ObjectId, StringComparer.Ordinal));
        var layouts = new BlockLayout?[sorted.Count];
        long total = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            layouts[i] = ReadLayout(sorted[i], problems);
            if (layouts[i] is { } layout)
            {
                total += layout.StateCount;
            }
        }

        if (layouts[0] is { } airLayout)
        {
            CheckAir(airLayout, problems);
        }

        if (total > int.MaxValue)
        {
            problems.Add(new BlockProblem(AirId, $"the types of blocks have {total} states together, above the limit of {int.MaxValue}.", default));
        }

        if (problems.Count > 0)
        {
            throw new BlockLoadException(problems);
        }

        _types = new BlockType[sorted.Count];
        _typeOfState = new int[total];
        _opacity = new BlockOpacity[total];
        _collision = new BlockCollision[total];
        _hardness = new float[total];
        _rotated = new uint[total];
        uint first = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            BlockLayout layout = layouts[i]!;
            var type = new BlockType(sorted[i].Data.ObjectId, i, first, (int)layout.StateCount, sorted[i].Data);
            _types[i] = type;
            _byId[type.ObjectId] = type;
            Apply(type, layout, sorted[i].Data, createProperties: true);
            first += (uint)layout.StateCount;
        }

        _typesView = Array.AsReadOnly(_types);
        ApplyTags(tags);
        IsFrozen = true;
    }

    /// <summary>Applies reloaded resources in place; refuses what changes the states.</summary>
    /// <returns><see langword="true"/> when the tags or the types changed, so the hook tables are rebuilt.</returns>
    public bool Reload(ResourceReload reload, IReadOnlyList<BlockDefinition> definitions, IReadOnlyList<ResourceTag> tags)
    {
        foreach (ResourceData added in reload.Added)
        {
            reload.Refuse(added.ObjectId, "a new type of block changes the states: restart the context (reload the world once worlds exist)");
        }

        foreach (string removed in reload.Removed)
        {
            reload.Refuse(removed, "a removed type of block changes the states: restart the context (reload the world once worlds exist)");
        }

        bool changed = false;
        var byId = definitions.ToDictionary(d => d.Data.ObjectId, StringComparer.Ordinal);
        foreach (ResourceData data in reload.Changed)
        {
            if (!_byId.TryGetValue(data.ObjectId, out BlockType? type) || !byId.TryGetValue(data.ObjectId, out BlockDefinition? definition))
            {
                continue;
            }

            var problems = new List<BlockProblem>();
            BlockLayout? layout = ReadLayout(definition, problems);
            if (layout is not null && type.IsAir)
            {
                CheckAir(layout, problems);
            }

            if (layout is null || problems.Count > 0)
            {
                reload.Refuse(data.ObjectId, string.Join("; ", problems.Select(p => p.ToString())));
                continue;
            }

            if (layout.Signature != type.Layout.Signature)
            {
                reload.Refuse(data.ObjectId, $"the states of the type change ({Difference(type.Layout, layout)}): restart the context (reload the world once worlds exist)");
                continue;
            }

            Apply(type, layout, data, createProperties: false);
            changed = true;
        }

        if (reload.ChangedTags.Count > 0)
        {
            ApplyTags(tags);
            changed = true;
        }

        return changed;
    }

    /// <summary>Gets the values of the properties of a local state of a type, in index order.</summary>
    [HotPath]
    internal static void Decompose(BlockType type, uint local, int[] values)
    {
        IReadOnlyList<StateProperty> properties = type.Properties;
        for (int i = 0; i < values.Length; i++)
        {
            StateProperty property = properties[i];
            values[i] = (int)(local / property.Stride % (uint)property.Count);
        }
    }

    private static bool Matches(VariantLayout variant, int[] values)
    {
        foreach ((int property, int value) in variant.Condition)
        {
            if (values[property] != value)
            {
                return false;
            }
        }

        return true;
    }

    private static GenericElement? Find(GenericElement? block, string name, string shortName)
    {
        if (block is null)
        {
            return null;
        }

        GenericElement? plain = null;
        foreach (GenericElement child in block.Children)
        {
            if (child.Name == name)
            {
                plain ??= child;
            }
            else if (shortName.Length > 0 && child.Name == shortName + "." + name)
            {
                return child;
            }
        }

        return plain;
    }

    private static bool Accepts(PropertyType type, GenericElement value) => type switch
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

    private static SourcePosition Position(SourcePosition position, ResourceData data) =>
        string.IsNullOrEmpty(position.File) ? data.Source : position;

    private static string Difference(BlockLayout before, BlockLayout after)
    {
        var old = before.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var current = after.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
        string? added = after.Properties.Select(p => p.Name).FirstOrDefault(n => !old.ContainsKey(n));
        if (added is not null)
        {
            return $"property '{added}' added";
        }

        string? removed = before.Properties.Select(p => p.Name).FirstOrDefault(n => !current.ContainsKey(n));
        return removed is not null ? $"property '{removed}' removed" : "the values or the order of the properties change";
    }

    private BlockLayout? ReadLayout(BlockDefinition definition, List<BlockProblem> problems)
    {
        var found = new List<LayoutProblem>();
        BlockLayout? layout = BlockLayout.Read(definition.Content, found);
        foreach (LayoutProblem problem in found)
        {
            problems.Add(new BlockProblem(definition.Data.ObjectId, $"{problem.Location}: {problem.Message}.", definition.Data.Source));
        }

        if (layout is not null && layout.StateCount > MaxStatesPerType)
        {
            problems.Add(new BlockProblem(
                definition.Data.ObjectId,
                $"the type has {(layout.StateCount > int.MaxValue ? "more than " + int.MaxValue.ToString(CultureInfo.InvariantCulture) : layout.StateCount.ToString(CultureInfo.InvariantCulture))} states, above blocks.maxStatesPerType, {MaxStatesPerType}; raise the key in engine.nobara, or keep in states only what is discrete and useful to draw the block, and the rest in block data.",
                definition.Data.Source));
            return null;
        }

        return layout;
    }

    private static void CheckAir(BlockLayout layout, List<BlockProblem> problems)
    {
        bool same = layout.StateCount == 1 && layout.Item is null && layout.Variants.Count == 0 && layout.Shape == BlockLayout.DefaultShape
            && layout.Collision == BlockCollision.None && layout.Opacity == BlockOpacity.Opaque && float.IsPositiveInfinity(layout.Hardness);
        if (!same)
        {
            problems.Add(new BlockProblem(AirId, "the air has no shape, no collision, no state property, no variant, and no item, and is unbreakable; an override cannot change its content.", default));
        }
    }

    // Sets the native fields of a type and fills the tables of its states.
    private void Apply(BlockType type, BlockLayout layout, ResourceData data, bool createProperties)
    {
        type.Layout = layout;
        type.Data = data;
        type.Shape = type.IsAir ? string.Empty : layout.Shape;
        type.Collision = layout.Collision;
        type.Opacity = type.IsAir ? BlockOpacity.Transparent : layout.Opacity;
        type.Hardness = layout.Hardness;
        type.Orientations = layout.Orientations;
        type.Footprint = layout.Cells.ToArray();
        type.ItemId = layout.Item is null ? string.Empty : BlocksModule.ItemType + data.ObjectId[BlocksModule.ResourceType.Length..];

        if (createProperties)
        {
            var properties = new StateProperty[layout.Properties.Count];
            uint stride = 1;
            for (int i = properties.Length - 1; i >= 0; i--)
            {
                PropertyLayout p = layout.Properties[i];
                properties[i] = new StateProperty(type, p.Name, p.Kind, p.Values, p.Default, p.Min, stride);
                stride *= (uint)p.Values.Length;
            }

            type.SetProperties(properties);
        }
        else
        {
            for (int i = 0; i < type.Properties.Count; i++)
            {
                type.Properties[i].Default = layout.Properties[i].Default;
            }
        }

        uint defaultState = type.FirstState;
        foreach (StateProperty property in type.Properties)
        {
            defaultState += (uint)property.Default * property.Stride;
        }

        type.DefaultState = defaultState;
        int[] values = new int[type.Properties.Count];
        int[] turned = new int[values.Length];
        for (uint local = 0; local < (uint)type.StateCount; local++)
        {
            uint state = type.FirstState + local;
            Decompose(type, local, values);
            _typeOfState[state] = type.Index;
            BlockOpacity opacity = type.Opacity;
            BlockCollision collision = type.Collision;
            float hardness = type.Hardness;
            foreach (VariantLayout variant in layout.Variants)
            {
                if (Matches(variant, values))
                {
                    opacity = variant.Opacity ?? opacity;
                    collision = variant.Collision ?? collision;
                    hardness = variant.Hardness ?? hardness;
                }
            }

            _opacity[state] = opacity;
            _collision[state] = collision;
            _hardness[state] = hardness;
            _rotated[state] = Turn(type, layout, values, turned);
        }
    }

    // The state after a quarter turn: each property turns its value, and a
    // property that another one turns into takes the turned value of that one.
    private static uint Turn(BlockType type, BlockLayout layout, int[] values, int[] turned)
    {
        for (int i = 0; i < values.Length; i++)
        {
            int[] rotation = layout.Properties[i].ValueRotation!;
            turned[i] = rotation[values[i]];
        }

        for (int i = 0; i < values.Length; i++)
        {
            PropertyLayout property = layout.Properties[i];
            if (property.RotatesTo >= 0)
            {
                turned[property.RotatesTo] = property.ValueRotation![values[i]];
            }
        }

        uint state = type.FirstState;
        for (int i = 0; i < turned.Length; i++)
        {
            state += (uint)turned[i] * type.Properties[i].Stride;
        }

        return state;
    }

    private void ApplyTags(IReadOnlyList<ResourceTag> tags)
    {
        foreach (ResourceTag resolved in tags)
        {
            if (!_tagsById.TryGetValue(resolved.Name, out BlockTag? tag))
            {
                tag = new BlockTag(resolved.Name, _types.Length);
                _tagsById[resolved.Name] = tag;
            }

            var members = new List<BlockType>(resolved.Members.Count);
            foreach (string member in resolved.Members)
            {
                if (_byId.TryGetValue(member, out BlockType? type))
                {
                    members.Add(type);
                }
            }

            tag.SetMembers(members);
        }

        // A tag that lost every member stays known, empty.
        var current = tags.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (BlockTag tag in _tagsById.Values.Where(t => !current.Contains(t.Name)))
        {
            tag.SetMembers([]);
        }

        _tagsView = Array.AsReadOnly(_tagsById.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray());
    }

    [HotPath]
    private uint Check(uint state)
    {
        if (state >= (uint)_typeOfState.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, $"The registry has {_typeOfState.Length} states.");
        }

        return state;
    }

    [HotPath]
    private uint Local(uint state, StateProperty property)
    {
        BlockType type = property.Block;
        uint local = state - type.FirstState;
        if (local >= (uint)type.StateCount)
        {
            throw new ArgumentException($"State {state} is not a state of '{type.ObjectId}', the type of '{property.Name}'.", nameof(state));
        }

        return local;
    }

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
            throw new InvalidOperationException("The blocks are read once nobara.blocks has initialized.");
        }
    }
}
