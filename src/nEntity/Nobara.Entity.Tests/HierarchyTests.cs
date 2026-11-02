// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Core.Mathematics;
using Nobara.Entity.Tests.Support;

namespace Nobara.Entity.Tests;

/// <summary>Parents and children: propagation of transforms, detachment, destruction, and cycles.</summary>
public sealed class HierarchyTests : IDisposable
{
    private readonly EntityHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private IEntityWorlds Worlds => _harness.Owner.Worlds;

    [Fact]
    public void A_hierarchy_follows_its_parent_level_by_level()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId root = Placed(world, WorldPosition.FromBlocks(100, 0, 0));
        EntityId child = Placed(world, default);
        EntityId grandchild = Placed(world, default);
        world.SetParent(child, root, keepWorldTransform: false);
        world.SetParent(grandchild, child, keepWorldTransform: false);
        world.Set(child, Worlds.LocalTransform, LocalTransform.At(new Vector3(0, 2, 0)));
        world.Set(grandchild, Worlds.LocalTransform, new LocalTransform(new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One));

        // The root turns a quarter around Y and moves; its descendants follow at the next tick.
        world.Set(root, Worlds.WorldTransform, new WorldTransform(WorldPosition.FromBlocks(200, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Vector3.One));
        _harness.Tick(engine);

        Assert.Equal(2, world.GetDepth(grandchild));
        AssertAt(world, child, 200, 2, 0);
        AssertAt(world, grandchild, 200, 2, -1);
    }

    [Fact]
    public void Detaching_a_child_keeps_its_place_in_the_world()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId parent = Placed(world, WorldPosition.FromBlocks(10, 0, 0));
        EntityId child = Placed(world, WorldPosition.FromBlocks(12, 0, 0));
        world.SetParent(child, parent);
        world.Set(parent, Worlds.WorldTransform, WorldTransform.At(WorldPosition.FromBlocks(20, 0, 0)));

        // Not ticked yet: the child is computed from where the parent is now.
        world.ClearParent(child);
        world.Set(parent, Worlds.WorldTransform, WorldTransform.At(WorldPosition.FromBlocks(50, 0, 0)));
        _harness.Tick(engine);

