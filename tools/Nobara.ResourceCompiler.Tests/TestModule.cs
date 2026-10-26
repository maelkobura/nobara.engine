// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Xml.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Nobara.Formats;
using Nobara.ResourceCompiler.Nof;
using Nobara.ResourceCompiler.Plugins;
using Nobara.ResourceCompiler.Tests.Schemas;

namespace Nobara.ResourceCompiler.Tests;

/// <summary>A module in a scratch folder: resources written by a test, compiled with the test schemas.</summary>
internal sealed class TestModule : IDisposable
{
    public TestModule(string id = "example.blocks")
    {
        Id = id;
        Root = Path.Combine(Path.GetTempPath(), "nobara-compiler-tests", Guid.NewGuid().ToString("N"));
        Resources = Path.Combine(Root, "Resources");
        Directory.CreateDirectory(Resources);
    }

    public string Id { get; }

    public string Root { get; }

    public string Resources { get; }

    public byte[]? Schemas { get; set; } = TestSchemas.Content;

    public List<CompiledModule> Dependencies { get; } = [];

    public List<ResourcePlugin> Plugins { get; } = [];

    public string? CacheFile { get; set; }

    /// <summary>Wraps resource elements in a root element.</summary>
    public static string NofText(string ns, string body) => $"<Nobara version=\"1.0.0\" namespace=\"{ns}\">\n{body}\n</Nobara>";

    /// <summary>Writes a file under the resources folder and returns its full path.</summary>
    public string Write(string relativePath, string content)
    {
        string path = Path.Combine(Resources, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public ResourceCompiler Compiler() => new(new ResourceCompilerOptions
    {
        ModuleId = Id,
        ResourcesDirectory = Resources,
        Schemas = Schemas,
        Dependencies = Dependencies,
        Plugins = Plugins,
        CacheFile = CacheFile,
    });

    public IEnumerable<string> NofFiles() => Directory.EnumerateFiles(Resources, "*.nof", SearchOption.AllDirectories);

    public CompilationResult Compile() => Compiler().Compile(NofFiles());

    /// <summary>Compiles the module and returns it as a dependency of another module.</summary>
    public CompiledModule CompileAsDependency()
    {
        CompilationResult result = Compile();
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return new CompiledModule(new ModuleManifest { Format = 1, Id = Id, Version = "1.0.0" }, Schemas, result.Bundle!);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // The folder is in the temporary directory.
        }
    }
}

/// <summary>Serialized descriptor sets of the test schemas.</summary>
internal static class TestSchemas
{
    public static byte[] Content => Set(ContentReflection.Descriptor);

    public static byte[] Set(params FileDescriptor[] files)
    {
        var set = new FileDescriptorSet();
        var seen = new HashSet<string>();
        void Add(FileDescriptor file)
        {
            foreach (FileDescriptor dependency in file.Dependencies)
            {
                Add(dependency);
            }

            if (seen.Add(file.Name))
            {
                set.File.Add(FileDescriptorProto.Parser.ParseFrom(file.SerializedData));
            }
        }

        foreach (FileDescriptor file in files)
        {
            Add(file);
        }

        return set.ToByteArray();
    }
}

/// <summary>Assertions on diagnostics.</summary>
internal static class Expect
{
    public static Diagnostic Error(CompilationResult result, string code) => Error(result.Diagnostics, code);

    public static Diagnostic Error(IReadOnlyList<Diagnostic> diagnostics, string code)
    {
        Diagnostic? found = diagnostics.FirstOrDefault(d => d.Code == code && d.Severity == DiagnosticSeverity.Error);
        Assert.True(found is not null, $"Expected error {code}, got:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
        return found;
    }

    public static Diagnostic Warning(IReadOnlyList<Diagnostic> diagnostics, string code)
    {
        Diagnostic? found = diagnostics.FirstOrDefault(d => d.Code == code && d.Severity == DiagnosticSeverity.Warning);
        Assert.True(found is not null, $"Expected warning {code}, got:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
        return found;
    }

    public static ResourceBundle Success(CompilationResult result)
    {
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Bundle!;
    }
}

/// <summary>
/// A plugin that defines the Gem type by itself, validates it, derives an
/// item from gems and blocks, converts old widgets, and changes text files.
/// </summary>
internal sealed class TestPlugin : ResourcePlugin
{
    public override IReadOnlyCollection<string> ResourceTypes => ["Gem", "Block", "Texture"];

    public override IEnumerable<FileDescriptor> Schemas => [GemReflection.Descriptor];

    public override IEnumerable<SchemaConverter> Converters => [new WidgetConverter()];

    public override void Validate(ResourceContext context)
    {
        if (context.Message.Name == "Gem" && context.Parse(Gem.Parser).Color == "black")
        {
            context.Error("TEST001", "black gems are not allowed", context.Content.Element("Color"));
        }
    }

    public override IEnumerable<DerivedResource> Derive(ResourceContext context)
    {
        bool create = context.Message.Name switch
        {
            "Gem" => context.Parse(Gem.Parser).CreateItem,
            "Block" => context.Parse(Block.Parser).CreateItem,
            _ => false,
        };
        if (create)
        {
            yield return new DerivedResource("Item", new XElement("Content", new XElement("MaxStack", "64")));
        }
    }

    public override byte[] ProcessFile(FileContext context) =>
        context.Path.EndsWith(".txt", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(context.Content).ToUpperInvariant()) : context.Content;

    private sealed class WidgetConverter : SchemaConverter
    {
        public override string Element => "Widget";

        public override uint FromVersion => 1;

        // Version 1 called the title Caption.
        public override XElement Convert(XElement resource)
        {
            var copy = new XElement(resource);
            copy.Attribute("version")?.Remove();
            XElement? caption = copy.Element("Content")?.Element("Caption");
            caption?.ReplaceWith(new XElement("Title", caption.Value));
            return copy;
        }
    }
}
