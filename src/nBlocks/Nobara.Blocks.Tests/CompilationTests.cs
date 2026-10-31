// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks.Build;
using Nobara.Blocks.Schemas;
using Nobara.Items.Schemas;
using Nobara.ResourceCompiler;
using static Nobara.Blocks.Tests.Support.BlocksHarness;
using F = Nobara.Formats;

namespace Nobara.Blocks.Tests;

public sealed class CompilationTests
{
    private const string Module = "example.blocks";

    [Fact]
    public void AValidBlockCompilesWithItsFields()
    {
        F.ResourceBundle bundle = Bundle(Module, Lamp("""
            <Shape>shape:nobara.slab</Shape>
            <Collision>none</Collision>
            <Opacity>cutout</Opacity>
            <Hardness>2.5</Hardness>
            <Orientations>horizontal</Orientations>
            <States>
              <Bool name="lit" default="true"/>
              <Int name="age" min="0" max="3"/>
              <Enum name="half" values="bottom,top" default="top"/>
            </States>
            <Variants>
              <Variant when="lit=true" opacity="translucent"/>
            </Variants>
            """));

        F.Resource lamp = Assert.Single(bundle.Resources, r => r.Type == "block");
        Block content = Block.Parser.ParseFrom(lamp.Content);
        Assert.Equal("block:example.blocks.lamp", lamp.ObjectId);
        Assert.Equal("shape:nobara.slab", content.Shape);
        Assert.Equal("2.5", content.Hardness);
        Assert.Equal("lit", Assert.Single(content.States.Bool).Name);
        Assert.Equal(3, content.States.Int[0].Max);
        Assert.Equal("top", content.States.Enum[0].Default);
        Assert.Equal("lit=true", Assert.Single(content.Variants.Variant).When);
    }

    [Theory]
    [InlineData("<Shape>cube</Shape>", BlockPlugin.InvalidShape)]
    [InlineData("<Collision>solid</Collision>", BlockPlugin.InvalidCollision)]
    [InlineData("<Opacity>transparent</Opacity>", BlockPlugin.InvalidOpacity)]
    [InlineData("<Hardness>-1</Hardness>", BlockPlugin.InvalidHardness)]
    [InlineData("<Hardness>hard</Hardness>", BlockPlugin.InvalidHardness)]
    [InlineData("<Orientations>diagonal</Orientations>", BlockPlugin.InvalidOrientations)]
    [InlineData("<States><Bool name=\"Lit\"/></States>", BlockPlugin.InvalidProperty)]
    [InlineData("<States><Bool name=\"orientation\"/></States>", BlockPlugin.InvalidProperty)]
    [InlineData("<States><Bool name=\"lit\"/><Enum name=\"lit\" values=\"a,b\"/></States>", BlockPlugin.InvalidProperty)]
    [InlineData("<States><Int name=\"age\" min=\"3\" max=\"1\"/></States>", BlockPlugin.InvalidProperty)]
    [InlineData("<States><Int name=\"age\" max=\"3\" default=\"4\"/></States>", BlockPlugin.InvalidProperty)]
    [InlineData("<States><Enum name=\"half\" values=\"bottom,bottom\"/></States>", BlockPlugin.InvalidProperty)]
    [InlineData("<States><Enum name=\"half\" values=\"bottom,top\" default=\"middle\"/></States>", BlockPlugin.InvalidProperty)]
    [InlineData("<States><Bool name=\"north\" rotatesTo=\"east\"/></States>", BlockPlugin.InvalidRotation)]
    [InlineData("<States><Enum name=\"facing\" values=\"north,east\" rotation=\"north:west\"/></States>", BlockPlugin.InvalidRotation)]
    [InlineData("<States><Enum name=\"facing\" values=\"north,east,south\" rotation=\"north:east\"/></States>", BlockPlugin.InvalidRotation)]
    [InlineData("<Footprint><Cell x=\"0\" y=\"0\" z=\"0\"/></Footprint>", BlockPlugin.InvalidFootprint)]
    [InlineData("<Footprint><Cell y=\"1\"/><Cell y=\"1\"/></Footprint>", BlockPlugin.InvalidFootprint)]
    [InlineData("<Footprint><Cell x=\"-7\"/><Cell x=\"7\"/></Footprint>", BlockPlugin.InvalidFootprint)]
    [InlineData("<Footprint><Cell x=\"9\"/></Footprint>", "NRC310")]
    [InlineData("<Variants><Variant when=\"lit=true\"/></Variants>", BlockPlugin.InvalidVariant)]
    [InlineData("<States><Bool name=\"lit\"/></States><Variants><Variant when=\"lit=maybe\"/></Variants>", BlockPlugin.InvalidVariant)]
    [InlineData("<States><Bool name=\"lit\"/></States><Variants><Variant when=\"lit=true\" opacity=\"clear\"/></Variants>", BlockPlugin.InvalidOpacity)]
    [InlineData("<CreateItem state=\"lit=true\"/>", BlockPlugin.InvalidItem)]
    public void AnInvalidBlockFailsWithTheFileLineAndColumn(string content, string code)
    {
        CompilationResult result = Compile(Module, Lamp(content));

        Assert.False(result.Success);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(code, diagnostic.Code);
        Assert.EndsWith("blocks.nof", diagnostic.File, StringComparison.Ordinal);
        Assert.Equal(5, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
    }

    [Fact]
    public void ARotationThatFormsNoCycleIsRefused()
    {
        CompilationResult result = Compile(Module, Lamp("""
            <States><Bool name="north" rotatesTo="east"/><Bool name="east"/></States>
            """));

        Assert.Equal(BlockPlugin.InvalidRotation, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void AProblemPointsAtTheElementItIsAbout()
    {
        CompilationResult result = Compile(Module, Lamp("""
            <States>
              <Bool name="lit"/>
              <Int name="age" max="3" default="9"/>
            </States>
            """));

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(BlockPlugin.InvalidProperty, diagnostic.Code);
        Assert.Equal(7, diagnostic.Line);
    }

    [Fact]
    public void ATypeAboveTheDefaultCapCompilesWithAWarning()
    {
        CompilationResult result = Compile(Module, Lamp("""
            <States><Int name="level" max="1999"/></States>
            """));

        Assert.True(result.Success);
        Diagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(BlockPlugin.TooManyStates, warning.Code);
        Assert.Contains("2000 states", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateItemDerivesAnItemThatPlacesTheBlock()
    {
        F.ResourceBundle bundle = Bundle(Module, Lamp("""
            <States><Bool name="lit"/></States>
            <CreateItem maxStack="16" state="lit=true">
              <DisplayName>block.example.lamp</DisplayName>
              <Properties><Weight>2</Weight></Properties>
            </CreateItem>
            """));

        F.Resource item = Assert.Single(bundle.Resources, r => r.Type == "item");
        Assert.Equal("item:example.blocks.lamp", item.ObjectId);
        Assert.Equal("block:example.blocks.lamp", item.DerivedFrom);
        Item content = Item.Parser.ParseFrom(item.Content);
        Assert.Equal(16, content.MaxStack);
        Assert.Equal("block.example.lamp", content.DisplayName);
        Assert.Single(content.Component);
        Assert.Contains(item.Properties, p => p.Name == "Weight");
    }

    [Fact]
    public void ABlockWithoutCreateItemDerivesNothing()
    {
        F.ResourceBundle bundle = Bundle(Module, Lamp("<Hardness>unbreakable</Hardness>"));

        Assert.DoesNotContain(bundle.Resources, r => r.Type == "item");
    }
}
