// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Game;

/// <summary>A stage that <c>engine.nobara</c> inserts.</summary>
internal sealed record ConfigStage(string Name, string? After, string? Before, Side Side, SourcePosition Source);

/// <summary>A constraint that <c>engine.nobara</c> adds to a system.</summary>
internal sealed record ConfigConstraint(string System, string? After, string? Before, Side Side, SourcePosition Source);

/// <summary>A system that <c>engine.nobara</c> disables.</summary>
internal sealed record ConfigDisable(string System, Side Side, SourcePosition Source);

/// <summary>The <c>&lt;ModuleConfig module="nobara.game"&gt;</c> block, read and checked.</summary>
internal sealed class GameConfig
{
    public static readonly GameConfig Empty = new([], [], []);

    private GameConfig(List<ConfigStage> stages, List<ConfigConstraint> constraints, List<ConfigDisable> disables)
    {
        Stages = stages;
        Constraints = constraints;
        Disables = disables;
    }

    public IReadOnlyList<ConfigStage> Stages { get; }

    public IReadOnlyList<ConfigConstraint> Constraints { get; }

    public IReadOnlyList<ConfigDisable> Disables { get; }

    /// <summary>Reads the block. Every problem is collected before throwing.</summary>
    /// <exception cref="TickScheduleException">An element or an attribute is unknown, missing, or has a wrong value.</exception>
    public static GameConfig Read(GenericElement? block)
    {
        if (block is null)
        {
            return Empty;
        }

        var problems = new List<string>();
        var stages = new List<ConfigStage>();
        var constraints = new List<ConfigConstraint>();
        var disables = new List<ConfigDisable>();
        foreach (GenericElement element in block.Children)
        {
            switch (element.Name)
            {
                case "Stage":
                    if (Check(element, ["name", "after", "before", "side"], problems)
                        && Required(element, "name", problems) is { } stage
                        && Anchors(element, problems, out string? after, out string? before))
                    {
                        stages.Add(new ConfigStage(stage, after, before, ReadSide(element, problems), element.Source));
                    }

                    break;
                case "Constraint":
                    if (Check(element, ["system", "after", "before", "side"], problems)
                        && Required(element, "system", problems) is { } constrained
                        && Anchors(element, problems, out string? first, out string? then))
                    {
                        constraints.Add(new ConfigConstraint(constrained, first, then, ReadSide(element, problems), element.Source));
                    }

                    break;
                case "Disable":
                    if (Check(element, ["system", "side"], problems) && Required(element, "system", problems) is { } disabled)
                    {
                        disables.Add(new ConfigDisable(disabled, ReadSide(element, problems), element.Source));
                    }

                    break;
                default:
                    problems.Add($"{element.Source}: unknown element <{element.Name}>; nobara.game reads <Stage>, <Constraint>, and <Disable>.");
                    break;
            }
        }

        if (block.Value is { Text.Length: > 0 } text)
        {
            problems.Add($"{block.Source}: <ModuleConfig module=\"nobara.game\"> holds text '{text.Text}'; it holds only elements.");
        }

        return problems.Count > 0 ? throw new TickScheduleException(problems) : new GameConfig(stages, constraints, disables);
    }

    private static bool Check(GenericElement element, string[] allowed, List<string> problems)
    {
        bool valid = true;
        foreach (GenericAttribute attribute in element.Attributes)
        {
            if (!allowed.Contains(attribute.Name, StringComparer.Ordinal))
            {
                problems.Add($"{attribute.Source}: unknown attribute '{attribute.Name}' in <{element.Name}>; expected {string.Join(", ", allowed.Select(a => "'" + a + "'"))}.");
                valid = false;
            }
        }

        if (element.Children.Count > 0)
        {
            problems.Add($"{element.Children[0].Source}: <{element.Name}> has no child element.");
            valid = false;
        }

        return valid;
    }

    private static string? Required(GenericElement element, string name, List<string> problems)
    {
        string? value = Optional(element, name, problems);
        if (value is null)
        {
            problems.Add($"{element.Source}: <{element.Name}> needs the attribute '{name}'.");
        }

        return value;
    }

    private static string? Optional(GenericElement element, string name, List<string> problems)
    {
        GenericAttribute? attribute = element.Attribute(name);
        if (attribute is null)
        {
            return null;
        }

        if (attribute.Value.Text.Length == 0)
        {
            problems.Add($"{attribute.Source}: the attribute '{name}' of <{element.Name}> is empty.");
            return null;
        }

        return attribute.Value.Text;
    }

    private static bool Anchors(GenericElement element, List<string> problems, out string? after, out string? before)
    {
        after = Optional(element, "after", problems);
        before = Optional(element, "before", problems);
        if (after is null && before is null)
        {
            problems.Add($"{element.Source}: <{element.Name}> needs 'after', 'before', or both.");
            return false;
        }

        return true;
    }

    private static Side ReadSide(GenericElement element, List<string> problems)
    {
        GenericAttribute? attribute = element.Attribute("side");
        if (attribute is null)
        {
            return Side.Both;
        }

        switch (attribute.Value.Text.ToLowerInvariant())
        {
            case "client":
                return Side.Client;
            case "server":
                return Side.Server;
            default:
                problems.Add($"{attribute.Source}: the side of <{element.Name}> is 'client' or 'server', not '{attribute.Value.Text}'.");
                return Side.None;
        }
    }
}
