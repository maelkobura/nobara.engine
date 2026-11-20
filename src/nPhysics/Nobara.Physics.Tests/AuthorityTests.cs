// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Game;
using Nobara.Physics.Tests.Support;
using Nobara.Voxel;

namespace Nobara.Physics.Tests;

/// <summary>nPhysics as the only writer of positions, grids included, and its precision far from the origin.</summary>
public sealed class AuthorityTests : IDisposable
{
    private const float Delta = 1f / 30;
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void RefusesTheWriterOfGridTransformsToAnotherModule()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => probe.Voxels.ClaimGridTransforms(probe.Context));

        Assert.Contains($"Module '{PhysicsHarness.GameId}' cannot move grids", error.Message);
        Assert.Contains(GridTransformWriter.Reservation, error.Message);
    }

    [Fact]
    public void FailsToStartWhenAnotherModuleHoldsTheGridTransforms()
    {
        _harness.GameReservesGridTransforms = true;

        EngineStopException stop = Assert.Throws<EngineStopException>(() => _harness.Start());

        Assert.Contains($"Module '{PhysicsModule.ModuleId}' cannot move grids", stop.ToString());
    }

    [Fact]
    public void MovesAGridThatIsABodyAndUpdatesTheSpatialIndex()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        Grid ship = probe.Voxels.CreateGrid(probe.Context, DimensionId.Default, new GridOptions
        {
            Bounds = new GridBounds(new BlockCoordinate(0, 0, 0), new BlockCoordinate(3, 3, 3)),
            Transform = WorldTransform.At(WorldPosition.FromBlocks(0, 20, 0)),
        });
        BodyHandle body = probe.Physics.AddBody(DimensionId.Default, ship.Entity, new BodyDescription
        {
            Kind = BodyKind.Kinematic,
            Shape = probe.Physics.CreateBox(new Vector3(4)),
            Center = new Vector3(2),
        });
        Assert.Single(probe.Voxels.SpatialIndex.Query(WorldPosition.FromBlocks(2, 22, 2), Vector3.One), g => g == ship);

        probe.Physics.SetVelocity(body, new Vector3(300, 0, 0), Vector3.Zero);
        _harness.Tick(engine);

        AssertNear(WorldPosition.FromBlocks(10, 20, 0), ship.Transform.Position);
        Assert.True(Vector3.Distance(new Vector3(12, 22, 2), Assert.Single(_harness.Backend.Worlds).Body(0).Position) < 1e-4f);
        Assert.DoesNotContain(probe.Voxels.SpatialIndex.Query(WorldPosition.FromBlocks(2, 22, 2), Vector3.One), g => g == ship);
        Assert.Contains(probe.Voxels.SpatialIndex.Query(WorldPosition.FromBlocks(12, 22, 2), Vector3.One), g => g == ship);
    }

    [Theory]
    [InlineData(1_000_000_000.25, 64.5, -1_000_000_000.5)]
    [InlineData(-1_000_000_000.75, -300.25, 1_000_000_000.125)]
    public void KeepsBodiesExactFarFromTheOrigin(double x, double y, double z)
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        WorldPosition start = WorldPosition.FromBlocks(x, y, z);
        probe.Activate(start, new ChunkArea(1, 1));
        BodyHandle body = probe.CreateBody(start, BodyKind.Kinematic);
        BodyHandle neighbor = probe.CreateBody(start.Offset(new Vector3(5, 0, 0)), BodyKind.Kinematic);

        probe.Physics.SetVelocity(body, new Vector3(0.75f, 0, -1.5f), Vector3.Zero);
        probe.Physics.Teleport(neighbor, start.Offset(new Vector3(-7.25f, 1, 0)), Quaternion.Identity);
        _harness.Tick(engine, 3);

        TestPhysicsWorld world = Assert.Single(_harness.Backend.Worlds);
        Assert.True(world.LargestCoordinate < 64, $"The backend received {world.LargestCoordinate}.");
        Assert.Equal(PhysicsFrame.OriginAt(start), world.Options.Origin);
        AssertNear(start.Offset(new Vector3(0.75f * 3 * Delta, 0, -1.5f * 3 * Delta)), probe.TransformOf(body).Position);
        AssertNear(start.Offset(new Vector3(-7.25f, 1, 0)), probe.TransformOf(neighbor).Position);
    }

    [Fact]
    public void KeepsAGridExactFarFromTheOrigin()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        WorldPosition start = WorldPosition.FromBlocks(-1_000_000_000.5, 80, 1_000_000_000.25);
        probe.Activate(start, new ChunkArea(1, 1));
        Grid ship = probe.Voxels.CreateGrid(probe.Context, DimensionId.Default, new GridOptions
        {
            Bounds = new GridBounds(new BlockCoordinate(0, 0, 0), new BlockCoordinate(1, 1, 1)),
            Transform = WorldTransform.At(start),
        });
        BodyHandle body = probe.Physics.AddBody(DimensionId.Default, ship.Entity, new BodyDescription { Kind = BodyKind.Kinematic, Shape = probe.Physics.CreateBox(new Vector3(2)), Center = Vector3.One });

        probe.Physics.SetKinematicTarget(body, start.Offset(new Vector3(0.125f, 0.5f, 0)), Quaternion.Identity);
        _harness.Tick(engine);

        Assert.True(Assert.Single(_harness.Backend.Worlds).LargestCoordinate < 64);
        AssertNear(start.Offset(new Vector3(0.125f, 0.5f, 0)), ship.Transform.Position);
    }

    [Fact]
    public void ConvertsPositionsExactlyAroundAFarOrigin()
    {
        var origin = new ChunkCoordinate(31_250_000, -2, -31_250_000);
        WorldPosition position = WorldPosition.FromBlocks(1_000_000_010.5, -60.25, -999_999_990.75);

        Vector3 local = PhysicsFrame.ToLocal(origin, position);

        Assert.Equal(new Vector3(10.5f, 3.75f, 9.25f), local);
        Assert.Equal(position, PhysicsFrame.ToWorld(origin, local));
        Assert.Equal(new ChunkCoordinate(31_250_000, -2, -31_250_000), PhysicsFrame.OriginAt(position));
    }

    private static void AssertNear(in WorldPosition expected, in WorldPosition actual)
    {
        Vector3 offset = actual.RelativeTo(expected);
        Assert.True(offset.Length() < 1e-4f, $"Expected {expected}, got {actual}: {offset} apart.");
    }
}
