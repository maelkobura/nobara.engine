// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Nobara.ResourceCompiler.Plugins;

/// <summary>
/// Declares the plugin types of a plugin assembly (a <c>.Build</c> project).
/// The compiler creates one instance of each with its parameterless constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ResourcePluginAttribute : Attribute
{
    /// <summary>Declares a plugin type.</summary>
    /// <param name="pluginType">A public, non-abstract subclass of <see cref="ResourcePlugin"/>.</param>
    public ResourcePluginAttribute(Type pluginType)
    {
        PluginType = pluginType;
    }

    /// <summary>Gets the plugin type.</summary>
    public Type PluginType { get; }
}

/// <summary>
/// Adds to the generic compiler what the schema of a resource type cannot
/// declare: validation rules, derived resources, processing of binary files,
/// and converters from older schema versions.
/// </summary>
/// <remarks>
/// A type whose constraints fit in its <c>.proto</c> file needs no plugin.
/// The compiler calls a plugin from one thread at a time.
/// </remarks>
public abstract class ResourcePlugin
{
    /// <summary>Gets the XML elements of the resource types the plugin handles, such as <c>Block</c>.</summary>
    public abstract IReadOnlyCollection<string> ResourceTypes { get; }

    /// <summary>
    /// Gets schemas that the plugin provides as descriptors, for example
    /// <c>BlockReflection.Descriptor</c>. The schemas embedded in the assemblies
    /// of the dependencies are read without them.
    /// </summary>
    public virtual IEnumerable<FileDescriptor> Schemas => [];

    /// <summary>Gets the converters from older versions of the schemas of the plugin's types.</summary>
    public virtual IEnumerable<SchemaConverter> Converters => [];

    /// <summary>Checks a resource after its content was validated against its schema.</summary>
    /// <param name="context">The resource and the means to report problems.</param>
    public virtual void Validate(ResourceContext context)
    {
    }

    /// <summary>Generates resources derived from a resource, such as the item of a block.</summary>
    /// <param name="context">The resource and the means to report problems.</param>
    /// <returns>The derived resources, compiled like resources written in the same file.</returns>
    public virtual IEnumerable<DerivedResource> Derive(ResourceContext context) => [];

    /// <summary>Processes a binary file referenced by a resource of the plugin's types before it is embedded.</summary>
    /// <param name="context">The file and the means to report problems.</param>
    /// <returns>The bytes to embed. The default embeds the file as it is.</returns>
    public virtual byte[] ProcessFile(FileContext context) => context.Content;
}

/// <summary>A resource given to a plugin.</summary>
public sealed class ResourceContext
{
    private readonly Action<string, string, XObject?, bool> _report;

    internal ResourceContext(string objectId, string ns, string name, XElement element, XElement content, MessageDescriptor message, byte[] contentBytes, Action<string, string, XObject?, bool> report)
    {
        ObjectId = objectId;
        Namespace = ns;
        Name = name;
        Element = element;
        Content = content;
        Message = message;
        ContentBytes = contentBytes;
        _report = report;
    }

    /// <summary>Gets the object identifier: <c>block:example.blocks.dirt</c>.</summary>
    public string ObjectId { get; }

    /// <summary>Gets the namespace as written.</summary>
    public string Namespace { get; }

    /// <summary>Gets the name as written.</summary>
    public string Name { get; }

    /// <summary>Gets the resource element, with line information.</summary>
    public XElement Element { get; }

    /// <summary>Gets the <c>&lt;Content&gt;</c> element.</summary>
    public XElement Content { get; }

    /// <summary>Gets the message of the resource type.</summary>
    public MessageDescriptor Message { get; }

    /// <summary>Gets the compiled content.</summary>
    public ReadOnlyMemory<byte> ContentBytes { get; }

    /// <summary>Decodes the compiled content with a class generated from the schema.</summary>
    /// <typeparam name="T">The generated class of the resource type.</typeparam>
    /// <param name="parser">The parser of the class, such as <c>Block.Parser</c>.</param>
    /// <returns>The content.</returns>
    public T Parse<T>(MessageParser<T> parser)
        where T : IMessage<T> => parser.ParseFrom(ContentBytes.Span);

    /// <summary>Reports an error, which fails the compilation.</summary>
    /// <param name="code">A code of the plugin, such as <c>NBLK001</c>.</param>
    /// <param name="message">The message.</param>
    /// <param name="at">The node the problem is about, or <see langword="null"/> for the resource element.</param>
    public void Error(string code, string message, XObject? at = null) => _report(code, message, at, true);

    /// <summary>Reports a warning.</summary>
    /// <param name="code">A code of the plugin.</param>
    /// <param name="message">The message.</param>
    /// <param name="at">The node the problem is about, or <see langword="null"/> for the resource element.</param>
    public void Warning(string code, string message, XObject? at = null) => _report(code, message, at, false);
}

/// <summary>A resource generated by a plugin from another resource.</summary>
/// <remarks>
/// It takes the namespace and the name of its source resource, with its own
/// type: the item derived from <c>block:example.blocks.dirt</c> is
/// <c>item:example.blocks.dirt</c>.
/// </remarks>
public sealed class DerivedResource
{
    /// <summary>Creates a derived resource.</summary>
    /// <param name="element">The XML element of its type: <c>Item</c>.</param>
    /// <param name="content">Its <c>&lt;Content&gt;</c> element, compiled against the schema of the type.</param>
    public DerivedResource(string element, XElement content)
    {
        Element = element;
        Content = content;
    }

    /// <summary>Gets the XML element of the type.</summary>
    public string Element { get; }

    /// <summary>Gets the <c>&lt;Content&gt;</c> element.</summary>
    public XElement Content { get; }

    /// <summary>Gets or sets an optional <c>&lt;Properties&gt;</c> element of free properties.</summary>
    public XElement? Properties { get; init; }
}

/// <summary>
/// Converts a resource element from one version of the schema of its type
/// to the next one.
/// </summary>
public abstract class SchemaConverter
{
    /// <summary>Gets the XML element of the type.</summary>
    public abstract string Element { get; }

    /// <summary>Gets the version the converter reads; it produces version <c>FromVersion + 1</c>.</summary>
    public abstract uint FromVersion { get; }

    /// <summary>Converts a resource element.</summary>
    /// <param name="resource">The resource element at <see cref="FromVersion"/>. Do not change it.</param>
    /// <returns>A new resource element at the next version.</returns>
    public abstract XElement Convert(XElement resource);
}

/// <summary>A binary file given to a plugin.</summary>
public sealed class FileContext
{
    private readonly Action<string, string, bool> _report;

    internal FileContext(string path, byte[] content, string referencedBy, Action<string, string, bool> report)
    {
        Path = path;
        Content = content;
        ReferencedBy = referencedBy;
        _report = report;
    }

    /// <summary>Gets the path of the file, relative to the resources folder.</summary>
    public string Path { get; }

    /// <summary>Gets the content of the file.</summary>
    public byte[] Content { get; }

    /// <summary>Gets the object identifier of a resource that references the file.</summary>
    public string ReferencedBy { get; }

    /// <summary>Reports an error, which fails the compilation.</summary>
    /// <param name="code">A code of the plugin.</param>
    /// <param name="message">The message.</param>
    public void Error(string code, string message) => _report(code, message, true);

    /// <summary>Reports a warning.</summary>
    /// <param name="code">A code of the plugin.</param>
    /// <param name="message">The message.</param>
    public void Warning(string code, string message) => _report(code, message, false);
}
