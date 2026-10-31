// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;

namespace Nobara.Input.Tests.Support;

/// <summary>
/// The test game running: the engine, the module of the test game, and the
/// layer of its player controller, at the game priority.
/// </summary>
internal sealed class Game : IDisposable
{
    private Game(InputHarness harness, Engine engine, Probe probe)
    {
        Harness = harness;
        Engine = engine;
        Probe = probe;
        Layer = probe.Input.AddLayer(probe.Context, new InputLayerOptions { Name = "game", Priority = InputLayerPriority.Game });
    }

    public InputHarness Harness { get; }

    public Engine Engine { get; }

    public Probe Probe { get; }

    public IInputSystem Input => Probe.Input;

    /// <summary>Gets the layer of the player controller.</summary>
    public InputLayer Layer { get; }

    /// <summary>Starts the test game.</summary>
    /// <param name="harness">The harness, set up already; the module of the test game is added to it.</param>
    /// <param name="behavior">More set-up of the module of the test game.</param>
    public static Game Start(InputHarness harness, Action<Probe>? behavior = null)
    {
        Probe? probe = null;
        harness.Module(GameInput.Module, GameInput.Nof, p =>
        {
            probe = p;
            behavior?.Invoke(p);
        });
        Engine engine = harness.Start();
        return new Game(harness, engine, probe!);
    }

    public InputAction Action(string id) => Input.GetAction(id);

    /// <summary>Reads an action in the last frame, as the layer of the game sees it.</summary>
    public ActionState Read(string id) => Layer.Read(Action(id));

    /// <summary>Runs one frame that lasts <paramref name="seconds"/>, 1/60 s by default.</summary>
    public Game Frame(double seconds = 1.0 / 60)
    {
        Harness.Frame(Engine, seconds);
        return this;
    }

    public void Dispose()
    {
        Engine.Dispose();
        Harness.Dispose();
    }
}
