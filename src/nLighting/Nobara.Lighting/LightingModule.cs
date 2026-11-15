// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

[assembly: ModuleEntry(typeof(Nobara.Lighting.LightingModule))]

namespace Nobara.Lighting;

/// <summary>The voxel lighting module: light sources, light storage, the propagation of the sky and block light, and the brightness query.</summary>
public sealed class LightingModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string Id = "nobara.lighting";

    private LightSourceRegistry? _sources;
    private Lighting? _lighting;
    private IDisposable? _debug;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _sources = new LightSourceRegistry(context);
        _lighting = new Lighting(context, _sources);
        context.Publish<ILightSources>(_sources);
        context.Publish<ILighting>(_lighting);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _lighting!.Initialize(
            context.Resolve<IVoxelWorlds>(),
            context.Resolve<IVoxelBatches>(),
            context.Resolve<IChunkLoading>(),
            context.Resolve<IWorldSessions>(),
            context.Resolve<ITickSchedule>(),
            context.Resolve<IHeightMaps>(),
            context.Resolve<IEntityWorlds>());
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        IShapeRegistry shapes = context.Resolve<IShapeRegistry>();
        _sources!.Start(context, context.Resolve<IBlockRegistry>(), shapes, context.Settings.GetBool(LightingSettings.Color));
        _lighting!.Start(shapes, context.Settings.GetInt(LightingSettings.OperationsPerTick));
        if (context.IsModuleActive(LightingDebugWindow.DebugModuleId)) _debug = AddDebugWindow(context, _lighting);
    }

    /// <inheritdoc/>
    public void Stop(ModuleContext context)
    {
        _debug?.Dispose();
        _debug = null;
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context) => _lighting?.Dispose();

    // A method of its own, so that the types of nDebug load only when it runs.
    private static IDisposable? AddDebugWindow(ModuleContext context, Lighting lighting) => LightingDebugWindow.Add(context, lighting);
}
