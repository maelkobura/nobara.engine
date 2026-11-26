// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core.Mathematics;
using Nobara.Motion.Tests.Support;
using Nobara.Physics;
using Nobara.Voxel;

namespace Nobara.Motion.Tests;

/// <summary>The mass, the center of mass, and the inertia of grids, from their blocks.</summary>
public sealed class MassTests
{
    [Fact]
    public void TheMassAndTheCenterOfAKnownGridComeFromItsBlocks()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 10, 16), scene.State("stone"), MotionScene.Box(0, 0, 0, 2, 0, 1));   // six blocks of stone
        scene.Motion.Add(grid, GridMotionMode.Kinematic);

        GridMotionState state = scene.Motion.GetState(grid);

        Assert.Equal(6 * 2500f, state.Mass);
        Assert.Equal(new Vector3(1.5f, 0.5f, 1f), state.CenterOfMass);
        Assert.Equal(6, state.Blocks);
        Assert.True(state.IsSmall);   // 6 blocks, under the threshold of 8
    }

    [Fact]
    public void AWeightGivenForMotionWinsAndAMissingOneIsTheDefault()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 10, 16), scene.State("lead"), new BlockCoordinate(0, 0, 0));
        scene.Grid(scene.At(20, 10, 16), scene.State("wood"), new BlockCoordinate(0, 0, 0));
        Grid wood = scene.Grid(scene.At(24, 10, 16), scene.State("wood"), new BlockCoordinate(0, 0, 0));
        scene.Motion.Add(grid, GridMotionMode.Kinematic);
        scene.Motion.Add(wood, GridMotionMode.Kinematic);

        Assert.Equal(5000f, scene.Motion.GetState(grid).Mass);
        Assert.Equal(BlockMasses.DefaultWeight, scene.Motion.GetState(wood).Mass);
    }

    [Fact]
    public void AMultiBlockCountsOnceAtTheMiddleOfItsCells()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 10, 16), scene.State("stone"), new BlockCoordinate(0, 0, 0));
        scene.Motion.Add(grid, GridMotionMode.Kinematic);

        scene.Place(grid, scene.State("bench"), new BlockCoordinate(1, 0, 0));   // cells (1, 0, 0) and (2, 0, 0)
        scene.Tick();

        GridMotionState state = scene.Motion.GetState(grid);
        Assert.Equal(3, state.Blocks);
        Assert.Equal(2500f + 300f, state.Mass);
        float x = ((2500f * 0.5f) + (300f * 2f)) / 2800f;   // the bench weighs at x = 2, between its two cells
        Assert.Equal(x, state.CenterOfMass.X, 4);
    }

    [Fact]
    public void AShapeThatDependsOnItsInstanceGivesTheMassOfEachCell()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 10, 16), scene.State("stone"), new BlockCoordinate(5, 0, 0));
        scene.Motion.Add(grid, GridMotionMode.Kinematic);
        scene.Carved.Height = 0.5f;
        scene.Place(grid, scene.State("carved"), new BlockCoordinate(0, 0, 0));
        scene.Tick();
        scene.Carved.Height = 0.25f;
        scene.Place(grid, scene.State("carved"), new BlockCoordinate(1, 0, 0));
        scene.Tick();

        GridMotionState state = scene.Motion.GetState(grid);
        Assert.Equal(2500f + 400f + 200f, state.Mass, 3);
        float y = ((2500f * 0.5f) + (400f * 0.25f) + (200f * 0.125f)) / 3100f;   // each at the middle of its box
        Assert.Equal(y, state.CenterOfMass.Y, 4);

        scene.Break(grid, new BlockCoordinate(0, 0, 0));
        scene.Tick();
        Assert.Equal(2500f + 200f, scene.Motion.GetState(grid).Mass, 3);   // the cell takes away the mass it gave, not the one its shape would give now
    }

    [Fact]
    public void TheIncrementalMassEqualsTheMassCountedAgainAfter10000Changes()
    {
        using var scene = new MotionScene();
        Grid grid = scene.Grid(scene.At(16, 10, 16), scene.State("stone"), MotionScene.Box(0, 0, 0, 3, 0, 3));
        scene.Motion.Add(grid, GridMotionMode.Kinematic);
        var system = (MotionSystem)scene.Motion;
        var random = new Random(1234);
        uint[] states = [scene.State("stone"), scene.State("wood"), scene.State("lead"), scene.State("carved"), scene.State("grass"), scene.State("bench")];
        int changes = 0;
        while (changes < 10_000)
        {
            var places = new HashSet<BlockCoordinate>();
            var breaks = new HashSet<BlockCoordinate>();
            for (int i = 0; i < 100; i++)
            {
                var cell = new BlockCoordinate(random.Next(-20, 20), random.Next(-4, 4), random.Next(-20, 20));
                if (grid.GetBlock(cell) != 0)
                {
                    breaks.Add(cell);
                }
                else if (!breaks.Contains(cell) && !places.Contains(cell) && !places.Contains(new BlockCoordinate(cell.X - 1, cell.Y, cell.Z)) && grid.GetBlock(new BlockCoordinate(cell.X + 1, cell.Y, cell.Z)) == 0 && !places.Contains(new BlockCoordinate(cell.X + 1, cell.Y, cell.Z)))
                {
                    places.Add(cell);
                }
            }

            foreach (BlockCoordinate cell in places)
            {
                scene.Place(grid, states[random.Next(states.Length)], cell);   // the carved block keeps one height: counting again evaluates its shape now
            }

            if (breaks.Count > 0)
            {
                scene.Break(grid, [.. breaks]);
            }

            scene.Tick();
            changes += places.Count + breaks.Count;
        }

        Assert.True(system.SumsOf(grid).Blocks > 50);
        GridMass sums = system.SumsOf(grid);
        GridMass counted = system.Recount(grid);
        Assert.True(sums.Matches(counted, 1e-9), $"The sums drifted from the blocks: {sums.Blocks} blocks and {sums.Mass} kg, against {counted.Blocks} and {counted.Mass}.");
    }

    [Fact]
    public void TheInertiaIsTheOneOfTheBlocksAboutTheCenterOfMass()
    {
        var sums = new GridMass();
        var block = new BlockMass(12, new Vector3(0.5f));
        sums.Add(block, new Vector3(-1, 0, 0), 1, true);
        sums.Add(block, new Vector3(0, 0, 0), 1, true);

        sums.Compute(1, out float mass, out Vector3 center, out BodyInertia inertia);

        Assert.Equal(24, mass);
        Assert.Equal(new Vector3(0, 0.5f, 0.5f), center);
        Assert.Equal(2 * 12 / 6f, inertia.XX, 4);                  // two cubes about their own centers
        Assert.Equal((2 * 12 / 6f) + (2 * 12 * 0.25f), inertia.YY, 4);   // plus the masses half a block off the axis
        Assert.Equal(0, inertia.YX, 4);
    }
}
