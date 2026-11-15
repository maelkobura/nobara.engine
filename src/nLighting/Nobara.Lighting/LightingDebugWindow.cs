// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using ImGuiNET;
using Nobara.Core;
using Nobara.Debug;
using Nobara.Voxel;

namespace Nobara.Lighting;

/// <summary>
/// The only place where nLighting uses nDebug, its optional dependency: the
/// "Lighting" tool window, which shows the light of the block that the first
/// focus with a direction aims at, the work left to the propagation, and the
/// memory of the light.
/// </summary>
[UsesOptionalDependency(DebugModuleId)]
internal sealed class LightingDebugWindow : IDisposable
{
    /// <summary>The identifier of nDebug.</summary>
    public const string DebugModuleId = "nobara.debug";

    private const float Reach = 64;

    private readonly Lighting _lighting;
    private readonly IVoxelQueries _queries;
    private DebugWindow? _window;

    private LightingDebugWindow(Lighting lighting, IVoxelQueries queries) => (_lighting, _queries) = (lighting, queries);

    /// <summary>Registers the window. Call it only when nDebug is active.</summary>
    /// <returns>The window, which the module disposes when it stops; <see langword="null"/> without the service of nDebug.</returns>
    public static LightingDebugWindow? Add(ModuleContext context, Lighting lighting)
    {
        if (!context.TryResolve(out IDebugUi? debug)) return null;
        var window = new LightingDebugWindow(lighting, context.Resolve<IVoxelQueries>());
        window._window = debug.AddWindow(context, new DebugWindowOptions { Title = "Lighting", Open = false }, new Content(window));
        return window;
    }

    /// <summary>Removes the window.</summary>
    public void Dispose() => _window?.Dispose();

    private void Draw()
    {
        DrawAimed();
        ImGui.Separator();

        LightingBacklog backlog = _lighting.Backlog;
        LightingStatistics statistics = _lighting.Statistics;
        Text($"Budget: {backlog.LastTickOperations:N0} of {backlog.Budget:N0} operations in the last tick, {backlog.LastTickDuration.TotalMilliseconds:F2} ms");
        Text($"Work left: {backlog.Chunks:N0} chunks, {backlog.Cells:N0} cells, oldest {backlog.OldestTicks:N0} ticks");
        Text($"Last pass: {statistics.LastPassChunks:N0} chunks, {statistics.LastPassRounds:N0} rounds, {statistics.LastPassDuration.TotalMilliseconds:F2} ms");
        ImGui.Separator();
        Text($"Chunks: {statistics.Chunks:N0}, {statistics.Chunks - statistics.ArrayChunks:N0} uniform, {statistics.ArrayChunks:N0} arrays");
        Text($"Memory of the arrays: {statistics.StorageBytes / 1024.0:N0} KiB ({(_lighting.IsColored ? "color" : "no color")})");
    }

    private void DrawAimed()
    {
        LightFocus? focus = null;
        foreach (LightFocus candidate in _lighting.Focuses)
        {
            if (!candidate.IsRevoked && candidate.Direction != System.Numerics.Vector3.Zero)
            {
                focus = candidate;
                break;
            }
        }

        if (focus is null)
        {
            ImGui.TextDisabled("No focus with a direction: no aimed block.");
            return;
        }

        if (!_queries.Raycast(focus.Dimension, focus.Position, System.Numerics.Vector3.Normalize(focus.Direction), Reach, out VoxelRayHit hit) || hit.Face is not { } face)
        {
            ImGui.TextDisabled("No block aimed at.");
            return;
        }

        // The light that lights the aimed face is that of the cell in front of it.
        BlockCoordinate cell = Step(hit.Position, face);
        Text($"Aimed: {hit.Position.X} {hit.Position.Y} {hit.Position.Z}, face {face}");
        if (_lighting.TryGetBrightness(hit.Grid, cell, out Brightness brightness))
        {
            Text($"In front: sky {brightness.Sky}, block {brightness.Block}, dynamic {brightness.Dynamic}");
        }
        else
        {
            ImGui.TextDisabled("In front: no light (chunk not loaded).");
        }
    }

    private static BlockCoordinate Step(BlockCoordinate block, BlockFace face) => face switch
    {
        BlockFace.North => new BlockCoordinate(block.X, block.Y, block.Z - 1),
        BlockFace.East => new BlockCoordinate(block.X + 1, block.Y, block.Z),
        BlockFace.South => new BlockCoordinate(block.X, block.Y, block.Z + 1),
        BlockFace.West => new BlockCoordinate(block.X - 1, block.Y, block.Z),
        BlockFace.Up => new BlockCoordinate(block.X, block.Y + 1, block.Z),
        _ => new BlockCoordinate(block.X, block.Y - 1, block.Z),
    };

    private static void Text(FormattableString text) => ImGui.TextUnformatted(text.ToString(CultureInfo.InvariantCulture));

    // Only the isolation class creates it, so the types of nDebug load only when nDebug is active.
    private sealed class Content(LightingDebugWindow owner) : IDebugWindow
    {
        public void Draw(DebugWindow window) => owner.Draw();
    }
}
