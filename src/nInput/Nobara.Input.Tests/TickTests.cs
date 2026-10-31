// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Loop;
using Nobara.Frames;
using Nobara.Input.Tests.Support;
using static Nobara.Input.Tests.Support.GameInput;

namespace Nobara.Input.Tests;

public sealed class TickTests : IDisposable
{
    private readonly InputHarness _harness = new();
    private readonly TickRecorder _ticks = new();
    private readonly Game _game;

    public TickTests()
    {
        _game = Game.Start(_harness, p => p.OnRegister = probe => probe.Context.Loop.Subscribe("Simulation", _ticks));
        _ticks.Game = _game;
        _ticks.Sampler = _game.Layer.CreateSampler();
    }

    public void Dispose() => _game.Dispose();

    [Fact]
    public void APressAndAReleaseBetweenTwoSamplesAreSeenAtTheNextSample()
    {
        InputSampler sampler = _game.Layer.CreateSampler();
        _harness.Key(Scancode.Space, true);
        _game.Frame();
        _harness.Key(Scancode.Space, false);
        _game.Frame();

        sampler.Sample();
        ActionState state = sampler.Read(_game.Action(Jump));
        Assert.True(state.WasPressed);
        Assert.True(state.WasReleased);
        Assert.False(state.IsHeld);

        sampler.Sample();
        Assert.False(sampler.Read(_game.Action(Jump)).WasPressed);
    }

    [Fact]
    public void TheMovementsOfTheMouseAddUpUntilTheSample()
    {
        InputSampler sampler = _game.Layer.CreateSampler();
        _harness.Move(2, 0);
        _game.Frame();
        _harness.Move(3, -1);
        _game.Frame();
        _harness.Move(1, 0);
        _game.Frame();

        // The camera reads the movement of each frame; the tick reads the sum.
        Assert.Equal(new Vector2(1, 0), _game.Read(Look).Vector);
        sampler.Sample();
        Assert.Equal(new Vector2(6, 1), sampler.Read(_game.Action(Look)).Vector);
        sampler.Sample();
        Assert.Equal(Vector2.Zero, sampler.Read(_game.Action(Look)).Vector);
    }

    [Fact]
    public void SeveralFramesPerTickLoseNoPress()
    {
        // 30 ticks per second, 120 frames per second: four frames per tick.
        _harness.Tap(Scancode.Space).Move(5, 0);
        for (int i = 0; i < 8; i++)
        {
            _game.Frame(1.0 / 120);
        }

        Assert.Contains(_ticks.Jumps, j => j.Pressed && j.Released);
        Assert.Equal(1, _ticks.Jumps.Count(j => j.Pressed));
        Assert.Equal(5, _ticks.Look.Sum(v => v.X));
    }

    [Fact]
    public void SeveralTicksPerFrameSeeThePressOnce()
    {
        _game.Frame(1.0 / 30);
        _ticks.Jumps.Clear();
        _harness.Key(Scancode.Space, true);
        _game.Frame(0.11);

        Assert.Equal(3, _ticks.Jumps.Count);
        Assert.True(_ticks.Jumps[0].Pressed);
        Assert.All(_ticks.Jumps, j => Assert.True(j.Held));
        Assert.All(_ticks.Jumps.Skip(1), j => Assert.False(j.Pressed));
    }

    /// <summary>The tick system of the test: it samples the layer of the game at each tick.</summary>
    private sealed class TickRecorder : IPhaseHandler
    {
        public Game? Game { get; set; }

        public InputSampler? Sampler { get; set; }

        public List<(bool Pressed, bool Released, bool Held)> Jumps { get; } = [];

        public List<Vector2> Look { get; } = [];

        public void Run(in PhaseTime time)
        {
            if (Sampler is null || Game is null)
            {
                return;
            }

            Sampler.Sample();
            ActionState jump = Sampler.Read(Game.Action(GameInput.Jump));
            Jumps.Add((jump.WasPressed, jump.WasReleased, jump.IsHeld));
            Look.Add(Sampler.Read(Game.Action(GameInput.Look)).Vector);
        }
    }
}
