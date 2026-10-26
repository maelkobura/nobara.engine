// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nobara.Analyzers.Tests;

public sealed class HotPathAnalyzerTests
{
    private static void Verify(string markup, Setup? setup = null) =>
        AnalyzerVerifier.Verify(new HotPathAnalyzer(), "using System; using System.Collections.Generic; using System.Linq; using Nobara.Core;\n" + markup, setup);

    [Fact]
    public void Leaves_unmarked_code_alone()
    {
        Verify("""
            class Loader
            {
                object Load(List<int> values) => values.Where(v => v > 0).Select(v => (object)v).ToList() + "done";
            }
            """);
    }

    [Fact]
    public void Accepts_allocation_free_code()
    {
        Verify("""
            struct Cell { public int Value; }
            class Mesher
            {
                [HotPath]
                static int Sum(Span<int> values, ReadOnlySpan<Cell> cells)
                {
                    Span<int> scratch = stackalloc int[4];
                    int total = scratch.Length;
                    foreach (int value in values) total += value;
                    foreach (Cell cell in cells) total += cell.Value;
                    var local = new Cell { Value = total };
                    const string label = "a" + "b";
                    return local.Value + label.Length;
                }
            }
            """);
    }

    [Fact]
    public void Reports_creation_of_a_reference_type()
    {
        Verify("""
            class Chunk { }
            class Mesher
            {
                [HotPath] void Build() { var chunk = {|NOB101:new Chunk()|}; Chunk other = {|NOB101:new()|}; }
            }
            """);
    }

    [Fact]
    public void Reports_arrays_anonymous_types_and_collection_expressions()
    {
        Verify("""
            class Mesher
            {
                [HotPath]
                void Build()
                {
                    int[] a = {|NOB101:new int[8]|};
                    var b = {|NOB101:new { X = 1 }|};
                    List<int> c = {|NOB101:[1, 2]|};
                    Span<int> d = [1, 2];
                    int[] e = [];
                }
            }
            """);
    }

    [Fact]
    public void Reports_a_params_array_with_elements_only()
    {
        Verify("""
            [HotPath] static class Log { public static void Write(params int[] values) { } }
            class Mesher
            {
                [HotPath] void Build() { {|NOB101:Log.Write(1, 2)|}; Log.Write(); }
            }
            """);
    }

    [Fact]
    public void Reports_strings_built_at_run_time()
    {
        Verify("""
            class Mesher
            {
                [HotPath]
                string Name(string prefix, int index)
                {
                    string a = {|NOB101:prefix + "/" + prefix|};
                    {|NOB101:a += prefix|};
                    return {|NOB101:$"{prefix}{index}"|};
                }
            }
            """);
    }

    [Fact]
    public void Accepts_an_interpolated_string_handler_of_the_callee()
    {
        Verify("""
            using System.Runtime.CompilerServices;
            [InterpolatedStringHandler, HotPath]
            ref struct LogHandler
            {
                public LogHandler(int literalLength, int formattedCount) { }
                public void AppendLiteral(string text) { }
                public void AppendFormatted<T>(T value) { }
            }
            [HotPath] static class Log { public static void Debug(ref LogHandler message) { } }
            class Mesher
            {
                [HotPath] void Build(int count) => Log.Debug($"count {count}");
            }
            """);
    }

    [Fact]
    public void Reports_delegates_from_instance_method_groups_only()
    {
        Verify("""
            class Mesher
            {
                [HotPath] static void Static() { }
                [HotPath] void Instance() { }
                [HotPath] void Build() { Action a = Static; Action b = {|NOB101:Instance|}; }
            }
            """);
    }

    [Fact]
    public void Reports_delegates_from_static_method_groups_before_csharp_11()
    {
        Verify(
            """
            class Mesher
            {
                [HotPath] static void Static() { }
                [HotPath] void Build() { Action a = {|NOB101:Static|}; }
            }
            """,
            new Setup { LanguageVersion = LanguageVersion.CSharp10 });
    }

    [Fact]
    public void Reports_type_parameter_creation_without_a_struct_constraint()
    {
        Verify("""
            class Pool
            {
                [HotPath] T Make<T>() where T : new() => {|NOB101:new T()|};
                [HotPath] T MakeValue<T>() where T : struct => new T();
            }
            """);
    }

    [Fact]
    public void Reports_foreach_over_a_reference_enumerator()
    {
        Verify("""
            class Mesher
            {
                [HotPath]
                int Sum(IEnumerable<int> values, List<int> list)
                {
                    int total = 0;
                    foreach (int value in {|NOB101:values|}) total += value;
                    foreach (int value in list) total += value;
                    return total;
                }
            }
            """);
    }

    [Fact]
    public void Reports_iterators_and_with_expressions()
    {
        Verify("""
            record Settings(int Radius);
            class Mesher
            {
                [HotPath] IEnumerable<int> {|NOB101:Cells|}() { yield return 1; }
                [HotPath] Settings Grow(Settings s) => {|NOB101:s with { Radius = 2 }|};
            }
            """);
    }

    [Fact]
    public void Reports_linq_methods_and_query_expressions()
    {
        Verify("""
            class Mesher
            {
                [HotPath]
                int Count(int[] values)
                {
                    int a = {|NOB102:values.Count()|};
                    var b = {|NOB102:from v in values where v > 0 select v|};
                    return a;
                }
            }
            """);
    }

