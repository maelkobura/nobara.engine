// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Nobara.Core.Resources;
using Nobara.Core.Tests.Schemas;
using Nobara.Core.Tests.Support;
using Nobara.ResourceCompiler.Nof;
using static Nobara.Core.Tests.Support.TestResources;
using F = Nobara.Formats;

namespace Nobara.Core.Tests;

/// <summary>Inventory, duplicates, overrides, tags, free properties, and decoders.</summary>
public sealed class ResourceTests : IDisposable
{
    private const string Blocks = "example.blocks";

    private const string Dirt = """
        <Block>
          <Name>Dirt</Name>
          <Content>
            <DisplayName>Dirt</DisplayName>
            <Hardness>15</Hardness>
            <Drops><Drop item="item:a" min="1" /><Drop item="item:b" /><Drop item="item:c" /></Drops>
          </Content>
        </Block>
        <Block><Name>Stone</Name><Content><DisplayName>Stone</DisplayName><Hardness>30</Hardness></Content></Block>
        """;

    private readonly TestDistribution _distribution = new();
    private CompiledModule _blocks = null!;
    private int _decoded;

    public void Dispose() => _distribution.Dispose();

    // The module that defines the block type, decodes it, and defines resources.
    private TestDistribution WithBlocks(string nof, Action<ModuleContext>? register = null, Action<ModuleContext>? initialize = null, IReadOnlyDictionary<string, string>? files = null)
    {
        var sources = new Dictionary<string, string>(files ?? new Dictionary<string, string>()) { ["blocks.nof"] = Nof("Example/Blocks", nof) };
        F.ResourceBundle bundle = Compile(Blocks, sources);
        _blocks = Dependency(Blocks, bundle);
        _distribution.Module(Blocks, behavior: m =>
        {
            m.OnRegister = c =>
            {
                c.Resources.RegisterDecoder("block", data =>
                {
                    _decoded++;
                    return Block.Parser.ParseFrom(data.Content.Span);
                });
                register?.Invoke(c);
            };
            m.OnInitialize = initialize;
        });
        return _distribution.Data(Blocks, bundle, TestResources.Schemas, files?.Where(f => !f.Key.EndsWith(".nof", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)));
    }

    // A module that depends on the blocks module and brings resources.
    private CompiledModule Mod(string id, string nof, string ns = "Example/Blocks", Action<TestModule>? behavior = null, CompiledModule[]? also = null, string? shortName = null)
    {
        CompiledModule[] dependencies = [_blocks, .. also ?? []];
        F.ResourceBundle bundle = Compile(id, Nof(ns, nof), dependencies, ownSchemas: false);
        _distribution.Module(
            id,
            m =>
            {
                foreach (CompiledModule dependency in dependencies)
                {
                    m.Requires(dependency.Id);
                }

                m.ShortName = shortName ?? string.Empty;
            },
            behavior);
        _distribution.Data(id, bundle);
        return Dependency(id, bundle, ownSchemas: false);
    }

    private static ResourceLoadException LoadFails(TestDistribution distribution) =>
        Assert.Throws<ResourceLoadException>(() => distribution.Load());

