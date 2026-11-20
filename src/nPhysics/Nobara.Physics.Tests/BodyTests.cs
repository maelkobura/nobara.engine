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

/// <summary>The life of a body: its components, its world, its removal, and what is refused.</summary>
public sealed class BodyTests : IDisposable
{
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void MakesAnEntityABodyInTheWorldOfItsDimension()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        PhysicsShape box = probe.Physics.CreateBox(new Vector3(1, 2, 1));
        EntityId entity = probe.CreateEntity(new WorldTransform(WorldPosition.FromBlocks(40, 70, -10), Quaternion.Identity, new Vector3(2)));

        BodyHandle body = probe.Physics.AddBody(DimensionId.Default, entity, new BodyDescription
        {
            Shape = box,
            Mass = 4,
            Material = new PhysicsMaterial(0.2f, 0.5f),
            Filter = new CollisionFilter(2, 6),
            Center = new Vector3(0, 1, 0),
            Continuous = true,
        });

        Assert.True(probe.Physics.IsAlive(body));
        EntityWorld world = probe.World;
        PhysicsBody component = world.Read(entity, ComponentOf<PhysicsBody>("nobara.physics.body"));
        Assert.Equal(body, component.Handle);
        Assert.Equal(BodyKind.Dynamic, component.Kind);
        Assert.Equal(BodyFlags.Continuous | BodyFlags.CanSleep, component.Flags);
        Assert.Equal(new CollisionFilter(2, 6), component.Filter);
        Assert.Equal(box, world.Read(entity, ComponentOf<BodyShape>("nobara.physics.shape")).Shape);
        Assert.Equal(new PhysicsMaterial(0.2f, 0.5f), world.Read(entity, ComponentOf<BodyMaterial>("nobara.physics.material")).Material);
        Assert.Equal(4, world.Read(entity, ComponentOf<BodyMass>("nobara.physics.mass")).Mass);
        Assert.True(probe.Physics.TryGetBody(DimensionId.Default, entity, out BodyHandle found));
        Assert.Equal(body, found);

