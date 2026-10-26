// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Nobara.Formats;
using Nobara.ResourceCompiler.Caching;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>
/// Links the files of a module with the resources of its dependencies:
/// duplicates, references, tag cycles, overrides, and content hashes.
/// </summary>
internal sealed class Linker
{
    public const string Duplicate = "NRC400";
    public const string UnknownObject = "NRC401";
    public const string UnknownResource = "NRC402";
    public const string UnknownTag = "NRC403";
    public const string TagCycle = "NRC404";
    public const string QueryWithoutMatch = "NRC501";

    private readonly string _moduleId;
    private readonly string _resourcesDirectory;
    private readonly SchemaSet _schemas;
    private readonly DiagnosticBag _diagnostics;

    private readonly Dictionary<string, Definition> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Definition> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(TagContribution Contribution, string Module, string? File)>> _tags = new(StringComparer.Ordinal);

    public Linker(string moduleId, string resourcesDirectory, SchemaSet schemas, DiagnosticBag diagnostics)
    {
        _moduleId = moduleId;
        _resourcesDirectory = resourcesDirectory;
        _schemas = schemas;
        _diagnostics = diagnostics;
    }

    public ResourceBundle Link(IReadOnlyList<FileUnit> units, IReadOnlyList<CompiledModule> dependencies, IReadOnlyDictionary<string, byte[]> fileHashes)
    {
        foreach (FileUnit unit in units)
        {
            foreach (Resource resource in unit.Resources.Where(r => r.Override is null))
            {
                Define(resource, _moduleId, unit.Path);
            }

            foreach (TagContribution tag in unit.Tags)
            {
                AddTag(tag, _moduleId, unit.Path);
            }
        }

        foreach (CompiledModule dependency in dependencies)
        {
            foreach (Resource resource in dependency.Resources.Resources.Where(r => r.Override is null))
            {
                Define(resource, dependency.Id, null);
            }

            foreach (TagContribution tag in dependency.Resources.Tags)
            {
                AddTag(tag, dependency.Id, null);
            }
        }

        foreach (FileUnit unit in units)
        {
            foreach (PendingReference reference in unit.References)
            {
                Resolve(reference);
            }
        }

        CheckTagCycles();
        foreach (FileUnit unit in units)
        {
            foreach (Resource resource in unit.Resources.Where(r => r.Override is not null && r.Type != "tag"))
            {
                CheckOverride(resource);
            }
        }

        var bundle = new ResourceBundle { Format = 1, Module = _moduleId };
        List<Resource> own = units.SelectMany(u => u.Resources).Select(r => r.Clone()).ToList();
        Hash(own, fileHashes);
        bundle.Resources.AddRange(own);
        bundle.Tags.AddRange(units.SelectMany(u => u.Tags));
        bundle.Files.AddRange(units.SelectMany(u => u.Files).Select(f => f.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return bundle;
    }

    private void Define(Resource resource, string module, string? file)
    {
        var definition = new Definition(resource, module, file);
        if (_objects.TryGetValue(resource.ObjectId, out Definition? existing))
        {
            if (file is not null)
            {
                string other = existing.File is null ? $"module '{existing.Module}'" : $"{existing.File}({existing.Resource.Source.Line},{existing.Resource.Source.Column})";
                _diagnostics.Error(Duplicate, $"'{resource.ObjectId}' is already defined by {other}; names that differ only by case are the same; use override=\"true\" to change it", At(resource));
            }
            else if (existing.File is not null)
            {
                _diagnostics.Error(Duplicate, $"'{resource.ObjectId}' is also defined by the dependency '{module}'; use override=\"true\" to change it", At(existing.Resource));
            }
            else if (existing.Module != module)
            {
                _diagnostics.Error(Duplicate, $"'{resource.ObjectId}' is defined by both dependencies '{existing.Module}' and '{module}'", null);
            }

            return;
        }

        _objects[resource.ObjectId] = definition;
        _resources.TryAdd(resource.ResourceId.ToLowerInvariant(), definition);
    }

    private void AddTag(TagContribution tag, string module, string? file)
    {
        string key = $"#{tag.Registry}:{tag.Tag}";
        if (!_tags.TryGetValue(key, out var list))
        {
            _tags[key] = list = [];
        }

        list.Add((tag, module, file));
    }

    private void Resolve(PendingReference reference)
    {
        Location at = At(reference.Source);
        switch (reference.Kind)
        {
            case PendingReference.Types.Kind.Object:
                if (!_objects.ContainsKey(reference.Value) && !reference.Optional)
                {
                    _diagnostics.Error(UnknownObject, $"'{reference.Value}' is not defined by this module or its dependencies; check the identifier, or declare a dependency on the module that defines it", at);
                }

                break;
            case PendingReference.Types.Kind.Resource:
                if (!_resources.ContainsKey(reference.Value.ToLowerInvariant()) && !reference.Optional)
                {
                    _diagnostics.Error(UnknownResource, $"the resource '{reference.Value}' is not defined by this module or its dependencies", at);
                }

                break;
            case PendingReference.Types.Kind.Tag:
                if (!_tags.ContainsKey(reference.Value) && !reference.Optional)
                {
                    _diagnostics.Error(UnknownTag, $"the tag '{reference.Value}' has no member in this module or its dependencies", at);
                }

                break;
        }
    }

    private void CheckTagCycles()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string tag)
        {
            state[tag] = 1;
            stack.Add(tag);
            foreach (var (contribution, _, file) in _tags[tag])
            {
                foreach (TagMember member in contribution.Members.Where(m => m.Id.StartsWith('#')))
                {
                    if (!_tags.ContainsKey(member.Id))
                    {
                        continue;
                    }

                    int mark = state.GetValueOrDefault(member.Id);
                    if (mark == 1)
                    {
                        List<string> cycle = stack.Skip(stack.IndexOf(member.Id)).Append(member.Id).ToList();
                        if (reported.Add(string.Join(",", cycle.Order(StringComparer.Ordinal))))
                        {
                            var (ownContribution, _, ownFile) = cycle.SelectMany(t => _tags[t]).FirstOrDefault(c => c.File is not null);
                            Location? location = ownFile is null ? null : At(ownContribution.Source);
                            _diagnostics.Error(TagCycle, $"tags contain each other: {string.Join(" -> ", cycle)}", location);
                        }
                    }
                    else if (mark == 0)
                    {
                        Visit(member.Id);
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[tag] = 2;
        }

        foreach (string tag in _tags.Keys.Order(StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(tag) == 0)
            {
                Visit(tag);
            }
        }
    }

    // A query that selects nothing in the target as defined is reported as a
    // warning: another override may add the element before this one applies.
    private void CheckOverride(Resource resource)
    {
        if (!_objects.TryGetValue(resource.ObjectId, out Definition? target) || !_schemas.Types.TryGetValue(target.Resource.Element, out ResourceType? type))
        {
            return;
        }

        ContentTree tree = ContentTree.Decode(type.Message, target.Resource.Content);
        foreach (Query query in resource.Override.Queries)
        {
            if (!tree.Matches(query))
            {
                string owner = target.File is null ? $"the dependency '{target.Module}'" : "this module";
                _diagnostics.Warning(QueryWithoutMatch, $"this query selects nothing in '{resource.ObjectId}' as defined by {owner}; it fails when the resources are loaded unless another override adds the element first or the override is optional", At(query.Source));
            }
        }
    }

    // The hash of a resource covers its own data, its binary files, and the
    // hashes of the resources it references. Resources that reference each
    // other share the hashes of their own data.
    private void Hash(List<Resource> own, IReadOnlyDictionary<string, byte[]> fileHashes)
    {
        var index = new Dictionary<string, Resource>(StringComparer.Ordinal);
        foreach (Resource resource in own.Where(r => r.Override is null))
        {
            index.TryAdd(resource.ObjectId, resource);
        }

        var ownHash = new Dictionary<Resource, byte[]>(ReferenceEqualityComparer.Instance);
        foreach (Resource resource in own)
        {
            ownHash[resource] = OwnHash(resource, fileHashes);
        }

        Resource? Local(string reference)
        {
            if (index.TryGetValue(reference, out Resource? local))
            {
                return local;
            }

            return _resources.TryGetValue(reference.ToLowerInvariant(), out Definition? definition) && definition.File is not null && index.TryGetValue(definition.Resource.ObjectId, out local) ? local : null;
        }

        byte[]? External(string reference)
        {
            if (_objects.TryGetValue(reference, out Definition? definition) && definition.File is null)
            {
                return definition.Resource.ContentHash.ToByteArray();
            }

            return _resources.TryGetValue(reference.ToLowerInvariant(), out definition) && definition.File is null ? definition.Resource.ContentHash.ToByteArray() : null;
        }

        // Tarjan's algorithm yields each group of mutually referencing
        // resources after every group it references.
        var final = new Dictionary<Resource, byte[]>(ReferenceEqualityComparer.Instance);
        var order = new Dictionary<Resource, int>(ReferenceEqualityComparer.Instance);
        var low = new Dictionary<Resource, int>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Resource>();
        var onStack = new HashSet<Resource>(ReferenceEqualityComparer.Instance);
        int counter = 0;

        void Connect(Resource resource)
        {
            order[resource] = low[resource] = counter++;
            stack.Push(resource);
            onStack.Add(resource);
            foreach (string reference in resource.References)
            {
                if (Local(reference) is not Resource next)
                {
                    continue;
                }

                if (!order.ContainsKey(next))
                {
                    Connect(next);
                    low[resource] = Math.Min(low[resource], low[next]);
                }
                else if (onStack.Contains(next))
                {
                    low[resource] = Math.Min(low[resource], order[next]);
                }
            }

            if (low[resource] != order[resource])
            {
                return;
            }

            var group = new List<Resource>();
            Resource member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                group.Add(member);
            }
            while (!ReferenceEquals(member, resource));

            var groupSet = new HashSet<Resource>(group, ReferenceEqualityComparer.Instance);
            foreach (Resource item in group)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(ownHash[item]);
                var parts = new List<byte[]>();
                foreach (string reference in item.References)
                {
                    if (Local(reference) is Resource target)
                    {
                        parts.Add(groupSet.Contains(target) ? ownHash[target] : final[target]);
                    }
                    else if (External(reference) is byte[] external)
                    {
                        parts.Add(external);
                    }
                }

                foreach (byte[] part in parts.OrderBy(Convert.ToHexString, StringComparer.Ordinal))
                {
                    hash.AppendData(part);
                }

                final[item] = hash.GetHashAndReset();
            }
        }

        foreach (Resource resource in own)
        {
            if (!order.ContainsKey(resource))
            {
                Connect(resource);
            }

            resource.ContentHash = ByteString.CopyFrom(final[resource]);
        }
    }

    private static byte[] OwnHash(Resource resource, IReadOnlyDictionary<string, byte[]> fileHashes)
    {
        Resource copy = resource.Clone();
        copy.ContentHash = ByteString.Empty;
        copy.Source = null;
        foreach (Property property in copy.Properties)
        {
            ClearPositions(property.Value);
        }

        if (copy.Override is not null)
        {
            ClearPositions(copy.Override.Queries);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(copy.ToByteArray());
        foreach (string reference in resource.References.Where(r => r.StartsWith("file:", StringComparison.Ordinal)))
        {
            if (fileHashes.TryGetValue(reference["file:".Length..], out byte[]? file))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(reference));
                hash.AppendData(file);
            }
        }

        return hash.GetHashAndReset();
    }

    private static void ClearPositions(IEnumerable<Query> queries)
    {
        foreach (Query query in queries)
        {
            query.Source = null;
            foreach (Operation operation in query.Operations)
            {
                operation.Source = null;
                foreach (GenericNode node in operation.Content)
                {
                    ClearPositions(node);
                }
            }

            ClearPositions(query.Queries);
        }
    }

    // Positions change when lines move; they do not change the resource.
    private static void ClearPositions(GenericNode? node)
    {
        if (node is null)
        {
            return;
        }

        node.Source = null;
        foreach (GenericAttribute attribute in node.Attributes)
        {
            attribute.Source = null;
        }

        foreach (GenericNode child in node.Children)
        {
            ClearPositions(child);
        }
    }

    private Location? At(Resource resource) => resource.Source is null ? null : At(resource.Source);

    private Location At(SourcePosition position) =>
        new(Path.Combine(_resourcesDirectory, position.File.Replace('/', Path.DirectorySeparatorChar)), (int)position.Line, (int)position.Column);

    private sealed record Definition(Resource Resource, string Module, string? File);
}
