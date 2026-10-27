// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace Nobara.Core.Resources;

/// <summary>
/// A resource as the core loaded it: its identity, and its content with the
/// overrides of every module applied. A decoder turns it into an object of its
/// module.
/// </summary>
/// <remarks>Instances are immutable and shared by every context of the process.</remarks>
public sealed class ResourceData
{
    internal ResourceData(
        string type,
        string objectId,
        string resourceId,
        string ns,
        string name,
        string moduleId,
        string version,
        uint schemaVersion,
        ReadOnlyMemory<byte> content,
        ReadOnlyMemory<byte> contentHash,
        SourcePosition source,
        string derivedFrom,
        IReadOnlyList<ResourceProperty> properties,
        IReadOnlyList<string> overriddenBy,
        IReadOnlyList<string> references)
    {
        Type = type;
        ObjectId = objectId;
        ResourceId = resourceId;
        Namespace = ns;
        Name = name;
        ModuleId = moduleId;
        Version = version;
        SchemaVersion = schemaVersion;
        Content = content;
        ContentHash = contentHash;
        Source = source;
        DerivedFrom = derivedFrom;
        Properties = properties;
        OverriddenBy = overriddenBy;
        References = references;
    }

    /// <summary>Gets the type prefix of the identifiers: <c>block</c>.</summary>
    public string Type { get; }

    /// <summary>Gets the object identifier: <c>block:example.blocks.dirt</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the resource identifier: <c>block://Example/Blocks/Dirt</c>.</summary>
    public string ResourceId { get; }

    /// <summary>Gets the namespace as written: <c>Example/Blocks</c>.</summary>
    public string Namespace { get; }

    /// <summary>Gets the name as written: <c>Dirt</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the identifier of the module that defines the resource.</summary>
    public string ModuleId { get; }

    /// <summary>Gets the optional <c>&lt;Version&gt;</c> of the resource, a hint for people; empty when absent.</summary>
    public string Version { get; }

    /// <summary>Gets the version of the schema the content follows.</summary>
    public uint SchemaVersion { get; }

    /// <summary>Gets the content, serialized as the protobuf message of the type, overrides applied.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>
    /// Gets the SHA-256 of the content: the hash computed when the resource was
    /// compiled, combined with the hashes of the overrides applied to it, so a
    /// client and a server that apply different overrides see different hashes.
    /// </summary>
    public ReadOnlyMemory<byte> ContentHash { get; }

    /// <summary>Gets where the resource is written.</summary>
    public SourcePosition Source { get; }

    /// <summary>Gets the object identifier of the resource that produced this derived resource, or an empty string.</summary>
    public string DerivedFrom { get; }

    /// <summary>Gets the free properties, in the order written, those of the overrides included.</summary>
    public IReadOnlyList<ResourceProperty> Properties { get; }

    /// <summary>Gets the modules whose overrides changed the resource, in the order they applied.</summary>
    public IReadOnlyList<string> OverriddenBy { get; }

    /// <summary>Gets the identifiers that the content references, sorted: object, resource, tag identifiers, and <c>file:</c> paths.</summary>
    public IReadOnlyList<string> References { get; }

    /// <summary>Returns the object identifier and where the resource is written.</summary>
    /// <returns>For example <c>block:example.blocks.dirt (Resources/blocks.nof(3,5))</c>.</returns>
    public override string ToString() => $"{ObjectId} ({Source})";
}

/// <summary>A free property of a resource.</summary>
public sealed class ResourceProperty
{
    internal ResourceProperty(string name, string qualifier, GenericElement value)
    {
        Name = name;
        Qualifier = qualifier;
        Value = value;
    }

    /// <summary>Gets the name of the property: <c>Weight</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the short name of the module the value is meant for, or an empty string for a plain property: <c>Motion</c> in <c>Motion.Weight</c>.</summary>
    public string Qualifier { get; }

    /// <summary>Gets the value.</summary>
    public GenericElement Value { get; }
}

