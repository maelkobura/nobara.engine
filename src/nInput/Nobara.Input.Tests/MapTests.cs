// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Frames;
using Nobara.Input.Tests.Support;
using static Nobara.Input.Tests.Support.GameInput;

namespace Nobara.Input.Tests;

public sealed class MapTests : IDisposable
{
    private readonly InputHarness _harness = new();
    private readonly Game _game;

    public MapTests()
    {
        _game = Game.Start(_harness);
    }

    public void Dispose() => _game.Dispose();

    [Fact]
    public void MapsAreReadWithTheirActions()
    {
        InputMap onFoot = _game.Input.GetMap(OnFoot);
        InputMap vehicle = _game.Input.GetMap(Vehicle);

        Assert.True(onFoot.IsActive);
        Assert.Equal("input.map.on_foot", onFoot.DisplayName);
        Assert.False(vehicle.ActiveByDefault);
        Assert.Equal("inputmap.example.input.vehicle", vehicle.DisplayName);
        Assert.Equal([Jump, Move], onFoot.Actions.Select(a => a.Id));
        Assert.Same(vehicle, _game.Action(Accelerate).Map);
        Assert.Null(_game.Action(Look).Map);
        Assert.Equal("inputaction.example.input.look", _game.Action(Look).DisplayName);
    }

    [Fact]
    public void TheSameKeyServesTwoMapsThatAreNotActiveTogether()
    {
        _harness.Key(Scancode.W, true);
        _game.Frame();
        Assert.Equal(new Vector2(0, 1), _game.Read(Move).Vector);
        Assert.False(_game.Read(Accelerate).IsHeld);

        _harness.Key(Scancode.W, false);
        _game.Input.GetMap(OnFoot).IsActive = false;
        _game.Input.GetMap(Vehicle).IsActive = true;
        _game.Frame();
        _harness.Key(Scancode.W, true);
        _game.Frame();

        Assert.True(_game.Read(Accelerate).WasPressed);
        Assert.Equal(Vector2.Zero, _game.Read(Move).Vector);
    }

    [Fact]
    public void AnActionWhoseMapTurnsOffIsReleased()
    {
        _harness.Key(Scancode.Space, true);
        _game.Frame();
        Assert.True(_game.Read(Jump).IsHeld);

        _game.Input.GetMap(OnFoot).IsActive = false;
        _game.Frame();
        Assert.False(_game.Read(Jump).IsHeld);

        // Turned on again, the action waits for a new press.
        _game.Input.GetMap(OnFoot).IsActive = true;
        _game.Frame();
        Assert.False(_game.Read(Jump).IsHeld);
        _harness.Key(Scancode.Space, false).Key(Scancode.Space, true);
        _game.Frame();
        Assert.True(_game.Read(Jump).WasPressed);
    }
}