    [Fact]
    public void Decodes_each_resource_once_with_the_overrides_applied()
    {
        IReadOnlyList<Block>? all = null;
        Block? stone = null;
        ResourceData? dirtData = null;
        WithBlocks(Dirt);
        Mod("example.mod", """
            <Block override="true">
              <Name>Dirt</Name>
              <Content>
                <Hardness>20</Hardness>
                <Shape>HalfSlab</Shape>
                <Query type="Drop" field="item" value="item:a"><Set field="max" value="3" /></Query>
                <Query type="Drop" field="item" value="item:b"><Remove /></Query>
                <Query type="Drop" field="item" value="item:c"><Replace><Drop item="item:z" min="2" /></Replace></Query>
                <Query type="Drops">
                  <Query type="Drop" index="all"><Set field="chance" value="0.5" /></Query>
                  <Add index="0"><Drop item="item:first" /></Add>
                </Query>
                <Query type="Drop" field="item" value="item:a">
                  <InsertAfter><Drop item="item:after" /></InsertAfter>
                  <InsertBefore><Drop item="item:before" /></InsertBefore>
                </Query>
              </Content>
            </Block>
            """, behavior: m => m.OnInitialize = c =>
            {
                all = c.Resources.GetAll<Block>("block");
                stone = c.Resources.Get<Block>("block:example.blocks.stone");
                dirtData = c.Resources.Find("block://example/blocks/DIRT");
            });

        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        Block dirt = all![0];
        Assert.Equal(20, dirt.Hardness);
        Assert.Equal(Shape.HalfSlab, dirt.Shape);
        Assert.Equal(["item:first", "item:before", "item:a", "item:after", "item:z"], dirt.Drops.Drop.Select(d => d.Item));
        Drop a = dirt.Drops.Drop[2];
        Assert.Equal((1, 3, 0.5f), (a.Min, a.Max, a.Chance));
        Assert.Equal((2, 0.5f), (dirt.Drops.Drop[4].Min, dirt.Drops.Drop[4].Chance));
        Assert.Equal(0f, dirt.Drops.Drop[0].Chance);
        Assert.Equal(30, stone!.Hardness);
        Assert.Equal(2, _decoded);

        Assert.Equal("block:example.blocks.dirt", dirtData!.ObjectId);
        Assert.Equal(Blocks, dirtData.ModuleId);
        Assert.Equal(["example.mod"], dirtData.OverriddenBy);
        byte[] compiledHash = _blocks.Resources.Resources.Single(r => r.Name == "Dirt").ContentHash.ToByteArray();
        Assert.NotEqual(compiledHash, dirtData.ContentHash.ToArray());
    }

    [Fact]
    public void Applies_overrides_by_priority_then_in_load_order()
    {
        Block? dirt = null;
        Block? stone = null;
        WithBlocks(Dirt, initialize: c =>
        {
            dirt = c.Resources.Get<Block>("block:example.blocks.dirt");
            stone = c.Resources.Get<Block>("block:example.blocks.stone");
        });
        CompiledModule first = Mod("example.first", """
            <Block override="true" priority="5"><Name>Dirt</Name><Content><Hardness>30</Hardness></Content></Block>
            <Block override="true"><Name>Stone</Name><Content><Hardness>50</Hardness></Content></Block>
            """);
        Mod("example.second", """
            <Block override="true"><Name>Dirt</Name><Content><Hardness>40</Hardness></Content></Block>
            <Block override="true"><Name>Stone</Name><Content><Hardness>60</Hardness></Content></Block>
            """, also: [first]);

        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        Assert.Equal(30, dirt!.Hardness);
        Assert.Equal(60, stone!.Hardness);
    }

    [Fact]
    public void Refuses_an_override_whose_target_or_query_is_missing_unless_it_is_optional()
    {
        WithBlocks(Dirt);
        Mod("example.mod", """
            <Block override="true"><Name>Ghost</Name><Content><Hardness>1</Hardness></Content></Block>
            <Block override="true" optional="true"><Name>Phantom</Name><Content><Hardness>1</Hardness></Content></Block>
            <Block override="true">
              <Name>Dirt</Name>
              <Content><Query type="Drop" field="item" value="item:nothing"><Remove /></Query></Content>
            </Block>
            <Block override="true" optional="true">
              <Name>Stone</Name>
              <Content><Query type="Drop" field="item" value="item:nothing"><Remove /></Query></Content>
            </Block>
            """);

        ResourceLoadException exception = LoadFails(_distribution);

        Assert.Equal(2, exception.Problems.Count);
        ResourceProblem missing = exception.Problems.Single(p => p.Kind == ResourceProblemKind.MissingTarget);
        Assert.Equal(("example.mod", "block:example.blocks.ghost"), (missing.ModuleId, missing.ResourceId));
        Assert.StartsWith("Module 'example.mod' overrides 'block:example.blocks.ghost', which no loaded module defines", missing.Message, StringComparison.Ordinal);
        ResourceProblem query = exception.Problems.Single(p => p.Kind == ResourceProblemKind.QueryWithoutMatch);
        Assert.Equal("In the override of 'block:example.blocks.dirt' by module 'example.mod', the query <Query type=\"Drop\" field=\"item\" value=\"item:nothing\"> selects nothing in <Content>.", query.Message);
        Assert.Equal("resources.nof", query.Source.File);
        Assert.Equal(0, _distribution.Instances);
    }

