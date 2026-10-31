// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using Nobara.Core;
using Nobara.Blocks.Schemas;

namespace Nobara.Blocks;

/// <summary>
/// What the content of a <c>Block</c> resource declares, read and checked:
/// native fields, state properties in index order, footprint, variants, and
/// the generated item. The plugin of the type runs it when a module is built,
/// and the registry runs it again when the blocks load, overrides applied.
/// </summary>
internal sealed class BlockLayout
{
    public const string DefaultShape = "shape:nobara.cube";
    public const string OrientationProperty = "orientation";
    public const string PartProperty = "part";

    /// <summary>The default of <c>blocks.maxStatesPerType</c>.</summary>
    public const int DefaultMaxStates = 1024;

    // The limit of a footprint along each axis, origin included.
    private const int MaxExtent = 8;

    // The most values of one <Int>; far above any sensible cap of states per type.
    private const int MaxIntValues = 65536;

    private BlockLayout()
    {
    }

    public string Shape { get; private set; } = DefaultShape;

    public BlockCollision Collision { get; private set; }

    public BlockOpacity Opacity { get; private set; }

    /// <summary>Gets the hardness; <see cref="float.PositiveInfinity"/> for <c>unbreakable</c>.</summary>
    public float Hardness { get; private set; } = 1;

    public BlockOrientations Orientations { get; private set; }

    /// <summary>Gets the cells, origin first.</summary>
    public List<BlockCell> Cells { get; } = [new BlockCell(0, 0, 0)];

    /// <summary>Gets the properties in index order: engine properties, then each kind in the order written.</summary>
    public List<PropertyLayout> Properties { get; } = [];

    public List<VariantLayout> Variants { get; } = [];

    /// <summary>Gets the generated item, or <see langword="null"/> without <c>&lt;CreateItem&gt;</c>.</summary>
    public ItemLayout? Item { get; private set; }

    /// <summary>Gets the number of states, saturated above <see cref="int.MaxValue"/>.</summary>
    public long StateCount { get; private set; } = 1;

    /// <summary>Gets a text that two layouts share exactly when they have the same states with the same indices.</summary>
    public string Signature
    {
        get
        {
            var text = new StringBuilder();
            foreach (PropertyLayout property in Properties)
            {
                text.Append(property.Kind).Append(':').Append(property.Name).Append('=').AppendJoin(',', property.Values).Append(';');
            }

            return text.ToString();
        }
    }

    /// <summary>Reads the content of a block; the problems are added to <paramref name="problems"/>.</summary>
    /// <returns>The layout, or <see langword="null"/> when it has problems.</returns>
    public static BlockLayout? Read(Block content, List<LayoutProblem> problems)
    {
        int before = problems.Count;
        var layout = new BlockLayout();
        var reader = new Reader(layout, problems);
        reader.Natives(content);
        reader.Footprint(content.Footprint);
        reader.States(content.States);
        if (problems.Count == before)
        {
            reader.Rotations();
            reader.Variants(content.Variants);
            reader.Item(content.CreateItem);
            layout.StateCount = Count(layout.Properties);
        }

        return problems.Count == before ? layout : null;
    }

    /// <summary>Parses a condition such as <c>lit=true,age=7</c>: property index and value index pairs.</summary>
    /// <returns>The pairs, or <see langword="null"/> with the reason in <paramref name="error"/>.</returns>
    public (int Property, int Value)[]? ParseCondition(string text, out string error)
    {
        error = string.Empty;
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (text.Trim().Length == 0)
        {
            error = "the condition is empty; write property=value pairs separated by commas, such as 'lit=true'";
            return null;
        }

        var result = new (int, int)[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int equals = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                error = $"'{parts[i]}' is not a property=value pair";
                return null;
            }

            string name = parts[i][..equals].Trim();
            string value = parts[i][(equals + 1)..].Trim();
            int property = Properties.FindIndex(p => p.Name == name);
            if (property < 0)
            {
                error = Properties.Count == 0
                    ? $"'{name}' is not a state property; the block has none"
                    : $"'{name}' is not a state property; the properties are {string.Join(", ", Properties.Select(p => "'" + p.Name + "'"))}";
                return null;
            }

            if (result.Take(i).Any(r => r.Item1 == property))
            {
                error = $"the property '{name}' appears twice";
                return null;
            }

            int index = Properties[property].IndexOf(value);
            if (index < 0)
            {
                error = $"'{value}' is not a value of '{name}'; the values are {Properties[property].Describe()}";
                return null;
            }

            result[i] = (property, index);
        }

