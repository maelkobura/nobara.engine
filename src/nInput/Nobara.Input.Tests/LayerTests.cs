// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Frames;
using Nobara.Input.Tests.Support;
using static Nobara.Input.Tests.Support.GameInput;

namespace Nobara.Input.Tests;

public sealed class LayerTests : IDisposable
{
    private readonly InputHarness _harness = new();
    private readonly Game _game;

    public LayerTests()
    {
        _harness.Policies[GameInput.Module] = Nobara.Formats.Policy.Log;
        _game = Game.Start(_harness);
    }

    public void Dispose() => _game.Dispose();

    private FakeWindow Window => _harness.Backend.Window!;

    private InputLayer Add(string name, int priority, InputCapture capture = InputCapture.None, CursorRequest cursor = CursorRequest.None, Recorder? handler = null, bool raw = false) =>
        _game.Input.AddLayer(_game.Probe.Context, new InputLayerOptions { Name = name, Priority = priority, Capture = capture, Cursor = cursor, Handler = handler, ReceivesRawInput = raw });

    [Fact]
    public void LayersAreOrderedByPriorityThenLastRegisteredFirst()
    {
        InputLayer hud = Add("hud", InputLayerPriority.Hud);
        InputLayer first = Add("inventory", InputLayerPriority.Modal);
        InputLayer second = Add("chest", InputLayerPriority.Modal);
        InputLayer debug = Add("debug", InputLayerPriority.Debug);

        Assert.Equal([debug, second, first, hud, _game.Layer], _game.Input.Layers);
    }

    [Fact]
    public void ALayerThatPassesLetsTheLayersBelowSeeTheInput()
    {
        var recorder = new Recorder();
        InputLayer hud = Add("hud", InputLayerPriority.Hud, handler: recorder);
        _harness.Key(Scancode.Space, true);
        _game.Frame();

        Assert.True(hud.Read(_game.Action(Jump)).WasPressed);
        Assert.True(_game.Read(Jump).WasPressed);
        Assert.Equal(["jump Pressed"], recorder.Actions);
    }

    [Fact]
    public void ALayerThatConsumesAnActionKeepsItsKeyFromTheLayersBelow()
    {
        InputLayer hud = Add("hud", InputLayerPriority.Hud, handler: new Recorder { Consume = Jump });
        _harness.Key(Scancode.Space, true).Key(Scancode.W, true);
        _game.Frame();

        Assert.True(hud.Read(_game.Action(Jump)).IsHeld);
        Assert.False(_game.Read(Jump).WasPressed);
        Assert.Equal(new Vector2(0, 1), _game.Read(Move).Vector);
    }

    [Fact]
    public void ALayerThatConsumesARawEventKeepsItFromItsActionsAndTheLayersBelow()
    {
        var recorder = new Recorder { ConsumeRaw = Scancode.Space };
        InputLayer debug = Add("debug", InputLayerPriority.Debug, handler: recorder, raw: true);
        _harness.Key(Scancode.Space, true).Key(Scancode.W, true);
        _game.Frame();

        Assert.False(debug.Read(_game.Action(Jump)).IsHeld);
        Assert.False(_game.Read(Jump).IsHeld);
        Assert.True(_game.Read(Move).IsHeld);
        Assert.Equal(["KeyDown Space", "KeyDown W"], recorder.Raw);
    }

    [Fact]
    public void ALayerThatCapturesTheKeyboardKeepsAllOfItAndReleasesWhatWasHeldBelow()
    {
        _harness.Key(Scancode.W, true);
        _game.Frame();
        Assert.True(_game.Read(Move).IsHeld);

        InputLayer inventory = Add("inventory", InputLayerPriority.Modal, InputCapture.Keyboard);
        _game.Frame();
        Assert.False(_game.Read(Move).IsHeld);
        Assert.True(_game.Read(Move).WasReleased);

        _harness.Key(Scancode.Space, true).Move(4, 0);
        _game.Frame();
        Assert.True(inventory.Read(_game.Action(Jump)).IsHeld);
        Assert.False(_game.Read(Jump).IsHeld);
        Assert.NotEqual(Vector2.Zero, _game.Read(Look).Vector);

        // The key released while the inventory is open does not reach the game, which forgot it.
        inventory.Dispose();
        _harness.Key(Scancode.Space, false).Key(Scancode.W, false);
        _game.Frame();
        Assert.False(_game.Read(Jump).WasReleased);
    }

    [Fact]
    public void ADisabledLayerLetsEverythingThrough()
    {
        InputLayer inventory = Add("inventory", InputLayerPriority.Modal, InputCapture.All);
        _harness.Key(Scancode.Space, true);
        _game.Frame();
        Assert.True(inventory.Read(_game.Action(Jump)).IsHeld);

        inventory.Enabled = false;
        _game.Frame();
        Assert.False(inventory.Read(_game.Action(Jump)).IsHeld);
        _harness.Key(Scancode.Space, false).Key(Scancode.Space, true);
        _game.Frame();
        Assert.True(_game.Read(Jump).WasPressed);
    }

