// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nobara.Analyzers.Tests;

/// <summary>
/// Runs an analyzer on C# source written in markup and compares its
/// diagnostics with the expected ones. An expected diagnostic is written
/// <c>{|NOB101:code|}</c> around the code it is reported on; markers can nest.
/// </summary>
/// <remarks>
/// Sources compile against the assemblies of the running .NET, so the tests
/// need no network. The attributes recognized by the analyzers are added to
/// every compilation, in their namespace, as the core defines them.
/// </remarks>
internal static class AnalyzerVerifier
{
    /// <summary>The recognized attributes, as the core declares them.</summary>
    public const string Attributes = """
        namespace Nobara.Core
        {
            using System;

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property)]
            public sealed class HotPathAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method)]
            public sealed class LoopCallbackAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
            public sealed class UsesOptionalDependencyAttribute : Attribute
            {
                public UsesOptionalDependencyAttribute(string moduleId) => ModuleId = moduleId;

                public string ModuleId { get; }
            }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> PlatformReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path) is var name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "System.dll" or "netstandard.dll" or "mscorlib.dll"))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();

    /// <summary>Checks that the analyzer reports exactly the diagnostics marked in <paramref name="markup"/>.</summary>
    public static void Verify(DiagnosticAnalyzer analyzer, string markup, Setup? setup = null)
    {
        (string source, List<Expected> expected) = Parse(markup);
        ImmutableArray<Diagnostic> actual = Run(analyzer, source, setup ?? new Setup());

        string[] expectedKeys = expected.Select(e => $"{e.Id} {Excerpt(source, e.Span)}").Order(StringComparer.Ordinal).ToArray();
        string[] actualKeys = actual.Select(d => $"{d.Id} {Excerpt(source, d.Location.SourceSpan)}").Order(StringComparer.Ordinal).ToArray();
        if (!expectedKeys.SequenceEqual(actualKeys))
        {
            var message = new StringBuilder("Diagnostics differ.").AppendLine()
                .AppendLine("Expected:").AppendJoin(Environment.NewLine, expectedKeys.Select(k => "  " + k)).AppendLine()
                .AppendLine("Actual:").AppendJoin(Environment.NewLine, actual.Select(d => $"  {d.Id} {Excerpt(source, d.Location.SourceSpan)} -> {d.GetMessage()}"));
            Assert.Fail(message.ToString());
        }
    }

    /// <summary>Runs the analyzer and returns its diagnostics, for tests that inspect messages.</summary>
    public static ImmutableArray<Diagnostic> Run(DiagnosticAnalyzer analyzer, string source, Setup? setup = null)
    {
        setup ??= new Setup();
        var parse = new CSharpParseOptions(setup.LanguageVersion);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source, parse, path: "Source.cs"),
            CSharpSyntaxTree.ParseText(Attributes, parse, path: "Attributes.cs"),
        };
        trees.AddRange(setup.ExtraSources.Select(extra => CSharpSyntaxTree.ParseText(extra.Source, parse, path: extra.Path)));

        var references = PlatformReferences.ToList();
        foreach ((string name, string librarySource) in setup.Libraries)
        {
            CSharpCompilation library = CSharpCompilation.Create(
                name,
                [CSharpSyntaxTree.ParseText(librarySource, parse)],
                PlatformReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            AssertNoErrors(library);
            references.Add(library.ToMetadataReference());
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Nobara.Sample",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        AssertNoErrors(compilation);

        var global = new Dictionary<string, string>();
        if (setup.OptionalModules is not null)
        {
            global["build_property.NobaraOptionalModules"] = setup.OptionalModules;
        }

        var options = new AnalyzerOptions([], new ConfigProvider(new ConfigOptions(global)));
        ImmutableArray<Diagnostic> diagnostics = compilation
            .WithAnalyzers([analyzer], options)
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();

        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(diagnostics, d => Assert.False(d.Id.StartsWith("AD", StringComparison.Ordinal), d.ToString()));
        return diagnostics.Where(d => d.Location.SourceTree?.FilePath == "Source.cs").ToImmutableArray();
    }

    private static void AssertNoErrors(Compilation compilation)
    {
        Diagnostic[] errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "The sample does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    private static string Excerpt(string source, Microsoft.CodeAnalysis.Text.TextSpan span) => $"[{source.Substring(span.Start, span.Length)}]@{span.Start}";

    private static (string Source, List<Expected> Expected) Parse(string markup)
    {
        var source = new StringBuilder();
        var expected = new List<Expected>();
        var open = new Stack<(string Id, int Start)>();
        for (int i = 0; i < markup.Length; i++)
        {
            if (markup[i] == '{' && i + 1 < markup.Length && markup[i + 1] == '|')
            {
                int colon = markup.IndexOf(':', i);
                open.Push((markup.Substring(i + 2, colon - i - 2), source.Length));
                i = colon;
            }
            else if (markup[i] == '|' && i + 1 < markup.Length && markup[i + 1] == '}' && open.Count > 0)
            {
                (string id, int start) = open.Pop();
                expected.Add(new Expected(id, new Microsoft.CodeAnalysis.Text.TextSpan(start, source.Length - start)));
                i++;
            }
            else
            {
                source.Append(markup[i]);
            }
        }

        Assert.Empty(open);
        return (source.ToString(), expected);
    }

    private sealed record Expected(string Id, Microsoft.CodeAnalysis.Text.TextSpan Span);

    private sealed class ConfigOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
    }

    private sealed class ConfigProvider(AnalyzerConfigOptions global) : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty = new ConfigOptions([]);

        public override AnalyzerConfigOptions GlobalOptions => global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }
}

/// <summary>What surrounds the source under test.</summary>
internal sealed record Setup
{
    /// <summary>Gets the value of the <c>NobaraOptionalModules</c> build property, or null when it is not set.</summary>
    public string? OptionalModules { get; init; }

    /// <summary>Gets referenced assemblies, built from source: assembly name and code.</summary>
    public (string Name, string Source)[] Libraries { get; init; } = [];

    /// <summary>Gets further source files of the compilation, which are not checked against markup.</summary>
    public (string Path, string Source)[] ExtraSources { get; init; } = [];

    /// <summary>Gets the C# version of the compilation.</summary>
    public LanguageVersion LanguageVersion { get; init; } = LanguageVersion.Latest;
}
