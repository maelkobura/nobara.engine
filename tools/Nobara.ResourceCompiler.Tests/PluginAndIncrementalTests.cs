// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Google.Protobuf;
using Nobara.Formats;
using Nobara.ResourceCompiler.Plugins;
using Nobara.ResourceCompiler.Tests.Schemas;
using static Nobara.ResourceCompiler.Tests.TestModule;

namespace Nobara.ResourceCompiler.Tests;

/// <summary>Plugins, schema versions, the incremental compilation, and the single-file API.</summary>
public sealed class PluginAndIncrementalTests : IDisposable
{
    private readonly TestModule _module = new();

    public void Dispose() => _module.Dispose();

    [Fact]
    public void Compiles_a_type_that_a_plugin_provides()
    {
        _module.Plugins.Add(new TestPlugin());
        _module.Write("gems.nof", NofText("Example/Gems", "<Gem><Name>Ruby</Name><Content><Color>red</Color><Carats>3</Carats></Content></Gem>"));

        Resource gem = Expect.Success(_module.Compile()).Resources.Single();

        Assert.Equal("gem:example.gems.ruby", gem.ObjectId);
        Assert.Equal(3, Gem.Parser.ParseFrom(gem.Content).Carats);
    }

    [Fact]
    public void Reports_the_errors_of_a_plugin_at_the_node_it_names()
    {
        _module.Plugins.Add(new TestPlugin());
        _module.Write("gems.nof", NofText("Example/Gems", "<Gem>\n<Name>Onyx</Name>\n<Content>\n<Color>black</Color>\n</Content>\n</Gem>"));

        Diagnostic error = Expect.Error(_module.Compile(), "TEST001");

        Assert.Equal(5, error.Line);
    }

    [Fact]
    public void Compiles_the_resources_that_a_plugin_derives()
    {
        _module.Plugins.Add(new TestPlugin());
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><CreateItem/></Content></Block>"));

        ResourceBundle bundle = Expect.Success(_module.Compile());

        Resource item = bundle.Resources.Single(r => r.Type == "item");
        Assert.Equal("item:example.blocks.dirt", item.ObjectId);
        Assert.Equal("block:example.blocks.dirt", item.DerivedFrom);
        Assert.Equal(64, Item.Parser.ParseFrom(item.Content).MaxStack);
    }

    [Fact]
    public void Reports_a_derived_resource_that_duplicates_a_written_one()
    {
        _module.Plugins.Add(new TestPlugin());
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><CreateItem/></Content></Block><Item><Name>Dirt</Name><Content/></Item>"));

        Expect.Error(_module.Compile(), "NRC400");
    }

    [Fact]
    public void Lets_a_plugin_process_a_binary_file()
    {
        _module.Plugins.Add(new TestPlugin());
        _module.Write("Textures/notes.txt", "hello");
        _module.Write("textures.nof", NofText("Example/Textures", "<Texture><Name>Notes</Name><Content><File>Textures/notes.txt</File></Content></Texture>"));

        EmbeddedFile file = Assert.Single(_module.Compile().Files);

        Assert.Equal("HELLO", System.Text.Encoding.UTF8.GetString(file.Content!));
    }

    [Fact]
    public void Converts_an_old_schema_version_with_the_converter_of_a_plugin()
    {
        _module.Plugins.Add(new TestPlugin());
        _module.Write("widgets.nof", NofText("Example/UI", "<Widget version=\"1\"><Name>Menu</Name><Content><Caption>Start</Caption></Content></Widget>"));

        Resource widget = Expect.Success(_module.Compile()).Resources.Single();

        Assert.Equal("Start", Widget.Parser.ParseFrom(widget.Content).Title);
        Assert.Equal(2u, widget.SchemaVersion);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("3", true)]
    [InlineData("0", true)]
    [InlineData("two", true)]
    public void Rejects_a_schema_version_that_cannot_be_read(string version, bool withPlugin)
    {
        if (withPlugin)
        {
            _module.Plugins.Add(new TestPlugin());
        }

        _module.Write("widgets.nof", NofText("Example/UI", $"<Widget version=\"{version}\"><Name>Menu</Name><Content/></Widget>"));

        Expect.Error(_module.Compile(), "NRC313");
    }

    [Fact]
    public void Reads_a_resource_without_a_version_as_the_current_version()
    {
        _module.Write("widgets.nof", NofText("Example/UI", "<Widget version=\"2\"><Name>A</Name><Content><Title>a</Title></Content></Widget><Widget><Name>B</Name><Content><Title>b</Title></Content></Widget>"));

        Assert.All(Expect.Success(_module.Compile()).Resources, r => Assert.Equal(2u, r.SchemaVersion));
    }

    [Fact]
    public void Reports_a_plugin_that_throws()
    {
        _module.Plugins.Add(new ThrowingPlugin());
        _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Stick</Name><Content/></Item>"));

        Assert.Contains("boom", Expect.Error(_module.Compile(), "NRC602").Message);
    }

