// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context;
using Nobara.Core;
using Nobara.Entity;
using Nobara.Game;

[assembly: ModuleEntry(typeof(Nobara.Voxel.VoxelModule))]

namespace Nobara.Voxel;

/// <summary>The voxel storage module: dimensions, grids, chunks, palettes, and block metadata.</summary>
public sealed class VoxelModule : IModule
{
    private VoxelWorlds? _worlds;
    private ShapeRegistry? _shapes;
    private MultiBlockRegistry? _multiBlocks;
    private VoxelBatches? _batches;
    private ChunkLoader? _loader;
    private HeightMaps? _heightMaps;
    private BlockTicks? _ticks;
    private VoxelQueries? _queries;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _worlds = new VoxelWorlds(context);
        _shapes = new ShapeRegistry();
        _shapes.RegisterNative(new CubeShape("shape:nobara.cube"));
        _shapes.RegisterNative(new CubeShape("shape:nobara.model"));
        context.Publish<IVoxelWorlds>(_worlds);
        context.Publish<IShapeRegistry>(_shapes);
        _multiBlocks = new MultiBlockRegistry();
        context.Publish<IMultiBlockRegistry>(_multiBlocks);
        _batches = new VoxelBatches(context, _worlds);
        context.Publish<IVoxelBatches>(_batches);
        _loader = new ChunkLoader(context, _worlds);
        _worlds.AttachLoader(_loader);
        context.Publish<IChunkLoading>(_loader);
        _heightMaps = new HeightMaps();
        context.Publish<IHeightMaps>(_heightMaps);
        _ticks = new BlockTicks(_loader);
        context.Publish<IBlockTicks>(_ticks);
        _queries = new VoxelQueries(_worlds, _loader);
        context.Publish<IVoxelQueries>(_queries);
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _worlds!.Initialize(context.Resolve<IEntityWorlds>(), context.Resolve<IGameTime>(), context.Resolve<IWorldSessions>());
        _batches!.Initialize(
            context.Resolve<Nobara.Blocks.IBlockRegistry>(),
            context.Resolve<Nobara.Blocks.IBlockHooks>(),
            _multiBlocks!,
            context.Resolve<ITickSchedule>(),
            context.Resolve<Nobara.Items.IItemUsage>());
        _worlds.AttachBatches(_batches);
        Nobara.Blocks.IBlockRegistry blocks = context.Resolve<Nobara.Blocks.IBlockRegistry>();
        ITickSchedule schedule = context.Resolve<ITickSchedule>();
        IGameTime time = context.Resolve<IGameTime>();
        _loader!.Initialize(_batches, _heightMaps!, _multiBlocks!, blocks, time, context.Resolve<IWorldSessions>(), schedule);
        _batches.AttachLoading(_loader, _loader, _heightMaps!);
        _ticks!.Initialize(context, _worlds, context.Resolve<Nobara.Blocks.IBlockHooks>(), time, schedule);
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        Nobara.Blocks.IBlockRegistry blocks = context.Resolve<Nobara.Blocks.IBlockRegistry>();
        _shapes!.Freeze(blocks);
        _multiBlocks!.Initialize(blocks);
        _heightMaps!.Start(_shapes, blocks);
        _queries!.Start(_shapes, _multiBlocks);
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context)
    {
        _batches?.Dispose();
        _worlds?.Dispose();
    }
}
