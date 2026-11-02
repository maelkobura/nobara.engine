// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Logging;
using Nobara.Entity.Tests.Support;
using Nobara.Game;
using F = Nobara.Formats;

namespace Nobara.Entity.Tests;

/// <summary>The worlds of a context: sessions, leaks, sides, contexts, and component types.</summary>
public sealed class WorldTests : IDisposable
{
    private readonly EntityHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Worlds_exist_during_the_session_only_and_a_new_session_starts_empty()
    {
        _harness.DevelopmentMode = true;
        using Engine engine = _harness.StartSession(Side.Server);
        Probe owner = _harness.Owner;
        EntityWorld world = owner.World();
        world.Create(world.GetArchetype(owner.Worlds.WorldTransform), new EntityId[100]);

        owner.Sessions.End();
        _harness.Tick(engine);

        Assert.Equal(SessionState.None, owner.Sessions.State);
        Assert.Empty(owner.Worlds.Worlds);
        Assert.True(world.IsDestroyed);
        Assert.DoesNotContain(_harness.Log.Entries, e => e.Exception is WorldStateLeakException);

        owner.Sessions.Start();
        _harness.Tick(engine);
        Assert.Equal(0, owner.World().EntityCount);
    }

    [Fact]
    public void A_world_left_after_the_session_is_reported_as_a_leak_of_its_owner_in_development_mode()
    {
        _harness.DevelopmentMode = true;
        _harness.LeakWorlds = true;
        _harness.OwnerPolicy = F.Policy.Log;
        using Engine engine = _harness.StartSession();
        Probe owner = _harness.Owner;
        EntityWorld world = owner.World();
        world.Create(world.GetArchetype(owner.Worlds.WorldTransform), new EntityId[7]);

        owner.Sessions.End();
        _harness.Tick(engine);

        WorldStateLeakException[] leaks = _harness.Log.Entries.Select(e => e.Exception).OfType<WorldStateLeakException>().ToArray();
        Assert.Equal(["entity worlds=1", "entities=7"], leaks.Select(l => $"{l.Counter}={l.Count}"));
        Assert.All(leaks, l => Assert.Equal(EntityHarness.OwnerId, l.Module));

        // The next session starts clean: the leaked world is destroyed, with a warning.
        owner.Sessions.Start();
        _harness.Tick(engine);
        Assert.True(world.IsDestroyed);
        Assert.Contains(_harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not destroy the entity world", StringComparison.Ordinal));
    }

    [Fact]
    public void A_world_is_created_during_a_session_only_by_its_owner_and_once_per_dimension()
    {
        using Engine engine = _harness.StartSession(Side.Server);
        Probe owner = _harness.Owner;

        Assert.Throws<ArgumentException>(() => owner.Worlds.Create(owner.Context, DimensionId.Default));
        Assert.Throws<ArgumentException>(() => owner.Worlds.Create(owner.Context, new DimensionId(9)));
        owner.Sessions.End();
        _harness.Tick(engine);
        Assert.Throws<InvalidOperationException>(() => owner.Worlds.Create(owner.Context, DimensionId.Default));
    }

    [Fact]
    public void Another_module_cannot_destroy_a_world()
    {
        _harness.Module("example.intruder", null);
        using Engine engine = _harness.StartSession();

        ArgumentException error = Assert.Throws<ArgumentException>(() => _harness.Probes["example.intruder"].Worlds.Destroy(_harness.Probes["example.intruder"].Context, DimensionId.Default));

        Assert.Contains("module 'example.world' created it", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_holds_one_world_at_a_time()
    {
        using Engine engine = _harness.StartSession(Side.Client);
        Probe owner = _harness.Owner;
        owner.Time.AddDimension(new DimensionId(1));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => owner.Worlds.Create(owner.Context, new DimensionId(1)));

        Assert.Contains("holds the world of the dimension the player sees only", error.Message, StringComparison.Ordinal);
        owner.Worlds.Destroy(owner.Context, DimensionId.Default);
        Assert.Equal(new DimensionId(1), owner.Worlds.Create(owner.Context, new DimensionId(1)).Dimension);
    }

    [Fact]
    public void Two_contexts_of_one_process_keep_their_own_worlds_and_components()
    {
        ComponentType<Health> first = default;
        ComponentType<Health> second = default;
        int calls = 0;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            ComponentType<Health> health = p.Worlds.RegisterComponent<Health>(p.Context, "example.farm.health");
            if (calls++ == 0)
            {
                first = health;
            }
            else
            {
                second = health;
            }
        });

        using Engine client = _harness.StartSession(Side.Client);
        Probe clientOwner = _harness.Owner;
        using Engine server = _harness.StartSession(Side.Server);
        Probe serverOwner = _harness.Owner;
        EntityWorld clientWorld = clientOwner.World();
        EntityWorld serverWorld = serverOwner.World();
        clientWorld.Create(clientWorld.GetArchetype(first), new EntityId[3]);
        serverWorld.Create(serverWorld.GetArchetype(second), new EntityId[5]);
        _harness.Tick(client);
        _harness.Tick(server);

        Assert.NotSame(clientOwner.Worlds, serverOwner.Worlds);
        Assert.Equal(3, clientWorld.EntityCount);
        Assert.Equal(5, serverWorld.EntityCount);
        Assert.True(clientOwner.Worlds.TryGetComponent("example.farm.health", out ComponentType _));
    }