        Assert.Equal(EntityId.Null, world.GetParent(child));
        Assert.Equal(0, world.GetDepth(child));
        AssertAt(world, child, 22, 0, 0);
    }

    [Fact]
    public void Setting_a_parent_keeps_the_child_where_it_is_unless_asked_otherwise()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId parent = world.Create(world.GetArchetype(Worlds.WorldTransform));
        world.Set(parent, Worlds.WorldTransform, new WorldTransform(WorldPosition.FromBlocks(5, 5, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1), new Vector3(2)));
        EntityId kept = Placed(world, WorldPosition.FromBlocks(-3, 7, 1));
        EntityId moved = Placed(world, WorldPosition.FromBlocks(-3, 7, 1));

        world.SetParent(kept, parent);
        world.SetParent(moved, parent, keepWorldTransform: false);
        _harness.Tick(engine);

        AssertAt(world, kept, -3, 7, 1);
        AssertAt(world, moved, 5, 5, 5);
    }

    [Fact]
    public void Destroying_a_parent_detaches_or_destroys_its_children_as_their_links_say()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId parent = Placed(world, WorldPosition.FromBlocks(1, 0, 0));
        EntityId rider = Placed(world, WorldPosition.FromBlocks(1, 3, 0));
        EntityId saddle = Placed(world, WorldPosition.FromBlocks(1, 1, 0));
        EntityId strap = Placed(world, WorldPosition.FromBlocks(1, 1, 1));
        world.SetParent(rider, parent, OrphanPolicy.Detach);
        world.SetParent(saddle, parent, OrphanPolicy.Destroy);
        world.SetParent(strap, saddle, OrphanPolicy.Destroy);

        world.Destroy(parent);

        Assert.True(world.IsAlive(rider));
        Assert.False(world.IsAlive(saddle));
        Assert.False(world.IsAlive(strap));
        Assert.Equal(EntityId.Null, world.GetParent(rider));
        AssertAt(world, rider, 1, 3, 0);
        Assert.Equal(0, world.MaxDepth);
    }

    [Fact]
    public void A_hierarchy_refuses_cycles()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId a = Placed(world, default);
        EntityId b = Placed(world, default);
        EntityId c = Placed(world, default);
        world.SetParent(b, a);
        world.SetParent(c, b);

        Assert.Throws<ArgumentException>(() => world.SetParent(a, c));
        Assert.Throws<ArgumentException>(() => world.SetParent(a, a));
        Assert.Equal(EntityId.Null, world.GetParent(a));
    }

    [Fact]
    public void Changing_the_parent_moves_the_entity_to_no_other_archetype_and_updates_the_depths()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId first = Placed(world, default);
        EntityId second = Placed(world, default);
        EntityId middle = Placed(world, default);
        EntityId child = Placed(world, default);
        EntityId leaf = Placed(world, default);
        world.SetParent(child, first);
        world.SetParent(leaf, child);
        world.SetParent(middle, second);
        EntityArchetype archetype = world.GetArchetypeOf(child);

        world.SetParent(child, middle);

        Assert.Same(archetype, world.GetArchetypeOf(child));
        Assert.Equal(2, world.GetDepth(child));
        Assert.Equal(3, world.GetDepth(leaf));
        Assert.Empty(Children(world, first));
        Assert.Equal([child], Children(world, middle));
        Assert.Equal(3, world.MaxDepth);
    }

    [Fact]
    public void A_system_changes_parents_through_its_commands()
    {
        _harness.Module("example.mount", m => m.OnInitialize = p => p.Worlds.AddSystem(p.Context, "example.mount.ride", new CommandSystem(), new EntitySystemOptions
        {
            Query = new QueryDescription { All = [p.Worlds.WorldTransform], None = [p.Worlds.ParentLink] },
            ChangesStructure = true,
        }));
        _harness.DevelopmentMode = true;
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId horse = Placed(world, WorldPosition.FromBlocks(0, 0, 0));
        EntityId rider = Placed(world, WorldPosition.FromBlocks(0, 2, 0));

        _harness.Tick(engine);
        world.Set(horse, Worlds.WorldTransform, WorldTransform.At(WorldPosition.FromBlocks(9, 0, 0)));
        _harness.Tick(engine);

        Assert.Equal(horse, world.GetParent(rider));
        AssertAt(world, rider, 9, 2, 0);
    }

    [Fact]
    public void Propagation_goes_through_many_hierarchies_in_parallel_without_allocating()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        var roots = new EntityId[2000];
        var children = new EntityId[2000];
        var grandchildren = new EntityId[2000];
        EntityArchetype placed = world.GetArchetype(Worlds.WorldTransform);
        world.Create(placed, roots);
        world.Create(placed, children);
        world.Create(placed, grandchildren);
        for (int i = 0; i < roots.Length; i++)
        {
            world.Set(roots[i], Worlds.WorldTransform, WorldTransform.At(WorldPosition.FromBlocks(i, 0, 0)));
            world.SetParent(children[i], roots[i], keepWorldTransform: false);
            world.SetParent(grandchildren[i], children[i], keepWorldTransform: false);
            world.Set(children[i], Worlds.LocalTransform, LocalTransform.At(Vector3.UnitY));
            world.Set(grandchildren[i], Worlds.LocalTransform, LocalTransform.At(Vector3.UnitZ));
        }

        _harness.Tick(engine, 5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _harness.Tick(engine, 50);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        AssertAt(world, grandchildren[1234], 1234, 1, 1);
    }

    private static List<EntityId> Children(EntityWorld world, EntityId parent)
    {
        var children = new List<EntityId>();
        foreach (EntityId child in world.GetChildren(parent))
        {
            children.Add(child);
        }

        return children;
    }

    private EntityId Placed(EntityWorld world, WorldPosition position)
    {
        EntityId entity = world.Create(world.GetArchetype(Worlds.WorldTransform));
        world.Set(entity, Worlds.WorldTransform, WorldTransform.At(position));
        return entity;
    }

    private void AssertAt(EntityWorld world, EntityId entity, double x, double y, double z)
    {
        Vector3 offset = world.Read(entity, Worlds.WorldTransform).Position.RelativeTo(WorldPosition.FromBlocks(x, y, z));
        Assert.True(offset.Length() < 1e-4f, $"The {entity} is {offset} away from ({x}, {y}, {z}).");
    }

    /// <summary>Makes the first placed entity without a parent ride the second one.</summary>
    private sealed class CommandSystem : IEntitySystem
    {
        public void Run(in EntitySystemRun run)
        {
            if (run.Query.Count() != 2)
            {
                return;
            }

            EntityId horse = EntityId.Null;
            EntityId rider = EntityId.Null;
            foreach (ChunkView block in run.Query)
            {
                foreach (EntityId entity in block.Entities)
                {
                    if (horse.IsNull)
                    {
                        horse = entity;
                    }
                    else
                    {
                        rider = entity;
                    }
                }
            }

            run.Commands.SetParent(rider, horse);
        }
    }
}
