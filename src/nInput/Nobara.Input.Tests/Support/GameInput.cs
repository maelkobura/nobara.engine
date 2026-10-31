// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Input.Tests.Support;

/// <summary>
/// The actions of the test game, declared in a <c>.nof</c> file: the four
/// kinds of values, two maps that share a key, combinations, and the
/// interactions.
/// </summary>
internal static class GameInput
{
    public const string Module = "example.game";

    public const string OnFoot = "inputmap:example.input.onfoot";
    public const string Vehicle = "inputmap:example.input.vehicle";
    public const string Move = "inputaction:example.input.move";
    public const string Look = "inputaction:example.input.look";
    public const string Jump = "inputaction:example.input.jump";
    public const string Accelerate = "inputaction:example.input.accelerate";
    public const string Zoom = "inputaction:example.input.zoom";
    public const string Hotbar4 = "inputaction:example.input.hotbar4";
    public const string Preset4 = "inputaction:example.input.preset4";
    public const string SaveAll = "inputaction:example.input.saveall";
    public const string Sprint = "inputaction:example.input.sprint";
    public const string Crouch = "inputaction:example.input.crouch";
    public const string Attack = "inputaction:example.input.attack";
    public const string Interact = "inputaction:example.input.interact";
    public const string Inspect = "inputaction:example.input.inspect";
    public const string Dodge = "inputaction:example.input.dodge";
    public const string Drop = "inputaction:example.input.drop";
    public const string Pause = "inputaction:example.input.pause";

    /// <summary>The <c>.nof</c> file of the test game.</summary>
    public static string Nof { get; } = InputHarness.Nof("Example/Input", """
        <InputMap><Name>OnFoot</Name><Content><DisplayName>input.map.on_foot</DisplayName><Active>true</Active></Content></InputMap>
        <InputMap><Name>Vehicle</Name><Content/></InputMap>
        <InputAction>
          <Name>Move</Name>
          <Content>
            <DisplayName>input.move</DisplayName>
            <Value>Axis2D</Value>
            <Map>inputmap:example.input.onfoot</Map>
            <Bindings><Composite2D up="Key.W" down="Key.S" left="Key.A" right="Key.D"/></Bindings>
          </Content>
        </InputAction>
        <InputAction>
          <Name>Look</Name>
          <Content><Value>Axis2D</Value><Bindings><Axis>Mouse.Delta</Axis></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Jump</Name>
          <Content><Value>Button</Value><Map>inputmap:example.input.onfoot</Map><Bindings><Key>Key.Space</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Accelerate</Name>
          <Content threshold="0.4"><Value>Float</Value><Map>inputmap:example.input.vehicle</Map><Bindings><Key>Key.W</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Zoom</Name>
          <Content>
            <Value>Axis1D</Value>
            <Bindings><Axis>Mouse.Wheel</Axis><Composite1D negative="Key.Minus" positive="Key.Equals"/></Bindings>
          </Content>
        </InputAction>
        <InputAction>
          <Name>Hotbar4</Name>
          <Content><Value>Button</Value><Bindings><Key>Key.Digit4</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Preset4</Name>
          <Content><Value>Button</Value><Bindings><Combo><Key>Key.Control</Key><Key>Key.Digit4</Key></Combo></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>SaveAll</Name>
          <Content><Value>Button</Value><Bindings><Combo><Key>Key.Control</Key><Key>Key.Shift</Key><Key>Key.Digit4</Key></Combo></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Sprint</Name>
          <Content><Value>Button</Value><Interaction><Hold duration="0.3"/></Interaction><Bindings><Key>Key.LeftShift</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Crouch</Name>
          <Content><Value>Button</Value><Interaction><Toggle/></Interaction><Bindings><Key>Key.C</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Attack</Name>
          <Content><Value>Button</Value><Bindings><Key>Mouse.Left</Key><Combo><Key>Key.Shift</Key><Key>Mouse.Right</Key></Combo></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Interact</Name>
          <Content><Value>Button</Value><Interaction><Tap max="0.25"/></Interaction><Bindings><Key>Key.E</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Inspect</Name>
          <Content><Value>Button</Value><Interaction><Hold duration="0.2"/></Interaction><Bindings><Key>Key.E</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Dodge</Name>
          <Content><Value>Button</Value><Interaction><DoubleTap window="0.3"/></Interaction><Bindings><Key>Key.Q</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Drop</Name>
          <Content><Value>Button</Value><Interaction><Release/></Interaction><Bindings><Key>Key.G</Key><Key>Mouse.WheelDown</Key></Bindings></Content>
        </InputAction>
        <InputAction>
          <Name>Pause</Name>
          <Content><Value>Button</Value><Bindings><Key>Key.Escape</Key></Bindings></Content>
        </InputAction>
        """);
}
