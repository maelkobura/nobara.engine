// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.ResourceCompiler.Formats;
using Nobara.Schema;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>A resource type: a message of a schema that carries <c>(nobara.resource_type)</c>.</summary>
public sealed class ResourceType
{
    internal ResourceType(string element, string prefix, MessageDescriptor message, bool acceptsTags, uint schemaVersion, string owner)
    {
        Element = element;
        Prefix = prefix;
        Message = message;
        AcceptsTags = acceptsTags;
        SchemaVersion = schemaVersion;
        Owner = owner;
    }

    /// <summary>Gets the XML element of the type, which is the name of its message: <c>Block</c>.</summary>
    public string Element { get; }

    /// <summary>Gets the type prefix of the object identifiers: <c>block</c>.</summary>
    public string Prefix { get; }

    /// <summary>Gets the message of the type.</summary>
    public MessageDescriptor Message { get; }

    /// <summary>Gets whether the registry of the type accepts tags.</summary>
    public bool AcceptsTags { get; }

    /// <summary>Gets the current version of the schema of the type.</summary>
    public uint SchemaVersion { get; }

    /// <summary>Gets the identifier of the module whose schemas define the type.</summary>
    public string Owner { get; }
}

/// <summary>The serialized schemas of one module: a <c>FileDescriptorSet</c>.</summary>
/// <param name="Owner">The identifier of the module that provides the schemas.</param>
/// <param name="DescriptorSet">The serialized <c>google.protobuf.FileDescriptorSet</c>, imports included.</param>
public sealed record SchemaSource(string Owner, byte[] DescriptorSet);

/// <summary>Every schema known to a compilation, and the resource types they define.</summary>
public sealed class SchemaSet
{
    private const string Code = "NRC600";

    private static readonly ExtensionRegistry Registry = CreateRegistry();

    private readonly Dictionary<string, ResourceType> _byElement;
    private readonly Dictionary<string, ResourceType> _byPrefix;

    private SchemaSet(Dictionary<string, ResourceType> byElement, byte[] fingerprint, IReadOnlyList<FileDescriptor> files)
    {
        _byElement = byElement;
        _byPrefix = byElement.Values.ToDictionary(t => t.Prefix, StringComparer.Ordinal);
        Fingerprint = fingerprint;
        Files = files;
    }

    /// <summary>Gets an empty set, without any resource type.</summary>
    public static SchemaSet Empty { get; } = new([], SHA256.HashData([]), []);

    /// <summary>Gets the resource types, by element name.</summary>
    public IReadOnlyDictionary<string, ResourceType> Types => _byElement;

    /// <summary>Gets the descriptors of every file of the set.</summary>
    public IReadOnlyList<FileDescriptor> Files { get; }

    /// <summary>Gets a hash of the content of every schema.</summary>
    public byte[] Fingerprint { get; }

    /// <summary>Gets a resource type by the type prefix of its identifiers.</summary>
    /// <param name="prefix">The prefix: <c>block</c>.</param>
    /// <returns>The type, or <see langword="null"/> when no schema defines it.</returns>
    public ResourceType? ByPrefix(string prefix) => _byPrefix.GetValueOrDefault(prefix);

