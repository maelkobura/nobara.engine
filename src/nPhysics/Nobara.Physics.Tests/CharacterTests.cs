// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Physics.Tests.Support;

namespace Nobara.Physics.Tests;

/// <summary>Adding and removing characters, and what the character controller writes, with a test backend.</summary>
public sealed class CharacterTests : IDisposable
{
    private readonly PhysicsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AddingACharacterAddsItsComponentsAndRemovingItTakesThemAway()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        EntityId entity = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(16, 20, 16)));

        probe.Physics.AddCharacter(DimensionId.Default, entity, CharacterBody.Default);

        EntityWorld world = probe.World;
        Assert.True(probe.Physics.IsCharacter(DimensionId.Default, entity));
        Assert.True(world.Has(entity, Component<CharacterBody>()));
        Assert.True(world.Has(entity, Component<CharacterState>()));
        Assert.True(world.Has(entity, Component<MovementIntent>()));
        Assert.True(world.Has(entity, Component<BodyVelocity>()));
        Assert.Equal(1, probe.Physics.Statistics.Characters);

        probe.Physics.RemoveCharacter(DimensionId.Default, entity);

        Assert.False(probe.Physics.IsCharacter(DimensionId.Default, entity));
        Assert.False(world.Has(entity, Component<CharacterBody>()));
        Assert.False(world.Has(entity, Component<CharacterState>()));
        Assert.True(world.Has(entity, Component<MovementIntent>()));   // its module may keep writing it
        Assert.True(world.Has(entity, Component<BodyVelocity>()));     // the last velocity stays readable
        Assert.Equal(0, probe.Physics.Statistics.Characters);
    }

    [Fact]
    public void ACharacterFallsWithTheGravityOfTheWorldTimesItsScale()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        EntityId heavy = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(16, 20, 16)));
        EntityId light = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(20, 20, 16)));
        CharacterBody half = CharacterBody.Default;
        half.GravityScale = 0.5f;
        probe.Physics.AddCharacter(DimensionId.Default, heavy, CharacterBody.Default);
        probe.Physics.AddCharacter(DimensionId.Default, light, half);

        _harness.Tick(engine, 30);

        float fallen = 20 - Height(probe, heavy);
        Assert.InRange(fallen, 4.9f, 5.1f);   // ½ × 9.81 × 1², in steps of 1/30 s
        Assert.Equal(fallen / 2, 20 - Height(probe, light), 3);
        Assert.Equal(-9.81f, probe.World.Read(heavy, Component<BodyVelocity>()).Linear.Y, 2);
        Assert.False(probe.World.Read(heavy, Component<CharacterState>()).IsGrounded);
    }

    [Fact]
    public void RemovingTheComponentOfACharacterStopsMovingItAtTheNextStep()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        EntityId entity = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(16, 20, 16)));
        probe.Physics.AddCharacter(DimensionId.Default, entity, CharacterBody.Default);
        _harness.Tick(engine, 5);

        probe.World.Remove(entity, Component<CharacterBody>());
        _harness.Tick(engine);
        WorldTransform left = probe.World.Read(entity, probe.Entities.WorldTransform);
        _harness.Tick(engine, 5);

        Assert.Equal(left, probe.World.Read(entity, probe.Entities.WorldTransform));
        Assert.False(probe.Physics.IsCharacter(DimensionId.Default, entity));
        Assert.Equal(0, probe.Physics.Statistics.Characters);
    }

    [Fact]
    public void AnEntityIsEitherABodyOrACharacter()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        BodyHandle body = probe.CreateBody(WorldPosition.FromBlocks(16, 20, 16));
        EntityId character = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(20, 20, 16)));
        probe.Physics.AddCharacter(DimensionId.Default, character, CharacterBody.Default);

        Assert.Throws<ArgumentException>(() => probe.Physics.AddCharacter(DimensionId.Default, probe.Physics.GetState(body).Entity, CharacterBody.Default));
        Assert.Throws<ArgumentException>(() => probe.Physics.AddBody(DimensionId.Default, character, new BodyDescription { Shape = probe.Physics.CreateSphere(0.5f) }));
        Assert.Throws<ArgumentException>(() => probe.Physics.AddCharacter(DimensionId.Default, character, CharacterBody.Default));

        probe.Physics.RemoveCharacter(DimensionId.Default, character);
        BodyHandle ragdoll = probe.Physics.AddBody(DimensionId.Default, character, new BodyDescription { Shape = probe.Physics.CreateSphere(0.5f) });   // what was a character becomes a body
        Assert.True(probe.Physics.IsAlive(ragdoll));
    }

    [Fact]
    public void ACharacterNeedsAValidBodyAndARootEntity()
    {
        using Engine engine = _harness.Start();
        PhysicsProbe probe = _harness.Probe;
        EntityId parent = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(16, 20, 16)));
        EntityId child = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(16, 21, 16)));
        probe.World.SetParent(child, parent);
        EntityId free = probe.CreateEntity(WorldTransform.At(WorldPosition.FromBlocks(20, 20, 16)));
        CharacterBody flat = CharacterBody.Default;
        flat.Size = new Vector3(0.6f, 0, 0.6f);
        CharacterBody weightless = CharacterBody.Default;
        weightless.Mass = 0;

        Assert.Throws<ArgumentException>(() => probe.Physics.AddCharacter(DimensionId.Default, child, CharacterBody.Default));
        Assert.Throws<ArgumentException>(() => probe.Physics.AddCharacter(DimensionId.Default, free, flat));
        Assert.Throws<ArgumentException>(() => probe.Physics.AddCharacter(DimensionId.Default, free, weightless));
        Assert.Throws<ArgumentException>(() => probe.Physics.RemoveCharacter(DimensionId.Default, free));
    }

    private ComponentType<T> Component<T>()
        where T : unmanaged
    {
        Assert.True(_harness.Probe.Entities.TryGetComponent(out ComponentType<T> component));
        return component;
    }

    private static float Height(PhysicsProbe probe, EntityId entity)
    {
        WorldPosition position = probe.World.Read(entity, probe.Entities.WorldTransform).Position;
        return (position.ChunkY * WorldPosition.ChunkSize) + position.Local.Y;
    }
}
