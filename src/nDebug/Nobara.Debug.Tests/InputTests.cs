// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using ImGuiNET;
using Nobara.Core;
using Nobara.Debug.Tests.Support;
using Nobara.Frames;
using Nobara.Input;

namespace Nobara.Debug.Tests;

/// <summary>The input layer of nDebug: its place in the stack, the toggle, what it consumes, and the text focus.</summary>
public sealed class InputTests : IDisposable
{
    private readonly DebugHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void The_layer_of_nDebug_is_above_every_other_layer()
    {
        using Engine engine = _harness.Start();

        IReadOnlyList<InputLayer> layers = _harness.GameModule.Context.Resolve<IInputSystem>().Layers;

        Assert.Same(_harness.Ui.Layer, layers[0]);
        Assert.Equal(InputLayerPriority.Debug, layers[0].Priority);
        Assert.True(layers[0].ReceivesRawInput);
    }

    [Fact]
    public void F1_opens_and_closes_the_tools_and_frees_the_mouse_meanwhile()
    {
        using Engine engine = _harness.Start();
        _harness.Frame(engine);
        Assert.True(_harness.FakeWindow.RelativeMouse);

        _harness.Tap(Scancode.F1).Frame(engine);
        Assert.True(_harness.Debug.IsOpen);
        Assert.False(_harness.FakeWindow.RelativeMouse);
        Assert.DoesNotContain(_harness.GameModule.Raw, e => e.Kind == RawInputKind.KeyDown && e.Scancode == Scancode.F1);

        _harness.Tap(Scancode.F1).Frame(engine);
        Assert.False(_harness.Debug.IsOpen);
        Assert.True(_harness.FakeWindow.RelativeMouse);
    }

    [Fact]
    public void F1_does_nothing_while_the_tools_are_not_available()
    {
        _harness.DevelopmentMode = false;
        using Engine engine = _harness.Start();

        _harness.Tap(Scancode.F1).Frame(engine);

        Assert.False(_harness.Debug.IsOpen);
        Assert.Contains(_harness.GameModule.Raw, e => e.Kind == RawInputKind.KeyDown && e.Scancode == Scancode.F1);
    }

    [Fact]
    public void A_click_in_a_window_does_not_reach_the_game_and_a_click_elsewhere_does()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        _harness.Debug.AddWindow(
            _harness.GameModule.Context,
            new DebugWindowOptions { Title = "Chunks", Placement = DebugWindowPlacement.Center, Size = new Vector2(200, 100) },
            new DelegateWindow(_ => ImGui.Text("Chunks: 12")));
        _harness.Frame(engine, 2);

        _harness.MoveTo(640, 360).Frame(engine);
        Assert.True(_harness.Debug.WantsMouse);
        _harness.GameModule.Clear();
        _harness.Click(640, 360).Frame(engine);
        Assert.False(_harness.GameModule.SawClick());
        Assert.DoesNotContain(_harness.GameModule.Actions, a => a.Action == DebugHarness.Fire);

        // Dear ImGui spreads a press and a release of one frame over two frames.
        _harness.Frame(engine);
        _harness.MoveTo(20, 700).Frame(engine);
        Assert.False(_harness.Debug.WantsMouse);
        _harness.GameModule.Clear();
        _harness.Click(20, 700).Frame(engine);
        Assert.True(_harness.GameModule.SawClick());
        Assert.Contains((DebugHarness.Fire, ActionEdge.Pressed), _harness.GameModule.Actions);
    }

    [Fact]
    public void Without_anything_to_show_nDebug_lets_the_input_through()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Chunks", Placement = DebugWindowPlacement.Center }, new DelegateWindow());
        _harness.Frame(engine);

        _harness.MoveTo(640, 360).Frame(engine);
        _harness.Click(640, 360).Tap(Scancode.Space).Frame(engine);

        Assert.False(_harness.Debug.WantsMouse);
        Assert.True(_harness.GameModule.SawClick());
        Assert.Contains((DebugHarness.Jump, ActionEdge.Pressed), _harness.GameModule.Actions);
    }

    [Fact]
    public void A_text_field_takes_the_text_focus_and_receives_typed_text()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        string name = string.Empty;
        Vector2 field = default;
        _harness.Debug.AddWindow(
            _harness.GameModule.Context,
            new DebugWindowOptions { Title = "Rename", Placement = DebugWindowPlacement.Center, Size = new Vector2(300, 120) },
            new DelegateWindow(_ =>
            {
                ImGui.InputText("Name", ref name, 64);
                field = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
            }));
        _harness.Frame(engine, 2);

        _harness.MoveTo(field.X, field.Y).Frame(engine);
        _harness.Click(field.X, field.Y).Frame(engine);
        _harness.Frame(engine);
        Assert.True(_harness.Ui.Layer!.HasTextFocus);
        Assert.True(_harness.FakeWindow.TextInput);
        Assert.True(_harness.Debug.WantsKeyboard);

        _harness.GameModule.Clear();
        _harness.Text("ab").Tap(Scancode.Space).Frame(engine);
        _harness.Frame(engine);
        Assert.Equal("ab", name);
        Assert.DoesNotContain(_harness.GameModule.Actions, a => a.Action == DebugHarness.Jump);

        _harness.MoveTo(20, 700).Frame(engine);
        _harness.Click(20, 700).Frame(engine);
        _harness.Frame(engine);
        Assert.False(_harness.Ui.Layer.HasTextFocus);
        Assert.False(_harness.FakeWindow.TextInput);
    }

    [Fact]
    public void Letter_keys_follow_the_layout_and_other_keys_keep_their_position()
    {
        using Engine engine = _harness.Start();
        IRawInput input = _harness.GameModule.Context.Resolve<IWindowSystem>().Input;
        var keys = new ImGuiKeys();

        Assert.Equal(ImGuiKey.A, keys.Map(Scancode.Q, input));
        Assert.Equal(ImGuiKey.Q, keys.Map(Scancode.A, input));
        Assert.Equal(ImGuiKey.B, keys.Map(Scancode.B, input));
        Assert.Equal(ImGuiKey.Enter, keys.Map(Scancode.Enter, input));
        Assert.Equal(ImGuiKey._0, keys.Map(Scancode.Digit0, input));
        Assert.Equal(ImGuiKey.F12, keys.Map(Scancode.F12, input));
        Assert.Equal(ImGuiKey.None, keys.Map(Scancode.Mute, input));
    }
}
