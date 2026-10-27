// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace Nobara.Core.Resources;

/// <summary>
/// The resources of one type that changed while the game runs, as the module
/// that defines the type receives them in development mode.
/// </summary>
/// <remarks>
/// <para>
/// The core hands a reload to the module at the synchronization point of a
/// frame, on the loop thread of its context, through the handler that the
/// module registered with <see cref="ModuleResources.RegisterReloadHandler"/>.
/// When the handler runs, the resources of the context are already the new
/// ones: <see cref="ModuleResources.GetAll{T}"/> decodes them again.
/// </para>
/// <para>
/// The handler applies what it can, such as a changed value, and calls
/// <see cref="Refuse"/> for what it cannot apply while the game runs, such as
/// a resource that changes the layout of a table. The core logs each refusal
/// as a warning.
/// </para>
/// </remarks>
public sealed class ResourceReload
{
    private readonly List<(string ObjectId, string Reason)> _refusals = [];

    internal ResourceReload(string type, IReadOnlyList<ResourceData> added, IReadOnlyList<ResourceData> changed, IReadOnlyList<string> removed, IReadOnlyList<string> changedTags)
    {
        Type = type;
        Added = added;
        Changed = changed;
        Removed = removed;
        ChangedTags = changedTags;
    }

    /// <summary>Gets the type prefix of the resources: <c>block</c>.</summary>
    public string Type { get; }

    /// <summary>Gets the resources that did not exist before, in their new form.</summary>
    public IReadOnlyList<ResourceData> Added { get; }

    /// <summary>Gets the resources whose content, content hash, or free properties changed, in their new form.</summary>
    public IReadOnlyList<ResourceData> Changed { get; }

    /// <summary>Gets the object identifiers of the resources that no longer exist.</summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>Gets the identifiers of the tags of the registry whose members changed, appeared, or disappeared: <c>#block:example.wood</c>.</summary>
    public IReadOnlyList<string> ChangedTags { get; }

    internal IReadOnlyList<(string ObjectId, string Reason)> Refusals => _refusals;

    /// <summary>Reports a change that the module cannot apply while the game runs.</summary>
    /// <param name="objectId">The resource whose change is not applied, or an empty string for the whole reload.</param>
    /// <param name="reason">Why, and what applies it: <c>a new state needs a restart of the context</c>.</param>
    public void Refuse(string objectId, string reason)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(reason);
        _refusals.Add((objectId, reason));
    }
}

/// <summary>A catalog that replaced the previous one, and what changed, type by type.</summary>
/// <param name="Version">1 for the first reload, then one more for each.</param>
/// <param name="Catalog">The new catalog.</param>
/// <param name="Changes">The changed types and what changed in each.</param>
/// <param name="Modules">The modules whose resources were reloaded.</param>
internal sealed record ResourceGeneration(int Version, ResourceCatalog Catalog, IReadOnlyList<TypeChange> Changes, IReadOnlyList<string> Modules);

/// <summary>What changed in one type between two catalogs.</summary>
internal sealed record TypeChange(
    string Type,
    string OwnerId,
    IReadOnlyList<ResourceData> Added,
    IReadOnlyList<ResourceData> Changed,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> ChangedTags);

/// <summary>Compares two catalogs.</summary>
internal static class ResourceDiff
{
    public static List<TypeChange> Compare(ResourceCatalog before, ResourceCatalog after)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        types.UnionWith(before.All.Select(r => r.Type));
        types.UnionWith(after.All.Select(r => r.Type));

        var changes = new List<TypeChange>();
        foreach (string type in types)
        {
            var old = before.OfType(type).ToDictionary(r => r.ObjectId, StringComparer.Ordinal);
            IReadOnlyList<ResourceData> current = after.OfType(type);
            var added = current.Where(r => !old.ContainsKey(r.ObjectId)).ToList();
            var changed = current.Where(r => old.TryGetValue(r.ObjectId, out ResourceData? previous) && !Same(previous, r)).ToList();
            var currentIds = current.Select(r => r.ObjectId).ToHashSet(StringComparer.Ordinal);
            var removed = old.Keys.Where(id => !currentIds.Contains(id)).Order(StringComparer.Ordinal).ToList();
            List<string> tags = ChangedTags(before.Tags(type), after.Tags(type));
            if (added.Count + changed.Count + removed.Count + tags.Count == 0)
            {
                continue;
            }

            string owner = after.Type(type)?.OwnerId ?? before.Type(type)?.OwnerId ?? string.Empty;
            changes.Add(new TypeChange(type, owner, added, changed, removed, tags));
        }

        return changes;
    }

    private static bool Same(ResourceData a, ResourceData b) =>
        a.ContentHash.Span.SequenceEqual(b.ContentHash.Span)
        && a.Content.Span.SequenceEqual(b.Content.Span)
        && Describe(a.Properties) == Describe(b.Properties);

    private static List<string> ChangedTags(IReadOnlyList<ResourceTag> before, IReadOnlyList<ResourceTag> after)
    {
        var old = before.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (ResourceTag tag in after)
        {
            if (!old.Remove(tag.Id, out ResourceTag? previous) || !previous.Members.SequenceEqual(tag.Members, StringComparer.Ordinal))
            {
                result.Add(tag.Id);
            }
        }

        result.AddRange(old.Keys);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string Describe(IReadOnlyList<ResourceProperty> properties)
    {
        var text = new StringBuilder();
        foreach (ResourceProperty property in properties)
        {
            text.Append(property.Qualifier).Append('.').Append(property.Name).Append('=');
            Describe(property.Value, text);
            text.Append(';');
        }

        return text.ToString();
    }

    private static void Describe(GenericElement element, StringBuilder text)
    {
        text.Append('<').Append(element.Name);
        foreach (GenericAttribute attribute in element.Attributes)
        {
            text.Append(' ').Append(attribute.Name).Append("=\"").Append(attribute.Value.Text).Append('"');
        }

        text.Append('>').Append(element.Value?.Text);
        foreach (GenericElement child in element.Children)
        {
            Describe(child, text);
        }

        text.Append("</>");
    }
}