/// <summary>A tag resolved by the core: every member of the tag, nested tags expanded, overrides applied.</summary>
/// <remarks>Instances are immutable and shared by every context of the process.</remarks>
public sealed class ResourceTag
{
    private readonly HashSet<string> _members;

    internal ResourceTag(string registry, string name, IReadOnlyList<string> members)
    {
        Registry = registry;
        Name = name;
        Id = $"#{registry}:{name}";
        Members = members;
        _members = new HashSet<string>(members, StringComparer.Ordinal);
    }

    /// <summary>Gets the identifier of the tag in full form: <c>#block:example.wood</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the type of the objects of the tag: <c>block</c>.</summary>
    public string Registry { get; }

    /// <summary>Gets the identifier of the tag without its registry: <c>example.wood</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the object identifiers of the members, each once, in the order the contributions list them.</summary>
    public IReadOnlyList<string> Members { get; }

    /// <summary>Tells whether an object is a member of the tag.</summary>
    /// <param name="objectId">An object identifier: <c>block:example.blocks.oak_log</c>.</param>
    /// <returns><see langword="true"/> when the object is a member.</returns>
    public bool Contains(string objectId) => _members.Contains(objectId);

    /// <summary>Returns the identifier of the tag.</summary>
    /// <returns>The identifier in full form.</returns>
    public override string ToString() => Id;
}

/// <summary>Why the core refuses the resources of a distribution.</summary>
public enum ResourceProblemKind
{
    /// <summary>The compiled resources or schemas of a module cannot be read.</summary>
    InvalidData,

    /// <summary>A resource has a type that no schema of the loaded modules defines.</summary>
    UnknownType,

    /// <summary>Two modules define the same object without <c>override</c>.</summary>
    Duplicate,

    /// <summary>A module loaded dynamically defines or overrides a resource in a protected namespace.</summary>
    ProtectedNamespace,

    /// <summary>An override, which is not optional, targets an object that no module defines.</summary>
    MissingTarget,

    /// <summary>A query of an override, which is not optional, selects nothing.</summary>
    QueryWithoutMatch,

    /// <summary>An operation of an override cannot apply to what its query selected.</summary>
    InvalidOperation,

    /// <summary>A member of a tag, which is not optional, is not defined.</summary>
    MissingMember,

    /// <summary>Tags contain each other.</summary>
    TagCycle,

    /// <summary>A qualified free property names a short name that no module of the distribution has.</summary>
    UnknownQualifier,

    /// <summary>Two modules read the same free property with different types.</summary>
    PropertyTypeConflict,

    /// <summary>The value of a free property does not have the type its readers declare.</summary>
    InvalidProperty,
}

/// <summary>One reason why the core refuses the resources of a distribution.</summary>
/// <param name="Kind">The cause.</param>
/// <param name="ModuleId">The module at fault.</param>
/// <param name="ResourceId">The object identifier of the resource concerned, or an empty string.</param>
/// <param name="Message">A message that names the modules, the resource, and the cause.</param>
/// <param name="Source">Where the problem is written, when known.</param>
public sealed record ResourceProblem(ResourceProblemKind Kind, string ModuleId, string ResourceId, string Message, SourcePosition Source);

/// <summary>Thrown when the resources of the loaded modules cannot be used together.</summary>
public sealed class ResourceLoadException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="problems">Every problem found, at least one.</param>
    public ResourceLoadException(IReadOnlyList<ResourceProblem> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Gets every problem found.</summary>
    public IReadOnlyList<ResourceProblem> Problems { get; }

    private static string Describe(IReadOnlyList<ResourceProblem> problems)
    {
        var text = new StringBuilder("The resources of the modules cannot be loaded:");
        foreach (ResourceProblem problem in problems)
        {
            text.AppendLine().Append("- ");
            if (!string.IsNullOrEmpty(problem.Source.File))
            {
                text.Append(problem.Source.ToString()).Append(": ");
            }

            text.Append(problem.Message);
        }

        return text.ToString();
    }
}
