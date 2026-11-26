// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using ImGuiNET;
using Nobara.Core;
using Nobara.Debug;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>
/// The only place where Motion uses nDebug, its optional dependency: the
/// "Motion" tool window, which lists the grids that Motion moves, with their
/// mode, mass, center of mass, blocks, whether they are small grids, and
/// whether they sleep or are frozen.
/// </summary>
[UsesOptionalDependency(DebugModuleId)]
internal sealed class MotionDebugWindow : IDisposable
{
    /// <summary>The identifier of nDebug.</summary>
    public const string DebugModuleId = "nobara.debug";

    /// <summary>The most grids the window lists.</summary>
    private const int ListedGrids = 200;

    private readonly MotionSystem _system;
    private DebugWindow? _window;

    private MotionDebugWindow(MotionSystem system) => _system = system;

    /// <summary>Registers the window. Call it only when nDebug is active.</summary>
    /// <param name="context">The context of Motion.</param>
    /// <param name="system">The grids of Motion.</param>
    /// <returns>The window, which the module disposes when it is released; <see langword="null"/> without the service of nDebug.</returns>
    public static MotionDebugWindow? Add(ModuleContext context, MotionSystem system)
    {
        if (!context.TryResolve(out IDebugUi? debug))
        {
            return null;
        }

        var window = new MotionDebugWindow(system);
        window._window = debug.AddWindow(context, new DebugWindowOptions { Title = "Motion", Open = false }, new Content(window));
        return window;
    }

    /// <summary>Removes the window.</summary>
    public void Dispose() => _window?.Dispose();

    private static void Text(FormattableString text) => ImGui.TextUnformatted(text.ToString(CultureInfo.InvariantCulture));

    private void Draw()
    {
        IReadOnlyCollection<Grid> grids = _system.Grids;
        Text($"Grids: {grids.Count}; small grids up to {_system.SmallGridBlocks} blocks");
        int listed = 0;
        foreach (Grid grid in grids)
        {
            if (listed++ == ListedGrids)
            {
                Text($"... and {grids.Count - ListedGrids} more");
                break;
            }

            GridMotionState state = _system.GetState(grid);
            string status = state.IsFrozen ? "frozen" : state.IsAsleep ? "asleep" : "awake";
            Text($"#{grid.Identifier}: {state.Mode}, {state.Mass:0.#} kg, center {state.CenterOfMass}, {state.Blocks} blocks{(state.IsSmall ? ", small" : string.Empty)}, {status}");
        }
    }

    private sealed class Content(MotionDebugWindow owner) : IDebugWindow
    {
        public void Draw(DebugWindow window) => owner.Draw();
    }
}
