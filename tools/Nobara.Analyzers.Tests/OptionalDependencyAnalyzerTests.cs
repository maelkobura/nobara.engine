// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Analyzers.Tests;

public sealed class OptionalDependencyAnalyzerTests
{
    private const string Lighting = """
        namespace Nobara.Lighting
        {
            public sealed class LightService
            {
                public static LightService Shared { get; } = new LightService();
                public int Level(int x) => x;
            }

            public struct LightLevel { public int Value; }

            public interface ILightSource { int Emit(); }
        }
        """;

    private const string Debug = """
        namespace Nobara.Debug
        {
            public static class Overlay { public static void Show() { } }
        }
        """;

    private static readonly Setup Optional = new()
    {
        OptionalModules = "nobara.lighting;nobara.debug",
        Libraries = [("Nobara.Lighting", Lighting), ("Nobara.Debug", Debug)],
    };

    private static void Verify(string markup, Setup? setup = null) =>
        AnalyzerVerifier.Verify(new OptionalDependencyAnalyzer(), "using System; using System.Collections.Generic; using Nobara.Core; using Nobara.Lighting; using Nobara.Debug;\n" + markup, setup ?? Optional);

    [Fact]
    public void Accepts_a_required_dependency_used_anywhere()
    {
        Verify(
            """
            class Renderer { LightService _light = LightService.Shared; int Level() => _light.Level(1); }
            """,
            Optional with { OptionalModules = null });
    }

    [Fact]
    public void Reports_each_statement_or_declaration_that_uses_an_optional_dependency_outside_isolation()
    {
        Verify("""
            class Renderer
            {
                {|NOB401:LightService|} _light;
                List<{|NOB401:LightLevel|}> _levels = new();
                int Level(int x)
                {
                    {|NOB401:var|} light = LightService.Shared;
                    int a = {|NOB401:light|}.Level(x);
                    int b = {|NOB401:LightService|}.Shared.Level(x);
                    {|NOB401:Overlay|}.Show();
                    return a + b;
                }
                void Apply({|NOB401:ILightSource|} source) { }
            }
            """);
    }

    [Fact]
    public void Accepts_uses_inside_an_isolation_class_and_its_nested_code()
    {
        Verify("""
            [UsesOptionalDependency("nobara.lighting")]
            internal sealed class LightingBridge
            {
                private readonly LightService _light = LightService.Shared;
                public int Level(int x)
                {
                    var light = _light;
                    Func<int, int> level = y => light.Level(y);
                    return level(x);
                }
                private sealed class Cache { LightLevel _last; }
            }
            [UsesOptionalDependency("nobara.lighting"), UsesOptionalDependency("nobara.debug")]
            internal sealed class DebugLighting { void Show() { Overlay.Show(); _ = LightService.Shared; } }
            """);
    }

    [Fact]
    public void Reports_an_isolation_class_that_uses_another_optional_dependency()
    {
        Verify("""
            [UsesOptionalDependency("nobara.lighting")]
            internal sealed class LightingBridge { void Show() => {|NOB401:Overlay|}.Show(); }
            """);
    }

    [Fact]
    public void Ignores_using_directives_nameof_and_documentation()
    {
        Verify("""
            /// <summary>Works without <see cref="LightService"/>.</summary>
            class Renderer { string Name => nameof(LightService); }
            """);
    }

    [Fact]
    public void Reports_what_an_isolation_class_exposes()
    {
        Verify("""
            [UsesOptionalDependency("nobara.lighting")]
            internal sealed class {|NOB402:LightingBridge|} : ILightSource
            {
                public LightService {|NOB402:Light|} => LightService.Shared;
                internal void {|NOB402:Apply|}(LightLevel level) { }
                private LightService Hidden => LightService.Shared;
                public int Emit() => 1;
                public int Level(int x) => Hidden.Level(x);
                public sealed class Handle { public LightLevel {|NOB402:Level|}; }
                private sealed class Inner { public LightLevel Level; }
            }
            """);
    }

    [Fact]
    public void Reports_an_isolation_class_that_names_an_unknown_dependency()
    {
        Verify("""
            [{|NOB403:UsesOptionalDependency("nobara.weather")|}]
            internal sealed class WeatherBridge { }
            """);
    }

    [Fact]
    public void Matches_identifiers_with_assembly_names_without_case()
    {
        Verify(
            """
            class Renderer { {|NOB401:LightService|} _light; }
            """,
            Optional with { OptionalModules = " NOBARA.LIGHTING ; " });
    }

    [Fact]
    public void Names_the_type_and_the_dependency()
    {
        var diagnostic = Assert.Single(AnalyzerVerifier.Run(
            new OptionalDependencyAnalyzer(),
            "using Nobara.Lighting; class Renderer { LightService _light; }",
            Optional));

        Assert.Equal("'LightService' belongs to the optional dependency 'nobara.lighting' and is used outside a class marked [UsesOptionalDependency(\"nobara.lighting\")]", diagnostic.GetMessage());
    }
}
