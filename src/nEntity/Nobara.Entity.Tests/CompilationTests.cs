// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Entity.Build;
using Nobara.ResourceCompiler;
using static Nobara.Entity.Tests.Support.EntityHarness;
using F = Nobara.Formats;

namespace Nobara.Entity.Tests;

public sealed class CompilationTests
{
    private static string Animals(string content) => Nof("Example/Animals", $"""
        <Entity>
          <Name>Pig</Name>
          <Content>
            {content}
          </Content>
        </Entity>
        <Entity><Name>Piglet</Name><Content/></Entity>
        """);

    [Fact]
    public void AnEntityCompilesWithItsComponentsInTheGenericRepresentation()
    {
        F.ResourceBundle bundle = Bundle("example.animals", Animals("""
            <Component ref="example.animals.stats"><Health>12</Health><Size><X>1</X><Y>2</Y><Z>3</Z></Size></Component>
                <Component ref="example.animals.young" optional="true"><Kind>entity:example.animals.piglet</Kind></Component>
            """));

        F.Resource pig = bundle.Resources.Single(r => r.ObjectId == "entity:example.animals.pig");
        Schemas.Entity content = Schemas.Entity.Parser.ParseFrom(pig.Content);
        Assert.Equal(2, content.Component.Count);

        GenericElement stats = GenericElement.Parse(content.Component[0].Span);
        Assert.Equal("example.animals.stats", stats.Attribute("ref")!.Value.Text);
        Assert.Equal(12, stats.Child("Health")!.Value!.TryGetInteger(out long health) ? health : 0);
        Assert.Equal([1d, 2d, 3d], stats.Child("Size")!.Vector);
        Assert.EndsWith("animals.nof", stats.Source.File, StringComparison.Ordinal);
        Assert.Equal(5, stats.Source.Line);

        GenericElement young = GenericElement.Parse(content.Component[1].Span);
        Assert.Equal("true", young.Attribute("optional")!.Value.Text);
        Assert.Equal(GenericValueKind.ObjectId, young.Child("Kind")!.Value!.Kind);
    }

    [Theory]
    [InlineData("<Component ref=\"example.animals.stats\"/><Component ref=\"example.animals.stats\"/>", EntityPlugin.DuplicateComponent)]
    [InlineData("<Component ref=\"nobara.entity.parent\"/>", EntityPlugin.ParentLinkInDefinition)]
    [InlineData("<Component ref=\"Stats\"/>", "NRC311")]
    [InlineData("<Component/>", "NRC308")]
    [InlineData("<Component ref=\"example.animals.stats\" optional=\"yes\"/>", "NRC309")]
    [InlineData("<Component ref=\"example.animals.stats\" weight=\"2\"/>", "NRC306")]
    [InlineData("<Component ref=\"example.animals.young\"><Kind>entity:example.animals.missing</Kind></Component>", "NRC401")]
    [InlineData("<Health>3</Health>", "NRC306")]
    public void AnInvalidEntityFailsWithTheFileLineAndColumn(string content, string code)
    {
        CompilationResult result = Compile("example.animals", Animals(content));

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(code, diagnostic.Code);
        Assert.EndsWith("animals.nof", diagnostic.File, StringComparison.Ordinal);
        Assert.Equal(5, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
    }

    [Fact]
    public void AnObjectIdentifierOfAnOptionalComponentMayBeMissing()
    {
        CompilationResult result = Compile("example.animals", Animals("""
            <Component ref="example.particles.emitter" optional="true"><Effect>particle:example.fx.mud</Effect></Component>
            """));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
