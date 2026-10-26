// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Google.Protobuf;
using Nobara.Formats;
using Nobara.ResourceCompiler.Caching;
using Nobara.ResourceCompiler.Formats;
using Nobara.ResourceCompiler.Plugins;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>
/// Compiles one <c>.nof</c> file up to, but not including, linking: parsing,
/// schema conversion, validation, identifiers, derived resources, and
/// serialization. References are recorded to be resolved by the linker.
/// </summary>
internal sealed class NofParser
{
    public const string Malformed = "NRC300";
    public const string InvalidRoot = "NRC301";
    public const string UnknownType = "NRC302";
    public const string InvalidStructure = "NRC303";
    public const string MissingPart = "NRC304";
    public const string InvalidName = "NRC305";
    public const string SchemaVersion = "NRC313";
    public const string InvalidTags = "NRC315";
    public const string PluginFailure = "NRC602";

    private static readonly string[] ResourceAttributes = ["version", "override", "optional", "priority"];

    private readonly SchemaSet _schemas;
    private readonly PluginSet _plugins;
    private readonly string _resourcesDirectory;

    public NofParser(SchemaSet schemas, PluginSet plugins, string resourcesDirectory)
    {
        _schemas = schemas;
        _plugins = plugins;
        _resourcesDirectory = resourcesDirectory;
    }

    public FileUnit Parse(string fullPath, string relativePath, string text, DiagnosticBag diagnostics)
    {
        var unit = new FileUnit { Path = relativePath };
        var source = new SourceFile(fullPath, relativePath, _resourcesDirectory);
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            diagnostics.Error(Malformed, $"the file is not well-formed XML: {exception.Message}", new Location(fullPath, exception.LineNumber, exception.LinePosition));
            return unit;
        }

        XElement root = document.Root!;
        if (root.Name != "Nobara")
        {
            diagnostics.Error(InvalidRoot, $"the root element is '{root.Name.LocalName}', expected 'Nobara'", source.At(root));
            return unit;
        }

        string? ns = ReadRoot(root, source, diagnostics);
        if (ns is null)
        {
            return unit;
        }

        foreach (XNode node in root.Nodes())
        {
            if (node is XText textNode && textNode.Value.Trim().Length > 0)
            {
                diagnostics.Error(InvalidStructure, "<Nobara> contains text; it holds one element per resource", source.At(textNode));
                break;
            }
        }

        foreach (XElement element in root.Elements())
        {
            if (element.Name.LocalName == "Tag" && element.Name.NamespaceName.Length == 0)
            {
                ParseTag(element, ns, source, unit, diagnostics);
            }
            else
            {
                ParseResource(element, ns, source, unit, diagnostics);
            }
        }