        return result;
    }

    private static long Count(List<PropertyLayout> properties)
    {
        long count = 1;
        foreach (PropertyLayout property in properties)
        {
            count = Math.Min(count * property.Values.Length, (long)int.MaxValue + 1);
        }

        return count;
    }

    /// <summary>Reads the parts of the content, reporting each problem where it is written.</summary>
    private sealed class Reader(BlockLayout layout, List<LayoutProblem> problems)
    {
        public void Natives(Block content)
        {
            if (content.Shape.Length > 0)
            {
                if (!IsShapeId(content.Shape))
                {
                    Error(LayoutProblem.InvalidShape, $"'{content.Shape}' is not a shape: an object identifier such as 'shape:nobara.cube'", new LayoutLocation("Shape"));
                }
                else
                {
                    layout.Shape = content.Shape;
                }
            }

            layout.Collision = ReadCollision(content.Collision, new LayoutLocation("Collision")) ?? BlockCollision.Shape;
            layout.Opacity = ReadOpacity(content.Opacity, new LayoutLocation("Opacity")) ?? BlockOpacity.Opaque;
            layout.Hardness = ReadHardness(content.Hardness, new LayoutLocation("Hardness")) ?? 1;
            if (!Blocks.Orientations.TryParse(content.Orientations, out BlockOrientations orientations))
            {
                Error(LayoutProblem.InvalidOrientations, $"'{content.Orientations}' is not a set of orientations: 'none', 'horizontal', 'facing', or 'full'", new LayoutLocation("Orientations"));
            }

            layout.Orientations = orientations < 0 ? BlockOrientations.None : orientations;
        }

        public void Footprint(Schemas.Footprint? footprint)
        {
            if (footprint is null)
            {
                return;
            }

            for (int i = 0; i < footprint.Cell.Count; i++)
            {
                Cell cell = footprint.Cell[i];
                var at = new LayoutLocation("Footprint/Cell", i);
                var value = new BlockCell(cell.X, cell.Y, cell.Z);
                if (value == default)
                {
                    Error(LayoutProblem.InvalidFootprint, "the origin (0, 0, 0) is always occupied; list the other cells only", at);
                }
                else if (layout.Cells.Contains(value))
                {
                    Error(LayoutProblem.InvalidFootprint, $"the cell ({cell.X}, {cell.Y}, {cell.Z}) is listed twice", at);
                }
                else
                {
                    layout.Cells.Add(value);
                }
            }

            foreach ((string axis, Func<BlockCell, int> coordinate) in new (string, Func<BlockCell, int>)[] { ("X", c => c.X), ("Y", c => c.Y), ("Z", c => c.Z) })
            {
                int extent = layout.Cells.Max(coordinate) - layout.Cells.Min(coordinate) + 1;
                if (extent > MaxExtent)
                {
                    Error(LayoutProblem.InvalidFootprint, $"the footprint spans {extent} cells along {axis}, origin included; the limit is {MaxExtent}", new LayoutLocation("Footprint"));
                }
            }
        }

        public void States(States? states)
        {
            if (layout.Orientations != BlockOrientations.None)
            {
                layout.Properties.Add(new PropertyLayout(OrientationProperty, StatePropertyKind.Orientation, Blocks.Orientations.Names(layout.Orientations), 0, 0, new LayoutLocation("Orientations"))
                {
                    ValueRotation = Blocks.Orientations.QuarterTurn(layout.Orientations),
                });
            }

            if (layout.Cells.Count > 1)
            {
                string[] parts = Enumerable.Range(0, layout.Cells.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
                layout.Properties.Add(new PropertyLayout(PartProperty, StatePropertyKind.Part, parts, 0, 0, new LayoutLocation("Footprint")));
            }

            if (states is null)
            {
                return;
            }

            for (int i = 0; i < states.Bool.Count; i++)
            {
                BoolProperty property = states.Bool[i];
                var at = new LayoutLocation("States/Bool", i);
                if (CheckName(property.Name, at))
                {
                    layout.Properties.Add(new PropertyLayout(property.Name, StatePropertyKind.Bool, ["false", "true"], property.Default ? 1 : 0, 0, at) { RotatesToName = property.RotatesTo });
                }
            }

            for (int i = 0; i < states.Int.Count; i++)
            {
                IntProperty property = states.Int[i];
                var at = new LayoutLocation("States/Int", i);
                if (!CheckName(property.Name, at))
                {
                    continue;
                }

                if (property.Max < property.Min)
                {
                    Error(LayoutProblem.InvalidProperty, $"the maximum of '{property.Name}', {property.Max}, is below its minimum, {property.Min}", at with { Attribute = "max" });
                    continue;
                }

                long count = (long)property.Max - property.Min + 1;
                if (count > MaxIntValues)
                {
                    Error(LayoutProblem.InvalidProperty, $"'{property.Name}' has {count} values; keep the values that change the look or the rules, and put the others in block data", at);
                    continue;
                }

                int value = property.HasDefault ? property.Default : property.Min;
                if (value < property.Min || value > property.Max)
                {
                    Error(LayoutProblem.InvalidProperty, $"the default of '{property.Name}', {value}, is outside {property.Min} to {property.Max}", at with { Attribute = "default" });
                    continue;
                }

                string[] values = Enumerable.Range(0, (int)count).Select(v => (property.Min + v).ToString(CultureInfo.InvariantCulture)).ToArray();
                layout.Properties.Add(new PropertyLayout(property.Name, StatePropertyKind.Int, values, value - property.Min, property.Min, at) { RotatesToName = property.RotatesTo });
            }

            for (int i = 0; i < states.Enum.Count; i++)
            {
                EnumProperty property = states.Enum[i];
                var at = new LayoutLocation("States/Enum", i);
                if (!CheckName(property.Name, at))
                {
                    continue;
                }

                string[] values = property.Values.Split(',', StringSplitOptions.TrimEntries);
                string? invalid = values.FirstOrDefault(v => !IsValueName(v));
                if (invalid is not null)
                {
                    Error(LayoutProblem.InvalidProperty, $"'{invalid}' is not a value of '{property.Name}': lowercase letters, digits, and underscores", at with { Attribute = "values" });
                    continue;
                }

                string? twice = values.GroupBy(v => v, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
                if (twice is not null)
                {
                    Error(LayoutProblem.InvalidProperty, $"the value '{twice}' of '{property.Name}' is listed twice", at with { Attribute = "values" });
                    continue;
                }

                int value = property.Default.Length == 0 ? 0 : Array.IndexOf(values, property.Default);
                if (value < 0)
                {
                    Error(LayoutProblem.InvalidProperty, $"the default of '{property.Name}', '{property.Default}', is not one of its values: {string.Join(", ", values)}", at with { Attribute = "default" });
                    continue;
                }

                layout.Properties.Add(new PropertyLayout(property.Name, StatePropertyKind.Enum, values, value, 0, at) { RotatesToName = property.RotatesTo, RotationText = property.Rotation });
            }
        }

        public void Rotations()
        {
            var sources = new List<PropertyLayout>();
            foreach (PropertyLayout property in layout.Properties)
            {
                property.ValueRotation ??= Enumerable.Range(0, property.Values.Length).ToArray();
                if (property.RotationText.Length > 0)
                {
                    ReadValueRotation(property);
                }

                if (property.RotatesToName.Length == 0)
                {
                    continue;
                }

                var at = property.Location with { Attribute = "rotatesTo" };
                int target = layout.Properties.FindIndex(p => p.Name == property.RotatesToName);
                if (target < 0 || layout.Properties[target].Kind is StatePropertyKind.Orientation or StatePropertyKind.Part)
                {
                    Error(LayoutProblem.InvalidRotation, $"'{property.Name}' turns into '{property.RotatesToName}', which is not a state property of the block", at);
                    continue;
                }

                PropertyLayout other = layout.Properties[target];
                if (ReferenceEquals(other, property))
                {
                    Error(LayoutProblem.InvalidRotation, $"'{property.Name}' turns into itself; remove rotatesTo", at);
                    continue;
                }

                if (other.Kind != property.Kind || !other.Values.SequenceEqual(property.Values, StringComparer.Ordinal))
                {
                    Error(LayoutProblem.InvalidRotation, $"'{property.Name}' turns into '{other.Name}', which has another kind or other values", at);
                    continue;
                }

                property.RotatesTo = target;
                sources.Add(property);
            }

            var targets = sources.Select(s => layout.Properties[s.RotatesTo].Name).ToList();
            foreach (PropertyLayout source in sources)
            {
                string target = layout.Properties[source.RotatesTo].Name;
                if (targets.Count(t => t == target) > 1)
                {
                    Error(LayoutProblem.InvalidRotation, $"several properties turn into '{target}'", source.Location with { Attribute = "rotatesTo" });
                }
                else if (layout.Properties[source.RotatesTo].RotatesToName.Length == 0)
                {
                    Error(LayoutProblem.InvalidRotation, $"'{source.Name}' turns into '{target}', which turns into nothing; the properties of a rotation form a cycle, such as north, east, south, west", source.Location with { Attribute = "rotatesTo" });
                }
            }
        }

        public void Variants(Schemas.Variants? variants)
        {
            if (variants is null)
            {
                return;
            }

            for (int i = 0; i < variants.Variant.Count; i++)
            {
                Variant variant = variants.Variant[i];
                var at = new LayoutLocation("Variants/Variant", i);
                (int, int)[]? condition = layout.ParseCondition(variant.When, out string error);
                if (condition is null)
                {
                    Error(LayoutProblem.InvalidVariant, error, at with { Attribute = "when" });
                    continue;
                }

                GenericElement? properties = null;
                if (!variant.Properties.IsEmpty)
                {
                    properties = GenericElement.Parse(variant.Properties.Span);
                }

                layout.Variants.Add(new VariantLayout(
                    condition,
                    variant.Collision.Length > 0 ? ReadCollision(variant.Collision, at with { Attribute = "Collision" }) : null,
                    variant.Opacity.Length > 0 ? ReadOpacity(variant.Opacity, at with { Attribute = "Opacity" }) : null,
                    variant.Hardness.Length > 0 ? ReadHardness(variant.Hardness, at with { Attribute = "Hardness" }) : null,
                    properties));
            }
        }

        public void Item(CreateItem? item)
        {
            if (item is null)
            {
                return;
            }

            (int, int)[] state = [];
            if (item.State.Length > 0)
            {
                (int, int)[]? parsed = layout.ParseCondition(item.State, out string error);
                if (parsed is null)
                {
                    Error(LayoutProblem.InvalidItem, $"the state of the item: {error}", new LayoutLocation("CreateItem") { Attribute = "State" });
                    return;
                }

                state = parsed;
            }

            layout.Item = new ItemLayout(item.MaxStack, item.DisplayName, item.State, state);
        }

        private void ReadValueRotation(PropertyLayout property)
        {
            var at = property.Location with { Attribute = "rotation" };
            int[] rotation = Enumerable.Range(0, property.Values.Length).ToArray();
            var from = new HashSet<int>();
            var to = new HashSet<int>();
            foreach (string pair in property.RotationText.Split(',', StringSplitOptions.TrimEntries))
            {
                string[] sides = pair.Split(':', StringSplitOptions.TrimEntries);
                int a = sides.Length == 2 ? property.IndexOf(sides[0]) : -1;
                int b = sides.Length == 2 ? property.IndexOf(sides[1]) : -1;
                if (a < 0 || b < 0)
                {
                    Error(LayoutProblem.InvalidRotation, $"'{pair}' is not a pair of values of '{property.Name}' such as 'north:east'; the values are {property.Describe()}", at);
                    return;
                }

                if (!from.Add(a) || !to.Add(b))
                {
                    Error(LayoutProblem.InvalidRotation, $"the rotation of '{property.Name}' maps a value twice, or two values to one", at);
                    return;
                }

                rotation[a] = b;
            }

            if (!from.SetEquals(to))
            {
                Error(LayoutProblem.InvalidRotation, $"the rotation of '{property.Name}' is not a permutation: the values it maps from and to differ", at);
                return;
            }

            property.ValueRotation = rotation;
        }

        private bool CheckName(string name, LayoutLocation at)
        {
            if (!IsPropertyName(name))
            {
                Error(LayoutProblem.InvalidProperty, $"'{name}' is not a property name: a lowercase letter, then lowercase letters, digits, and underscores", at with { Attribute = "name" });
                return false;
            }

            if (name is OrientationProperty or PartProperty)
            {
                Error(LayoutProblem.InvalidProperty, $"'{name}' is a property managed by the engine; choose another name", at with { Attribute = "name" });
                return false;
            }

            if (layout.Properties.Any(p => p.Name == name))
            {
                Error(LayoutProblem.InvalidProperty, $"the property '{name}' is declared twice", at with { Attribute = "name" });
                return false;
            }

            return true;
        }

        private BlockCollision? ReadCollision(string text, LayoutLocation at)
        {
            switch (text)
            {
                case "" or "shape":
                    return BlockCollision.Shape;
                case "none":
                    return BlockCollision.None;
                default:
                    Error(LayoutProblem.InvalidCollision, $"'{text}' is not a collision: 'shape' or 'none'", at);
                    return null;
            }
        }

        private BlockOpacity? ReadOpacity(string text, LayoutLocation at)
        {
            switch (text)
            {
                case "" or "opaque":
                    return BlockOpacity.Opaque;
                case "cutout":
                    return BlockOpacity.Cutout;
                case "translucent":
                    return BlockOpacity.Translucent;
                default:
                    Error(LayoutProblem.InvalidOpacity, $"'{text}' is not an opacity: 'opaque', 'cutout', or 'translucent'", at);
                    return null;
            }
        }

        private float? ReadHardness(string text, LayoutLocation at)
        {
            if (text.Length == 0)
            {
                return 1;
            }

            if (text == "unbreakable")
            {
                return float.PositiveInfinity;
            }

            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) && value >= 0)
            {
                return value;
            }

            Error(LayoutProblem.InvalidHardness, $"'{text}' is not a hardness: a number of at least 0, or 'unbreakable'", at);
            return null;
        }

        private void Error(string code, string message, LayoutLocation at) => problems.Add(new LayoutProblem(code, message, at));

        private static bool IsShapeId(string text)
        {
            const string prefix = "shape:";
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts = text[prefix.Length..].Split('.');
            return parts.Length >= 2 && parts.All(IsValueName);
        }

        private static bool IsPropertyName(string text) => text.Length > 0 && text[0] is >= 'a' and <= 'z' && IsValueName(text);

        private static bool IsValueName(string text) => text.Length > 0 && text.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
    }
}

