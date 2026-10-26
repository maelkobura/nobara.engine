// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Xml.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Formats;
using Nobara.ResourceCompiler.Formats;
using Nobara.Schema;

namespace Nobara.ResourceCompiler.Nof;

/// <summary>
/// Compiles the content of an override: simple fields to merge, and queries
/// with their operations, checked against the schema of the type.
/// </summary>
internal static class OverrideCompiler
{
    public const string InvalidOverride = "NRC500";

    private static readonly string[] Operations = ["Replace", "Add", "InsertBefore", "InsertAfter", "Remove", "Set"];

    public static Override? Compile(MessageDescriptor message, XElement content, WriteContext context)
    {
        int errors = context.Diagnostics.ErrorCount;
        var schema = new Reachable(message);
        var result = new Override();
        var merges = new List<(FieldDescriptor, XElement)>();
        NoAttributes(content, context);

        foreach (XElement child in content.Elements())
        {
            if (child.Name.LocalName == "Query")
            {
                if (CompileQuery(child, schema, context, tagRegistry: null) is Query query)
                {
                    result.Queries.Add(query);
                }

                continue;
            }

            FieldDescriptor? field = MessageWriter.FieldByElement(message, child.Name.LocalName);
            if (field is null)
            {
                context.Error(MessageWriter.UnknownField, $"unknown element <{child.Name.LocalName}> in the <Content> of an override of <{message.Name}>; it holds simple fields of <{message.Name}> and <Query> elements", child);
                continue;
            }

            if (field.IsRepeated || field.FieldType == FieldType.Message || field.GetOptions()?.HasExtension(OptionsExtensions.Generic) == true)
            {
                context.Error(InvalidOverride, $"<{child.Name.LocalName}> is not a simple field; change it with a <Query> and an operation", child);
                continue;
            }

            if (merges.Any(m => m.Item1 == field))
            {
                context.Error(MessageWriter.DuplicateField, $"<{child.Name.LocalName}> is set twice", child);
                continue;
            }

            merges.Add((field, child));
        }

        if (merges.Count > 0)
        {
            result.Merge = ByteString.CopyFrom(MessageWriter.WriteFields(merges, context));
        }

        if (merges.Count == 0 && result.Queries.Count == 0)
        {
            context.Error(InvalidOverride, "the <Content> of an override is empty; it holds simple fields and <Query> elements", content);
        }

        return context.Diagnostics.ErrorCount == errors ? result : null;
    }

    /// <summary>Compiles an override of a tag: queries on its <c>&lt;Member&gt;</c> elements, whose fields are <c>id</c> and <c>optional</c>.</summary>
    public static Override? CompileTag(XElement content, string registry, WriteContext context)
    {
        int errors = context.Diagnostics.ErrorCount;
        var result = new Override();
        NoAttributes(content, context);
        foreach (XElement child in content.Elements())
        {
            if (child.Name.LocalName != "Query")
            {
                context.Error(InvalidOverride, $"the <Content> of a tag override holds <Query> elements, not <{child.Name.LocalName}>", child);
                continue;
            }

            if (CompileQuery(child, Reachable.Tag, context, registry) is Query query)
            {
                result.Queries.Add(query);
            }
        }

        if (result.Queries.Count == 0)
        {
            context.Error(InvalidOverride, "the <Content> of a tag override needs at least one <Query>", content);
        }

        return context.Diagnostics.ErrorCount == errors ? result : null;
    }

    private static Query? CompileQuery(XElement element, Reachable schema, WriteContext context, string? tagRegistry)
    {
        int errors = context.Diagnostics.ErrorCount;
        var query = new Query { Source = context.Source.Position(element) };
        foreach (XAttribute attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && attribute.Name.LocalName is not ("field" or "value" or "type" or "index"))
            {
                context.Error(InvalidOverride, $"unknown attribute '{attribute.Name.LocalName}' in <Query>; expected 'field', 'value', 'type', and 'index'", attribute);
            }
        }

        XAttribute? field = element.Attribute("field");
        XAttribute? value = element.Attribute("value");
        XAttribute? type = element.Attribute("type");
        XAttribute? index = element.Attribute("index");
        if ((field is null) != (value is null))
        {
            context.Error(InvalidOverride, "<Query> takes 'field' and 'value' together", element);
        }

        if (field is null && type is null)
        {
            context.Error(InvalidOverride, "<Query> needs a criterion: 'field' and 'value', 'type', or both", element);
        }

        if (field is not null)
        {
            if (!schema.HasField(field.Value))
            {
                context.Error(InvalidOverride, $"no element of the schema has a field '{field.Value}'", field);
            }

            query.Field = field.Value;
        }

        if (value is not null)
        {
            query.Value = value.Value;
        }

        if (type is not null)
        {
            if (!schema.HasElement(type.Value))
            {
                context.Error(InvalidOverride, $"no element of the schema is named <{type.Value}>", type);
            }

            query.Type = type.Value;
        }

        if (index is not null)
        {
            if (index.Value == "all")
            {
                query.All = true;
            }
            else if (uint.TryParse(index.Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint position))
            {
                query.Position = position;
            }
            else
            {
                context.Error(InvalidOverride, $"'index' is '{index.Value}', expected a position from 0 or 'all'", index);
            }
        }