    [Fact]
    public void Compiles_nothing_again_when_nothing_changed()
    {
        _module.CacheFile = Path.Combine(_module.Root, "obj", "resources.cache");
        _module.Write("a.nof", NofText("Example/Items", "<Item><Name>A</Name><Content/></Item>"));
        _module.Write("b.nof", NofText("Example/Items", "<Item><Name>B</Name><Content/></Item>"));
        CompilationResult first = _module.Compile();

        CompilationResult second = _module.Compile();

        Assert.Equal(["a.nof", "b.nof"], first.CompiledFiles);
        Assert.Empty(second.CompiledFiles);
        Assert.Equal(["a.nof", "b.nof"], second.ReusedFiles);
        Assert.Equal(Expect.Success(first).ToByteArray(), Expect.Success(second).ToByteArray());
    }

    [Fact]
    public void Compiles_again_only_the_changed_file_and_relinks_the_others()
    {
        _module.CacheFile = Path.Combine(_module.Root, "obj", "resources.cache");
        _module.Write("Textures/dirt.png", "one");
        _module.Write("textures.nof", NofText("Example/Textures", "<Texture><Name>Dirt</Name><Content><File>Textures/dirt.png</File></Content></Texture>"));
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Texture>texture://Example/Textures/Dirt</Texture></Content></Block>"));
        ResourceBundle before = Expect.Success(_module.Compile());

        _module.Write("Textures/dirt.png", "two");
        CompilationResult result = _module.Compile();

        Assert.Equal(["textures.nof"], result.CompiledFiles);
        Assert.Equal(["blocks.nof"], result.ReusedFiles);
        Assert.NotEqual(
            before.Resources.Single(r => r.Type == "block").ContentHash,
            Expect.Success(result).Resources.Single(r => r.Type == "block").ContentHash);
    }

    [Fact]
    public void Compiles_everything_again_when_the_schemas_change()
    {
        _module.CacheFile = Path.Combine(_module.Root, "obj", "resources.cache");
        _module.Write("a.nof", NofText("Example/Items", "<Item><Name>A</Name><Content/></Item>"));
        _module.Compile();

        _module.Plugins.Add(new TestPlugin());
        CompilationResult result = _module.Compile();

        Assert.Equal(["a.nof"], result.CompiledFiles);
    }

    [Fact]
    public void Reports_an_error_again_on_the_next_compilation()
    {
        _module.CacheFile = Path.Combine(_module.Root, "obj", "resources.cache");
        _module.Write("a.nof", NofText("Example/Items", "<Item><Name>A</Name><Content><MaxStack>0</MaxStack></Content></Item>"));
        Expect.Error(_module.Compile(), "NRC310");

        Expect.Error(_module.Compile(), "NRC310");
    }

    [Fact]
    public void Compiles_one_file_again_for_hot_reload()
    {
        _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Stick</Name><Content><MaxStack>16</MaxStack></Content></Item>"));
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Drops><Drop item=\"item:example.items.stick\" /></Drops></Content></Block>"));
        ResourceCompiler compiler = _module.Compiler();
        Expect.Success(compiler.Compile(_module.NofFiles()));

        string path = _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Stick</Name><Content><MaxStack>32</MaxStack></Content></Item>"));
        FileCompilationResult result = compiler.CompileFile(path);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(32, Item.Parser.ParseFrom(Assert.Single(result.Resources).Content).MaxStack);
    }

    [Fact]
    public void Reports_the_errors_of_a_file_compiled_for_hot_reload()
    {
        _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Stick</Name><Content/></Item>"));
        _module.Write("blocks.nof", NofText("Example/Blocks", "<Block><Name>Dirt</Name><Content><DisplayName>block.dirt</DisplayName><Drops><Drop item=\"item:example.items.stick\" /></Drops></Content></Block>"));
        ResourceCompiler compiler = _module.Compiler();
        Expect.Success(compiler.Compile(_module.NofFiles()));

        string path = _module.Write("items.nof", NofText("Example/Items", "<Item><Name>Branch</Name><Content/></Item>"));
        FileCompilationResult result = compiler.CompileFile(path);

        Assert.False(result.Success);
        Assert.Empty(result.Resources);
        Assert.Contains(result.Diagnostics, d => d.Code == "NRC401");
    }

    [Fact]
    public void Reports_schemas_that_define_the_same_type_twice()
    {
        _module.Plugins.Add(new TestPlugin());
        _module.Plugins.Add(new DuplicatePlugin());

        Assert.Contains(_module.Compile().Diagnostics, d => d.Code == "NRC601");
    }

    private sealed class ThrowingPlugin : ResourcePlugin
    {
        public override IReadOnlyCollection<string> ResourceTypes => ["Item"];

        public override void Validate(ResourceContext context) => throw new InvalidOperationException("boom");
    }

    private sealed class DuplicatePlugin : ResourcePlugin
    {
        public override IReadOnlyCollection<string> ResourceTypes => [];

        public override IEnumerable<SchemaConverter> Converters => new TestPlugin().Converters;
    }
}