    /// <summary>Builds the set from the schemas of the compiled module and of its dependencies.</summary>
    /// <param name="sources">The serialized descriptor sets, the compiled module's own first.</param>
    /// <param name="descriptors">Schemas given as descriptors, by plugins, with the module that owns them.</param>
    /// <param name="diagnostics">Receives the problems found.</param>
    /// <returns>The set; it holds the valid types even when an error is reported.</returns>
    public static SchemaSet Build(IEnumerable<SchemaSource> sources, IEnumerable<(string Owner, FileDescriptor File)> descriptors, DiagnosticBag diagnostics)
    {
        // Files by name: a file imported by several modules (the options,
        // the protobuf descriptors) appears in each of their sets.
        var protos = new Dictionary<string, (FileDescriptorProto Proto, ByteString Data, string Owner)>(StringComparer.Ordinal);
        foreach (SchemaSource source in sources)
        {
            FileDescriptorSet set;
            try
            {
                set = FileDescriptorSet.Parser.ParseFrom(source.DescriptorSet);
            }
            catch (InvalidProtocolBufferException exception)
            {
                diagnostics.Error(Code, $"the schemas of '{source.Owner}' cannot be read: {exception.Message}", null);
                continue;
            }

            foreach (FileDescriptorProto file in set.File)
            {
                Add(protos, file, file.ToByteString(), source.Owner, diagnostics);
            }
        }

        foreach ((string owner, FileDescriptor file) in descriptors)
        {
            foreach (FileDescriptor dependency in WithDependencies(file))
            {
                Add(protos, FileDescriptorProto.Parser.ParseFrom(dependency.SerializedData), dependency.SerializedData, owner, diagnostics);
            }
        }

        List<string>? order = Order(protos, diagnostics);
        if (order is null || order.Count == 0)
        {
            return order is null ? Empty : new SchemaSet([], ComputeFingerprint(protos, []), []);
        }

        IReadOnlyList<FileDescriptor> built;
        try
        {
            built = FileDescriptor.BuildFromByteStrings(order.Select(n => protos[n].Data), Registry);
        }
        catch (Exception exception) when (exception is DescriptorValidationException or InvalidProtocolBufferException or ArgumentException)
        {
            diagnostics.Error(Code, $"the schemas cannot be loaded: {exception.Message}", null);
            return Empty;
        }

        var types = new Dictionary<string, ResourceType>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, ResourceType>(StringComparer.Ordinal);
        foreach (FileDescriptor file in built)
        {
            string owner = protos[file.Name].Owner;
            foreach (MessageDescriptor message in file.MessageTypes)
            {
                MessageOptions? options = message.GetOptions();
                if (options is null || !options.HasExtension(OptionsExtensions.ResourceType))
                {
                    continue;
                }

                string prefix = options.GetExtension(OptionsExtensions.ResourceType);
                uint version = options.HasExtension(OptionsExtensions.SchemaVersion) ? options.GetExtension(OptionsExtensions.SchemaVersion) : 1;
                if (!Identifiers.IsTypePrefix(prefix) || prefix == "tag")
                {
                    diagnostics.Error(Code, $"message '{message.FullName}' has the resource type '{prefix}'; a type prefix is a lowercase word, other than 'tag'", null);
                    continue;
                }

                if (version == 0)
                {
                    diagnostics.Error(Code, $"message '{message.FullName}' has schema version 0; versions start at 1", null);
                    continue;
                }

                if (message.Name == "Tag" || message.Name == "Nobara")
                {
                    diagnostics.Error(Code, $"message '{message.FullName}' cannot be a resource type: the element <{message.Name}> is reserved", null);
                    continue;
                }

                var type = new ResourceType(message.Name, prefix, message, options.GetExtension(OptionsExtensions.AcceptsTags), version, owner);
                if (types.TryGetValue(type.Element, out ResourceType? existing))
                {
                    diagnostics.Error(Code, $"the resource element <{type.Element}> is defined twice: by '{existing.Message.FullName}' ({existing.Owner}) and '{message.FullName}' ({owner})", null);
                    continue;
                }

                if (prefixes.TryGetValue(prefix, out existing))
                {
                    diagnostics.Error(Code, $"the resource type '{prefix}' is defined twice: by '{existing.Message.FullName}' ({existing.Owner}) and '{message.FullName}' ({owner})", null);
                    continue;
                }

                CheckFields(message, new HashSet<string>(StringComparer.Ordinal), diagnostics);
                types[type.Element] = type;
                prefixes[prefix] = type;
            }
        }

        return new SchemaSet(types, ComputeFingerprint(protos, order), built);
    }

