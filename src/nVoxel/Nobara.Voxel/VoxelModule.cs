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
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _worlds!.Initialize(context.Resolve<IEntityWorlds>(), context.Resolve<IGameTime>(), context.Resolve<IWorldSessions>());
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        Nobara.Blocks.IBlockRegistry blocks = context.Resolve<Nobara.Blocks.IBlockRegistry>();
        _shapes!.Freeze(blocks);
        _multiBlocks!.Initialize(blocks);
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context) => _worlds?.Dispose();
}
