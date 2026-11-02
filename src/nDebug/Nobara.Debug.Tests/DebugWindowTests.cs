// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using ImGuiNET;
using Nobara.Core;
using Nobara.Debug.Tests.Support;
using Nobara.Graphics;
using Nobara.Renderer;
using F = Nobara.Formats;

namespace Nobara.Debug.Tests;

/// <summary>Windows: registration, removal, tools and screens, and their draws in the pass of the user interface.</summary>
public sealed class DebugWindowTests : IDisposable
{
    private readonly DebugHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void A_tool_window_shows_while_the_tools_are_open()
    {
        using Engine engine = _harness.Start();
        var content = new DelegateWindow(_ => ImGui.Text("Chunks: 12"));
        DebugWindow window = _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Chunks" }, content);
        Assert.True(_harness.Debug.ToolsEnabled);
        Assert.Contains(window, _harness.Debug.Windows);

        _harness.Frame(engine);
        Assert.Equal(0, content.Draws);
        Assert.Equal(0, _harness.Ui.Pass!.CommandCount);

        _harness.Debug.IsOpen = true;
        _harness.Frame(engine);
        Assert.Equal(1, content.Draws);
        Assert.True(_harness.Ui.Pass.CommandCount > 0);
        Assert.Contains(CommandKind.DrawIndexed, _harness.Device.Executed);
        Assert.Contains(ImGuiPass.PassName, _harness.GameModule.Context.Resolve<IRenderer>().LastGraph!.Passes);

        _harness.Debug.IsOpen = false;
        _harness.Frame(engine);
        Assert.Equal(1, content.Draws);
        Assert.Equal(0, _harness.Ui.Pass.CommandCount);
        Assert.Empty(_harness.Errors);
    }

    [Fact]
    public void A_closed_tool_window_does_not_show_until_it_opens_again()
    {
        using Engine engine = _harness.Start();
        var content = new DelegateWindow();
        DebugWindow window = _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Chunks", Open = false }, content);
        _harness.Debug.IsOpen = true;

        _harness.Frame(engine);
        Assert.Equal(0, content.Draws);

        window.IsOpen = true;
        _harness.Frame(engine);
        Assert.Equal(1, content.Draws);
    }

    [Fact]
    public void Disposing_a_window_removes_it_even_while_it_draws()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        var once = new DelegateWindow(w => w.Dispose());
        var other = new DelegateWindow();
        DebugWindow removed = _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Once" }, once);
        _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Other" }, other);

        _harness.Frame(engine, 2);

