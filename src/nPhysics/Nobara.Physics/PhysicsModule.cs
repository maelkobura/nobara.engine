// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context;
using Nobara.Core;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

[assembly: ModuleEntry(typeof(Nobara.Physics.PhysicsModule))]

namespace Nobara.Physics;

/// <summary>
/// The physics module: the neutral physics API, the registry of backends, and
/// the step of the physics worlds in the stage <c>Physics</c>. It is the only
/// module that writes the positions of simulated entities and grids. It
/// publishes <see cref="IPhysicsSystem"/> and <see cref="IPhysicsBackends"/>.
/// </summary>
public sealed class PhysicsModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.physics";

    /// <summary>The name of the tick system that steps the physics worlds.</summary>
    public const string StepSystem = PhysicsSimulation.StepSystem;

    private PhysicsSimulation? _simulation;
    private IDisposable? _debug;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _simulation = new PhysicsSimulation(context);
        context.Publish<IPhysicsSystem>(_simulation);
        context.Publish<IPhysicsBackends>(_simulation);
    }

    /// <inheritdoc/>
    /// <remarks>Registers the components and the tick system, and takes the writer of grid transforms from nVoxel.</remarks>
    public void Initialize(ModuleContext context)
    {
        _simulation!.Initialize(
            context.Resolve<IEntityWorlds>(),
            context.Resolve<IVoxelWorlds>(),
            context.Resolve<ITickSchedule>(),
            context.Resolve<IWorldSessions>());
        if (context.IsModuleActive(PhysicsDebugWindow.DebugModuleId))
        {
            _debug = AddDebugWindow(context, _simulation);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Chooses the backend from <c>physics.backend</c>; without any backend, the start fails.</remarks>
    public void Start(ModuleContext context) => _simulation!.Start();

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _debug?.Dispose();
        _simulation?.Dispose();
    }

    /// <inheritdoc/>
    public void OnDependencyDisabled(ModuleContext context, ModuleInfo dependency)
    {
        if (dependency.Id == PhysicsDebugWindow.DebugModuleId)
        {
            _debug?.Dispose();
            _debug = null;
        }
    }

    // A method of its own, so that the types of nDebug load only when it runs.
    private static IDisposable? AddDebugWindow(ModuleContext context, PhysicsSimulation simulation) => PhysicsDebugWindow.Add(context, simulation);
}