    [Fact]
    public void A_component_of_an_assembly_loaded_after_the_start_is_registered_and_used()
    {
        _harness.Module("example.mod", null);
        using Engine engine = _harness.StartSession();
        Probe mod = _harness.Probes["example.mod"];
        Type type = EmitComponent(out AssemblyLoadContext context);

        ComponentType component = mod.Worlds.RegisterComponent(mod.Context, "example.mod.charge", type);
        EntityWorld world = _harness.Owner.World();
        EntityId entity = world.Create(world.GetArchetype(component, mod.Worlds.WorldTransform));
        _harness.Tick(engine);

        Assert.True(world.Has(entity, component));
        Assert.True(mod.Worlds.TryGetComponent("example.mod.charge", out ComponentType found));
        Assert.Equal(component, found);
        ComponentInfo info = mod.Worlds.GetComponent(component);
        Assert.Equal(type, info.Type);
        Assert.Equal(12, info.Size);
        Assert.Equal("example.mod", info.ModuleId);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(type.Assembly));
    }

    [Fact]
    public void A_component_is_refused_when_its_name_is_not_qualified_by_its_module_or_is_taken()
    {
        _harness.Module("example.farm", null);
        using Engine engine = _harness.StartSession();
        Probe farm = _harness.Probes["example.farm"];
        IEntityWorlds worlds = farm.Worlds;

        ArgumentException foreign = Assert.Throws<ArgumentException>(() => worlds.RegisterComponent<Health>(farm.Context, "example.world.health"));
        Assert.Contains("starts with the identifier of the module that registers it", foreign.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => worlds.RegisterComponent<Health>(farm.Context, "example.farm"));
        Assert.Throws<ArgumentException>(() => worlds.RegisterComponent<Health>(farm.Context, "example.farmhouse.health"));
        worlds.RegisterComponent<Health>(farm.Context, "example.farm.health");
        Assert.Throws<ArgumentException>(() => worlds.RegisterComponent<Velocity>(farm.Context, "example.farm.health"));
        Assert.Throws<ArgumentException>(() => worlds.RegisterComponent<Health>(farm.Context, "example.farm.life"));
        Assert.Throws<ArgumentException>(() => worlds.RegisterComponent(farm.Context, "example.farm.name", typeof(string)));
        Assert.Throws<ArgumentException>(() => worlds.RegisterComponent(farm.Context, "example.farm.pair", typeof(KeyValuePair<int, string>)));
    }

    [Fact]
    public void Systems_are_registered_while_modules_initialize_only()
    {
        _harness.Module("example.farm", null);
        using Engine engine = _harness.StartSession();
        Probe farm = _harness.Probes["example.farm"];

        Assert.Throws<InvalidOperationException>(() => farm.Worlds.AddSystem(farm.Context, "example.farm.late", new ActionSystem((_, _) => { })));
    }

    // A struct of three floats, in an assembly built and loaded at run time, as a mod would be.
    private static Type EmitComponent(out AssemblyLoadContext context)
    {
        context = new AssemblyLoadContext("example mod", isCollectible: true);
        using (context.EnterContextualReflection())
        {
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Example.Mod"), AssemblyBuilderAccess.RunAndCollect);
            ModuleBuilder module = assembly.DefineDynamicModule("Example.Mod");
            TypeBuilder builder = module.DefineType("Example.Mod.Charge", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
            builder.DefineField("X", typeof(float), FieldAttributes.Public);
            builder.DefineField("Y", typeof(float), FieldAttributes.Public);
            builder.DefineField("Z", typeof(float), FieldAttributes.Public);
            return builder.CreateType();
        }
    }
}
