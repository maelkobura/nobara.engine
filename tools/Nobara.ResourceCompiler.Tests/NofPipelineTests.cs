// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Formats;
using Nobara.ResourceCompiler.Tests.Schemas;
using static Nobara.ResourceCompiler.Tests.TestModule;

namespace Nobara.ResourceCompiler.Tests;

/// <summary>Parsing, validation against the schema, identifiers, and serialization of .nof files.</summary>
public sealed class NofPipelineTests : IDisposable
{
    private const string Dirt = """
        <Block>
          <Name>Dirt</Name>
          <Version>1.2.0</Version>
          <Content>
            <DisplayName>block.natural.dirt</DisplayName>
            <Hardness>15</Hardness>
            <Shape>HalfSlab</Shape>
            <Friction>0.6</Friction>
            <Label>soft</Label>
            <Label>brown</Label>
            <Drops>
              <Drop item="item:example.blocks.dirt" min="1" max="1" chance="1.0" />
            </Drops>
          </Content>
        </Block>
        <Item>
          <Name>Dirt</Name>
          <Content><DisplayName>item.dirt</DisplayName><MaxStack>64</MaxStack></Content>
        </Item>
        """;

    private readonly TestModule _module = new();

    public void Dispose() => _module.Dispose();

    [Fact]
    public void Compiles_content_to_the_message_of_the_type()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", Dirt));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Resource resource = bundle.Resources.Single(r => r.Type == "block");
        Block block = Block.Parser.ParseFrom(resource.Content);
        Assert.Equal("block.natural.dirt", block.DisplayName);
        Assert.Equal(15, block.Hardness);
        Assert.Equal(Shape.HalfSlab, block.Shape);
        Assert.Equal(0.6f, block.Friction);
        Assert.Equal(["soft", "brown"], block.Label);
        Drop drop = Assert.Single(block.Drops.Drop);
        Assert.Equal("item:example.blocks.dirt", drop.Item);
        Assert.Equal(1.0f, drop.Chance);
        Assert.Equal("1.2.0", resource.Version);
        Assert.Equal("nobara.tests.Block", resource.Message);
        Assert.Equal("blocks.nof", resource.Source.File);
    }

    [Fact]
    public void Derives_identifiers_from_the_namespace_and_the_name()
    {
        _module.Write("blocks.nof", NofText("Example/Natural/Blocks", Dirt.Replace("item:example.blocks.dirt", "item:example.natural.blocks.dirt")));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Resource block = bundle.Resources.Single(r => r.Type == "block");
        Assert.Equal("block:example.natural.blocks.dirt", block.ObjectId);
        Assert.Equal("block://Example/Natural/Blocks/Dirt", block.ResourceId);
        Assert.Equal("Dirt", block.Name);
        Assert.Equal("example.blocks", bundle.Module);
    }

    [Fact]
    public void Accepts_a_type_without_plugin_code()
    {
        _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Stick</Name><Content><MaxStack>16</MaxStack></Content></Item>"));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Assert.Equal(16, Item.Parser.ParseFrom(bundle.Resources.Single().Content).MaxStack);
    }

    [Fact]
    public void Reports_an_unknown_field_with_its_position()
    {
        string path = _module.Write("blocks.nof", NofText("Example/Blocks", "<Block>\n  <Name>Dirt</Name>\n  <Content>\n    <DisplayName>block.dirt</DisplayName>\n    <Hardnes>3</Hardnes>\n  </Content>\n</Block>"));

        Diagnostic error = Expect.Error(_module.Compile(), "NRC306");

        Assert.Equal(path, error.File);
        Assert.Equal(6, error.Line);
        Assert.Equal(6, error.Column);
        Assert.Contains("Hardness", error.Message);
    }

    [Fact]
    public void Reports_a_field_written_with_the_wrong_case()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><displayName>block.dirt</displayName></Content></Block>"));

        Diagnostic error = Expect.Error(_module.Compile(), "NRC306");

        Assert.Contains("did you mean 'DisplayName'", error.Message);
    }

    [Theory]
    [InlineData("<Hardness>101</Hardness>", "NRC310")]
    [InlineData("<Hardness>-2</Hardness>", "NRC310")]
    [InlineData("<Friction>1.5</Friction>", "NRC310")]
    [InlineData("<Hardness>soft</Hardness>", "NRC309")]
    [InlineData("<Hardness>99999999999</Hardness>", "NRC309")]
    [InlineData("<Shape>Sphere</Shape>", "NRC309")]
    [InlineData("<CreateItem>yes</CreateItem>", "NRC309")]
    [InlineData("<Hardness>1</Hardness><Hardness>2</Hardness>", "NRC307")]
    [InlineData("<Texture>Dirt.png</Texture>", "NRC311")]
    [InlineData("<Drops><Drop item=\"block:example.blocks.dirt\" /></Drops>", "NRC311")]
    [InlineData("<Drops><Drop min=\"1\" /></Drops>", "NRC308")]
    [InlineData("<Drops item=\"x\" />", "NRC306")]
    public void Rejects_a_value_that_the_schema_does_not_accept(string fields, string code)
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", $"<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName>{fields}</Content></Block>"));

        Expect.Error(_module.Compile(), code);
    }

    [Fact]
    public void Reports_a_missing_required_field_and_text_instead_of_a_key()
    {
        _module.Write("a.nof", NofText("Example/Blocks", "<Block><Name>A</Name><Content><Hardness>1</Hardness></Content></Block>"));
        _module.Write("b.nof", NofText("Example/Blocks", "<Block><Name>B</Name><Content><DisplayName>Plain text</DisplayName></Content></Block>"));

        CompilationResult result = _module.Compile();

        Assert.Contains("DisplayName", Expect.Error(result, "NRC308").Message);
        Assert.Contains("localization key", Expect.Error(result, "NRC311").Message);
    }

    [Fact]
    public void Writes_an_empty_boolean_element_as_true()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><CreateItem/></Content></Block>"));

        Assert.True(Block.Parser.ParseFrom(Expect.Success(_module.Compile()).Resources.Single().Content).CreateItem);
    }

    [Theory]
    [InlineData("<Other version=\"1.0.0\" namespace=\"Example\" />", "NRC301")]
    [InlineData("<Nobara namespace=\"Example\" />", "NRC301")]
    [InlineData("<Nobara version=\"2.0.0\" namespace=\"Example\" />", "NRC301")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example.Blocks\" />", "NRC301")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\" extra=\"x\" />", "NRC301")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\"><Bloc><Name>A</Name><Content/></Bloc></Nobara>", "NRC302")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\"><Block><Content/></Block></Nobara>", "NRC304")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\"><Block><Name>A</Name></Block></Nobara>", "NRC304")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\"><Block><Name>A b</Name><Content/></Block></Nobara>", "NRC305")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\"><Block><Name>A</Name><Id>a</Id><Content/></Block></Nobara>", "NRC303")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\"><Block optional=\"true\"><Name>A</Name><Content/></Block></Nobara>", "NRC303")]
    [InlineData("<Nobara version=\"1.0.0\" namespace=\"Example\"><Block>", "NRC300")]
    public void Rejects_an_invalid_structure(string text, string code)
    {
        _module.Write("broken.nof", text);

        Expect.Error(_module.Compile(), code);
    }

    [Fact]
    public void Reports_two_resources_whose_names_differ_only_by_case()
    {
        _module.Write("a.nof", NofText("Example/Blocks", "<Item><Name>Dirt</Name><Content/></Item>"));
        _module.Write("b.nof", NofText("Example/Blocks", "<Item><Name>dirt</Name><Content/></Item>"));

        Diagnostic error = Expect.Error(_module.Compile(), "NRC400");

        Assert.Contains("item:example.blocks.dirt", error.Message);
        Assert.Contains("a.nof", error.Message);
    }

    [Fact]
    public void Reports_a_duplicate_of_a_resource_of_a_dependency()
    {
        using var dependency = new TestModule("example.base");
        dependency.Write("items.nof", NofText("Example/Items", "<Item><Name>Stick</Name><Content/></Item>"));
        _module.Dependencies.Add(dependency.CompileAsDependency());
        _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Stick</Name><Content/></Item>"));

        Diagnostic error = Expect.Error(_module.Compile(), "NRC400");

        Assert.Contains("example.base", error.Message);
    }

    [Fact]
    public void Resolves_references_to_the_resources_of_a_dependency()
    {
        using var dependency = new TestModule("example.items");
        dependency.Write("items.nof", NofText("Example/Blocks", "<Item><Name>Dirt</Name><Content/></Item>"));
        _module.Dependencies.Add(dependency.CompileAsDependency());
        _module.Write("blocks.nof", NofText("Example/Blocks", Dirt[..Dirt.IndexOf("<Item>", StringComparison.Ordinal)]));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Assert.Contains("item:example.blocks.dirt", bundle.Resources.Single().References);
    }

    [Fact]
    public void Reports_a_broken_reference()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Drops><Drop item=\"item:example.missing\" /></Drops></Content></Block>"));

        Diagnostic error = Expect.Error(_module.Compile(), "NRC401");

        Assert.Contains("item:example.missing", error.Message);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void Resolves_a_resource_identifier_and_embeds_its_file()
    {
        _module.Write("Textures/dirt.png", "png");
        _module.Write("textures.nof", NofText("Example/Textures", "<Texture><Name>Dirt</Name><Content><File>Textures/dirt.png</File></Content></Texture>"));
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Texture>texture://Example/Textures/Dirt</Texture></Content></Block>"));

        CompilationResult result = _module.Compile();

        ResourceBundle bundle = Expect.Success(result);
        Assert.Equal(["Textures/dirt.png"], bundle.Files);
        EmbeddedFile file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine(_module.Resources, "Textures", "dirt.png"), file.SourcePath);
        Assert.Null(file.Content);
    }

    [Theory]
    [InlineData("<Texture>texture://Example/Textures/Grass</Texture>", "NRC402")]
    public void Reports_an_unknown_resource_identifier(string field, string code)
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", $"<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName>{field}</Content></Block>"));

        Expect.Error(_module.Compile(), code);
    }

    [Theory]
    [InlineData("Textures/missing.png")]
    [InlineData("../outside.png")]
    [InlineData("Textures\\dirt.png")]
    public void Reports_a_missing_or_invalid_file(string path)
    {
        _module.Write("Textures/dirt.png", "png");
        _module.Write("textures.nof", NofText("Example/Textures", $"<Texture><Name>Dirt</Name><Content><File>{path}</File></Content></Texture>"));

        Expect.Error(_module.Compile(), "NRC312");
    }

    [Fact]
    public void Compiles_free_properties_plain_and_qualified()
    {
        _module.Write("items.nof", NofText("Example/Items", """
            <Item>
              <Name>Stone</Name>
              <Content/>
              <Properties>
                <Weight>2.5</Weight>
                <Motion.Weight>3</Motion.Weight>
                <BreakSound>sound:example.stone.break</BreakSound>
                <Offset><X>1</X><Y>0</Y><Z>-1</Z></Offset>
              </Properties>
            </Item>
            """));

        Resource resource = Expect.Success(_module.Compile()).Resources.Single();

        Assert.Equal(["Weight", "Weight", "BreakSound", "Offset"], resource.Properties.Select(p => p.Name));
        Assert.Equal(["", "Motion", "", ""], resource.Properties.Select(p => p.Qualifier));
        Assert.Equal(2.5, resource.Properties[0].Value.Value.Number);
        Assert.Equal("sound:example.stone.break", resource.Properties[2].Value.Value.ObjectId);
        Assert.Equal([1.0, 0.0, -1.0], resource.Properties[3].Value.Vector);
        Assert.Equal(6u, resource.Properties[0].Value.Source.Line);
    }

    [Theory]
    [InlineData("<weight>1</weight>")]
    [InlineData("<A.B.Weight>1</A.B.Weight>")]
    [InlineData("<Weight>1</Weight><Weight>2</Weight>")]
    public void Rejects_an_invalid_free_property(string properties)
    {
        _module.Write("items.nof", NofText("Example/Items", $"<Item><Name>Stone</Name><Content/><Properties>{properties}</Properties></Item>"));

        Expect.Error(_module.Compile(), "NRC314");
    }

    [Fact]
    public void Stores_components_in_the_generic_representation()
    {
        _module.Write("mesh.nof", NofText("Example/Items", "<Item><Name>Duck</Name><Content/></Item>"));
        _module.Write("entities.nof", NofText("Example/Entities", """
            <Entity>
              <Name>Pig</Name>
              <Content>
                <Component ref="example.physics.rigidbody"><Mass>1</Mass><IsKinematic>false</IsKinematic></Component>
                <Component ref="example.renderer.mesh"><Mesh>item:example.items.duck</Mesh><Scale><X>0.5</X><Y>0.5</Y><Z>0.5</Z></Scale></Component>
                <Component ref="example.particles.emitter" optional="true"><Effect>particle:example.fx.mud</Effect></Component>
              </Content>
            </Entity>
            """));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Entity entity = Entity.Parser.ParseFrom(bundle.Resources.Single(r => r.Type == "entity").Content);
        List<GenericNode> components = entity.Component.Select(c => GenericNode.Parser.ParseFrom(c)).ToList();
        Assert.Equal("example.physics.rigidbody", components[0].Attributes.Single(a => a.Name == "ref").Value.Text);
        Assert.Equal(1, components[0].Children[0].Value.Integer);
        Assert.False(components[0].Children[1].Value.Boolean);
        Assert.Equal([0.5, 0.5, 0.5], components[1].Children[1].Vector);
        Assert.Equal("true", components[2].Attributes.Single(a => a.Name == "optional").Value.Text);
        Assert.Equal(5u, components[0].Source.Line);
    }

    [Theory]
    [InlineData("<Component><Mass>1</Mass></Component>", "NRC308")]
    [InlineData("<Component ref=\"rigidbody\" />", "NRC311")]
    [InlineData("<Component ref=\"example.physics.body\" optional=\"yes\" />", "NRC309")]
    [InlineData("<Component ref=\"example.physics.body\"><Mesh>mesh:example.missing</Mesh></Component>", "NRC401")]
    public void Checks_what_it_can_in_a_component(string component, string code)
    {
        _module.Write("entities.nof", NofText("Example/Entities", $"<Entity><Name>Pig</Name><Content>{component}</Content></Entity>"));

        Expect.Error(_module.Compile(), code);
    }

    [Fact]
    public void Checks_free_properties_in_a_generic_properties_field()
    {
        _module.Write("entities.nof", NofText("Example/Entities", "<Entity><Name>Pig</Name><Content><Extra><weight>1</weight></Extra></Content></Entity>"));

        Expect.Error(_module.Compile(), "NRC314");
    }

    [Fact]
    public void Includes_the_referenced_resources_in_the_content_hash()
    {
        _module.Write("Textures/dirt.png", "one");
        _module.Write("textures.nof", NofText("Example/Textures", "<Texture><Name>Dirt</Name><Content><File>Textures/dirt.png</File></Content></Texture>"));
        _module.Write("blocks.nof", NofText("Example/Blocks", """
            <Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Texture>texture://Example/Textures/Dirt</Texture></Content></Block>
            <Block><Name>Stone</Name><Content><DisplayName>block.stone</DisplayName></Content></Block>
            """));
        ResourceBundle before = Expect.Success(_module.Compile());

        _module.Write("Textures/dirt.png", "two");
        ResourceBundle after = Expect.Success(_module.Compile());

        Assert.NotEqual(Hash(before, "block:example.blocks.dirt"), Hash(after, "block:example.blocks.dirt"));
        Assert.NotEqual(Hash(before, "texture:example.textures.dirt"), Hash(after, "texture:example.textures.dirt"));
        Assert.Equal(Hash(before, "block:example.blocks.stone"), Hash(after, "block:example.blocks.stone"));
        Assert.Equal(32, before.Resources[0].ContentHash.Length);
    }

    [Fact]
    public void Keeps_the_content_hash_when_only_positions_change()
    {
        _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Stone</Name><Content/><Properties><Weight>1</Weight></Properties></Item>"));
        ResourceBundle before = Expect.Success(_module.Compile());

        _module.Write("items.nof", NofText("Example/Items", "\n\n  <Item>\n<Name>Stone</Name><Content/><Properties>\n<Weight>1</Weight></Properties></Item>"));
        ResourceBundle after = Expect.Success(_module.Compile());

        Assert.Equal(Hash(before, "item:example.items.stone"), Hash(after, "item:example.items.stone"));
    }

    [Fact]
    public void Hashes_resources_that_reference_each_other()
    {
        _module.Write("blocks.nof", NofText("Example/Blocks", """
            <Block><Name>A</Name><Content><DisplayName>block.a</DisplayName><Drops><Drop item="item:example.blocks.b" /></Drops></Content></Block>
            <Item><Name>B</Name><Content/></Item>
            """));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Assert.All(bundle.Resources, r => Assert.Equal(32, r.ContentHash.Length));
    }

    private static string Hash(ResourceBundle bundle, string id) => Convert.ToHexString(bundle.Resources.Single(r => r.ObjectId == id).ContentHash.ToByteArray());
}