        foreach (XElement child in element.Elements())
        {
            string name = child.Name.LocalName;
            if (name == "Query")
            {
                if (CompileQuery(child, schema, context, tagRegistry) is Query nested)
                {
                    query.Queries.Add(nested);
                }

                continue;
            }

            if (!Operations.Contains(name))
            {
                context.Error(InvalidOverride, $"unknown element <{name}> in <Query>; expected <Query>, {string.Join(", ", Operations.Select(o => "<" + o + ">"))}", child);
                continue;
            }

            if (CompileOperation(child, schema, context, tagRegistry) is Operation operation)
            {
                query.Operations.Add(operation);
            }
        }

        if (query.Queries.Count == 0 && query.Operations.Count == 0 && context.Diagnostics.ErrorCount == errors)
        {
            context.Error(InvalidOverride, "<Query> needs an operation or a nested <Query>", element);
        }

        return context.Diagnostics.ErrorCount == errors ? query : null;
    }

    private static Operation? CompileOperation(XElement element, Reachable schema, WriteContext context, string? tagRegistry)
    {
        var operation = new Operation
        {
            Source = context.Source.Position(element),
            Kind = element.Name.LocalName switch
            {
                "Replace" => Operation.Types.Kind.Replace,
                "Add" => Operation.Types.Kind.Add,
                "InsertBefore" => Operation.Types.Kind.InsertBefore,
                "InsertAfter" => Operation.Types.Kind.InsertAfter,
                "Remove" => Operation.Types.Kind.Remove,
                _ => Operation.Types.Kind.Set,
            },
        };

        string[] allowed = operation.Kind switch
        {
            Operation.Types.Kind.Add => ["index"],
            Operation.Types.Kind.Set => ["field", "value"],
            _ => [],
        };
        foreach (XAttribute attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && !allowed.Contains(attribute.Name.LocalName))
            {
                context.Error(InvalidOverride, $"unknown attribute '{attribute.Name.LocalName}' in <{element.Name.LocalName}>", attribute);
                return null;
            }
        }

        switch (operation.Kind)
        {
            case Operation.Types.Kind.Remove:
                if (element.Nodes().Any(n => n is XElement || (n is XText t && t.Value.Trim().Length > 0)))
                {
                    context.Error(InvalidOverride, "<Remove/> is empty", element);
                    return null;
                }

                return operation;
            case Operation.Types.Kind.Set:
                XAttribute? field = element.Attribute("field");
                XAttribute? value = element.Attribute("value");
                if (field is null || value is null || element.HasElements)
                {
                    context.Error(InvalidOverride, "<Set> takes a 'field' and a 'value' attribute, and nothing else", element);
                    return null;
                }

                if (!schema.HasField(field.Value))
                {
                    context.Error(InvalidOverride, $"no element of the schema has a field '{field.Value}'", field);
                    return null;
                }

                operation.Field = field.Value;
                operation.Value = value.Value;
                return operation;
        }

        if (element.Attribute("index") is XAttribute index)
        {
            if (!uint.TryParse(index.Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint position))
            {
                context.Error(InvalidOverride, $"'index' is '{index.Value}', expected a position from 0", index);
                return null;
            }

            operation.Index = position;
        }

        if (!element.HasElements)
        {
            context.Error(InvalidOverride, $"<{element.Name.LocalName}> needs the elements to insert", element);
            return null;
        }

        int errors = context.Diagnostics.ErrorCount;
        foreach (XElement child in element.Elements())
        {
            if (tagRegistry is not null)
            {
                NofParser.ParseMember(child, tagRegistry, context.Owner, context);
            }
            else
            {
                schema.Validate(child, context);
            }

            operation.Content.Add(GenericTree.FromElement(child, context.Source.RelativePath));
        }

        return context.Diagnostics.ErrorCount == errors ? operation : null;
    }

    private static void NoAttributes(XElement content, WriteContext context)
    {
        foreach (XAttribute attribute in content.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                context.Error(InvalidOverride, "the <Content> of an override takes no attributes", attribute);
            }
        }
    }

    /// <summary>The element and field names of every message reachable from a resource type.</summary>
    private sealed class Reachable
    {
        public static readonly Reachable Tag = new();

        private readonly Dictionary<string, FieldDescriptor> _elements = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fields = new(StringComparer.Ordinal);

        public Reachable(MessageDescriptor root)
        {
            var queue = new Queue<MessageDescriptor>([root]);
            var seen = new HashSet<string>(StringComparer.Ordinal) { root.FullName };
            while (queue.Count > 0)
            {
                foreach (FieldDescriptor field in queue.Dequeue().Fields.InFieldNumberOrder())
                {
                    _elements.TryAdd(Names.Element(field), field);
                    _fields.Add(Names.Element(field));
                    _fields.Add(Names.Attribute(field));
                    if (field.FieldType == FieldType.Message && seen.Add(field.MessageType.FullName))
                    {
                        queue.Enqueue(field.MessageType);
                    }
                }
            }
        }

        private Reachable()
        {
            _elements["Member"] = null!;
            _fields.Add("id");
            _fields.Add("optional");
        }

        public bool HasField(string name) => _fields.Contains(name);

        public bool HasElement(string name) => _elements.ContainsKey(name);

        public void Validate(XElement element, WriteContext context)
        {
            if (!_elements.TryGetValue(element.Name.LocalName, out FieldDescriptor? field))
            {
                context.Error(MessageWriter.UnknownField, $"no element of the schema is named <{element.Name.LocalName}>", element);
                return;
            }

            if (field.GetOptions()?.HasExtension(OptionsExtensions.Generic) == true)
            {
                return;
            }

            if (field.FieldType == FieldType.Message)
            {
                MessageWriter.Write(field.MessageType, element, context);
            }
            else
            {
                MessageWriter.WriteFields([(field, element)], context);
            }
        }
    }
}
