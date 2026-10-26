// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Formats;
using Nobara.ResourceCompiler.Tests.Schemas;
using static Nobara.ResourceCompiler.Tests.TestModule;

namespace Nobara.ResourceCompiler.Tests;

/// <summary>Tags, tag contributions, and the compilation of overrides.</summary>
public sealed class TagAndOverrideTests : IDisposable
{
    private const string Blocks = """
        <Block><Name>Oak_Log</Name><Content><DisplayName>block.oak_log</DisplayName></Content><Tags><Tag>#example.logs</Tag></Tags></Block>
        <Block><Name>Oak_Planks</Name><Content><DisplayName>block.oak_planks</DisplayName></Content></Block>
        """;

    private readonly TestModule _module = new();

    public void Dispose() => _module.Dispose();

    [Fact]
    public void Turns_a_tags_block_and_a_tag_resource_into_contributions()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", Blocks));
        _module.Write("tags.nof", NofText("Example", """
            <Tag registry="block">
              <Name>Wood</Name>
              <Content>
                <Member>block:example.blocks.oak_planks</Member>
                <Member>#example.logs</Member>
                <Member optional="true">block:other.blocks.birch_log</Member>
              </Content>
            </Tag>
            """));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        TagContribution logs = bundle.Tags.Single(t => t.Tag == "example.logs");
        Assert.Equal("block", logs.Registry);
        Assert.Equal(["block:example.blocks.oak_log"], logs.Members.Select(m => m.Id));
        TagContribution wood = bundle.Tags.Single(t => t.Tag == "example.wood");
        Assert.Equal(["block:example.blocks.oak_planks", "#block:example.logs", "block:other.blocks.birch_log"], wood.Members.Select(m => m.Id));
        Assert.Equal([false, false, true], wood.Members.Select(m => m.Optional));
    }

    [Fact]
    public void Merges_the_contributions_of_a_dependency_to_the_same_tag()
    {
        using var dependency = new TestModule("example.base");
        dependency.Write("blocks.nof", NofText("Example/Blocks", Blocks));
        _module.Dependencies.Add(dependency.CompileAsDependency());
        _module.Write("tags.nof", NofText("Example", "<Tag registry=\"block\"><Name>Wood</Name><Content><Member>#example.logs</Member></Content></Tag>"));

        Expect.Success(_module.Compile());
    }

    [Fact]
    public void Reports_a_cycle_between_nested_tags()
    {
        _module.Write("tags.nof", NofText("Example", """
            <Tag registry="block"><Name>A</Name><Content><Member>#example.b</Member></Content></Tag>
            <Tag registry="block"><Name>B</Name><Content><Member>#example.c</Member></Content></Tag>
            <Tag registry="block"><Name>C</Name><Content><Member>#block:example.a</Member></Content></Tag>
            """));

        Diagnostic error = Expect.Error(_module.Compile(), "NRC404");

        Assert.Contains("#block:example.a -> #block:example.b -> #block:example.c -> #block:example.a", error.Message);
    }

    [Theory]
    [InlineData("<Member>#example.unknown</Member>", "NRC403")]
    [InlineData("<Member>block:example.unknown</Member>", "NRC401")]
    [InlineData("<Member>item:example.stick</Member>", "NRC315")]
    [InlineData("<Member>#item:example.sticks</Member>", "NRC315")]
    [InlineData("<Member>#example.self</Member>", "NRC315")]
    [InlineData("<Item>x</Item>", "NRC315")]
    public void Rejects_an_invalid_or_missing_member(string member, string code)
    {
        _module.Write("tags.nof", NofText("Example", $"<Tag registry=\"block\"><Name>Self</Name><Content>{member}</Content></Tag>"));

        Expect.Error(_module.Compile(), code);
    }

    [Fact]
    public void Ignores_a_missing_optional_nested_tag()
    {
        _module.Write("tags.nof", NofText("Example", "<Tag registry=\"block\"><Name>A</Name><Content><Member optional=\"true\">#example.unknown</Member></Content></Tag>"));

        Expect.Success(_module.Compile());
    }

    [Theory]
    [InlineData("<Tag registry=\"texture\"><Name>A</Name><Content/></Tag>")]
    [InlineData("<Tag registry=\"sound\"><Name>A</Name><Content/></Tag>")]
    [InlineData("<Tag><Name>A</Name><Content/></Tag>")]
    [InlineData("<Texture><Name>T</Name><Content><File>a.png</File></Content><Tags><Tag>#example.a</Tag></Tags></Texture>")]
    [InlineData("<Block><Name>B</Name><Content><DisplayName>block.b</DisplayName></Content><Tags><Tag>#item:example.a</Tag></Tags></Block>")]
    [InlineData("<Block><Name>B</Name><Content><DisplayName>block.b</DisplayName></Content><Tags><Tag>wood</Tag></Tags></Block>")]
    public void Rejects_tags_where_the_registry_does_not_accept_them(string resource)
    {
        _module.Write("a.png", "png");
        _module.Write("tags.nof", NofText("Example", resource));

        Expect.Error(_module.Compile(), "NRC315");
    }

    [Fact]
    public void Compiles_an_override_with_simple_fields_and_queries()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", """
            <Item><Name>Dirt</Name><Content/></Item>
            <Item><Name>Worm</Name><Content/></Item>
            <Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Hardness>15</Hardness><Drops><Drop item="item:example.blocks.dirt" /></Drops></Content></Block>
            """));
        _module.Write("patch.nof", NofText("Example/Blocks", """
            <Block override="true" priority="5">
              <Name>Dirt</Name>
              <Content>
                <Hardness>20</Hardness>
                <Query field="item" value="item:example.blocks.dirt">
                  <InsertAfter><Drop item="item:example.blocks.worm" min="0" max="1" chance="0.1" /></InsertAfter>
                </Query>
                <Query type="Drop" index="all">
                  <Set field="chance" value="0.5" />
                </Query>
              </Content>
            </Block>
            """));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Resource patch = bundle.Resources.Single(r => r.Override is not null);
        Assert.Equal("block:example.blocks.dirt", patch.ObjectId);
        Assert.True(patch.Content.IsEmpty);
        Assert.Equal(5, patch.Override.Priority);
        Assert.Equal(20, Block.Parser.ParseFrom(patch.Override.Merge).Hardness);
        Query insert = patch.Override.Queries[0];
        Assert.Equal(("item", "item:example.blocks.dirt"), (insert.Field, insert.Value));
        Assert.Equal(Operation.Types.Kind.InsertAfter, insert.Operations.Single().Kind);
        Assert.Equal("Drop", insert.Operations.Single().Content.Single().Name);
        Query set = patch.Override.Queries[1];
        Assert.True(set.All);
        Assert.Equal(("chance", "0.5"), (set.Operations.Single().Field, set.Operations.Single().Value));
        Assert.Contains("item:example.blocks.worm", patch.References);
    }

    [Fact]
    public void Validates_an_override_against_the_target_of_a_dependency()
    {
        using var dependency = new TestModule("example.base");
        dependency.Write("blocks.nof", NofText("Example/Blocks", "<Item><Name>Dirt</Name><Content/></Item><Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Drops><Drop item=\"item:example.blocks.dirt\" /></Drops></Content></Block>"));
        _module.Dependencies.Add(dependency.CompileAsDependency());
        _module.Write("patch.nof", NofText("Example/Blocks", """
            <Block override="true">
              <Name>Dirt</Name>
              <Content>
                <Query field="item" value="item:example.blocks.dirt"><Remove/></Query>
                <Query field="item" value="item:example.blocks.stone"><Remove/></Query>
              </Content>
            </Block>
            """));

        CompilationResult result = _module.Compile();

        Expect.Success(result);
        Diagnostic warning = Expect.Warning(result.Diagnostics, "NRC501");
        Assert.Equal(6, warning.Line);
        Assert.Single(result.Diagnostics, d => d.Code == "NRC501");
    }

    [Fact]
    public void Accepts_an_override_whose_target_is_not_known_when_compiled()
    {
        _module.Write("patch.nof", NofText("Other/Blocks", "<Block override=\"true\" optional=\"true\"><Name>Dirt</Name><Content><Hardness>3</Hardness></Content></Block>"));

        Resource patch = Expect.Success(_module.Compile()).Resources.Single();

        Assert.True(patch.Override.Optional);
    }

    [Theory]
    [InlineData("<Drops><Drop item=\"item:x.y\" /></Drops>", "NRC500")]
    [InlineData("<Label>a</Label>", "NRC500")]
    [InlineData("<Hardness>1</Hardness><Hardness>2</Hardness>", "NRC307")]
    [InlineData("<Colour>red</Colour>", "NRC306")]
    [InlineData("<Hardness>500</Hardness>", "NRC310")]
    [InlineData("<Query field=\"item\"><Remove/></Query>", "NRC500")]
    [InlineData("<Query index=\"all\"><Remove/></Query>", "NRC500")]
    [InlineData("<Query field=\"colour\" value=\"red\"><Remove/></Query>", "NRC500")]
    [InlineData("<Query type=\"Leaf\"><Remove/></Query>", "NRC500")]
    [InlineData("<Query type=\"Drop\" index=\"first\"><Remove/></Query>", "NRC500")]
    [InlineData("<Query type=\"Drop\" />", "NRC500")]
    [InlineData("<Query type=\"Drop\"><Delete/></Query>", "NRC500")]
    [InlineData("<Query type=\"Drop\"><Remove>x</Remove></Query>", "NRC500")]
    [InlineData("<Query type=\"Drop\"><Set field=\"chance\" /></Query>", "NRC500")]
    [InlineData("<Query type=\"Drop\"><Add /></Query>", "NRC500")]
    [InlineData("<Query type=\"Drop\"><Replace><Leaf /></Replace></Query>", "NRC306")]
    [InlineData("<Query type=\"Drop\"><Replace><Drop chance=\"2\" item=\"item:x.y\" /></Replace></Query>", "NRC310")]
    [InlineData("<Query type=\"Drop\"><Replace><Drop chance=\"0.5\" /></Replace></Query>", "NRC308")]
    [InlineData("", "NRC500")]
    public void Rejects_an_invalid_override(string content, string code)
    {
        _module.Write("patch.nof", NofText("Example/Blocks", $"<Block override=\"true\"><Name>Dirt</Name><Content>{content}</Content></Block>"));

        Expect.Error(_module.Compile(), code);
    }

    [Fact]
    public void Compiles_an_override_of_a_tag()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", Blocks));
        _module.Write("patch.nof", NofText("Example", """
            <Tag registry="block" override="true">
              <Name>Logs</Name>
              <Content>
                <Query field="id" value="block:example.blocks.oak_log"><Remove/></Query>
                <Query type="Member" index="0"><InsertBefore><Member>block:example.blocks.oak_planks</Member></InsertBefore></Query>
              </Content>
            </Tag>
            """));

        Resource patch = Expect.Success(_module.Compile()).Resources.Single(r => r.Type == "tag");

        Assert.Equal("#block:example.logs", patch.ObjectId);
        Assert.Equal(2, patch.Override.Queries.Count);
    }

    [Fact]
    public void Rejects_a_tags_block_in_an_override()
    {
        _module.Write("patch.nof", NofText("Example/Blocks", "<Block override=\"true\"><Name>Dirt</Name><Content><Hardness>1</Hardness></Content><Tags><Tag>#example.a</Tag></Tags></Block>"));

        Expect.Error(_module.Compile(), "NRC303");
    }
}
