// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion;

/// <summary>
/// The module that makes grids physical bodies: their mass, center of mass,
/// and inertia from their blocks, their mode, the small grids, and the API
/// that moves them, all simulated by nPhysics. It publishes
/// <see cref="IMotionSystem"/>.
/// </summary>
public sealed class MotionModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.motion";

    /// <summary>The name of the tick system that gives nPhysics the mass and the shape of the grids, before its step.</summary>
    public const string TickSystem = MotionSystem.TickName;

    private MotionSystem? _system;
    private IDisposable? _debug;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _system = new MotionSystem(context);
        context.Publish<IMotionSystem>(_system);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _system!.Initialize(
            context.Resolve<IPhysicsSystem>(),
            context.Resolve<IVoxelWorlds>(),
            context.Resolve<IVoxelBatches>(),
            context.Resolve<IChunkLoading>(),
            context.Resolve<IBlockRegistry>(),
            context.Resolve<IShapeRegistry>(),
            context.Resolve<IMultiBlockRegistry>(),
            context.Resolve<IEntityWorlds>(),
            context.Resolve<ITickSchedule>(),
            context.Resolve<IWorldSessions>());
        if (context.IsModuleActive(MotionDebugWindow.DebugModuleId))
        {
            _debug = AddDebugWindow(context, _system);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Reads the weight of every block state and the threshold of the small grids.</remarks>
    public void Start(ModuleContext context) => _system!.Start();

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _debug?.Dispose();
        _system?.Dispose();
    }

    /// <inheritdoc/>
    public void OnDependencyDisabled(ModuleContext context, ModuleInfo dependency)
    {
        if (dependency.Id == MotionDebugWindow.DebugModuleId)
        {
            _debug?.Dispose();
            _debug = null;
        }
    }

    // A method of its own, so that the types of nDebug load only when it runs.
    private static IDisposable? AddDebugWindow(ModuleContext context, MotionSystem system) => MotionDebugWindow.Add(context, system);
}