    [Fact]
    public void Reports_capturing_closures_only()
    {
        Verify("""
            class Mesher
            {
                int _radius;
                [HotPath]
                void Build(int size)
                {
                    Func<int, int> a = {|NOB103:x => x + size|};
                    Func<int, int> b = {|NOB103:x => x + _radius|};
                    Func<int, int> c = x => x + 1;
                    Func<int, int> d = static x => x * 2;
                    {|NOB103:int Local() => size;|}
                    static int Pure(int x) => x;
                    Func<int, Func<int, int>> e = x => {|NOB103:y => x + y|};
                }
            }
            """);
    }

    [Fact]
    public void Names_the_captured_variable()
    {
        var diagnostic = Assert.Single(AnalyzerVerifier.Run(new HotPathAnalyzer(), """
            using System; using Nobara.Core;
            class Mesher { int _radius; [HotPath] void Build() { Func<int> a = () => _radius; } }
            """));

        Assert.Equal("Hot path code creates a closure: lambda captures 'this'", diagnostic.GetMessage());
    }

    [Fact]
    public void Reports_boxing_conversions()
    {
        Verify("""
            struct Cell { }
            class Mesher
            {
                [HotPath]
                void Build(int value, Cell cell)
                {
                    object a = {|NOB104:value|};
                    IComparable<int> b = {|NOB104:value|};
                    ValueType c = {|NOB104:cell|};
                    long d = value;
                }
            }
            """);
    }

    [Fact]
    public void Reports_object_methods_that_a_struct_does_not_override()
    {
        Verify("""
            struct Plain { }
            struct Named { [HotPath] public override string ToString() => "named"; }
            enum Face { Up }
            class Mesher
            {
                [HotPath]
                void Build(Plain plain, Named named, Face face)
                {
                    {|NOB104:plain.GetHashCode()|};
                    {|NOB104:plain.GetType()|};
                    {|NOB104:face.ToString()|};
                    named.ToString();
                }
            }
            """);
    }

    [Fact]
    public void Exempts_code_that_throws_and_nameof()
    {
        Verify("""
            class Mesher
            {
                [HotPath]
                int Check(int index, object[] items)
                {
                    if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is negative.");
                    return index > 10 ? throw new InvalidOperationException("Too large: " + index) : nameof(items).Length;
                }
            }
            """);
    }

    [Fact]
    public void Covers_every_member_of_a_marked_type_but_not_its_static_constructor()
    {
        Verify("""
            [HotPath]
            class Mesher
            {
                static readonly int[] Table;
                static Mesher() { Table = new int[4]; }
                int[] _buffer = {|NOB101:new int[4]|};
                int Size => {|NOB101:new int[2]|}.Length;
                void Build() { var o = {|NOB101:new object()|}; }
                class Nested { void Free() { var o = new object(); } }
            }
            """);
    }

    [Fact]
    public void Covers_implementations_and_overrides_of_marked_members()
    {
        Verify("""
            interface IJob { [HotPath] void Execute(); }
            [HotPath] interface IFastJob { void Run(); }
            abstract class Step { [HotPath] public abstract void Apply(); }
            struct Fill : IJob, IFastJob
            {
                public void Execute() { var a = {|NOB101:new object()|}; }
                void IFastJob.Run() { var a = {|NOB101:new object()|}; }
            }
            class Grow : Step { public override void Apply() { var a = {|NOB101:new object()|}; } }
            """);
    }

    [Fact]
    public void Covers_accessors_of_a_marked_property()
    {
        Verify("""
            class Mesher
            {
                [HotPath] object Current => {|NOB101:new object()|};
            }
            """);
    }

    [Fact]
    public void Reports_calls_to_unmarked_code_of_the_same_project()
    {
        Verify("""
            struct Cell
            {
                public Cell(int value) { Value = value; }
                public int Value { get; set; }
                public int Doubled => Value * 2;
                public static Cell operator +(Cell a, Cell b) => new Cell(a.Value + b.Value);
            }
            interface IShape { int Area(); }
            class Mesher
            {
                static int Helper() => 1;
                [HotPath] static int Marked() => 2;
                [HotPath]
                int Build(Cell cell, IShape shape, List<int> list)
                {
                    int a = {|NOB105:Helper()|} + Marked() + list.Count + Math.Max(1, 2);
                    var c = {|NOB105:new Cell(1)|};
                    c.Value = 3;
                    int d = {|NOB105:c.Doubled|};
                    var e = {|NOB105:c + c|};
                    int f = {|NOB105:shape.Area()|};
                    int g = Local();
                    return a + d + f + g;
                    static int Local() => 4;
                }
            }
            """);
    }

    [Fact]
    public void Accepts_calls_to_code_marked_through_its_type_or_contract()
    {
        Verify("""
            [HotPath] struct Cell { public Cell(int v) { Value = v; } public int Value; public int Doubled => Value * 2; }
            [HotPath] interface IShape { int Area(); }
            class Mesher
            {
                [HotPath] int Build(IShape shape) => new Cell(1).Doubled + shape.Area();
            }
            """);
    }

    [Fact]
    public void Skips_generated_code()
    {
        AnalyzerVerifier.Verify(new HotPathAnalyzer(), """
            // <auto-generated/>
            using Nobara.Core;
            class Mesher { [HotPath] object Build() => new object(); }
            """);
    }

    [Fact]
    public void Reports_every_diagnostic_as_an_error()
    {
        Assert.All(new HotPathAnalyzer().SupportedDiagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.DefaultSeverity));
    }
}