    [Fact]
    public void Refuses_the_same_object_defined_by_two_modules()
    {
        _distribution.Module("example.types").Data("example.types", null, TestResources.Schemas);
        _blocks = Dependency("example.types", new F.ResourceBundle { Format = 1, Module = "example.types" });
        Mod("example.a", "<Block><Name>Dirt</Name><Content><DisplayName>A</DisplayName></Content></Block>");
        Mod("example.b", "<Block><Name>dirt</Name><Content><DisplayName>B</DisplayName></Content></Block>");

        ResourceLoadException exception = LoadFails(_distribution);

        ResourceProblem problem = Assert.Single(exception.Problems);
        Assert.Equal(ResourceProblemKind.Duplicate, problem.Kind);
        Assert.Equal("Modules 'example.a' and 'example.b' both define 'block:example.blocks.dirt'; one of them must use override=\"true\".", problem.Message);
        Assert.Equal(0, _distribution.Instances);
    }

    [Fact]
    public void Merges_the_contributions_to_a_tag_and_applies_its_overrides()
    {
        IReadOnlyList<ResourceTag>? tags = null;
        ResourceTag? wood = null;
        WithBlocks(
            """
            <Block><Name>Oak</Name><Content><DisplayName>Oak</DisplayName></Content><Tags><Tag>#example.logs</Tag></Tags></Block>
            <Block><Name>Planks</Name><Content><DisplayName>Planks</DisplayName></Content></Block>
            """,
            initialize: c =>
            {
                tags = c.Resources.GetTags("block");
                c.Resources.TryGetTag("#block:example.wood", out wood);
            },
            files: new Dictionary<string, string>
            {
                ["tags.nof"] = Nof("Example", """
                    <Tag registry="block">
                      <Name>Wood</Name>
                      <Content>
                        <Member>block:example.blocks.planks</Member>
                        <Member>#example.logs</Member>
                        <Member optional="true">block:other.blocks.birch</Member>
                        <Member optional="true">block:other.blocks.maple</Member>
                      </Content>
                    </Tag>
                    """),
            });
        CompiledModule mod = Mod("example.mod", "<Block><Name>Birch</Name><Content><DisplayName>Birch</DisplayName></Content><Tags><Tag>#example.wood</Tag></Tags></Block>", ns: "Other/Blocks");
        Mod("example.cleanup", """
            <Tag registry="block" override="true">
              <Name>Wood</Name>
              <Content><Query field="id" value="block:example.blocks.planks"><Remove /></Query></Content>
            </Tag>
            """, ns: "Example", also: [mod]);

        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        Assert.Equal(["#block:example.logs", "#block:example.wood"], tags!.Select(t => t.Id));
        Assert.Equal(["block:example.blocks.oak", "block:other.blocks.birch"], wood!.Members);
        Assert.True(wood.Contains("block:other.blocks.birch"));
        Assert.False(wood.Contains("block:example.blocks.planks"));
    }

    [Fact]
    public void Refuses_tags_that_contain_each_other_across_modules()
    {
        _distribution.Module("example.types").Data("example.types", null, TestResources.Schemas);
        _blocks = Dependency("example.types", new F.ResourceBundle { Format = 1, Module = "example.types" });
        Mod("example.a", "<Tag registry=\"block\"><Name>A</Name><Content><Member optional=\"true\">#example.b</Member></Content></Tag>", ns: "Example");
        Mod("example.b", "<Tag registry=\"block\"><Name>B</Name><Content><Member optional=\"true\">#example.a</Member></Content></Tag>", ns: "Example");

        ResourceProblem problem = Assert.Single(LoadFails(_distribution).Problems);

        Assert.Equal(ResourceProblemKind.TagCycle, problem.Kind);
        Assert.Equal("Tags contain each other: #block:example.a -> #block:example.b -> #block:example.a.", problem.Message);
    }

