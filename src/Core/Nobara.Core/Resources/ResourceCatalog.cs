// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Core.Logging;
using Nobara.Schema;
using F = Nobara.Formats;

namespace Nobara.Core.Resources;

/// <summary>A resource type defined by the schemas of a loaded module.</summary>
/// <param name="Prefix">The type prefix of the identifiers: <c>block</c>.</param>
/// <param name="Message">The message of the type.</param>
/// <param name="OwnerId">The module whose schemas define the type.</param>
/// <param name="AcceptsTags">Whether the registry of the type accepts tags.</param>
internal sealed record ResourceType(string Prefix, MessageDescriptor Message, string OwnerId, bool AcceptsTags);

/// <summary>A loaded module, as the catalog reads it.</summary>
/// <param name="Info">The module.</param>
/// <param name="Open">Opens a manifest resource of the module, or returns <see langword="null"/>.</param>
/// <param name="LoadedDynamically">Whether the module was added to the distribution, as a mod is; the protected namespaces apply to it.</param>
internal sealed record CatalogModule(ModuleInfo Info, Func<string, Stream?> Open, bool LoadedDynamically = false);

/// <summary>
/// The resources of every loaded module, read once per process: inventory,
/// duplicates, protected namespaces, overrides, tags, and the objects that
/// decoders produced, shared by every context.
/// </summary>
/// <remarks>
/// The catalog is immutable once built, except for the cache of decoded
/// objects, which is thread-safe.
/// </remarks>
internal sealed class ResourceCatalog
{
    public const string ResourcesResource = "nobara/resources.pb";
    public const string SchemasResource = "nobara/schemas.pb";
    public const string FilesPrefix = "nobara/files/";

    // Built per catalog: a registry is a mutable collection.
    private readonly ExtensionRegistry _extensions =
    [
        OptionsExtensions.ResourceType,
        OptionsExtensions.AcceptsTags,
        OptionsExtensions.SchemaVersion,
        OptionsExtensions.Ref,
        OptionsExtensions.Resource,
        OptionsExtensions.I18N,
        OptionsExtensions.Min,
        OptionsExtensions.Max,
        OptionsExtensions.Required,
        OptionsExtensions.File,
        OptionsExtensions.Generic,
    ];

