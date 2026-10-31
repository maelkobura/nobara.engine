// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Input.Build;
using S = Nobara.Input.Schemas;
using Nobara.Input.Tests.Support;
using Nobara.ResourceCompiler;
using static Nobara.Input.Tests.Support.InputHarness;
using F = Nobara.Formats;

namespace Nobara.Input.Tests;

public sealed class CompilationTests
{
    [Fact]
    public void TheActionsOfTheTestGameCompile()
    {
        F.ResourceBundle bundle = Bundle(GameInput.Module, GameInput.Nof);

        Assert.Equal(2, bundle.Resources.Count(r => r.Type == "inputmap"));
        F.Resource move = bundle.Resources.Single(r => r.ObjectId == GameInput.Move);
        S.InputAction content = S.InputAction.Parser.ParseFrom(move.Content);
        Assert.Equal(S.ValueKind.Axis2D, content.Value);
        Assert.Equal(GameInput.OnFoot, content.Map);
        Assert.Equal("Key.W", content.Bindings.Composite2D.Single().Up);
        S.InputAction sprint = S.InputAction.Parser.ParseFrom(bundle.Resources.Single(r => r.ObjectId == GameInput.Sprint).Content);
        Assert.Equal(0.3f, sprint.Interaction.Hold.Duration);
        S.InputAction accelerate = S.InputAction.Parser.ParseFrom(bundle.Resources.Single(r => r.ObjectId == GameInput.Accelerate).Content);
        Assert.Equal(0.4f, accelerate.Threshold);
    }

    [Theory]
    [InlineData("Button", "<Bindings><Key>Key.Nope</Key></Bindings>", InputPlugin.UnknownControl)]
    [InlineData("Button", "<Bindings><Key>key.w</Key></Bindings>", InputPlugin.UnknownControl)]
    [InlineData("Button", "<Bindings><Key>Mouse.Delta</Key></Bindings>", InputPlugin.InvalidBinding)]
    [InlineData("Axis2D", "<Bindings><Axis>Key.W</Axis></Bindings>", InputPlugin.InvalidBinding)]
    [InlineData("Button", "<Bindings><Combo><Key>Key.A</Key></Combo></Bindings>", InputPlugin.InvalidBinding)]
    [InlineData("Button", "<Bindings><Combo><Key>Key.Control</Key><Key>Key.LeftControl</Key></Combo></Bindings>", InputPlugin.InvalidBinding)]
    [InlineData("Button", "<Bindings><Combo><Key>Mouse.WheelUp</Key><Key>Key.A</Key></Combo></Bindings>", InputPlugin.InvalidBinding)]
    [InlineData("Button", "<Bindings><Composite2D up=\"Key.W\" down=\"Key.S\" left=\"Key.A\" right=\"Key.D\"/></Bindings>", InputPlugin.IncompatibleBinding)]
    [InlineData("Axis2D", "<Bindings><Axis>Mouse.Wheel</Axis></Bindings>", InputPlugin.IncompatibleBinding)]
    [InlineData("Float", "<Bindings><Composite1D negative=\"Key.Q\" positive=\"Key.E\"/></Bindings>", InputPlugin.IncompatibleBinding)]
    [InlineData("Axis1D", "<Interaction><Toggle/></Interaction>", InputPlugin.NotForAxis)]
    [InlineData("Axis2D", "<Threshold>0.3</Threshold>", InputPlugin.NotForAxis)]
    [InlineData("Button", "<Interaction><Hold duration=\"0\"/></Interaction>", InputPlugin.ZeroValue)]
    [InlineData("Float", "<Threshold>0</Threshold>", InputPlugin.ZeroValue)]
    [InlineData("Button", "<Bindings><Key>Key.A</Key><Key>Key.A</Key></Bindings>", InputPlugin.DuplicateBinding)]
    [InlineData("Button", "<Interaction><Press/><Toggle/></Interaction>", "NRC307")]
    [InlineData("Button", "<Interaction><Hold/></Interaction>", "NRC308")]
    [InlineData("Button", "<Bindings><Button>Key.A</Button></Bindings>", "NRC306")]
    [InlineData("Button", "<Map>inputmap:example.input.missing</Map>", "NRC401")]
    [InlineData("Button", "<DisplayName>Jump</DisplayName>", "NRC311")]
    [InlineData("Float", "<Threshold>2</Threshold>", "NRC310")]
    public void InvalidActionsFailWithTheFileLineAndColumn(string value, string field, string code)
    {
        CompilationResult result = Compile("example.game", Nof("Example/Input", $"""
            <InputAction>
              <Name>Jump</Name>
              <Content><Value>{value}</Value>
                {field}
              </Content>
            </InputAction>
            """));

        Assert.False(result.Success);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(code, diagnostic.Code);
        Assert.EndsWith("input.nof", diagnostic.File, StringComparison.Ordinal);
        Assert.Equal(5, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
    }

    [Fact]
    public void AnActionNeedsAKindOfValue()
    {
        CompilationResult result = Compile("example.game", Nof("Example/Input", "<InputAction><Name>Jump</Name><Content/></InputAction>"));

        Assert.Contains(result.Diagnostics, d => d.Code == "NRC308");
    }

    [Fact]
    public void GamepadBindingsAreAccepted()
    {
        F.ResourceBundle bundle = Bundle("example.game", Nof("Example/Input", """
            <InputAction><Name>Jump</Name><Content><Value>Button</Value><Bindings><Key>Gamepad.South</Key></Bindings></Content></InputAction>
            <InputAction><Name>Move</Name><Content><Value>Axis2D</Value><DeadZone>0.2</DeadZone><Bindings><Axis>Gamepad.LeftStick</Axis></Bindings></Content></InputAction>
            <InputAction><Name>Brake</Name><Content><Value>Float</Value><Bindings><Axis>Gamepad.LeftTrigger</Axis></Bindings></Content></InputAction>
            """));

        Assert.Equal(3, bundle.Resources.Count);
    }

    [Fact]
    public void EveryNameOfTheClosedListParses()
    {
        Assert.Contains("Key.W", InputControl.AllNames);
        Assert.Contains("Key.Control", InputControl.AllNames);
        Assert.Contains("Mouse.Delta", InputControl.AllNames);
        Assert.Contains("Gamepad.RightStick", InputControl.AllNames);
        Assert.DoesNotContain("Key.Unknown", InputControl.AllNames);
        foreach (string name in InputControl.AllNames)
        {
            Assert.Equal(name, InputControl.Parse(name).Name);
        }

        Assert.True(InputControl.Control.Covers(InputControl.Key(Frames.Scancode.RightControl)));
        Assert.False(InputControl.Key(Frames.Scancode.LeftControl).Covers(InputControl.Control));
        Assert.Throws<FormatException>(() => InputControl.Parse("Key.Ctrl"));
    }
}
