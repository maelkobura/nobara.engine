// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Debug;
using Nobara.Graphics;
using Nobara.Renderer;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The only place where nPhysics uses nDebug, nRenderer, and nGraphics, its
/// optional dependencies: the "Physics" tool window, which shows the backend,
/// the step, the queries and the contact events, the bodies by kind, awake,
/// asleep, and frozen, and the regions with their worlds; in development
/// mode, the substeps and the gravity to adjust; and, when nRenderer is
/// present, the drawing of the shapes, the contacts, and the regions in the
/// world.
/// </summary>
[UsesOptionalDependency(DebugModuleId)]
[UsesOptionalDependency(RendererModuleId)]
[UsesOptionalDependency(GraphicsModuleId)]
internal sealed class PhysicsDebugWindow : IDisposable
{
    /// <summary>The identifier of nDebug.</summary>
    public const string DebugModuleId = "nobara.debug";

    /// <summary>The identifier of nRenderer, whose debug drawing the window uses.</summary>
    public const string RendererModuleId = "nobara.renderer";

    /// <summary>The identifier of nGraphics, whose colors the drawing uses.</summary>
    public const string GraphicsModuleId = "nobara.graphics";

    /// <summary>The most bodies the window draws.</summary>
    private const int DrawnBodies = 2000;

    private static readonly ColorF StaticColor = new(0.85f, 0.85f, 0.85f, 1);
    private static readonly ColorF KinematicColor = new(0.3f, 0.6f, 1, 1);
    private static readonly ColorF AwakeColor = new(0.3f, 1, 0.4f, 1);
    private static readonly ColorF AsleepColor = new(0.45f, 0.55f, 0.45f, 1);
    private static readonly ColorF FrozenColor = new(1, 0.3f, 0.3f, 1);
    private static readonly ColorF CharacterColor = new(1, 0.85f, 0.2f, 1);
    private static readonly ColorF ContactColor = new(1, 0.5f, 0.1f, 1);
    private static readonly ColorF RegionColor = new(0.8f, 0.4f, 1, 0.6f);

    private readonly PhysicsSimulation _simulation;
    private readonly bool _development;
    private IDebugDraw? _draw;
    private DebugWindow? _window;
    private bool _drawBodies;
    private bool _drawContacts;
    private bool _drawRegions;

    private PhysicsDebugWindow(PhysicsSimulation simulation, IDebugDraw? draw, bool development) => (_simulation, _draw, _development) = (simulation, draw, development);

    /// <summary>Registers the window. Call it only when nDebug is active.</summary>
    /// <param name="context">The context of nPhysics.</param>
    /// <param name="simulation">The physics.</param>
    /// <param name="hasRenderer">Whether nRenderer is active, for the drawing in the world.</param>
    /// <returns>The window, which the module disposes when it is released; <see langword="null"/> without the service of nDebug.</returns>
    public static PhysicsDebugWindow? Add(ModuleContext context, PhysicsSimulation simulation, bool hasRenderer)
    {
        if (!context.TryResolve(out IDebugUi? debug))
        {
            return null;
        }

        IDebugDraw? draw = hasRenderer && context.TryResolve(out IDebugDraw? found) && found.IsEnabled ? found : null;
        var window = new PhysicsDebugWindow(simulation, draw, context.DevelopmentMode);
        window._window = debug.AddWindow(context, new DebugWindowOptions { Title = "Physics", Open = false }, new Content(window));
        return window;
    }

    /// <summary>Stops the drawing in the world, once nRenderer is disabled.</summary>
    public void DropDrawing() => _draw = null;

    /// <summary>Removes the window.</summary>
    public void Dispose() => _window?.Dispose();

    private static void Text(FormattableString text) => ImGui.TextUnformatted(text.ToString(CultureInfo.InvariantCulture));