    private readonly Dictionary<string, ResourceType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceData> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceData> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ResourceData>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceTag> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ResourceTag>> _tagsByRegistry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogModule> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, Type Result), Array> _decoded = [];
    private readonly Lock _decodeLock = new();
    private readonly List<ResourceProblem> _problems = [];
    private readonly List<string> _definitionOrder = [];
    private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);

    private ResourceCatalog()
    {
    }

    /// <summary>Gets the short names of the modules of the distribution, loaded or not.</summary>
    public HashSet<string> ShortNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the number of resources, overrides excluded.</summary>
    public int Count => _objects.Count;

    /// <summary>Reads and links the resources of the loaded modules, in load order.</summary>
    /// <param name="modules">The loaded modules, in load order.</param>
    /// <param name="shortNames">
    /// The short names of every module of the distribution, including those
    /// that run on a side this process does not host: a qualified property
    /// may target a client module on a server.
    /// </param>
    /// <param name="protection">The protected namespaces.</param>
    /// <param name="logger">The logger of the core.</param>
    /// <exception cref="ResourceLoadException">The resources cannot be used together.</exception>
    public static ResourceCatalog Build(IReadOnlyList<CatalogModule> modules, IEnumerable<string> shortNames, ProtectedNamespaces protection, Logger logger)
    {
        var catalog = new ResourceCatalog();
        catalog.ShortNames.UnionWith(shortNames.Where(n => n.Length > 0));
        var bundles = new List<(CatalogModule Module, F.ResourceBundle Bundle)>();
        var schemas = new List<(CatalogModule Module, FileDescriptorSet Set)>();
        foreach (CatalogModule module in modules)
        {
            catalog._modules[module.Info.Id] = module;

            if (catalog.Read(module, SchemasResource, FileDescriptorSet.Parser) is FileDescriptorSet set)
            {
                schemas.Add((module, set));
            }

            if (catalog.Read(module, ResourcesResource, F.ResourceBundle.Parser) is F.ResourceBundle bundle)
            {
                if (bundle.Module.Length > 0 && !string.Equals(bundle.Module, module.Info.Id, StringComparison.Ordinal))
                {
                    catalog.Problem(ResourceProblemKind.InvalidData, module.Info.Id, string.Empty, $"The resources of module '{module.Info.Id}' were compiled for module '{bundle.Module}'.", null);
                    continue;
                }

                bundles.Add((module, bundle));
            }
        }

        catalog.LoadSchemas(schemas);
        catalog.Define(bundles, protection);
        catalog.Override(bundles, protection);
        catalog.ResolveTags(bundles, protection);
        if (catalog._problems.Count > 0)
        {
            throw new ResourceLoadException(catalog._problems);
        }

        logger.Info($"Loaded {catalog._objects.Count} resources of {catalog._types.Count} types and {catalog._tags.Count} tags.");
        return catalog;
    }

    public ResourceType? Type(string prefix) => _types.GetValueOrDefault(prefix);

    /// <summary>Gets every resource, in load order of the modules that define them.</summary>
    public IEnumerable<ResourceData> All => _definitionOrder.Select(id => _objects[id]);

    /// <summary>Gets the position of a resource among the resources of its type, or -1.</summary>
    public int IndexOf(string objectId) => _positions.TryGetValue(objectId, out int index) ? index : -1;

    public IReadOnlyList<ResourceData> OfType(string prefix) => _byType.TryGetValue(prefix, out List<ResourceData>? list) ? list : [];

    /// <summary>Finds a resource by object identifier or by resource identifier.</summary>
    public ResourceData? Find(string id) =>
        _objects.TryGetValue(id, out ResourceData? data) ? data : _resources.GetValueOrDefault(id.ToLowerInvariant());

    public IReadOnlyList<ResourceTag> Tags(string registry) => _tagsByRegistry.TryGetValue(registry, out List<ResourceTag>? list) ? list : [];

    public ResourceTag? Tag(string id) => _tags.GetValueOrDefault(id);

    /// <summary>Opens a binary file embedded in the assembly of a module.</summary>
    public Stream? OpenFile(string moduleId, string path) =>
        _modules.TryGetValue(moduleId, out CatalogModule? module) ? module.Open(FilesPrefix + path) : null;

    /// <summary>Gets the decoded objects of a type, decoding them on first use.</summary>
    /// <remarks>Objects are decoded once per process: a second context reuses them.</remarks>
    public T[] Decoded<T>(string type, Func<ResourceData, T> decode)
    {
        lock (_decodeLock)
        {
            if (_decoded.TryGetValue((type, typeof(T)), out Array? cached))
            {
                return (T[])cached;
            }
        }

        IReadOnlyList<ResourceData> data = OfType(type);
        var result = new T[data.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = decode(data[i]);
        }

        lock (_decodeLock)
        {
            if (_decoded.TryGetValue((type, typeof(T)), out Array? other))
            {
                return (T[])other;
            }

            _decoded[(type, typeof(T))] = result;
            return result;
        }
    }

    /// <summary>Tells whether the objects of a type are decoded.</summary>
    public bool IsDecoded(string type, Type result)
    {
        lock (_decodeLock)
        {
            return _decoded.ContainsKey((type, result));
        }
    }

    /// <summary>Drops the decoded objects.</summary>
    public void Release()
    {
        lock (_decodeLock)
        {
            _decoded.Clear();
        }
    }

    private T? Read<T>(CatalogModule module, string name, MessageParser<T> parser)
        where T : class, IMessage<T>
    {
        using Stream? stream = module.Open(name);
        if (stream is null)
        {
            return null;
        }

        try
        {
            return parser.ParseFrom(stream);
        }
        catch (InvalidProtocolBufferException exception)
        {
            Problem(ResourceProblemKind.InvalidData, module.Info.Id, string.Empty, $"The data '{name}' of module '{module.Info.Id}' cannot be read: {exception.Message}", null);
            return null;
        }
    }

    // Files by name: a file imported by several modules (the options, the
    // descriptors of protobuf) is in the set of each. The owner of a file is
    // the first module in load order that has it.
    private void LoadSchemas(List<(CatalogModule Module, FileDescriptorSet Set)> schemas)
    {
        var files = new Dictionary<string, (FileDescriptorProto Proto, ByteString Data, string Owner)>(StringComparer.Ordinal);
        foreach ((CatalogModule module, FileDescriptorSet set) in schemas)
        {
            foreach (FileDescriptorProto file in set.File)
            {
                files.TryAdd(file.Name, (file, file.ToByteString(), module.Info.Id));
            }
        }

        if (files.Count == 0)
        {
            return;
        }

        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        bool missing = false;
        void Visit(string name)
        {
            state[name] = 1;
            foreach (string dependency in files[name].Proto.Dependency)
            {
                if (!files.ContainsKey(dependency))
                {
                    Problem(ResourceProblemKind.InvalidData, files[name].Owner, string.Empty, $"The schema '{name}' of module '{files[name].Owner}' imports '{dependency}', which no loaded module provides.", null);
                    missing = true;
                }
                else if (state.GetValueOrDefault(dependency) == 0)
                {
                    Visit(dependency);
                }
            }

            state[name] = 2;
            order.Add(name);
        }

        foreach (string name in files.Keys.Order(StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(name) == 0)
            {
                Visit(name);
            }
        }

        if (missing)
        {
            return;
        }

        IReadOnlyList<FileDescriptor> built;
        try
        {
            built = FileDescriptor.BuildFromByteStrings(order.Select(n => files[n].Data), _extensions);
        }
        catch (Exception exception) when (exception is DescriptorValidationException or InvalidProtocolBufferException or ArgumentException)
        {
            Problem(ResourceProblemKind.InvalidData, string.Empty, string.Empty, $"The schemas of the loaded modules cannot be built: {exception.Message}", null);
            return;
        }

        foreach (FileDescriptor file in built)
        {
            string owner = files[file.Name].Owner;
            foreach (MessageDescriptor message in file.MessageTypes)
            {
                MessageOptions? options = message.GetOptions();
                if (options is null || !options.HasExtension(OptionsExtensions.ResourceType))
                {
                    continue;
                }

                string prefix = options.GetExtension(OptionsExtensions.ResourceType);
                if (_types.TryGetValue(prefix, out ResourceType? existing))
                {
                    Problem(ResourceProblemKind.InvalidData, owner, string.Empty, $"The resource type '{prefix}' is defined by both '{existing.Message.FullName}' of module '{existing.OwnerId}' and '{message.FullName}' of module '{owner}'.", null);
                    continue;
                }

                _types[prefix] = new ResourceType(prefix, message, owner, options.GetExtension(OptionsExtensions.AcceptsTags));
            }
        }
    }

    private void Define(List<(CatalogModule Module, F.ResourceBundle Bundle)> bundles, ProtectedNamespaces protection)
    {
        foreach ((CatalogModule module, F.ResourceBundle bundle) in bundles)
        {
            foreach (F.Resource resource in bundle.Resources.Where(r => r.Override is null))
            {
                string id = module.Info.Id;
                if (module.LoadedDynamically && protection.IsProtected(resource.Namespace))
                {
                    Problem(ResourceProblemKind.ProtectedNamespace, id, resource.ObjectId, $"Module '{id}' defines '{resource.ObjectId}' in the protected namespace '{resource.Namespace}'.", resource.Source);
                    continue;
                }

                if (!_types.ContainsKey(resource.Type))
                {
                    Problem(ResourceProblemKind.UnknownType, id, resource.ObjectId, $"Module '{id}' defines '{resource.ObjectId}' of type '{resource.Type}', which no loaded module defines.", resource.Source);
                    continue;
                }

                if (_objects.TryGetValue(resource.ObjectId, out ResourceData? existing))
                {
                    Problem(ResourceProblemKind.Duplicate, id, resource.ObjectId, $"Modules '{existing.ModuleId}' and '{id}' both define '{resource.ObjectId}'; one of them must use override=\"true\".", resource.Source);
                    continue;
                }

                CheckQualifiers(id, resource.ObjectId, resource.Properties);
                var data = new ResourceData(
                    resource.Type,
                    resource.ObjectId,
                    resource.ResourceId,
                    resource.Namespace,
                    resource.Name,
                    id,
                    resource.Version,
                    resource.SchemaVersion,
                    resource.Content.Memory,
                    resource.ContentHash.Memory,
                    SourcePosition.From(resource.Source),
                    resource.DerivedFrom,
                    Properties(resource.Properties),
                    [],
                    resource.References.ToArray());
                _objects[data.ObjectId] = data;
                _resources.TryAdd(data.ResourceId.ToLowerInvariant(), data);
                _definitionOrder.Add(data.ObjectId);
            }
        }
    }

    private void Override(List<(CatalogModule Module, F.ResourceBundle Bundle)> bundles, ProtectedNamespaces protection)
    {
        var changes = new List<(F.Resource Resource, CatalogModule Module, int Order, int Index)>();
        for (int order = 0; order < bundles.Count; order++)
        {
            (CatalogModule module, F.ResourceBundle bundle) = bundles[order];
            for (int index = 0; index < bundle.Resources.Count; index++)
            {
                F.Resource resource = bundle.Resources[index];
                if (resource.Override is not null && resource.Type != "tag")
                {
                    changes.Add((resource, module, order, index));
                }
            }
        }

        // Increasing priority; at equal priority, load order, then module name.
        foreach (IGrouping<string, (F.Resource Resource, CatalogModule Module, int Order, int Index)> group in changes
            .OrderBy(c => c.Resource.Override.Priority)
            .ThenBy(c => c.Order)
            .ThenBy(c => c.Module.Info.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Index)
            .GroupBy(c => c.Resource.ObjectId, StringComparer.Ordinal))
        {
            if (!_objects.TryGetValue(group.Key, out ResourceData? target))
            {
                foreach (var change in group.Where(c => !c.Resource.Override.Optional))
                {
                    Problem(ResourceProblemKind.MissingTarget, change.Module.Info.Id, group.Key, $"Module '{change.Module.Info.Id}' overrides '{group.Key}', which no loaded module defines; mark the override optional=\"true\" if it may be absent.", change.Resource.Source);
                }

                continue;
            }

            ResourceType type = _types[target.Type];
            ContentNode root = ContentNode.Decode(type.Message, target.Content.Span);
            List<ResourceProperty> properties = [.. target.Properties];
            var overriddenBy = new List<string>();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(target.ContentHash.Span);
            foreach ((F.Resource resource, CatalogModule module, _, _) in group)
            {
                string id = module.Info.Id;
                if (module.LoadedDynamically && protection.IsProtected(target.Namespace))
                {
                    Problem(ResourceProblemKind.ProtectedNamespace, id, target.ObjectId, $"Module '{id}' overrides '{target.ObjectId}' in the protected namespace '{target.Namespace}'.", resource.Source);
                    continue;
                }

                OverrideApplier.Apply(root, resource.Override, (noMatch, message, source) => Problem(
                    noMatch ? ResourceProblemKind.QueryWithoutMatch : ResourceProblemKind.InvalidOperation,
                    id,
                    target.ObjectId,
                    $"In the override of '{target.ObjectId}' by module '{id}', {message}.",
                    source ?? resource.Source));

                CheckQualifiers(id, target.ObjectId, resource.Properties);
                foreach (ResourceProperty property in Properties(resource.Properties))
                {
                    int existing = properties.FindIndex(p => p.Name == property.Name && p.Qualifier == property.Qualifier);
                    if (existing >= 0)
                    {
                        properties[existing] = property;
                    }
                    else
                    {
                        properties.Add(property);
                    }
                }

                hash.AppendData(resource.ContentHash.Span);
                overriddenBy.Add(id);
            }

            _objects[target.ObjectId] = new ResourceData(
                target.Type,
                target.ObjectId,
                target.ResourceId,
                target.Namespace,
                target.Name,
                target.ModuleId,
                target.Version,
                target.SchemaVersion,
                root.Encode(),
                hash.GetHashAndReset(),
                target.Source,
                target.DerivedFrom,
                properties,
                overriddenBy,
                target.References);
            _resources[target.ResourceId.ToLowerInvariant()] = _objects[target.ObjectId];
        }

        // The lists by type follow the load order of the definitions.
        foreach (string id in _definitionOrder)
        {
            ResourceData data = _objects[id];
            if (!_byType.TryGetValue(data.Type, out List<ResourceData>? list))
            {
                _byType[data.Type] = list = [];
            }

            _positions[id] = list.Count;
            list.Add(data);
        }
    }

    private void ResolveTags(List<(CatalogModule Module, F.ResourceBundle Bundle)> bundles, ProtectedNamespaces protection)
    {
        // Contributions are additive: the tag is the union of its members.
        var contributions = new Dictionary<string, (string Registry, string Name, List<F.TagMember> Members, string Module, F.SourcePosition? Source)>(StringComparer.Ordinal);
        foreach ((CatalogModule module, F.ResourceBundle bundle) in bundles)
        {
            foreach (F.TagContribution contribution in bundle.Tags)
            {
                string key = $"#{contribution.Registry}:{contribution.Tag}";
                if (!contributions.TryGetValue(key, out var tag))
                {
                    if (!_types.ContainsKey(contribution.Registry))
                    {
                        Problem(ResourceProblemKind.UnknownType, module.Info.Id, key, $"Module '{module.Info.Id}' contributes to '{key}', a tag of the registry '{contribution.Registry}', which no loaded module defines.", contribution.Source);
                        continue;
                    }

                    if (!_types[contribution.Registry].AcceptsTags)
                    {
                        Problem(ResourceProblemKind.UnknownType, module.Info.Id, key, $"Module '{module.Info.Id}' contributes to '{key}', but the registry '{contribution.Registry}' does not accept tags.", contribution.Source);
                        continue;
                    }

                    contributions[key] = tag = (contribution.Registry, contribution.Tag, [], module.Info.Id, contribution.Source);
                }

                foreach (F.TagMember member in contribution.Members)
                {
                    int existing = tag.Members.FindIndex(m => m.Id == member.Id);
                    if (existing < 0)
                    {
                        tag.Members.Add(member.Clone());
                    }
                    else if (!member.Optional)
                    {
                        // A member that one contribution requires is required.
                        tag.Members[existing].Optional = false;
                    }
                }
            }
        }

        var changes = new List<(F.Resource Resource, string Module, int Order, int Index)>();
        for (int order = 0; order < bundles.Count; order++)
        {
            (CatalogModule module, F.ResourceBundle bundle) = bundles[order];
            for (int index = 0; index < bundle.Resources.Count; index++)
            {
                F.Resource resource = bundle.Resources[index];
                if (resource.Override is not null && resource.Type == "tag")
                {
                    if (module.LoadedDynamically && protection.IsProtected(resource.Namespace))
                    {
                        Problem(ResourceProblemKind.ProtectedNamespace, module.Info.Id, resource.ObjectId, $"Module '{module.Info.Id}' overrides '{resource.ObjectId}' in the protected namespace '{resource.Namespace}'.", resource.Source);
                        continue;
                    }

                    changes.Add((resource, module.Info.Id, order, index));
                }
            }
        }

        foreach (var change in changes.OrderBy(c => c.Resource.Override.Priority).ThenBy(c => c.Order).ThenBy(c => c.Module, StringComparer.Ordinal).ThenBy(c => c.Index))
        {
            string key = change.Resource.ObjectId;
            if (!contributions.TryGetValue(key, out var tag))
            {
                if (!change.Resource.Override.Optional)
                {
                    Problem(ResourceProblemKind.MissingTarget, change.Module, key, $"Module '{change.Module}' overrides the tag '{key}', which has no member in any loaded module; mark the override optional=\"true\" if it may be absent.", change.Resource.Source);
                }

                continue;
            }

            ContentNode root = ContentNode.FromMembers(tag.Members);
            OverrideApplier.Apply(root, change.Resource.Override, (noMatch, message, source) => Problem(
                noMatch ? ResourceProblemKind.QueryWithoutMatch : ResourceProblemKind.InvalidOperation,
                change.Module,
                key,
                $"In the override of '{key}' by module '{change.Module}', {message}.",
                source ?? change.Resource.Source));
            tag.Members.Clear();
            tag.Members.AddRange(root.Members());
        }

        // Nested tags are expanded; a cycle between tags is an error.
        var resolved = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var visiting = new List<string>();
        List<string>? Resolve(string key)
        {
            if (resolved.TryGetValue(key, out List<string>? done))
            {
                return done;
            }

            if (visiting.Contains(key))
            {
                var cycle = visiting.Skip(visiting.IndexOf(key)).Append(key).ToList();
                Problem(ResourceProblemKind.TagCycle, contributions[key].Module, key, $"Tags contain each other: {string.Join(" -> ", cycle)}.", contributions[key].Source);
                return null;
            }

            var tag = contributions[key];
            visiting.Add(key);
            var members = new List<string>();
            foreach (F.TagMember member in tag.Members)
            {
                if (member.Id.StartsWith('#'))
                {
                    if (!contributions.ContainsKey(member.Id))
                    {
                        if (!member.Optional)
                        {
                            Problem(ResourceProblemKind.MissingMember, tag.Module, key, $"The tag '{key}' contains the tag '{member.Id}', which has no member in any loaded module.", tag.Source);
                        }

                        continue;
                    }

                    if (Resolve(member.Id) is List<string> nested)
                    {
                        members.AddRange(nested);
                    }

                    continue;
                }

                if (!_objects.TryGetValue(member.Id, out ResourceData? data) || data.Type != tag.Registry)
                {
                    if (!member.Optional)
                    {
                        Problem(ResourceProblemKind.MissingMember, tag.Module, key, $"The tag '{key}' contains '{member.Id}', which no loaded module defines; mark the member optional=\"true\" if it may be absent.", tag.Source);
                    }

                    continue;
                }

                members.Add(member.Id);
            }

            visiting.RemoveAt(visiting.Count - 1);
            List<string> distinct = members.Distinct(StringComparer.Ordinal).ToList();
            resolved[key] = distinct;
            return distinct;
        }

        foreach (string key in contributions.Keys.Order(StringComparer.Ordinal))
        {
            if (Resolve(key) is not List<string> members)
            {
                continue;
            }

            var tag = new ResourceTag(contributions[key].Registry, contributions[key].Name, members);
            _tags[key] = tag;
            if (!_tagsByRegistry.TryGetValue(tag.Registry, out List<ResourceTag>? list))
            {
                _tagsByRegistry[tag.Registry] = list = [];
            }

            list.Add(tag);
        }
    }

    // A qualifier names the short name of a module of the distribution; a
    // module that is declared but inactive reads nothing, which is not an error.
    private void CheckQualifiers(string moduleId, string objectId, IEnumerable<F.Property> properties)
    {
        foreach (F.Property property in properties)
        {
            if (property.Qualifier.Length > 0 && !ShortNames.Contains(property.Qualifier))
            {
                Problem(ResourceProblemKind.UnknownQualifier, moduleId, objectId, $"The property '{property.Qualifier}.{property.Name}' of '{objectId}' names the short name '{property.Qualifier}', which no module of the distribution has.", property.Value?.Source);
            }
        }
    }

    private static ResourceProperty[] Properties(IEnumerable<F.Property> properties) =>
        properties.Select(p => new ResourceProperty(p.Name, p.Qualifier, GenericElement.From(p.Value ?? new F.GenericNode { Name = p.Name }))).ToArray();

    private void Problem(ResourceProblemKind kind, string moduleId, string resourceId, string message, F.SourcePosition? source) =>
        _problems.Add(new ResourceProblem(kind, moduleId, resourceId, message, SourcePosition.From(source)));
}