/// <summary>A cell of a block of several cells, relative to its origin.</summary>
/// <param name="X">The offset toward the east.</param>
/// <param name="Y">The offset upward.</param>
/// <param name="Z">The offset toward the south.</param>
public readonly record struct BlockCell(int X, int Y, int Z);

/// <summary>A state property as the layout reads it.</summary>
internal sealed class PropertyLayout(string name, StatePropertyKind kind, string[] values, int defaultValue, int min, LayoutLocation location)
{
    public string Name { get; } = name;

    public StatePropertyKind Kind { get; } = kind;

    public string[] Values { get; } = values;

    /// <summary>Gets the index of the default value.</summary>
    public int Default { get; } = defaultValue;

    /// <summary>Gets the number that value 0 stands for, for an <see cref="StatePropertyKind.Int"/>.</summary>
    public int Min { get; } = min;

    public LayoutLocation Location { get; } = location;

    public string RotatesToName { get; init; } = string.Empty;

    public string RotationText { get; init; } = string.Empty;

    /// <summary>Gets the index of the property that takes the value of this one after a quarter turn, or -1.</summary>
    public int RotatesTo { get; set; } = -1;

    /// <summary>Gets, for each value, the value after a quarter turn.</summary>
    public int[]? ValueRotation { get; set; }

