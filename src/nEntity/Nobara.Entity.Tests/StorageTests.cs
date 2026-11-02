// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Entity.Tests.Support;

namespace Nobara.Entity.Tests;

/// <summary>How a world stores its entities: archetypes, blocks, structural changes, and identifiers.</summary>
public sealed class StorageTests : IDisposable
{
    private readonly EntityHarness _harness = new();
    private FarmComponents _farm = null!;

    public StorageTests()
    {
        _harness.Module("example.farm", m => m.OnInitialize = p => _farm = new FarmComponents(p));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void An_entity_starts_with_the_default_value_of_each_component()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();

        EntityId entity = world.Create(world.GetArchetype(_farm.Position, _farm.Health));

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.EntityCount);
        Assert.Equal(Vector3.Zero, world.Read(entity, _farm.Position).Value);
        Assert.Equal(10, world.Read(entity, _farm.Health).Value);
        Assert.False(world.Has(entity, _farm.Velocity));
    }

    [Fact]
    public void Adding_and_removing_components_keeps_the_values_and_the_identifier()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId entity = world.Create(world.GetArchetype(_farm.Position));
        world.Set(entity, _farm.Position, new Position { Value = new Vector3(1, 2, 3) });

        world.Add(entity, _farm.Velocity, new Velocity { Value = Vector3.UnitX });
        world.Add(entity, _farm.Health, new Health { Value = 4 });
        world.Remove(entity, _farm.Velocity);

        Assert.True(world.IsAlive(entity));
        Assert.Same(world.GetArchetype(_farm.Health, _farm.Position), world.GetArchetypeOf(entity));
        Assert.Equal(new Vector3(1, 2, 3), world.Read(entity, _farm.Position).Value);
        Assert.Equal(4, world.Read(entity, _farm.Health).Value);
        Assert.Throws<ArgumentException>(() => world.Add(entity, _farm.Health, default));
        Assert.Throws<ArgumentException>(() => world.Remove(entity, _farm.Velocity));
    }

    [Fact]
    public void A_destroyed_entity_is_never_named_again_by_its_old_identifier()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityArchetype archetype = world.GetArchetype(_farm.Health);
        EntityId first = world.Create(archetype);

        world.Destroy(first);
        EntityId second = world.Create(archetype);

        Assert.False(world.IsAlive(first));
        Assert.True(world.IsAlive(second));
        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first, second);
        Assert.Throws<ArgumentException>(() => world.Read(first, _farm.Health));
        Assert.Throws<ArgumentException>(() => world.Destroy(first));
    }

    [Fact]
    public void Entities_moved_by_a_removal_keep_their_values_across_many_blocks()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityArchetype archetype = world.GetArchetype(_farm.Health, _farm.Cargo);
        var entities = new EntityId[200];
        world.Create(archetype, entities);
        for (int i = 0; i < entities.Length; i++)
        {
            world.Set(entities[i], _farm.Health, new Health { Value = i });
        }

        Assert.True(archetype.BlockCount > 10, $"{archetype.BlockCount} blocks");

        // Destroy every third entity: the last ones take their places.
        for (int i = 0; i < entities.Length; i += 3)
        {
            world.Destroy(entities[i]);
        }

        for (int i = 0; i < entities.Length; i++)
        {
            Assert.Equal(i % 3 != 0, world.IsAlive(entities[i]));
            if (i % 3 != 0)
            {
                Assert.Equal(i, world.Read(entities[i], _farm.Health).Value);
            }
        }

        int seen = 0;
        foreach (ChunkView block in world.Query(new QueryDescription { All = [_farm.Health] }))
        {
            ReadOnlySpan<Health> health = block.Read(_farm.Health);
            for (int i = 0; i < block.Count; i++)
            {
                Assert.Equal(health[i].Value, world.Read(block.Entities[i], _farm.Health).Value);
                seen++;
            }
        }

        Assert.Equal(world.EntityCount, seen);
    }

    [Fact]
    public void A_query_follows_the_archetypes_created_after_it()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityQuery query = world.Query(new QueryDescription { All = [_farm.Position], None = [_farm.Burning] });
        world.Create(world.GetArchetype(_farm.Position));

        world.Create(world.GetArchetype(_farm.Position, _farm.Velocity));
        world.Create(world.GetArchetype(_farm.Position, _farm.Burning));
        world.Create(world.GetArchetype(_farm.Velocity));

        Assert.Equal(2, query.Count());
    }

    [Fact]
    public void Destroying_a_query_destroys_every_entity_it_matches()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        world.Create(world.GetArchetype(_farm.Health), new EntityId[50]);
        EntityId kept = world.Create(world.GetArchetype(_farm.Position));

        world.Destroy(world.Query(new QueryDescription { All = [_farm.Health] }));

        Assert.Equal(1, world.EntityCount);
        Assert.True(world.IsAlive(kept));
    }

    [Fact]
    public void The_parent_link_cannot_be_written_or_removed_directly()
    {
        using Engine engine = _harness.StartSession();
        IEntityWorlds worlds = _harness.Owner.Worlds;
        EntityWorld world = _harness.Owner.World();
        EntityId parent = world.Create(world.GetArchetype(worlds.WorldTransform));
        EntityId child = world.Create(world.GetArchetype(worlds.WorldTransform));
        world.SetParent(child, parent);

        Assert.Throws<InvalidOperationException>(() => world.Set(child, worlds.ParentLink, default));
        Assert.Throws<ArgumentException>(() => world.Remove(child, worlds.ParentLink));
        Assert.Throws<InvalidOperationException>(() => world.Add(parent, worlds.ParentLink, world.Read(child, worlds.ParentLink)));
        Assert.Equal(EntityId.Null, world.GetParent(parent));
    }

    [Fact]
    public void The_transforms_of_an_entity_in_a_hierarchy_cannot_be_removed()
    {
        using Engine engine = _harness.StartSession();
        IEntityWorlds worlds = _harness.Owner.Worlds;
        EntityWorld world = _harness.Owner.World();
        EntityId parent = world.Create(world.GetArchetype(worlds.WorldTransform));
        EntityId child = world.Create(world.GetArchetype(worlds.WorldTransform));
        world.SetParent(child, parent);

        Assert.Throws<ArgumentException>(() => world.Remove(parent, worlds.WorldTransform));
        Assert.Throws<ArgumentException>(() => world.Remove(child, worlds.WorldTransform));
        Assert.Throws<ArgumentException>(() => world.Remove(child, worlds.LocalTransform));

        world.ClearParent(child);
        world.Remove(child, worlds.LocalTransform);
        world.Remove(parent, worlds.WorldTransform);
        Assert.False(world.Has(parent, worlds.WorldTransform));
    }

    [Fact]
    public void A_handle_of_another_context_is_refused()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();

        Assert.Throws<ArgumentException>(() => world.GetArchetype(default(ComponentType)));
        Assert.Throws<ArgumentException>(() => world.GetArchetype(new ComponentType(999)));
    }
}
