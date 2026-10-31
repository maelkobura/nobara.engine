// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Frames;
using Nobara.Input.Tests.Support;
using static Nobara.Input.Tests.Support.GameInput;

namespace Nobara.Input.Tests;

public sealed class InteractionTests : IDisposable
{
    private readonly InputHarness _harness = new();
    private readonly Game _game;

    public InteractionTests()
    {
        _game = Game.Start(_harness);
    }

    public void Dispose() => _game.Dispose();

    [Fact]
    public void HoldPressesOnceTheDurationPassed()
    {
        _harness.Key(Scancode.LeftShift, true);
        _game.Frame();
        Assert.False(_game.Read(Sprint).IsHeld);

        _game.Frame(0.2);
        Assert.False(_game.Read(Sprint).IsHeld);

        _game.Frame(0.2);
        Assert.True(_game.Read(Sprint).WasPressed);

        _harness.Key(Scancode.LeftShift, false);
        _game.Frame();
        Assert.True(_game.Read(Sprint).WasReleased);
    }

    [Fact]
    public void HoldReleasedBeforeTheDurationNeverPresses()
    {
        _harness.Key(Scancode.LeftShift, true);
        _game.Frame();
        _harness.Key(Scancode.LeftShift, false);
        _game.Frame(0.1);

        Assert.Equal(0, _game.Read(Sprint).Presses);
    }

    [Fact]
    public void HoldLongerThanItsDurationBetweenTwoFramesStillPresses()
    {
        // The press is dated by the frame that reads it; the duration of 0.3 s passes during the
        // frame that also reads the release.
        _harness.Key(Scancode.LeftShift, true);
        _game.Frame().Frame(0.2);
        Assert.Equal(0, _game.Read(Sprint).Presses);
        _harness.Key(Scancode.LeftShift, false);
        _game.Frame(0.2);

        ActionState state = _game.Read(Sprint);
        Assert.Equal(1, state.Presses);
        Assert.Equal(1, state.Releases);
    }

    [Fact]
    public void ToggleStaysHeldBetweenTwoPresses()
    {
        _harness.Tap(Scancode.C);
        _game.Frame();
        Assert.True(_game.Read(Crouch).WasPressed);

        _game.Frame(1);
        Assert.True(_game.Read(Crouch).IsHeld);

        _harness.Key(Scancode.C, true);
        _game.Frame();
        Assert.True(_game.Read(Crouch).WasReleased);
        Assert.False(_game.Read(Crouch).IsHeld);
    }

    [Fact]
    public void TapTriggersOnAShortPress()
    {
        _harness.Key(Scancode.E, true);
        _game.Frame();
        Assert.False(_game.Read(Interact).WasPressed);

        _harness.Key(Scancode.E, false);
        _game.Frame(0.1);
        ActionState state = _game.Read(Interact);
        Assert.Equal(1, state.Presses);
        Assert.Equal(1, state.Releases);
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void HoldWinsOverTapOnTheSameKeyOnceItsDurationPassed()
    {
        // Interact taps on E within 0.25 s; Inspect holds E for 0.2 s.
        _harness.Key(Scancode.E, true);
        _game.Frame();
        _game.Frame(0.21);
        Assert.True(_game.Read(Inspect).WasPressed);

        _harness.Key(Scancode.E, false);
        _game.Frame(0.01);
        Assert.Equal(0, _game.Read(Interact).Presses);
    }

    [Fact]
    public void DoubleTapNeedsTwoPressesCloseTogether()
    {
        _harness.Tap(Scancode.Q);
        _game.Frame();
        _harness.Key(Scancode.Q, true);
        _game.Frame(0.2);
        Assert.True(_game.Read(Dodge).WasPressed);

        _harness.Key(Scancode.Q, false);
        _game.Frame();
        Assert.True(_game.Read(Dodge).WasReleased);

        _harness.Tap(Scancode.Q);
        _game.Frame(1);
        _game.Frame(0.2);
        _harness.Tap(Scancode.Q);
        _game.Frame(0.2);
        Assert.Equal(0, _game.Read(Dodge).Presses);
    }

    [Fact]
    public void ReleaseTriggersWhenTheKeyGoesUp()
    {
        _harness.Key(Scancode.G, true);
        _game.Frame();
        Assert.False(_game.Read(Drop).WasPressed);

        _harness.Key(Scancode.G, false);
        _game.Frame();
        Assert.True(_game.Read(Drop).WasPressed);
        Assert.True(_game.Read(Drop).WasReleased);
    }

    [Fact]
    public void AStepOfTheWheelIsAButtonPressedAndReleasedAtOnce()
    {
        _harness.Wheel(0, -1);
        _game.Frame();

        ActionState state = _game.Read(Drop);
        Assert.Equal(1, state.Presses);
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void ThePlayerTurnsAHoldIntoAToggle()
    {
        _game.Input.Rebinding.SetInteraction(_game.Action(Sprint), InputInteraction.Toggle);
        _game.Frame();

        _harness.Tap(Scancode.LeftShift);
        _game.Frame();
        Assert.True(_game.Read(Sprint).IsHeld);
        Assert.Equal(InteractionKind.Hold, _game.Action(Sprint).DefaultInteraction.Kind);
        Assert.True(_game.Action(Sprint).HasCustomInteraction);
    }
}