    public int IndexOf(string value) => Array.IndexOf(Values, value);

    public string Describe() => Values.Length <= 8
        ? string.Join(", ", Values.Select(v => "'" + v + "'"))
        : $"'{Values[0]}' to '{Values[^1]}'";
}

/// <summary>A variant as the layout reads it.</summary>
/// <param name="Condition">The values it applies to: property index and value index pairs.</param>
/// <param name="Collision">The collision it sets, or <see langword="null"/>.</param>
/// <param name="Opacity">The opacity it sets, or <see langword="null"/>.</param>
/// <param name="Hardness">The hardness it sets, or <see langword="null"/>.</param>
/// <param name="Properties">Its block of free properties, or <see langword="null"/>.</param>
internal sealed record VariantLayout((int Property, int Value)[] Condition, BlockCollision? Collision, BlockOpacity? Opacity, float? Hardness, GenericElement? Properties);

/// <summary>The item that <c>&lt;CreateItem&gt;</c> generates.</summary>
/// <param name="MaxStack">The size of a stack, or 0 for the default.</param>
/// <param name="DisplayName">The localization key of the name, or empty.</param>
/// <param name="StateText">The state placed, as written, or empty for the default state.</param>
/// <param name="State">The state placed: property index and value index pairs that change the defaults.</param>
internal sealed record ItemLayout(int MaxStack, string DisplayName, string StateText, (int Property, int Value)[] State);

