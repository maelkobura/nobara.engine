// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Frames;
using Nobara.Input.Tests.Support;
using static Nobara.Input.Tests.Support.GameInput;

namespace Nobara.Input.Tests;

public sealed class EvaluationTests : IDisposable
{
    private readonly InputHarness _harness = new();
    private readonly Game _game;

    public EvaluationTests()
    {
        _harness.Settings["input.mouseSensitivity"] = "0.5";
        _game = Game.Start(_harness);
    }

    public void Dispose() => _game.Dispose();

    [Fact]
    public void AButtonIsPressedHeldAndReleased()
    {
        _harness.Key(Scancode.Space, true);
        _game.Frame();
        ActionState pressed = _game.Read(Jump);
        Assert.True(pressed.IsHeld);
        Assert.True(pressed.WasPressed);
        Assert.Equal(1, pressed.Value);

        // The core counts at most 0.25 s per frame.
        _game.Frame(0.2).Frame(0.2);
        ActionState held = _game.Read(Jump);
        Assert.True(held.IsHeld);
        Assert.False(held.WasPressed);
        Assert.Equal(0.4, held.HeldFor.TotalSeconds, 6);

        _harness.Key(Scancode.Space, false);
        _game.Frame();
        ActionState released = _game.Read(Jump);
        Assert.False(released.IsHeld);
        Assert.True(released.WasReleased);
        Assert.Equal(0, released.Value);
    }

    [Fact]
    public void APressAndAReleaseInOneFrameAreBothSeen()
    {
        _harness.Tap(Scancode.Space);
        _game.Frame();

        ActionState state = _game.Read(Jump);
        Assert.False(state.IsHeld);
        Assert.Equal(1, state.Presses);
        Assert.Equal(1, state.Releases);
    }

    [Fact]
    public void AKeyRepeatIsNotANewPress()
    {
        _harness.Key(Scancode.Space, true);
        _game.Frame();
        _harness.Key(Scancode.Space, true, repeat: true).Key(Scancode.Space, true, repeat: true);
        _game.Frame();

        Assert.True(_game.Read(Jump).IsHeld);
        Assert.Equal(0, _game.Read(Jump).Presses);
    }

    [Fact]
    public void AKeyBindsByItsPhysicalPosition()
    {
        // The fake layout names the key at the position of W "Z", as an AZERTY keyboard does.
        Assert.Equal("Z", _game.Input.GetLayoutName(InputControl.Key(Scancode.W)));
        Assert.Equal(string.Empty, _game.Input.GetLayoutName(InputControl.Control));

        _harness.Key(Scancode.W, true);
        _game.Frame();

        Assert.Equal(new Vector2(0, 1), _game.Read(Move).Vector);
    }

    [Fact]
    public void ACompositeGivesA2DAxis()
    {
        _harness.Key(Scancode.W, true).Key(Scancode.D, true);
        _game.Frame();
        Assert.Equal(new Vector2(1, 1), _game.Read(Move).Vector);

        _harness.Key(Scancode.S, true);
        _game.Frame();
        Assert.Equal(new Vector2(1, 0), _game.Read(Move).Vector);
        Assert.True(_game.Read(Move).IsHeld);

        _harness.Key(Scancode.W, false).Key(Scancode.S, false).Key(Scancode.D, false);
        _game.Frame();
        Assert.Equal(Vector2.Zero, _game.Read(Move).Vector);
        Assert.True(_game.Read(Move).WasReleased);
    }

    [Fact]
    public void TheMouseGivesA2DAxisWithTheSensitivityAndUpPositive()
    {
        _harness.Move(10, 4).Move(2, 2);
        _game.Frame();

        // Sensitivity 0.5; the raw movement is positive downward.
        Assert.Equal(new Vector2(6, -3), _game.Read(Look).Vector);

        _game.Frame();
        Assert.Equal(Vector2.Zero, _game.Read(Look).Vector);
    }

    [Fact]
    public void InvertYFlipsTheVerticalMovement()
    {
        _game.Probe.Context.Settings.Set(InputSettings.InvertY, true);
        _game.Frame();
        _harness.Move(0, 4);
        _game.Frame();

        Assert.Equal(new Vector2(0, 2), _game.Read(Look).Vector);
    }