    [Fact]
    public void Reads_a_free_property_in_its_qualified_form_for_the_module_it_names()
    {
        var weights = new Dictionary<string, double?>();
        WithBlocks("""
            <Block>
              <Name>Dirt</Name>
              <Content><DisplayName>Dirt</DisplayName></Content>
              <Properties><Weight>2</Weight><Motion.Weight>3.5</Motion.Weight></Properties>
            </Block>
            <Block><Name>Stone</Name><Content><DisplayName>Stone</DisplayName></Content></Block>
            """);
        void Reader(TestModule module)
        {
            FreeProperty? weight = null;
            module.OnRegister = c => weight = c.Resources.DeclareProperty("Weight", PropertyType.Float);
            module.OnInitialize = c =>
            {
                foreach (ResourceData block in c.Resources.GetData("block"))
                {
                    weights[module.Id + " " + block.Name] = weight!.TryGetFloat(block, out double value) ? value : null;
                }
            };
        }

        Mod("example.motion", string.Empty, behavior: Reader, shortName: "Motion");
        Mod("example.integrity", string.Empty, behavior: Reader);

        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        Assert.Equal(3.5, weights["example.motion Dirt"]);
        Assert.Equal(2, weights["example.integrity Dirt"]);
        Assert.Null(weights["example.motion Stone"]);
    }