    // Checks the fields reachable from a resource type: the options that the
    // compiler reads must be on fields of a matching type.
    private static void CheckFields(MessageDescriptor message, HashSet<string> visited, DiagnosticBag diagnostics)
    {
        if (!visited.Add(message.FullName))
        {
            return;
        }

        foreach (FieldDescriptor field in message.Fields.InFieldNumberOrder())
        {
            string where = $"field '{message.FullName}.{field.Name}'";
            if (field.IsMap)
            {
                diagnostics.Error(Code, $"{where} is a map; maps have no XML form, use a repeated message instead", null);
                continue;
            }

            FieldOptions? options = field.GetOptions();
            bool isString = field.FieldType == FieldType.String;
            if (options is not null)
            {
                if ((options.HasExtension(OptionsExtensions.Ref) || options.HasExtension(OptionsExtensions.Resource) || options.GetExtension(OptionsExtensions.I18N) || options.GetExtension(OptionsExtensions.File)) && !isString)
                {
                    diagnostics.Error(Code, $"{where} uses (nobara.ref), (nobara.resource), (nobara.i18n), or (nobara.file), which apply to string fields only", null);
                }

                if (options.HasExtension(OptionsExtensions.Ref) && !Identifiers.IsTypePrefix(options.GetExtension(OptionsExtensions.Ref)))
                {
                    diagnostics.Error(Code, $"{where}: (nobara.ref) must be a type prefix such as \"item\"", null);
                }

                if (options.HasExtension(OptionsExtensions.Resource) && !Identifiers.IsTypePrefix(options.GetExtension(OptionsExtensions.Resource)))
                {
                    diagnostics.Error(Code, $"{where}: (nobara.resource) must be a type prefix such as \"texture\"", null);
                }

                if ((options.HasExtension(OptionsExtensions.Min) || options.HasExtension(OptionsExtensions.Max)) && !MessageWriter.IsNumeric(field.FieldType))
                {
                    diagnostics.Error(Code, $"{where} uses (nobara.min) or (nobara.max), which apply to numeric fields only", null);
                }

                if (options.HasExtension(OptionsExtensions.Generic))
                {
                    string kind = options.GetExtension(OptionsExtensions.Generic);
                    if (field.FieldType != FieldType.Bytes)
                    {
                        diagnostics.Error(Code, $"{where} uses (nobara.generic), which applies to bytes fields only", null);
                    }
                    else if (kind != "tree" && kind != "component" && kind != "properties")
                    {
                        diagnostics.Error(Code, $"{where}: (nobara.generic) is \"{kind}\", expected \"tree\", \"component\", or \"properties\"", null);
                    }
                }
            }

            if (field.FieldType == FieldType.Message)
            {
                CheckFields(field.MessageType, visited, diagnostics);
            }
            else if (field.FieldType == FieldType.Group)
            {
                diagnostics.Error(Code, $"{where} is a group, which has no XML form", null);
            }
        }
    }

    private static void Add(Dictionary<string, (FileDescriptorProto, ByteString, string)> protos, FileDescriptorProto file, ByteString data, string owner, DiagnosticBag diagnostics)
    {
        if (protos.TryGetValue(file.Name, out var existing))
        {
            if (!existing.Item2.Equals(data) && !file.Name.StartsWith("google/protobuf/", StringComparison.Ordinal) && file.Name != "nobara/options.proto")
            {
                diagnostics.Error(Code, $"the schema file '{file.Name}' is provided by both '{existing.Item3}' and '{owner}' with different content", null);
            }

            return;
        }

        protos[file.Name] = (file, data, owner);
    }

    private static List<string>? Order(Dictionary<string, (FileDescriptorProto Proto, ByteString Data, string Owner)> protos, DiagnosticBag diagnostics)
    {
        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        bool failed = false;

        void Visit(string name)
        {
            state[name] = 1;
            foreach (string dependency in protos[name].Proto.Dependency)
            {
                if (!protos.ContainsKey(dependency))
                {
                    diagnostics.Error(Code, $"the schema file '{name}' imports '{dependency}', which no schema set provides", null);
                    failed = true;
                    continue;
                }

                if (state.GetValueOrDefault(dependency) == 0)
                {
                    Visit(dependency);
                }
            }

            state[name] = 2;
            order.Add(name);
        }

        foreach (string name in protos.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(name) == 0)
            {
                Visit(name);
            }
        }

        return failed ? null : order;
    }

    private static IEnumerable<FileDescriptor> WithDependencies(FileDescriptor file)
    {
        foreach (FileDescriptor dependency in file.Dependencies)
        {
            foreach (FileDescriptor nested in WithDependencies(dependency))
            {
                yield return nested;
            }
        }

        yield return file;
    }

    private static byte[] ComputeFingerprint(Dictionary<string, (FileDescriptorProto Proto, ByteString Data, string Owner)> protos, List<string> order)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in order)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            hash.AppendData(protos[name].Data.Span);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(protos[name].Owner));
        }

        return hash.GetHashAndReset();
    }

    private static ExtensionRegistry CreateRegistry()
    {
        var registry = new ExtensionRegistry
        {
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
        };
        return registry;
    }
}