    [Fact]
    public void A1DAxisTakesTheBindingOfLargestAmplitude()
    {
        _harness.Key(Scancode.Equals, true);
        _game.Frame();
        Assert.Equal(1, _game.Read(Zoom).Value);

        _harness.Wheel(0, -3);
        _game.Frame();
        Assert.Equal(-3, _game.Read(Zoom).Value);

        _harness.Key(Scancode.Minus, true);
        _game.Frame();
        Assert.Equal(0, _game.Read(Zoom).Value);
    }

    [Fact]
    public void AFloatGivesZeroOrOneOnAKey()
    {
        _game.Input.GetMap(Vehicle).IsActive = true;
        _game.Frame();
        _harness.Key(Scancode.W, true);
        _game.Frame();

        ActionState state = _game.Read(Accelerate);
        Assert.Equal(1, state.Value);
        Assert.True(state.IsHeld);
        Assert.Equal(0.4f, _game.Action(Accelerate).Threshold);
    }

    [Fact]
    public void SeveralBindingsAreActiveTogether()
    {
        _harness.Button(MouseButton.Left, true);
        _game.Frame();
        Assert.True(_game.Read(Attack).WasPressed);

        _harness.Key(Scancode.LeftShift, true).Button(MouseButton.Right, true);
        _game.Frame();
        Assert.True(_game.Read(Attack).IsHeld);
        Assert.Equal(0, _game.Read(Attack).Presses);

        _harness.Button(MouseButton.Left, false);
        _game.Frame();
        Assert.True(_game.Read(Attack).IsHeld);

        _harness.Button(MouseButton.Right, false);
        _game.Frame();
        Assert.False(_game.Read(Attack).IsHeld);
    }

    [Fact]
    public void AComboTriggersOnItsLastKeyWhileTheOthersAreHeld()
    {
        _harness.Key(Scancode.RightControl, true).Key(Scancode.Digit4, true);
        _game.Frame();

        Assert.True(_game.Read(Preset4).WasPressed);
        Assert.False(_game.Read(Hotbar4).WasPressed);
    }

    [Fact]
    public void AComboPressedInTheWrongOrderDoesNotTrigger()
    {
        _harness.Key(Scancode.Digit4, true).Key(Scancode.LeftControl, true);
        _game.Frame();

        Assert.False(_game.Read(Preset4).WasPressed);
        Assert.True(_game.Read(Hotbar4).IsHeld);
    }

    [Fact]
    public void TheComboWithTheMostKeysWins()
    {
        _harness.Key(Scancode.LeftControl, true).Key(Scancode.LeftShift, true).Key(Scancode.Digit4, true);
        _game.Frame();

        Assert.True(_game.Read(SaveAll).WasPressed);
        Assert.False(_game.Read(Preset4).WasPressed);
        Assert.False(_game.Read(Hotbar4).WasPressed);
    }

    [Fact]
    public void AComboEndsWhenOneOfItsKeysIsReleased()
    {
        _harness.Key(Scancode.LeftControl, true).Key(Scancode.Digit4, true);
        _game.Frame();
        _harness.Key(Scancode.LeftControl, false);
        _game.Frame();

        Assert.False(_game.Read(Preset4).IsHeld);
        Assert.True(_game.Read(Preset4).WasReleased);

        // The key stays held, but its press went to the combination.
        Assert.False(_game.Read(Hotbar4).IsHeld);
    }

    [Fact]
    public void BothSidesOfAModifierHoldAComboUntilTheLastIsReleased()
    {
        _harness.Key(Scancode.LeftControl, true).Key(Scancode.RightControl, true).Key(Scancode.Digit4, true);
        _game.Frame();
        _harness.Key(Scancode.LeftControl, false);
        _game.Frame();
        Assert.True(_game.Read(Preset4).IsHeld);

        _harness.Key(Scancode.RightControl, false);
        _game.Frame();
        Assert.False(_game.Read(Preset4).IsHeld);
    }

    [Fact]
    public void AnActionWithoutLayerReadsAsReleased()
    {
        _harness.Key(Scancode.Space, true);
        _game.Frame();
        _game.Layer.Dispose();

        Assert.False(_game.Read(Jump).IsHeld);
        Assert.Throws<KeyNotFoundException>(() => _game.Action("inputaction:example.input.missing"));
    }
}