    [Fact]
    public void ALayerThatAsksForRelativeModeCapturesTheMouseAndALayerAboveThatNeedsTheCursorFreesIt()
    {
        _game.Layer.Cursor = CursorRequest.Relative;
        Add("hud", InputLayerPriority.Hud);
        _game.Frame();
        Assert.True(Window.RelativeMouse);

        InputLayer pause = Add("pause", InputLayerPriority.Modal, cursor: CursorRequest.Cursor);
        _game.Frame();
        Assert.False(Window.RelativeMouse);

        pause.Dispose();
        _game.Frame();
        Assert.True(Window.RelativeMouse);

        // A layer above that captures the mouse needs the cursor too.
        Add("inventory", InputLayerPriority.Modal, InputCapture.Mouse);
        _game.Frame();
        Assert.False(Window.RelativeMouse);
    }

    [Fact]
    public void ALayerThatCapturesTheMouseKeepsItsMovements()
    {
        InputLayer map = Add("map", InputLayerPriority.Modal, InputCapture.Mouse);
        _harness.Move(4, 0).Button(MouseButton.Left, true).Key(Scancode.Space, true);
        _game.Frame();

        Assert.Equal(new Vector2(4, 0), map.Read(_game.Action(Look)).Vector);
        Assert.Equal(Vector2.Zero, _game.Read(Look).Vector);
        Assert.False(_game.Read(Attack).IsHeld);
        Assert.True(_game.Read(Jump).IsHeld);
    }

    [Fact]
    public void TheTextFocusReceivesTypedTextAndBlocksTheActionsOfTheKeyboard()
    {
        var chat = new Recorder();
        InputLayer layer = Add("chat", InputLayerPriority.Modal, handler: chat, raw: true);
        _harness.Key(Scancode.W, true);
        _game.Frame();
        Assert.True(_game.Read(Move).IsHeld);

        layer.TakeTextFocus();
        Assert.True(Window.TextInput);
        Assert.True(layer.HasTextFocus);
        _harness.Key(Scancode.Space, true).Text("hi").Button(MouseButton.Left, true);
        _game.Frame();

        Assert.Equal(["hi"], chat.Text);
        Assert.Contains("KeyDown Space", chat.Raw);
        Assert.False(_game.Read(Move).IsHeld);
        Assert.False(_game.Read(Jump).IsHeld);
        Assert.False(layer.Read(_game.Action(Jump)).IsHeld);
        Assert.True(_game.Read(Attack).IsHeld);

        layer.ReleaseTextFocus();
        Assert.False(Window.TextInput);
        _harness.Key(Scancode.Space, false).Key(Scancode.Space, true);
        _game.Frame();
        Assert.True(_game.Read(Jump).WasPressed);
    }

    [Fact]
    public void TheFocusLossOfTheWindowReleasesEverything()
    {
        _harness.Key(Scancode.Space, true);
        _game.Frame();
        _harness.Backend.Queue(sink =>
        {
            Window.HasFocus = false;
            sink.OnWindowEvent(WindowEventKind.FocusLost);
        });
        _game.Frame();

        Assert.False(_game.Read(Jump).IsHeld);
        Assert.True(_game.Read(Jump).WasReleased);
    }

    [Fact]
    public void AnExceptionOfAHandlerIsChargedToItsModuleAndTheInputPasses()
    {
        Add("hud", InputLayerPriority.Hud, handler: new Recorder { Throw = true });

        _harness.Key(Scancode.Space, true);
        _game.Frame();

        Assert.True(_game.Read(Jump).WasPressed);
        Assert.Contains(_harness.Log.Entries, e => e.Level == Nobara.Core.Logging.LogLevel.Error && e.Message.Contains(GameInput.Module, StringComparison.Ordinal));
    }

    /// <summary>A handler that records what reaches it.</summary>
    private sealed class Recorder : IInputLayerHandler
    {
        public List<string> Actions { get; } = [];

        public List<string> Raw { get; } = [];

        public List<string> Text { get; } = [];

        public string? Consume { get; init; }

        public Scancode? ConsumeRaw { get; init; }

        public bool Throw { get; init; }

        public InputResult OnRawInput(in RawInputEvent e, IRawInput input)
        {
            Raw.Add($"{e.Kind} {e.Scancode}");
            return e.Kind == RawInputKind.KeyDown && e.Scancode == ConsumeRaw ? InputResult.Consume : InputResult.Pass;
        }

        public InputResult OnAction(InputAction action, ActionEdge edge)
        {
            if (Throw)
            {
                throw new InvalidOperationException("The handler fails.");
            }

            Actions.Add($"{action.Id[(action.Id.LastIndexOf('.') + 1)..]} {edge}");
            return action.Id == Consume && edge == ActionEdge.Pressed ? InputResult.Consume : InputResult.Pass;
        }

        public void OnText(in RawInputEvent e, ReadOnlySpan<char> text) => Text.Add(text.ToString());
    }
}
