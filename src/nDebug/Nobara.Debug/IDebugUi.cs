// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;

namespace Nobara.Debug;

/// <summary>
/// The debug user interface: Dear ImGui, drawn by nRenderer over the frame,
/// with the windows that modules register. Resolved with
/// <c>context.Resolve&lt;IDebugUi&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DebugWindowKind.Tool"/> window shows while the debug tools are
/// open (<see cref="IsOpen"/>), and only when they are available
/// (<see cref="ToolsEnabled"/>). A <see cref="DebugWindowKind.Screen"/> window
/// shows whenever it is open, tools available or not: the menus of the game
/// are screens.
/// </para>
/// <para>Use the service on the loop thread of the client context.</para>
/// </remarks>
public interface IDebugUi
{
    /// <summary>
    /// Gets whether the debug tools are available: the key <c>debug.tools</c>
    /// is <c>always</c>, or <c>development</c> and the engine runs in
    /// development mode.
    /// </summary>
    bool ToolsEnabled { get; }

    /// <summary>Gets or sets whether the debug tools are open: their menu bar and their open tool windows show.</summary>
    /// <exception cref="InvalidOperationException">The value is set to <see langword="true"/> while the tools are not available.</exception>
    bool IsOpen { get; set; }

    /// <summary>Gets the windows registered, in the order of registration.</summary>
    IReadOnlyList<DebugWindow> Windows { get; }

    /// <summary>Gets whether Dear ImGui used the mouse in the last frame it drew: the cursor is over one of its windows, or drags one.</summary>
    bool WantsMouse { get; }

    /// <summary>Gets whether Dear ImGui used the keyboard in the last frame it drew: one of its widgets is active.</summary>
    bool WantsKeyboard { get; }

    /// <summary>Registers a window, drawn from the next frame on.</summary>
    /// <param name="module">The module of the window, charged with its exceptions and its measures.</param>
    /// <param name="options">The title, the kind, and the placement of the window.</param>
    /// <param name="window">The code that draws the content of the window.</param>
    /// <returns>The handle of the window; disposing it removes the window.</returns>
    DebugWindow AddWindow(ModuleContext module, DebugWindowOptions options, IDebugWindow window);
}

/// <summary>
/// The content of a debug window. nDebug calls it once per frame while the
/// window shows, between the begin and the end of the window, and the module
/// draws with Dear ImGui (<c>ImGuiNET.ImGui</c>) directly.
/// </summary>
/// <remarks>
/// It runs on the loop thread, while nRenderer draws the frame. An exception
/// is charged to the module of the window; nDebug ends the window, and Dear
/// ImGui closes what the content left open at the end of the frame.
/// </remarks>
public interface IDebugWindow
{
    /// <summary>Draws the content of the window.</summary>
    /// <param name="window">The window, to close it or read its title.</param>
    [LoopCallback]
    void Draw(DebugWindow window);
}

/// <summary>What a debug window is for.</summary>
public enum DebugWindowKind
{
    /// <summary>A tool of the developer: it shows while the debug tools are open, and the menu bar of the tools lists it.</summary>
    Tool,

    /// <summary>A screen of the game, such as a menu: it shows whenever it is open, even when the debug tools are not available, and shows the cursor.</summary>
    Screen,
}

/// <summary>Where a debug window appears.</summary>
public enum DebugWindowPlacement
{
    /// <summary>Where Dear ImGui places a new window; the player can move it.</summary>
    Free,

    /// <summary>At the center of the display when it appears; the player can move it.</summary>
    Center,

    /// <summary>Over the whole display, without title bar or border, behind the other windows.</summary>
    Fullscreen,
}

/// <summary>How a debug window is registered.</summary>
public sealed class DebugWindowOptions
{
    /// <summary>Gets the title, shown in the title bar and in the menu bar of the tools.</summary>
    public required string Title { get; init; }

    /// <summary>Gets what the window is for.</summary>
    public DebugWindowKind Kind { get; init; } = DebugWindowKind.Tool;

    /// <summary>Gets where the window appears.</summary>
    public DebugWindowPlacement Placement { get; init; } = DebugWindowPlacement.Free;

    /// <summary>Gets the size of the window when it first appears, in logical units of the window of the game; zero fits the content.</summary>
    public Vector2 Size { get; init; }

    /// <summary>Gets whether the window is open when it is registered. A closed tool window opens from the menu bar of the tools.</summary>
    public bool Open { get; init; } = true;
}

/// <summary>
/// A debug window registered with <see cref="IDebugUi.AddWindow"/>. Disposing
/// it removes the window.
/// </summary>
public sealed class DebugWindow : IDisposable
{
    private readonly DebugUi _ui;

    internal DebugWindow(DebugUi ui, ModuleContext module, DebugWindowOptions options, IDebugWindow content, int sequence)
    {
        _ui = ui;
        Module = module;
        Title = options.Title;
        Kind = options.Kind;
        Placement = options.Placement;
        Size = options.Size;
        IsOpen = options.Open;
        Content = content;

        // The part after ### identifies the window in Dear ImGui, whatever its title.
        Label = $"{options.Title}###{module.Module.Id}/{sequence}";
        Marker = module.Profiler.CreateMarker("Debug window " + options.Title);
    }

    /// <summary>Gets the title of the window.</summary>
    public string Title { get; }

    /// <summary>Gets what the window is for.</summary>
    public DebugWindowKind Kind { get; }

    /// <summary>Gets where the window appears.</summary>
    public DebugWindowPlacement Placement { get; }

    /// <summary>Gets the module of the window.</summary>
    public ModuleContext Module { get; }

    /// <summary>
    /// Gets or sets whether the window is open. The close button of a tool
    /// window closes it, and the menu bar of the tools opens it again.
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>Gets whether the window was removed.</summary>
    public bool IsRemoved { get; private set; }

    internal Vector2 Size { get; }

    internal IDebugWindow Content { get; }

    internal string Label { get; }

    internal Core.Profiling.ProfilerMarker Marker { get; }

    /// <summary>Removes the window. It is not drawn again, from the frame that runs on.</summary>
    public void Dispose()
    {
        if (!IsRemoved)
        {
            IsRemoved = true;
            _ui.Remove(this);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Title} ({Kind})";
}
