// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using ImGuiNET;
using Nobara.Core;
using Nobara.Debug;

namespace Nobara.Physics;

/// <summary>
/// The only place where nPhysics uses nDebug, its optional dependency: the
/// "Physics" tool window, which shows the backend, the step, the bodies by
/// kind, awake and asleep, and the physics worlds.
/// </summary>
[UsesOptionalDependency(DebugModuleId)]
internal sealed class PhysicsDebugWindow : IDisposable
{
    /// <summary>The identifier of nDebug.</summary>
    public const string DebugModuleId = "nobara.debug";

    private readonly PhysicsSimulation _simulation;
    private DebugWindow? _window;

    private PhysicsDebugWindow(PhysicsSimulation simulation) => _simulation = simulation;

    /// <summary>Registers the window. Call it only when nDebug is active.</summary>
    /// <returns>The window, which the module disposes when it is released; <see langword="null"/> without the service of nDebug.</returns>
    public static PhysicsDebugWindow? Add(ModuleContext context, PhysicsSimulation simulation)
    {
        if (!context.TryResolve(out IDebugUi? debug))
        {
            return null;
        }

        var window = new PhysicsDebugWindow(simulation);
        window._window = debug.AddWindow(context, new DebugWindowOptions { Title = "Physics", Open = false }, new Content(window));
        return window;
    }

    /// <summary>Removes the window.</summary>
    public void Dispose() => _window?.Dispose();

    private void Draw()
    {
        PhysicsStatistics statistics = _simulation.Statistics;
        CultureInfo culture = CultureInfo.InvariantCulture;
        ImGui.TextUnformatted(string.Create(culture, $"Backend: {(_simulation.BackendName.Length > 0 ? _simulation.BackendName : "none")}"));
        ImGui.TextUnformatted(string.Create(culture, $"Step: {statistics.LastStep.TotalMilliseconds:0.000} ms, {_simulation.Substeps} substeps, {_simulation.VelocityIterations} velocity iterations"));
        ImGui.TextUnformatted(string.Create(culture, $"Steps: {statistics.Steps}"));
        if (ImGui.CollapsingHeader("Bodies", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextUnformatted(string.Create(culture, $"Static: {statistics.StaticBodies}"));
            ImGui.TextUnformatted(string.Create(culture, $"Kinematic: {statistics.KinematicBodies}"));
            ImGui.TextUnformatted(string.Create(culture, $"Dynamic: {statistics.DynamicBodies}"));
            ImGui.TextUnformatted(string.Create(culture, $"Awake: {statistics.AwakeBodies}, asleep: {statistics.SleepingBodies}"));
            ImGui.TextUnformatted(string.Create(culture, $"Joints: {statistics.Joints}, shapes: {statistics.Shapes}"));
        }

        if (ImGui.CollapsingHeader("Worlds", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (PhysicsWorldInfo world in _simulation.Worlds)
            {
                ImGui.TextUnformatted(string.Create(culture, $"{world.Dimension}: origin {world.Origin}, {world.Bodies} bodies, {world.Joints} joints"));
            }
        }
    }

    private sealed class Content(PhysicsDebugWindow owner) : IDebugWindow
    {
        public void Draw(DebugWindow window) => owner.Draw();
    }
}
