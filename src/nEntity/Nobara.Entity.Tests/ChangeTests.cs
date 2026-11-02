// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Core;
using Nobara.Entity.Tests.Support;

namespace Nobara.Entity.Tests;

/// <summary>How writes to tracked components are recorded, and read back by a reader of changes.</summary>
public sealed class ChangeTests : IDisposable
{
    private readonly EntityHarness _harness = new();
    private FarmComponents _farm = null!;

    public ChangeTests()
    {
        _harness.Module("example.farm", m => m.OnInitialize = p => _farm = new FarmComponents(p));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void A_reader_finds_exactly_the_entities_written_since_its_last_read()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        var entities = new EntityId[5000];
        world.Create(world.GetArchetype(_farm.Position, _farm.Health), entities);
        uint seen = world.AdvanceChangeVersion();
        Assert.Equal(5000, Changed(world, 0).Count);

        seen = world.AdvanceChangeVersion();
        world.Set(entities[10], _farm.Position, new Position { Value = Vector3.One });
        world.Write(entities[4000], _farm.Position).Value = Vector3.UnitY;
        world.Set(entities[20], _farm.Health, new Health { Value = 1 });

        Assert.Equal([entities[10], entities[4000]], Changed(world, seen).OrderBy(e => e.Index).ToArray());
        seen = world.AdvanceChangeVersion();
        Assert.Empty(Changed(world, seen));
    }

    [Fact]
    public void Untouched_blocks_are_skipped_by_their_version()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        var entities = new EntityId[20_000];
        world.Create(world.GetArchetype(_farm.Position), entities);
        uint seen = world.AdvanceChangeVersion();

        world.Set(entities[0], _farm.Position, default);

        int changedBlocks = 0;
        int blocks = 0;
        foreach (ChunkView block in world.Query(new QueryDescription { All = [_farm.Position] }))
        {
            blocks++;
            changedBlocks += block.HasChangedSince(_farm.Position, seen) ? 1 : 0;
        }

        Assert.True(blocks > 1);
        Assert.Equal(1, changedBlocks);
    }

    [Fact]
    public void A_column_written_whole_marks_its_block_and_an_untracked_write_marks_what_it_says()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        var entities = new EntityId[3];
        world.Create(world.GetArchetype(_farm.Position), entities);
        uint seen = world.AdvanceChangeVersion();
        EntityQuery query = world.Query(new QueryDescription { All = [_farm.Position] });

        foreach (ChunkView block in query)
        {
            Span<Position> positions = block.WriteUntracked(_farm.Position);
            positions[1].Value = Vector3.One;
            block.MarkChanged(_farm.Position, 1);
        }

        Assert.Equal([entities[1]], Changed(world, seen));

        seen = world.AdvanceChangeVersion();
        foreach (ChunkView block in query)
        {
            block.Write(_farm.Position);
        }

        Assert.Equal(3, Changed(world, seen).Count);
    }

    [Fact]
    public void Moving_to_another_archetype_keeps_the_version_of_a_value()
    {
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId entity = world.Create(world.GetArchetype(_farm.Position));
        uint seen = world.AdvanceChangeVersion();

        world.Add(entity, _farm.Velocity, default);
        world.Remove(entity, _farm.Velocity);

        Assert.Empty(Changed(world, seen));
    }

    [Fact]
    public void A_system_sees_the_changes_made_since_its_previous_run_and_not_its_own()
    {
        var seenBySystem = new List<int>();
        _harness.Module("example.watch", m => m.OnInitialize = p => p.Worlds.AddSystem(p.Context, "example.watch.positions", new ActionSystem((world, last) =>
        {
            seenBySystem.Add(Changed(world, last).Count);
            foreach (ChunkView block in world.Query(new QueryDescription { All = [_farm.Position] }))
            {
                block.MarkChanged(_farm.Position, 0);
            }
        }), new EntitySystemOptions { Writes = [_farm.Position] }));
        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        var entities = new EntityId[4];
        world.Create(world.GetArchetype(_farm.Position), entities);

        _harness.Tick(engine);
        world.Set(entities[2], _farm.Position, default);
        world.Set(entities[3], _farm.Position, default);
        _harness.Tick(engine);

        // First tick: no entity yet. Then the 4 created. Then the 2 written between the ticks.
        Assert.Equal([0, 4, 2], seenBySystem);
    }

    [Fact]
    public void Versions_stay_comparable_when_the_counter_wraps_around()
    {
        Assert.True(ChangeVersions.IsNewer(5, uint.MaxValue - 5));
        Assert.False(ChangeVersions.IsNewer(uint.MaxValue - 5, 5));
        Assert.True(ChangeVersions.ChangedSince(10, 1, 2_000_000_000));
        Assert.True(ChangeVersions.ChangedSince(0, 1, 1 + ChangeVersions.MaxAge));

        using Engine engine = _harness.StartSession();
        EntityWorld world = _harness.Owner.World();
        EntityId old = world.Create(world.GetArchetype(_farm.Position));

        // Jump to 2^31: the version of the old value is brought up as the counter passes it.
        for (uint step = 1; step <= 4; step++)
        {
            world.ChangeVersion = (step << 29) - 1;
            world.AdvanceChangeVersion();
        }

        world.AdvanceChangeVersion();
        uint seen = world.AdvanceChangeVersion();
        EntityId fresh = world.Create(world.GetArchetype(_farm.Position));

        // Without the bring-up, version 1 would read as newer than 2^31 + 1.
        Assert.Equal([fresh], Changed(world, seen));
        Assert.True(world.IsAlive(old));
    }

    private List<EntityId> Changed(EntityWorld world, uint since)
    {
        var changed = new List<EntityId>();
        foreach (ChunkView block in world.Query(new QueryDescription { All = [_farm.Position] }))
        {
            if (!block.HasChangedSince(_farm.Position, since))
            {
                continue;
            }

            for (int i = 0; i < block.Count; i++)
            {
                if (block.HasChangedSince(_farm.Position, i, since))
                {
                    changed.Add(block.Entities[i]);
                }
            }
        }

        return changed;
    }
}
