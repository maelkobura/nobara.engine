// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Frames;
using Nobara.Input.Tests.Support;
using static Nobara.Input.Tests.Support.GameInput;

namespace Nobara.Input.Tests;

public sealed class AllocationTests : IDisposable
{
    private readonly InputHarness _harness = new();
    private readonly Game _game;

    public AllocationTests()
    {
        _game = Game.Start(_harness);
    }

    public void Dispose() => _game.Dispose();

    [Fact]
    public void EvaluatingReadingAndSamplingAllocateNothing()
    {
        InputSampler sampler = _game.Layer.CreateSampler();
        _game.Input.AddLayer(_game.Probe.Context, new InputLayerOptions { Name = "hud", Priority = InputLayerPriority.Hud });
        InputAction jump = _game.Action(Jump);
        InputAction move = _game.Action(Move);
        InputAction look = _game.Action(Look);

        // Warm up: the first frames build the index and grow the buffers.
        for (int i = 0; i < 4; i++)
        {
            Script();
            _game.Frame();
            sampler.Sample();
        }

        long quiet = Measure(script: false);
        long busy = Measure(script: true);

        Assert.Equal(quiet, busy);

        long reads = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = _game.Layer.Read(jump);
            _ = _game.Layer.Read(move);
            sampler.Sample();
            _ = sampler.Read(look);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - reads);

        long Measure(bool script)
        {
            long total = 0;
            for (int i = 0; i < 20; i++)
            {
                if (script)
                {
                    Script();
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                _game.Frame();
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            return total;
        }
    }

    private void Script() => _harness
        .Key(Scancode.W, true).Key(Scancode.LeftControl, true).Key(Scancode.Digit4, true)
        .Move(3, 2).Wheel(0, 1).Button(MouseButton.Left, true)
        .Key(Scancode.Digit4, false).Key(Scancode.LeftControl, false).Key(Scancode.W, false).Button(MouseButton.Left, false)
        .Tap(Scancode.Q).Tap(Scancode.C);
}
