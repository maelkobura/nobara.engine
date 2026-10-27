// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using F = Nobara.Formats;

namespace Nobara.Core.Resources;

/// <summary>
/// Applies the queries and operations of an override to the tree of its
/// target, the way the compiler checked them against the schema of the type.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A query selects among the elements under its scope, depth first: the
/// first match by default, the match at <c>index</c>, or every match with
/// <c>index="all"</c>. It runs its nested queries in each selected element,
/// then its operations on each selected element.</item>
/// <item>The content of an operation holds elements of fields: of the parent of
/// the selected element for <c>Replace</c>, <c>InsertBefore</c>, and
/// <c>InsertAfter</c>, of the selected element for <c>Add</c>. An element of a
/// field that holds one value replaces that value.</item>
/// <item><c>index</c> of <c>Add</c> is a position among the values of the same
/// field.</item>
/// </list>
/// </remarks>
internal static class OverrideApplier
{
    /// <summary>Applies an override to a tree.</summary>
    /// <param name="root">The content of the target.</param>
    /// <param name="change">The override.</param>
    /// <param name="fail">Receives each problem: whether the query selected nothing, a message, and where.</param>
    public static void Apply(ContentNode root, F.Override change, Action<bool, string, F.SourcePosition?> fail)
    {
        if (!change.Merge.IsEmpty && root.Message is not null)
        {
            ContentNode merge = ContentNode.Decode(root.Message, change.Merge.Span);
            foreach (ContentEntry entry in merge.Entries)
            {
                Place(root, [entry], root.Entries.FindIndex(e => e.SameField(entry)) is int i and >= 0 ? i : root.Entries.Count);
            }
        }

        foreach (F.Query query in change.Queries)
        {
            Run(root, query, change.Optional, fail);
        }
    }

    private static void Run(ContentNode scope, F.Query query, bool optional, Action<bool, string, F.SourcePosition?> fail)
    {
        List<ContentNode> selected = scope.Descendants().Where(n => Accepts(n, query)).ToList();
        if (query.IndexCase == F.Query.IndexOneofCase.Position)
        {
            selected = selected.Count > query.Position ? [selected[(int)query.Position]] : [];
        }
        else if (query.IndexCase != F.Query.IndexOneofCase.All && selected.Count > 1)
        {
            selected = [selected[0]];
        }

        if (selected.Count == 0)
        {
            if (!optional)
            {
                fail(true, $"the query {Describe(query)} selects nothing in <{scope.Element}>", query.Source);
            }

            return;
        }

        foreach (ContentNode node in selected)
        {
            foreach (F.Query nested in query.Queries)
            {
                Run(node, nested, optional, fail);
            }

            foreach (F.Operation operation in query.Operations)
            {
                if (Operate(node, operation) is string error)
                {
                    fail(false, error, operation.Source);
                }
            }
        }
    }

    private static bool Accepts(ContentNode node, F.Query query)
    {
        if (query.Type.Length > 0 && node.Element != query.Type)
        {
            return false;
        }

        return query.Field.Length == 0
            || node.Entries.Any(e => e.Node is null && (e.Attribute == query.Field || e.Element == query.Field) && ContentNode.Same(e, query.Value));
    }

    // Returns an error message, or null when the operation applied.
    private static string? Operate(ContentNode node, F.Operation operation)
    {
        if (node.Parent is null)
        {
            return $"<{node.Element}> was removed by an earlier operation";
        }

        switch (operation.Kind)
        {
            case F.Operation.Types.Kind.Remove:
                node.Parent.Entries.RemoveAt(node.IndexInParent());
                node.Parent = null;
                return null;

            case F.Operation.Types.Kind.Set:
                if (node.ScalarField(operation.Field) is not var (field, element, attribute))
                {
                    return $"<{node.Element}> has no simple field '{operation.Field}'";
                }

                object value;
                if (field is not null)
                {
                    if (ContentNode.ParseScalar(field, operation.Value, emptyElement: false, out object? parsed) is string error)
                    {
                        return error;
                    }

                    value = parsed!;
                }
                else if (attribute == "optional")
                {
                    if (operation.Value is not ("true" or "false"))
                    {
                        return $"'optional' is '{operation.Value}', expected 'true' or 'false'";
                    }

                    value = operation.Value == "true";
                }
                else
                {
                    value = operation.Value;
                }

                ContentEntry entry = field is not null ? new ContentEntry(field, value) : new ContentEntry(element, attribute, value);
                int first = node.Entries.FindIndex(e => e.SameField(entry));
                node.Entries.RemoveAll(e => e.SameField(entry));
                node.Insert(first >= 0 ? first : node.Entries.Count, entry);
                return null;

            case F.Operation.Types.Kind.Add:
                if (Convert(node, operation, out List<ContentEntry> added) is string addError)
                {
                    return addError;
                }

                foreach (ContentEntry child in added)
                {
                    List<int> same = node.Entries.Select((e, i) => (e, i)).Where(p => p.e.SameField(child)).Select(p => p.i).ToList();
                    if (!child.IsRepeated)
                    {
                        Place(node, [child], same.Count > 0 ? same[0] : node.Entries.Count);
                        continue;
                    }

                    int position = operation.HasIndex ? (int)operation.Index : same.Count;
                    if (position > same.Count)
                    {
                        return $"'index' is {operation.Index}, but <{node.Element}> has {same.Count} <{child.Element}>";
                    }

                    int at = position < same.Count ? same[position] : same.Count > 0 ? same[^1] + 1 : node.Entries.Count;
                    node.Insert(at, child);
                }

                return null;

            default:
                ContentNode parent = node.Parent;
                if (Convert(parent, operation, out List<ContentEntry> content) is string error2)
                {
                    return error2;
                }

                int index = node.IndexInParent();
                if (operation.Kind == F.Operation.Types.Kind.Replace)
                {
                    parent.Entries.RemoveAt(index);
                    node.Parent = null;
                }
                else if (operation.Kind == F.Operation.Types.Kind.InsertAfter)
                {
                    index++;
                }

                Place(parent, content, index);
                return null;
        }
    }

    // Inserts entries at a position; an entry of a field that holds one value
    // replaces the value already there.
    private static void Place(ContentNode parent, IEnumerable<ContentEntry> entries, int at)
    {
        foreach (ContentEntry entry in entries)
        {
            if (!entry.IsRepeated)
            {
                for (int i = parent.Entries.Count - 1; i >= 0; i--)
                {
                    if (parent.Entries[i].SameField(entry))
                    {
                        parent.Entries.RemoveAt(i);
                        if (i < at)
                        {
                            at--;
                        }
                    }
                }
            }

            parent.Insert(Math.Min(at, parent.Entries.Count), entry);
            at++;
        }
    }

    private static string? Convert(ContentNode parent, F.Operation operation, out List<ContentEntry> entries)
    {
        entries = [];
        foreach (F.GenericNode element in operation.Content)
        {
            if (parent.Convert(element, out ContentEntry? entry) is string error)
            {
                return error;
            }

            entries.Add(entry!);
        }

        return null;
    }

    private static string Describe(F.Query query)
    {
        var parts = new List<string>();
        if (query.Type.Length > 0)
        {
            parts.Add($"type=\"{query.Type}\"");
        }

        if (query.Field.Length > 0)
        {
            parts.Add($"field=\"{query.Field}\" value=\"{query.Value}\"");
        }

        if (query.IndexCase == F.Query.IndexOneofCase.Position)
        {
            parts.Add($"index=\"{query.Position}\"");
        }

        return "<Query " + string.Join(' ', parts) + ">";
    }
}
