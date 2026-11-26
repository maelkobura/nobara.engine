// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Tests;

/// <summary>The shapes of the voxels of a grid, and the cells that nPhysics keeps for them.</summary>
public sealed class GridShapeTests : IDisposable
{
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TheVoxelsOfAGridAreAShapeThatReadsItsChunks()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = Grid(probe, new BlockCoordinate(0, 0, 0), new BlockCoordinate(2, 1, 3));

        PhysicsShape shape = probe.Physics.CreateVoxels(grid, new Vector3(1, 0.5f, 0.5f));

        ShapeDescription description = probe.Physics.GetShape(shape);
        Assert.Equal(ShapeKind.Voxels, description.Kind);
        Assert.False(description.IsConvex);
        Assert.Same(grid, description.Grid);
        Assert.Equal(new Vector3(1, 0.5f, 0.5f), description.Origin);
        VoxelGridCells cells = description.Cells!;
        Assert.Same(grid, cells.Grid);
        Assert.Equal(1, cells.ChunkCount);
        Assert.Equal(Vector3.Zero, cells.Minimum);
        Assert.Equal(new Vector3(3, 2, 4), cells.Maximum);
        int slot = cells.SlotOf(new ChunkCoordinate(0, 0, 0));
        Assert.Equal(new ChunkCoordinate(0, 0, 0), cells.CoordinateOf(slot));
        Assert.Same(grid.Storage.TryGetChunk(new ChunkCoordinate(0, 0, 0), out Chunk? chunk) ? chunk : null, cells.GetChunk(slot)!.Chunk);
        Assert.Equal(-1, cells.SlotOf(new ChunkCoordinate(1, 0, 0)));
        Assert.Same(cells, probe.Physics.GetShape(probe.Physics.CreateVoxels(grid, Vector3.Zero)).Cells);   // one table of cells per grid
    }

    [Fact]
    public void TheVoxelsOfAGridAreTheShapeOfTheBodyOfThatGridOnly()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = Grid(probe, new BlockCoordinate(0, 0, 0), new BlockCoordinate(1, 0, 1));
        Grid other = Grid(probe, new BlockCoordinate(0, 0, 0), new BlockCoordinate(1, 0, 1));
        PhysicsShape voxels = probe.Physics.CreateVoxels(grid, Vector3.Zero);
        EntityId entity = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(0, 5, 0)));

        Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, entity, new BodyDescription { Shape = voxels }));
        Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, other.Entity, new BodyDescription { Shape = voxels }));
        BodyHandle body = probe.Physics.AddBody(DimensionId.Default, other.Entity, new BodyDescription { Kind = BodyKind.Kinematic, Shape = probe.Physics.CreateBox(Vector3.One) });
        Assert.Throws<ArgumentException>(() => probe.Physics.SetShape(body, voxels, Vector3.Zero));
        probe.Physics.SetShape(body, probe.Physics.CreateVoxels(other, Vector3.Zero), Vector3.Zero);
    }

    [Fact]
    public void ABodyOfVoxelsWithoutInertiaGetsTheOneOfTheBoxOfItsCells()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = Grid(probe, new BlockCoordinate(0, 0, 0), new BlockCoordinate(1, 0, 3));   // 2 × 1 × 4 blocks
        PhysicsShape voxels = probe.Physics.CreateVoxels(grid, new Vector3(1, 0.5f, 2));   // at the center of the box

        probe.Physics.AddBody(DimensionId.Default, grid.Entity, new BodyDescription { Shape = voxels, Center = new Vector3(1, 0.5f, 2), Mass = 12 });

        Assert.True(probe.Entities.TryGetComponent(out ComponentType<BodyMass> mass));
        BodyInertia inertia = probe.World.Read(grid.Entity, mass).Inertia;
        Assert.Equal(1 + 16, inertia.XX, 4);   // 12 / 12 × (1² + 4²)
        Assert.Equal(4 + 16, inertia.YY, 4);
        Assert.Equal(4 + 1, inertia.ZZ, 4);
        Assert.Equal(0, inertia.YX, 4);
    }

    [Fact]
    public void TheCellsOfAGridAreForgottenWithItsLastShape()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = Grid(probe, new BlockCoordinate(0, 0, 0), new BlockCoordinate(0, 0, 0));
        PhysicsShape first = probe.Physics.CreateVoxels(grid, Vector3.Zero);
        PhysicsShape second = probe.Physics.CreateVoxels(grid, Vector3.One);
        VoxelGridCells cells = probe.Physics.GetShape(first).Cells!;

        probe.Physics.DestroyShape(first);
        Assert.Equal(1, cells.ChunkCount);
        probe.Physics.DestroyShape(second);

        Assert.Equal(0, cells.ChunkCount);
        Assert.NotSame(cells, probe.Physics.GetShape(probe.Physics.CreateVoxels(grid, Vector3.Zero)).Cells);
    }

    [Fact]
    public void RefusesAGridThatIsDestroyedOrAnOriginThatIsNotFinite()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid grid = Grid(probe, new BlockCoordinate(0, 0, 0), new BlockCoordinate(0, 0, 0));

        Assert.Throws<ArgumentException>(() => probe.Physics.CreateVoxels(grid, new Vector3(float.NaN, 0, 0)));
        probe.Voxels.Destroy(probe.Context, grid);
        Assert.Throws<ArgumentException>(() => probe.Physics.CreateVoxels(grid, Vector3.Zero));
        Assert.Throws<ArgumentNullException>(() => probe.Physics.CreateVoxels(null!, Vector3.Zero));
    }

    /// <summary>Creates a grid whose blocks fill a box of cells, in its first chunk.</summary>
    private static Grid Grid(PhysicsProbe probe, BlockCoordinate minimum, BlockCoordinate maximum)
    {
        Grid grid = probe.Voxels.CreateGrid(probe.Context, DimensionId.Default, new GridOptions { Transform = WorldTransform.At(WorldPosition.FromBlocks(8, 20, 8)) });
        for (int y = minimum.Y; y <= maximum.Y; y++)
        {
            for (int z = minimum.Z; z <= maximum.Z; z++)
            {
                for (int x = minimum.X; x <= maximum.X; x++)
                {
                    var block = new BlockCoordinate(x, y, z);
                    grid.Storage.GetOrCreateChunk(VoxelCoordinates.ToChunk(block)).Palette.Set(VoxelCoordinates.ToIndex(block), 1);
                }
            }
        }

        return grid;
    }
}