        TestPhysicsWorld physics = Assert.Single(_harness.Backend.Worlds);
        Assert.Equal(new ChunkCoordinate(0, 0, 0), physics.Options.Origin);   // the center of the region of the active ticket
        Assert.Equal(new Vector3(0, -9.81f, 0), physics.Options.Gravity);
        TestBody added = Assert.Single(physics.Bodies);
        Assert.Equal(new Vector3(40, 71, -10), added.Position);   // the center, one block above the entity, from the corner of chunk (0, 0, 0)
        Assert.Equal(BodyInertia.Diagonal(new Vector3(4)), added.Description.Inertia);
        Assert.Equal(new PhysicsWorldInfo(DimensionId.Default, new ChunkCoordinate(0, 0, 0), 1, 0), Assert.Single(probe.Physics.Worlds));
        Assert.Equal(1, probe.Physics.Statistics.DynamicBodies);
    }

    [Fact]
    public void RemovesABodyItsComponentsAndItsEmptyWorld()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0));
        EntityId entity = probe.Physics.GetState(body).Entity;

        probe.Physics.RemoveBody(body);

        Assert.False(probe.Physics.IsAlive(body));
        Assert.True(probe.World.IsAlive(entity));
        Assert.False(probe.World.Has(entity, ComponentOf<PhysicsBody>("nobara.physics.body")));
        Assert.False(probe.World.Has(entity, ComponentOf<BodyVelocity>("nobara.physics.velocity")));
        Assert.True(Assert.Single(_harness.Backend.Worlds).IsDisposed);
        Assert.Empty(probe.Physics.Worlds);
        Assert.Throws<ArgumentException>(() => probe.Physics.ApplyImpulse(body, Vector3.UnitY));

        BodyHandle again = probe.Physics.AddBody(DimensionId.Default, entity, new BodyDescription { Shape = probe.Physics.CreateSphere(1) });
        Assert.Equal(body.Index, again.Index);
        Assert.NotEqual(body, again);
    }

    [Fact]
    public void RemovesTheBodyOfADestroyedEntityAtTheNextStep()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0));
        probe.World.Destroy(probe.Physics.GetState(body).Entity);

        _harness.Tick(engine);

        Assert.False(probe.Physics.IsAlive(body));
        Assert.True(Assert.Single(Assert.Single(_harness.Backend.Worlds).Bodies).Removed);
    }

    [Fact]
    public void RefusesEntitiesThatCannotBeBodies()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        PhysicsShape sphere = probe.Physics.CreateSphere(0.5f);
        EntityWorld world = probe.World;

        EntityId bare = world.Create();
        Assert.Contains("no world transform", Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, bare, new BodyDescription { Shape = sphere })).Message);

        EntityId parent = probe.CreateEntity(WorldTransform.Identity);
        EntityId child = probe.CreateEntity(WorldTransform.Identity);
        world.SetParent(child, parent);
        Assert.Contains("has a parent", Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, child, new BodyDescription { Shape = sphere })).Message);

        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(0, 0, 0), shape: sphere);
        EntityId taken = probe.Physics.GetState(body).Entity;
        Assert.Contains("a body already", Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, taken, new BodyDescription { Shape = sphere })).Message);

        PhysicsShape mesh = probe.Physics.CreateMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [0, 1, 2]);
        EntityId ground = probe.CreateEntity(WorldTransform.Identity);
        Assert.Contains("static body only", Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, ground, new BodyDescription { Shape = mesh })).Message);
        BodyHandle floor = probe.Physics.AddBody(DimensionId.Default, ground, new BodyDescription { Kind = BodyKind.Static, Shape = mesh });
        Assert.Throws<ArgumentException>(() => probe.Physics.SetKind(floor, BodyKind.Dynamic));
        Assert.Throws<ArgumentException>(() => probe.Physics.SetVelocity(floor, Vector3.UnitX, Vector3.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Physics.AddBody(DimensionId.Default, probe.CreateEntity(WorldTransform.Identity), new BodyDescription { Shape = sphere, Mass = 0 }));
        Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, probe.CreateEntity(WorldTransform.Identity), new BodyDescription { Shape = sphere, Material = new PhysicsMaterial(0.5f, 2f) }));
    }

    [Fact]
    public void ChangesTheKindShapeMassAndMaterialBeforeTheStep()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(0, 10, 0));
        EntityId entity = probe.Physics.GetState(body).Entity;
        PhysicsShape box = probe.Physics.CreateBox(Vector3.One);

        probe.Physics.SetMass(body, 3);
        probe.Physics.SetKind(body, BodyKind.Kinematic);
        probe.Physics.SetShape(body, box, new Vector3(0, 0.5f, 0));
        probe.Physics.SetMaterial(body, new PhysicsMaterial(1, 0));
        probe.Physics.SetFilter(body, new CollisionFilter(4, 4));
        probe.Physics.SetContinuous(body, true);
        Assert.Equal(BodyKind.Dynamic, probe.World.Read(entity, ComponentOf<PhysicsBody>("nobara.physics.body")).Kind);

        _harness.Tick(engine);

        PhysicsBody component = probe.World.Read(entity, ComponentOf<PhysicsBody>("nobara.physics.body"));
        Assert.Equal(BodyKind.Kinematic, component.Kind);
        Assert.Equal(new Vector3(0, 0.5f, 0), component.Center);
        Assert.Equal(new CollisionFilter(4, 4), component.Filter);
        Assert.True(component.Flags.HasFlag(BodyFlags.Continuous));
        Assert.Equal(box, probe.World.Read(entity, ComponentOf<BodyShape>("nobara.physics.shape")).Shape);
        Assert.Equal(0, probe.World.Read(entity, ComponentOf<BodyMass>("nobara.physics.mass")).Mass);   // not dynamic
        Assert.Equal(1, probe.Physics.Statistics.KinematicBodies);
        Assert.Equal(0, probe.Physics.Statistics.DynamicBodies);

        TestPhysicsWorld physics = Assert.Single(_harness.Backend.Worlds);
        TestBody moved = Assert.Single(physics.Bodies);
        Assert.Equal(BodyKind.Kinematic, moved.Kind);
        Assert.Equal(box, moved.Shape);
        Assert.Equal(3, moved.Mass);
        Assert.Equal(10.5f, moved.Position.Y, 4);   // the center moved half a block above the entity, which stays where it is
        Assert.Equal(10f, probe.TransformOf(body).Position.Local.Y, 4);
        Assert.Throws<InvalidOperationException>(() => probe.Physics.DestroyShape(box));
    }

    private ComponentType<T> ComponentOf<T>(string name)
        where T : unmanaged
    {
        Assert.True(_harness.Probe.Entities.TryGetComponent(out ComponentType<T> component), name);
        return component;
    }
}