    private void Draw()
    {
        PhysicsStatistics statistics = _simulation.Statistics;
        Text($"Backend: {(_simulation.BackendName.Length > 0 ? _simulation.BackendName : "none")}");
        Text($"Step: {statistics.LastStep.TotalMilliseconds:0.000} ms, {_simulation.Substeps} substeps, {_simulation.VelocityIterations} velocity iterations");
        Text($"Steps: {statistics.Steps}; queries in the last tick: {statistics.Queries}; contact events of the last step: {statistics.ContactEvents}");
        if (_development && ImGui.CollapsingHeader("Settings"))
        {
            DrawSettings();
        }

        if (ImGui.CollapsingHeader("Bodies", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Text($"Static: {statistics.StaticBodies}");
            Text($"Kinematic: {statistics.KinematicBodies}");
            Text($"Dynamic: {statistics.DynamicBodies}");
            Text($"Awake: {statistics.AwakeBodies}, asleep: {statistics.SleepingBodies}, frozen: {statistics.FrozenBodies}");
            Text($"Joints: {statistics.Joints}, shapes: {statistics.Shapes}, characters: {statistics.Characters}");
        }

        if (ImGui.CollapsingHeader("Regions", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Text($"Regions: {statistics.Regions}, worlds: {statistics.Worlds}, ghosts: {statistics.Ghosts}");
            Text($"Transfers: {statistics.Transfers}, merges: {statistics.Merges}, splits: {statistics.Splits}, recenters: {statistics.Recenters}");
            Text($"Chunks of voxels: {statistics.VoxelChunks}, wakes by changed blocks: {statistics.VoxelWakes}");
            foreach (PhysicsRegionInfo region in _simulation.Regions)
            {
                string world = region.HasWorld ? string.Create(CultureInfo.InvariantCulture, $"{region.Bodies} bodies, {region.Ghosts} ghosts, {region.Joints} joints") : "no world";
                Text($"#{region.Id} {region.Dimension}: chunks {region.Minimum} to {region.Maximum}, origin {region.Origin}, {world}");
            }
        }

        if (_draw is not null)
        {
            ImGui.Checkbox("Draw the shapes", ref _drawBodies);
            ImGui.Checkbox("Draw the contacts of the bodies that report them", ref _drawContacts);
            ImGui.Checkbox("Draw the regions", ref _drawRegions);
            DrawWorld(_draw);
        }
    }

    /// <summary>The settings of development: they change the simulation of this context until it stops, and are not saved.</summary>
    private void DrawSettings()
    {
        int substeps = _simulation.Substeps;
        if (ImGui.SliderInt("Substeps", ref substeps, 1, 32))
        {
            _simulation.Substeps = substeps;
        }

        float gravity = -_simulation.Gravity.Y;
        if (ImGui.SliderFloat("Gravity (blocks/s², down)", ref gravity, 0, 40))
        {
            _simulation.SetGravity(new Vector3(0, -gravity, 0));
        }

        if (ImGui.Button("Reset the gravity"))
        {
            _simulation.SetGravity(PhysicsSimulation.DefaultGravity);
        }
    }

    private void DrawWorld(IDebugDraw draw)
    {
        if (_drawBodies)
        {
            int drawn = 0;
            foreach (PhysicsSimulation.DrawnBody body in _simulation.DrawnBodies())
            {
                if (drawn++ == DrawnBodies)
                {
                    break;
                }

                DrawShape(draw, body.Shape, body.Center, body.Rotation, ColorOf(body));
            }

            foreach ((WorldPosition center, Vector3 half) in _simulation.DrawnCharacters())
            {
                draw.Box(center, half, CharacterColor);
            }
        }

        if (_drawContacts)
        {
            foreach (PhysicsSimulation.ContactRecord contact in _simulation.Contacts)
            {
                draw.Sphere(contact.Point, 0.05f, ContactColor);
                draw.Line(contact.Point, contact.Point.Offset(contact.Normal * 0.5f), ContactColor);
            }
        }

        if (_drawRegions)
        {
            foreach (PhysicsRegionInfo region in _simulation.Regions)
            {
                const float size = VoxelCoordinates.ChunkSize;
                var corner = new WorldPosition(region.Minimum.X, region.Minimum.Y, region.Minimum.Z, Vector3.Zero);
                Vector3 half = new Vector3(region.Maximum.X - region.Minimum.X + 1, region.Maximum.Y - region.Minimum.Y + 1, region.Maximum.Z - region.Minimum.Z + 1) * (size / 2);
                draw.Box(corner.Offset(half), half, RegionColor);
            }
        }
    }

    private static ColorF ColorOf(in PhysicsSimulation.DrawnBody body) => body switch
    {
        { Frozen: true } => FrozenColor,
        { Kind: BodyKind.Static } => StaticColor,
        { Kind: BodyKind.Kinematic } => KinematicColor,
        { Asleep: true } => AsleepColor,
        _ => AwakeColor,
    };

    /// <summary>Draws a shape: a sphere, a box, a capsule as two spheres and their lines, the others by the box that holds them, and a compound child by child.</summary>
    private void DrawShape(IDebugDraw draw, ShapeDescription shape, in WorldPosition center, Quaternion rotation, ColorF color)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Sphere:
                draw.Sphere(center, shape.Radius, color);
                break;
            case ShapeKind.Box:
                draw.Box(center, shape.Size / 2, rotation, color);
                break;
            case ShapeKind.Capsule:
                Vector3 axis = Vector3.Transform(new Vector3(0, shape.Length / 2, 0), rotation);
                draw.Sphere(center.Offset(axis), shape.Radius, color);
                draw.Sphere(center.Offset(-axis), shape.Radius, color);
                for (int i = 0; i < 4; i++)
                {
                    Vector3 side = Vector3.Transform(new Vector3(MathF.Cos(i * MathF.PI / 2), 0, MathF.Sin(i * MathF.PI / 2)) * shape.Radius, rotation);
                    draw.Line(center.Offset(side + axis), center.Offset(side - axis), color);
                }

                break;
            case ShapeKind.Compound:
                foreach (CompoundChild child in shape.Children.Span)
                {
                    DrawShape(draw, _simulation.GetShape(child.Shape), center.Offset(Vector3.Transform(child.Position, rotation)), rotation * child.Rotation, color);
                }

                break;
            default:
                PhysicsSimulation.BoundsOf(shape, out Vector3 minimum, out Vector3 maximum);
                draw.Box(center.Offset(Vector3.Transform((minimum + maximum) / 2, rotation)), (maximum - minimum) / 2, rotation, color);
                break;
        }
    }

    private sealed class Content(PhysicsDebugWindow owner) : IDebugWindow
    {
        public void Draw(DebugWindow window) => owner.Draw();
    }
}
