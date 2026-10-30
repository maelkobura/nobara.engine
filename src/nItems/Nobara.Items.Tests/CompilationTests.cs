// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Items.Build;
using Nobara.Items.Schemas;
using Nobara.ResourceCompiler;
using static Nobara.Items.Tests.Support.ItemsHarness;
using F = Nobara.Formats;

namespace Nobara.Items.Tests;

public sealed class CompilationTests
{
    [Fact]
    public void ValidItemsCompileWithTheirNativeFields()
    {
        F.ResourceBundle bundle = Bundle("example.food", Nof("Example/Food", """
            <Item>
              <Name>Steak</Name>
              <Content>
                <MaxStack>16</MaxStack>
                <DisplayName>item.food.steak</DisplayName>
                <Visual><Sprite>texture://Example/Art/Steak</Sprite></Visual>
                <Component ref="example.food.edible"><Food>3</Food></Component>
              </Content>
            </Item>
            <Item>
              <Name>Sword</Name>
              <Content><Visual><Model>mesh:example.art.sword</Model></Visual></Content>
            </Item>
            """));

        F.Resource steak = bundle.Resources.Single(r => r.ObjectId == "item:example.food.steak");
        Item content = Item.Parser.ParseFrom(steak.Content);
        Assert.Equal(16, content.MaxStack);
        Assert.Equal("item.food.steak", content.DisplayName);
        Assert.Equal("texture://Example/Art/Steak", content.Visual.Sprite);
        Assert.Single(content.Component);
        Item sword = Item.Parser.ParseFrom(bundle.Resources.Single(r => r.ObjectId == "item:example.food.sword").Content);
        Assert.Equal("mesh:example.art.sword", sword.Visual.Model);
        Assert.Equal(0, sword.MaxStack);
    }

    [Theory]
    [InlineData("<MaxStack>0</MaxStack>", "NRC310")]
    [InlineData("<Visual/>", ItemPlugin.EmptyVisual)]
    [InlineData("<Visual><Model>mesh:example.art.sword</Model><Sprite>texture://Example/Art/Steak</Sprite></Visual>", "NRC307")]
    [InlineData("<Visual><Model>mesh:example.art.missing</Model></Visual>", "NRC401")]
    [InlineData("<Visual><Sprite>texture://Example/Art/Missing</Sprite></Visual>", "NRC402")]
    [InlineData("<DisplayName>Steak</DisplayName>", "NRC311")]
    [InlineData("<Weight>3</Weight>", "NRC306")]
    [InlineData("<Component ref=\"example.food.edible\"/><Component ref=\"example.food.edible\"/>", ItemPlugin.DuplicateComponent)]
    [InlineData("<Component ref=\"Edible\"/>", "NRC311")]
    public void InvalidItemsFailWithTheFileLineAndColumn(string field, string code)
    {
        CompilationResult result = Compile("example.food", Nof("Example/Food", $"""
            <Item>
              <Name>Steak</Name>
              <Content>
                {field}
              </Content>
            </Item>
            """));

        Assert.False(result.Success);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(code, diagnostic.Code);
        Assert.EndsWith("food.nof", diagnostic.File, StringComparison.Ordinal);
        Assert.Equal(5, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
    }

    [Fact]
    public void AnotherPluginDerivesAnItemFromItsResource()
    {
        F.ResourceBundle bundle = Bundle("example.gems", Nof("Example/Gems", """
            <Gem><Name>Ruby</Name><Content><Hardness>7</Hardness><CreateItem/></Content></Gem>
            <Gem><Name>Pebble</Name><Content><Hardness>1</Hardness></Content></Gem>
            """));

        F.Resource item = Assert.Single(bundle.Resources, r => r.Type == "item");
        Assert.Equal("item:example.gems.ruby", item.ObjectId);
        Assert.Equal("gem:example.gems.ruby", item.DerivedFrom);
        Item content = Item.Parser.ParseFrom(item.Content);
        Assert.Equal(8, content.MaxStack);
        Assert.Single(content.Component);
    }

    [Fact]
    public void DerivedItemBuildsTheContentOfAnItem()
    {
        var derived = DerivedItem.Create(maxStack: 4, displayName: "item.gems.ruby", components: [DerivedItem.Component("example.gems.socket", new System.Xml.Linq.XElement("Hardness", 7))]);

        Assert.Equal("Item", derived.Element);
        Assert.Equal("4", derived.Content.Element("MaxStack")!.Value);
        Assert.Equal("item.gems.ruby", derived.Content.Element("DisplayName")!.Value);
        Assert.Equal("example.gems.socket", derived.Content.Element("Component")!.Attribute("ref")!.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivedItem.Create(maxStack: 0));
    }
}