        Assert.Equal(1, once.Draws);
        Assert.Equal(2, other.Draws);
        Assert.True(removed.IsRemoved);
        Assert.DoesNotContain(removed, _harness.Debug.Windows);
    }

    [Fact]
    public void A_screen_shows_without_the_tools_and_frees_the_mouse()
    {
        _harness.DevelopmentMode = false;
        using Engine engine = _harness.Start();
        Assert.False(_harness.Debug.ToolsEnabled);
        Assert.Throws<InvalidOperationException>(() => _harness.Debug.IsOpen = true);

        var tool = new DelegateWindow();
        _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Tool" }, tool);
        _harness.Frame(engine);
        Assert.True(_harness.FakeWindow.RelativeMouse);

        var menu = new DelegateWindow(_ => ImGui.Button("Resume"));
        DebugWindow screen = _harness.Debug.AddWindow(
            _harness.GameModule.Context,
            new DebugWindowOptions { Title = "Pause", Kind = DebugWindowKind.Screen, Placement = DebugWindowPlacement.Fullscreen },
            menu);
        _harness.Frame(engine, 2);

        Assert.Equal(2, menu.Draws);
        Assert.Equal(0, tool.Draws);
        Assert.False(_harness.FakeWindow.RelativeMouse);

        screen.Dispose();
        _harness.Frame(engine, 2);
        Assert.Equal(2, menu.Draws);
        Assert.True(_harness.FakeWindow.RelativeMouse);
    }

    [Theory]
    [InlineData(true, "development", true)]
    [InlineData(false, "development", false)]
    [InlineData(false, "always", true)]
    [InlineData(true, "never", false)]
    public void The_distribution_decides_when_the_tools_are_available(bool developmentMode, string tools, bool available)
    {
        _harness.DevelopmentMode = developmentMode;
        _harness.Settings[DebugSettings.Tools] = tools;
        using Engine engine = _harness.Start();

        Assert.Equal(available, _harness.Debug.ToolsEnabled);
    }

    [Fact]
    public void An_exception_of_a_window_is_charged_to_its_module_and_the_frame_goes_on()
    {
        _harness.Policies[DebugHarness.Game] = F.Policy.Log;
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        var failing = new DelegateWindow(_ =>
        {
            // The window leaves a group open; Dear ImGui closes it at the end of the frame.
            ImGui.BeginGroup();
            throw new InvalidOperationException("The chunk view failed.");
        });
        var other = new DelegateWindow(_ => ImGui.Text("Still drawn"));
        _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Failing" }, failing);
        _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Other" }, other);

        _harness.Frame(engine, 2);

        Assert.Equal(2, failing.Draws);
        Assert.Equal(2, other.Draws);
        Assert.Contains(_harness.Errors, e => e.Contains(DebugHarness.Game, StringComparison.Ordinal));
        Assert.True(_harness.Ui.Pass!.CommandCount > 0);
    }

    [Fact]
    public void A_window_of_a_disabled_module_is_not_drawn()
    {
        _harness.Policies["example.tools"] = F.Policy.Disable;
        var content = new DelegateWindow(_ => throw new InvalidOperationException("The tool failed."));
        _harness.Module("example.tools", debug: false, behavior: p => p.OnStart = p =>
            p.Context.Resolve<IDebugUi>().AddWindow(p.Context, new DebugWindowOptions { Title = "Tool" }, content));
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;

        _harness.Frame(engine, 3);

        Assert.Equal(1, content.Draws);
        Assert.False(_harness.GameModule.Context.IsModuleActive("example.tools"));
    }

    [Fact]
    public void Windows_take_their_placement_and_size()
    {
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        Vector2 centerPosition = default;
        Vector2 centerSize = default;
        Vector2 fullPosition = default;
        Vector2 fullSize = default;
        _harness.Debug.AddWindow(
            _harness.GameModule.Context,
            new DebugWindowOptions { Title = "Centered", Placement = DebugWindowPlacement.Center, Size = new Vector2(200, 100) },
            new DelegateWindow(_ => (centerPosition, centerSize) = (ImGui.GetWindowPos(), ImGui.GetWindowSize())));
        _harness.Debug.AddWindow(
            _harness.GameModule.Context,
            new DebugWindowOptions { Title = "Menu", Kind = DebugWindowKind.Screen, Placement = DebugWindowPlacement.Fullscreen },
            new DelegateWindow(_ => (fullPosition, fullSize) = (ImGui.GetWindowPos(), ImGui.GetWindowSize())));

        _harness.Frame(engine, 2);

        Assert.Equal(new Vector2(200, 100), centerSize);
        Assert.Equal(new Vector2(540, 310), centerPosition);
        Assert.Equal(new Vector2(1280, 720), fullPosition + fullSize);
    }

    [Fact]
    public void The_font_follows_the_scale_of_the_monitor()
    {
        _harness.Windows.ContentScale = 2;
        using Engine engine = _harness.Start();
        _harness.Debug.IsOpen = true;
        float fontSize = 0;
        _harness.Debug.AddWindow(_harness.GameModule.Context, new DebugWindowOptions { Title = "Scale" }, new DelegateWindow(_ => fontSize = ImGui.GetFontSize()));

        _harness.Frame(engine);

        Assert.Equal(DebugUi.FontSize * 2, fontSize);
        Assert.True(_harness.Ui.Pass!.Atlas!.Width > 0);
    }
}