        return unit;
    }

    private static string? ReadRoot(XElement root, SourceFile source, DiagnosticBag diagnostics)
    {
        foreach (XAttribute attribute in root.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && attribute.Name.LocalName != "version" && attribute.Name.LocalName != "namespace")
            {
                diagnostics.Error(InvalidRoot, $"unknown attribute '{attribute.Name.LocalName}' in <Nobara>; expected 'version' and 'namespace'", source.At(attribute));
            }
        }

        XAttribute? version = root.Attribute("version");
        XAttribute? ns = root.Attribute("namespace");
        bool valid = true;
        if (version is null)
        {
            diagnostics.Error(InvalidRoot, "<Nobara> needs a 'version' attribute: the version of the .nof format, 1", source.At(root));
            valid = false;
        }
        else if (!IsFormatOne(version.Value.Trim()))
        {
            diagnostics.Error(InvalidRoot, $"unsupported .nof format version '{version.Value}'; this compiler reads version 1", source.At(version));
            valid = false;
        }

        if (ns is null)
        {
            diagnostics.Error(InvalidRoot, "<Nobara> needs a 'namespace' attribute, such as 'Example/Blocks'", source.At(root));
            return null;
        }

        if (!Identifiers.IsNamespace(ns.Value))
        {
            diagnostics.Error(InvalidRoot, $"'{ns.Value}' is not a namespace: names of letters, digits, and underscores separated by '/'", source.At(ns));
            return null;
        }

        return valid ? ns.Value : null;
    }

    private static bool IsFormatOne(string version) => version is "1" or "1.0" || (version.StartsWith("1.", StringComparison.Ordinal) && Manifests.SemanticVersion.TryParse(version, out _));

    private void ParseResource(XElement original, string ns, SourceFile source, FileUnit unit, DiagnosticBag diagnostics)
    {
        string elementName = original.Name.LocalName;
        if (original.Name.NamespaceName.Length > 0 || !_schemas.Types.TryGetValue(elementName, out ResourceType? type))
        {
            string? hint = _schemas.Types.Keys.FirstOrDefault(k => string.Equals(k, elementName, StringComparison.OrdinalIgnoreCase));
            string known = _schemas.Types.Count == 0 ? "no resource type is available: declare a dependency on the module that provides it" : $"known types: {string.Join(", ", _schemas.Types.Keys.Order(StringComparer.Ordinal).Prepend("Tag"))}";
            diagnostics.Error(UnknownType, $"unknown resource type <{elementName}>; " + (hint is null ? known : $"names are case-sensitive, did you mean <{hint}>?"), source.At(original));
            return;
        }

        int errors = diagnostics.ErrorCount;
        (bool isOverride, bool optional, int priority) = ReadResourceAttributes(original, source, diagnostics);
        XElement? element = Convert(original, type, source, diagnostics);
        if (element is null)
        {
            return;
        }

        (string? name, string? version, XElement? content, XElement? properties, XElement? tags) = ReadParts(element, original, source, diagnostics, contentRequired: true);
        if (name is null || content is null || diagnostics.ErrorCount != errors)
        {
            return;
        }

        string objectId = Identifiers.ObjectIdOf(type.Prefix, ns, name);
        var context = new WriteContext(source, diagnostics, objectId);
        var resource = NewResource(type, ns, name, objectId, version, source.Position(original));

        if (isOverride)
        {
            if (tags is not null)
            {
                diagnostics.Error(InvalidStructure, "an override cannot have a <Tags> block; contribute to the tags with ordinary resources, or override the Tag resource", source.At(tags));
            }

            Override? compiled = OverrideCompiler.Compile(type.Message, content, context);
            if (compiled is null)
            {
                return;
            }

            compiled.Optional = optional;
            compiled.Priority = priority;
            resource.Override = compiled;
        }
        else
        {
            byte[]? bytes = MessageWriter.Write(type.Message, content, context);
            if (bytes is null)
            {
                return;
            }

            resource.Content = ByteString.CopyFrom(bytes);
            if (tags is not null)
            {
                ReadTags(tags, type, objectId, source, unit, diagnostics);
            }

            RunPlugins(type, resource, element, content, bytes, ns, name, source, unit, diagnostics);
        }

        if (properties is not null)
        {
            AddProperties(resource, properties, context);
        }

        Finish(resource, context, unit);
    }

    private static Resource NewResource(ResourceType type, string ns, string name, string objectId, string? version, SourcePosition position) => new()
    {
        Type = type.Prefix,
        Element = type.Element,
        Message = type.Message.FullName,
        Namespace = ns,
        Name = name,
        ObjectId = objectId,
        ResourceId = Identifiers.ResourceIdOf(type.Prefix, ns, name),
        SchemaVersion = type.SchemaVersion,
        Version = version ?? string.Empty,
        Source = position,
    };

    private static void Finish(Resource resource, WriteContext context, FileUnit unit)
    {
        unit.References.AddRange(context.References);
        resource.References.AddRange(context.References.Select(r => r.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        foreach (string file in context.Files.Distinct(StringComparer.Ordinal))
        {
            if (unit.Files.All(f => f.Path != file))
            {
                unit.Files.Add(new FileInput { Path = file });
            }

            resource.References.Add("file:" + file);
        }

        unit.Resources.Add(resource);
    }

    private void RunPlugins(ResourceType type, Resource resource, XElement element, XElement content, byte[] bytes, string ns, string name, SourceFile source, FileUnit unit, DiagnosticBag diagnostics)
    {
        foreach (ResourcePlugin plugin in _plugins.For(type.Element))
        {
            var resourceContext = new ResourceContext(resource.ObjectId, ns, name, element, content, type.Message, bytes, (code, message, at, isError) =>
            {
                Location location = source.At(at ?? element);
                if (isError)
                {
                    diagnostics.Error(code, message, location);
                }
                else
                {
                    diagnostics.Warning(code, message, location);
                }
            });

            List<DerivedResource> derived;
            try
            {
                plugin.Validate(resourceContext);
                derived = plugin.Derive(resourceContext).ToList();
            }
            catch (Exception exception)
            {
                diagnostics.Error(PluginFailure, $"plugin {plugin.GetType().FullName} failed on '{resource.ObjectId}': {exception.Message}", source.At(element));
                continue;
            }

            foreach (DerivedResource item in derived)
            {
                CompileDerived(item, resource, ns, name, element, source, unit, diagnostics);
            }
        }
    }

    private void CompileDerived(DerivedResource item, Resource from, string ns, string name, XElement origin, SourceFile source, FileUnit unit, DiagnosticBag diagnostics)
    {
        if (!_schemas.Types.TryGetValue(item.Element, out ResourceType? type))
        {
            diagnostics.Error(UnknownType, $"a plugin derives a <{item.Element}> from '{from.ObjectId}', but no schema defines that type; declare a dependency on the module that provides it", source.At(origin));
            return;
        }

        string objectId = Identifiers.ObjectIdOf(type.Prefix, ns, name);
        var context = new WriteContext(source, diagnostics, objectId);
        byte[]? bytes = MessageWriter.Write(type.Message, item.Content, context);
        if (bytes is null)
        {
            return;
        }

        Resource derived = NewResource(type, ns, name, objectId, null, source.Position(origin));
        derived.Content = ByteString.CopyFrom(bytes);
        derived.DerivedFrom = from.ObjectId;
        if (item.Properties is not null)
        {
            AddProperties(derived, item.Properties, context);
        }

        Finish(derived, context, unit);
    }

    private static void AddProperties(Resource resource, XElement block, WriteContext context)
    {
        MessageWriter.CheckProperties(block, context);
        foreach (XElement property in block.Elements())
        {
            string[] parts = property.Name.LocalName.Split('.');
            resource.Properties.Add(new Property
            {
                Name = parts[^1],
                Qualifier = parts.Length == 2 ? parts[0] : string.Empty,
                Value = GenericTree.FromElement(property, context.Source.RelativePath),
            });
        }
    }

    private static (bool IsOverride, bool Optional, int Priority) ReadResourceAttributes(XElement element, SourceFile source, DiagnosticBag diagnostics, bool isTag = false)
    {
        foreach (XAttribute attribute in element.Attributes())
        {
            string name = attribute.Name.LocalName;
            bool known = attribute.Name.NamespaceName.Length == 0 && (isTag ? name is "registry" or "override" or "optional" or "priority" : ResourceAttributes.Contains(name));
            if (!attribute.IsNamespaceDeclaration && !known)
            {
                string expected = isTag ? "'registry', 'override', 'optional', and 'priority'" : "'version', 'override', 'optional', and 'priority'";
                diagnostics.Error(InvalidStructure, $"unknown attribute '{name}' in <{element.Name.LocalName}>; it takes {expected}", source.At(attribute));
            }
        }

        bool isOverride = Boolean(element, "override", source, diagnostics);
        bool optional = Boolean(element, "optional", source, diagnostics);
        int priority = 0;
        XAttribute? priorityAttribute = element.Attribute("priority");
        if (priorityAttribute is not null && !int.TryParse(priorityAttribute.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out priority))
        {
            diagnostics.Error(InvalidStructure, $"'priority' is '{priorityAttribute.Value}', expected a whole number", source.At(priorityAttribute));
        }

        if (!isOverride)
        {
            foreach (string name in new[] { "optional", "priority" })
            {
                if (element.Attribute(name) is XAttribute attribute)
                {
                    diagnostics.Error(InvalidStructure, $"'{name}' applies to overrides only; add override=\"true\" or remove it", source.At(attribute));
                }
            }
        }

        return (isOverride, optional, priority);
    }

    private static bool Boolean(XElement element, string name, SourceFile source, DiagnosticBag diagnostics)
    {
        XAttribute? attribute = element.Attribute(name);
        if (attribute is null)
        {
            return false;
        }

        if (attribute.Value is "true" or "false")
        {
            return attribute.Value == "true";
        }

        diagnostics.Error(InvalidStructure, $"'{name}' is '{attribute.Value}', expected 'true' or 'false'", source.At(attribute));
        return false;
    }

    private XElement? Convert(XElement element, ResourceType type, SourceFile source, DiagnosticBag diagnostics)
    {
        XAttribute? attribute = element.Attribute("version");
        if (attribute is null)
        {
            return element;
        }

        if (!uint.TryParse(attribute.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint version) || version == 0)
        {
            diagnostics.Error(SchemaVersion, $"'version' is '{attribute.Value}', expected the version of the schema of <{type.Element}>, a whole number from 1", source.At(attribute));
            return null;
        }

        if (version > type.SchemaVersion)
        {
            diagnostics.Error(SchemaVersion, $"<{type.Element}> version {version} does not exist; the current version is {type.SchemaVersion}", source.At(attribute));
            return null;
        }

        XElement current = element;
        for (uint from = version; from < type.SchemaVersion; from++)
        {
            SchemaConverter? converter = _plugins.Converter(type.Element, from);
            if (converter is null)
            {
                diagnostics.Error(SchemaVersion, $"<{type.Element}> version {from} cannot be read: no plugin converts it to version {from + 1}", source.At(attribute));
                return null;
            }

            try
            {
                current = converter.Convert(current);
            }
            catch (Exception exception)
            {
                diagnostics.Error(PluginFailure, $"the converter of <{type.Element}> from version {from} failed: {exception.Message}", source.At(element));
                return null;
            }
        }

        return current;
    }

    // The structure common to every resource: Name, Version, Content,
    // Properties, Tags. Positions of a converted element fall back to the
    // element as written.
    private static (string? Name, string? Version, XElement? Content, XElement? Properties, XElement? Tags) ReadParts(XElement element, XElement original, SourceFile source, DiagnosticBag diagnostics, bool contentRequired)
    {
        Location At(XObject node) => ((IXmlLineInfo)node).HasLineInfo() ? source.At(node) : source.At(original);

        var parts = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement child in element.Elements())
        {
            string part = child.Name.LocalName;
            if (child.Name.NamespaceName.Length > 0 || part is not ("Name" or "Version" or "Content" or "Properties" or "Tags"))
            {
                string hint = part == "Id" || (part.EndsWith("Id", StringComparison.Ordinal) && part.Length > 2)
                    ? "; the identifier is derived from the namespace and <Name>"
                    : "; a resource holds <Name>, <Version>, <Content>, <Properties>, and <Tags>, and its fields go in <Content>";
                diagnostics.Error(InvalidStructure, $"unknown element <{part}> in <{element.Name.LocalName}>{hint}", At(child));
                continue;
            }

            if (!parts.TryAdd(part, child))
            {
                diagnostics.Error(InvalidStructure, $"<{part}> appears more than once in <{element.Name.LocalName}>", At(child));
            }
        }

        foreach (XNode node in element.Nodes())
        {
            if (node is XText text && text.Value.Trim().Length > 0)
            {
                diagnostics.Error(InvalidStructure, $"<{element.Name.LocalName}> contains text; it holds elements only", At(text));
                break;
            }
        }

        string? name = null;
        if (!parts.TryGetValue("Name", out XElement? nameElement))
        {
            diagnostics.Error(MissingPart, $"<{element.Name.LocalName}> needs a <Name>", At(element));
        }
        else
        {
            name = nameElement.Value.Trim();
            if (nameElement.HasElements || nameElement.HasAttributes || !Identifiers.IsName(name))
            {
                diagnostics.Error(InvalidName, $"'{name}' is not a resource name: a letter, then letters, digits, and underscores", At(nameElement));
                name = null;
            }
        }

        string? version = parts.TryGetValue("Version", out XElement? versionElement) ? versionElement.Value.Trim() : null;
        if (!parts.TryGetValue("Content", out XElement? content) && contentRequired)
        {
            diagnostics.Error(MissingPart, $"<{element.Name.LocalName}> needs a <Content>", At(element));
        }

        parts.TryGetValue("Properties", out XElement? properties);
        parts.TryGetValue("Tags", out XElement? tags);
        return (name, version, content, properties, tags);
    }

    private static void ReadTags(XElement tags, ResourceType type, string objectId, SourceFile source, FileUnit unit, DiagnosticBag diagnostics)
    {
        if (!type.AcceptsTags)
        {
            diagnostics.Error(InvalidTags, $"the registry of <{type.Element}> does not accept tags", source.At(tags));
            return;
        }

        foreach (XAttribute attribute in tags.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                diagnostics.Error(InvalidTags, "<Tags> takes no attributes", source.At(attribute));
            }
        }

        foreach (XElement tag in tags.Elements())
        {
            string id = tag.Value.Trim();
            if (tag.Name.LocalName != "Tag" || tag.HasAttributes || tag.HasElements)
            {
                diagnostics.Error(InvalidTags, $"<Tags> holds <Tag> elements with a tag identifier, such as <Tag>#example.wood</Tag>", source.At(tag));
                continue;
            }

            if (!Identifiers.IsTagId(id))
            {
                diagnostics.Error(InvalidTags, $"'{id}' is not a tag identifier: '#' then a lowercase namespace and name, as in '#example.wood'", source.At(tag));
                continue;
            }

            (string registry, string dotted) = Identifiers.SplitTag(type.Prefix, id);
            if (registry != type.Prefix)
            {
                diagnostics.Error(InvalidTags, $"'{id}' is a tag of the '{registry}' registry; a <{type.Element}> joins tags of the '{type.Prefix}' registry", source.At(tag));
                continue;
            }

            var contribution = new TagContribution { Registry = registry, Tag = dotted, Source = source.Position(tag) };
            contribution.Members.Add(new TagMember { Id = objectId });
            unit.Tags.Add(contribution);
        }
    }

    private void ParseTag(XElement element, string ns, SourceFile source, FileUnit unit, DiagnosticBag diagnostics)
    {
        int errors = diagnostics.ErrorCount;
        (bool isOverride, bool optional, int priority) = ReadResourceAttributes(element, source, diagnostics, isTag: true);
        XAttribute? registryAttribute = element.Attribute("registry");
        ResourceType? registry = null;
        if (registryAttribute is null)
        {
            diagnostics.Error(InvalidTags, "<Tag> needs a 'registry' attribute: the type of its members, such as 'block'", source.At(element));
        }
        else if ((registry = _schemas.ByPrefix(registryAttribute.Value.Trim())) is null)
        {
            diagnostics.Error(InvalidTags, $"'{registryAttribute.Value}' is not a resource type known to this module; declare a dependency on the module that provides it", source.At(registryAttribute));
        }
        else if (!registry.AcceptsTags)
        {
            diagnostics.Error(InvalidTags, $"the registry of <{registry.Element}> does not accept tags", source.At(registryAttribute));
        }

        (string? name, _, XElement? content, XElement? properties, XElement? tags) = ReadParts(element, element, source, diagnostics, contentRequired: true);
        if (properties is not null || tags is not null)
        {
            diagnostics.Error(InvalidStructure, "a <Tag> holds <Name> and <Content> only", source.At((XObject?)properties ?? tags!));
        }

        if (name is null || content is null || registry is null || diagnostics.ErrorCount != errors)
        {
            return;
        }

        string dotted = Identifiers.Dotted(ns, name);
        string fullId = $"#{registry.Prefix}:{dotted}";
        var context = new WriteContext(source, diagnostics, fullId);
        if (isOverride)
        {
            Override? compiled = OverrideCompiler.CompileTag(content, registry.Prefix, context);
            if (compiled is null)
            {
                return;
            }

            compiled.Optional = optional;
            compiled.Priority = priority;
            var resource = new Resource
            {
                Type = "tag",
                Element = "Tag",
                Namespace = ns,
                Name = name,
                ObjectId = fullId,
                ResourceId = $"tag://{registry.Prefix}/{ns}/{name}",
                Source = source.Position(element),
                Override = compiled,
            };
            Finish(resource, context, unit);
            return;
        }

        var contribution = new TagContribution { Registry = registry.Prefix, Tag = dotted, Source = source.Position(element) };
        foreach (XElement member in content.Elements())
        {
            TagMember? parsed = ParseMember(member, registry.Prefix, fullId, context);
            if (parsed is not null)
            {
                contribution.Members.Add(parsed);
            }
        }

        foreach (XAttribute attribute in content.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                diagnostics.Error(InvalidTags, "the <Content> of a tag takes no attributes", source.At(attribute));
            }
        }

        unit.References.AddRange(context.References);
        unit.Tags.Add(contribution);
    }

    /// <summary>Parses a <c>&lt;Member&gt;</c> of a tag: an object of the registry or a nested tag.</summary>
    internal static TagMember? ParseMember(XElement member, string registry, string tagId, WriteContext context)
    {
        if (member.Name.LocalName != "Member" || member.HasElements)
        {
            context.Error(InvalidTags, $"the <Content> of a tag holds <Member> elements, not <{member.Name.LocalName}>", member);
            return null;
        }

        foreach (XAttribute attribute in member.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && attribute.Name.LocalName != "optional")
            {
                context.Error(InvalidTags, $"unknown attribute '{attribute.Name.LocalName}' in <Member>; expected 'optional'", attribute);
            }
        }

        XAttribute? optionalAttribute = member.Attribute("optional");
        if (optionalAttribute is not null && optionalAttribute.Value is not ("true" or "false"))
        {
            context.Error(InvalidTags, $"'optional' is '{optionalAttribute.Value}', expected 'true' or 'false'", optionalAttribute);
            return null;
        }

        bool optional = optionalAttribute?.Value == "true";
        string id = member.Value.Trim();
        if (Identifiers.IsTagId(id))
        {
            (string memberRegistry, string dotted) = Identifiers.SplitTag(registry, id);
            if (memberRegistry != registry)
            {
                context.Error(InvalidTags, $"'{id}' is a tag of the '{memberRegistry}' registry; a tag of the '{registry}' registry nests tags of its own registry", member);
                return null;
            }

            string full = $"#{registry}:{dotted}";
            if (full == tagId)
            {
                context.Error(InvalidTags, $"tag '{tagId}' contains itself", member);
                return null;
            }

            context.Reference(PendingReference.Types.Kind.Tag, full, registry, member, optional);
            return new TagMember { Id = full, Optional = optional };
        }

        if (!Identifiers.IsObjectId(id) || Identifiers.TypeOf(id) != registry)
        {
            context.Error(InvalidTags, $"'{id}' is not a member of the '{registry}' registry: expected an identifier such as '{registry}:example.name' or a tag such as '#example.name'", member);
            return null;
        }

        context.Reference(PendingReference.Types.Kind.Object, id, registry, member, optional);
        return new TagMember { Id = id, Optional = optional };
    }
}
