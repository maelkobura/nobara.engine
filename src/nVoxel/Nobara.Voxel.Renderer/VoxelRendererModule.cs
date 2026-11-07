// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Renderer;

[assembly: ModuleEntry(typeof(Nobara.Voxel.Renderer.VoxelRendererModule))]

namespace Nobara.Voxel.Renderer;

/// <summary>
/// The client extension of nVoxel that meshes chunks: it turns the blocks of
/// every grid into the chunk geometry of nRenderer.
/// </summary>
public sealed class VoxelRendererModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.voxel.renderer";

    private ChunkRemesher? _remesher;
    private VisualProperties? _properties;
    private RenderRegistration? _extractor;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _properties = new VisualProperties(context);
        _remesher = new ChunkRemesher(context);
        context.Publish<IVoxelMeshing>(_remesher);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _remesher!.Initialize(
            context.Resolve<IBlockRegistry>(),
            context.Resolve<IVoxelWorlds>(),
            context.Resolve<IChunkLoading>(),
            context.Resolve<IVoxelBatches>(),
            context.Resolve<IWorldSessions>(),
            context.Resolve<IShapeRegistry>());
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        IRenderer renderer = context.Resolve<IRenderer>();
        BlockVisuals visuals = BlockVisualBuilder.Build(
            context,
            context.Resolve<IBlockRegistry>(),
            context.Resolve<IShapeRegistry>(),
            context.Resolve<IMultiBlockRegistry>(),
            context.Resolve<IRenderResources>(),
            _properties!,
            _remesher!.Meshers);
        renderer.Chunks.SetTints(visuals.Tints);
        _remesher.Start(visuals);
        _extractor = renderer.AddExtractor(context, _remesher);
    }

    /// <inheritdoc/>
    public void Stop(ModuleContext context)
    {
        _extractor?.Dispose();
        _extractor = null;
    }
}