    [Theory]
    [InlineData("<Weight>2</Weight>", PropertyType.Bool, ResourceProblemKind.PropertyTypeConflict, "read the free property 'Weight' as Float and Bool")]
    [InlineData("<Weight>heavy</Weight>", PropertyType.Float, ResourceProblemKind.InvalidProperty, "The free property 'Weight' of 'block:example.blocks.dirt' is 'heavy', but module 'example.motion' reads it as Float.")]
    public void Refuses_a_free_property_that_its_readers_cannot_read(string property, PropertyType other, ResourceProblemKind kind, string message)
    {
        WithBlocks($"<Block><Name>Dirt</Name><Content><DisplayName>Dirt</DisplayName></Content><Properties>{property}</Properties></Block>");
        CompiledModule motion = Mod("example.motion", string.Empty, behavior: m => m.OnRegister = c => c.Resources.DeclareProperty("Weight", PropertyType.Float));
        Mod("example.integrity", string.Empty, behavior: m => m.OnRegister = c => c.Resources.DeclareProperty("Weight", other), also: [motion]);
        using Engine engine = _distribution.Load();

        var exception = Assert.Throws<ResourceLoadException>(engine.MainContext.Start);

        ResourceProblem problem = exception.Problems.First(p => p.Kind == kind);
        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
        Assert.Equal(ContextState.Stopped, engine.MainContext.State);
        Assert.DoesNotContain(_distribution.Journal.Lines, l => l.EndsWith(":Initialization", StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_a_qualifier_that_names_no_module_of_the_distribution()
    {
        WithBlocks("<Block><Name>Dirt</Name><Content><DisplayName>Dirt</DisplayName></Content><Properties><Ghost.Weight>1</Ghost.Weight></Properties></Block>");

        ResourceProblem problem = Assert.Single(LoadFails(_distribution).Problems);

        Assert.Equal(ResourceProblemKind.UnknownQualifier, problem.Kind);
        Assert.Equal("The property 'Ghost.Weight' of 'block:example.blocks.dirt' names the short name 'Ghost', which no module of the distribution has.", problem.Message);
    }

    [Fact]
    public void Accepts_a_qualifier_of_a_module_that_this_process_does_not_load()
    {
        WithBlocks("<Block><Name>Dirt</Name><Content><DisplayName>Dirt</DisplayName></Content><Properties><Render.Tint>1</Render.Tint></Properties></Block>");
        _distribution.Module("example.render", m => (m.Side, m.ShortName) = (F.Side.Client, "Render"));

        using Engine engine = _distribution.Load(Side.Server);

        Assert.DoesNotContain(engine.Modules, m => m.Id == "example.render");
    }

    [Fact]
    public void Lets_a_module_read_the_types_of_its_own_and_of_its_dependencies_only()
    {
        Exception? undeclared = null;
        Exception? foreignDecoder = null;
        Exception? tooEarly = null;
        WithBlocks(Dirt, register: c => tooEarly = Record.Exception(() => c.Resources.GetAll<Block>("block")));
        Mod("example.mod", string.Empty, behavior: m => m.OnRegister = c => foreignDecoder = Record.Exception(() => c.Resources.RegisterDecoder("block", d => d)));
        _distribution.Module("example.other", behavior: m => m.OnInitialize = c => undeclared = Record.Exception(() => c.Resources.GetAll<Block>("block")));

        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        Assert.Equal(
            "Module 'example.other' asked for resources of type 'block' of module 'example.blocks', which is not one of its declared dependencies.",
            Assert.IsType<InvalidOperationException>(undeclared).Message);
        Assert.Equal(
            "Module 'example.mod' registers a decoder for 'block', which module 'example.blocks' defines; only the module that defines a type decodes it.",
            Assert.IsType<InvalidOperationException>(foreignDecoder).Message);
        Assert.StartsWith("Module 'example.blocks' cannot do this during registration.", Assert.IsType<InvalidOperationException>(tooEarly).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Opens_the_binary_files_of_a_resource()
    {
        string? picture = null;
        Exception? missing = null;
        WithBlocks(
            "<Block><Name>Dirt</Name><Content><DisplayName>Dirt</DisplayName><Picture>Pictures/dirt.txt</Picture></Content></Block>",
            initialize: c =>
            {
                ResourceData dirt = c.Resources.Find("block:example.blocks.dirt")!;
                using var reader = new StreamReader(c.Resources.OpenFile(dirt, "Pictures/dirt.txt"));
                picture = reader.ReadToEnd();
                missing = Record.Exception(() => c.Resources.OpenFile(dirt, "Pictures/none.txt"));
            },
            files: new Dictionary<string, string> { ["Pictures/dirt.txt"] = "brown" });

        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        Assert.Equal("brown", picture);
        Assert.IsType<FileNotFoundException>(missing);
    }

    [Fact]
    public void Disables_the_module_whose_decoder_throws_and_its_dependents()
    {
        F.ResourceBundle bundle = Compile(Blocks, Nof("Example/Blocks", Dirt));
        _blocks = Dependency(Blocks, bundle);
        _distribution
            .Module(Blocks, behavior: m => m.OnRegister = c => c.Resources.RegisterDecoder<Block>("block", _ => throw new InvalidDataException("bad block")))
            .Data(Blocks, bundle, TestResources.Schemas)
            .Policy(Blocks, F.Policy.Disable);
        Mod("example.mod", string.Empty);
        _distribution.Policy("example.mod", F.Policy.Disable);

        using Engine engine = _distribution.Load();
        engine.MainContext.Start();

        Assert.False(engine.MainContext.IsModuleActive(Blocks));
        Assert.False(engine.MainContext.IsModuleActive("example.mod"));
        Assert.DoesNotContain("example.blocks:Initialization", _distribution.Journal.Lines);
        Assert.Contains(_distribution.Log.Entries, e => e.Message.StartsWith("Module 'example.blocks' threw during initialization", StringComparison.Ordinal) && e.Exception?.Message == "bad block");
    }

    [Theory]
    [InlineData("Example/*", "Example", true)]
    [InlineData("Example/*", "example/Blocks/Stones", true)]
    [InlineData("Example/*", "Examples", false)]
    [InlineData("Example/Blocks", "Example/Blocks", true)]
    [InlineData("Example/Blocks", "Example/Blocks/Stones", false)]
    public void Tells_whether_a_namespace_is_protected(string pattern, string ns, bool expected)
    {
        Assert.Equal(expected, new ProtectedNamespaces([pattern]).IsProtected(ns));
    }

    [Fact]
    public void Gives_the_protected_namespaces_of_the_distribution()
    {
        ProtectedNamespaces? protection = null;
        _distribution.Configure = d => d.ProtectedNamespaces.Add("Example/*");
        using Engine engine = _distribution.Module("example.game", behavior: m => m.OnInitialize = c => protection = c.Resources.ProtectedNamespaces).Load();
        engine.MainContext.Start();

        Assert.True(protection!.IsProtected("Example/Blocks"));
    }
}