/// <summary>Where a problem is written: an element of the content, its index among its siblings, and an attribute or child.</summary>
/// <param name="Path">The path of the element below <c>&lt;Content&gt;</c>, such as <c>States/Int</c>.</param>
/// <param name="Index">The index of the element among the elements of that path, or -1 for the first.</param>
internal readonly record struct LayoutLocation(string Path, int Index = -1)
{
    /// <summary>Gets the attribute or child element of the element at fault, or <see langword="null"/>.</summary>
    public string? Attribute { get; init; }

    public override string ToString()
    {
        string element = "<" + Path.Replace("/", "><", StringComparison.Ordinal) + ">";
        string index = Index >= 0 ? $" #{Index + 1}" : string.Empty;
        return Attribute is null ? element + index : $"{element}{index} '{Attribute}'";
    }
}

/// <summary>A problem in the content of a block.</summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Location">Where.</param>
internal sealed record LayoutProblem(string Code, string Message, LayoutLocation Location)
{
    public const string InvalidShape = "NBLK001";
    public const string InvalidCollision = "NBLK002";
    public const string InvalidOpacity = "NBLK003";
    public const string InvalidHardness = "NBLK004";
    public const string InvalidOrientations = "NBLK005";
    public const string InvalidProperty = "NBLK006";
    public const string InvalidRotation = "NBLK007";
    public const string InvalidFootprint = "NBLK008";
    public const string InvalidVariant = "NBLK009";
    public const string InvalidItem = "NBLK010";
    public const string TooManyStates = "NBLK011";
}
