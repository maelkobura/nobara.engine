// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Game.Tests.Support;
using F = Nobara.Formats;

namespace Nobara.Game.Tests;

/// <summary>Stages, tick systems, and the resolution of their order.</summary>
public sealed class OrderTests : IDisposable
{
    private readonly GameHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static string[] Stages(ITickSchedule schedule) => schedule.GetOrder().Select(s => s.Name).ToArray();

    private static string[] Systems(ITickSchedule schedule) => schedule.GetOrder().SelectMany(s => s.Systems).Select(s => s.Name).ToArray();

    private static TickScheduleException StartFails(Engine engine)
    {
        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);
        Assert.Equal("nobara.game", stop.ModuleId);
        Assert.Equal(LifecycleStep.Start, stop.Step);
        return Assert.IsType<TickScheduleException>(stop.InnerException);
    }

    [Fact]
    public void Runs_the_six_standard_stages_in_order_with_the_scheduler_in_its_stage()
    {
        ITickSchedule? schedule = null;
        _harness.Module("example.farm", m => m.OnStart = p => schedule = p.Schedule);
        using Engine engine = _harness.Start();

        Assert.Equal(["Intents", "Schedule", "World", "Entities", "Physics", "Late"], Stages(schedule!));
        Assert.Equal([TickStages.SchedulerSystem], Systems(schedule!));
        Assert.Equal("nobara.game", schedule!.GetOrder()[1].ModuleId);
    }

    [Fact]
    public void Runs_systems_by_stage_then_by_constraints_then_by_load_order_then_by_name()
    {
        ITickSchedule? schedule = null;
        _harness
            .Module("example.world", m => m.OnInitialize = p =>
            {
                p.System("example.world.zeta", TickStages.Entities);
                p.System("example.world.alpha", TickStages.Entities);
                p.System("example.world.weather", TickStages.World);
            })
            .Module(
                "example.farm",
                m => m.OnInitialize = p =>
                {
                    p.System("example.farm.growth", TickStages.Entities, new TickSystemOptions { Before = ["example.world.alpha"] });
                    p.System("example.farm.animals", TickStages.Entities);
                    p.System("example.farm.input", TickStages.Intents);
                    schedule = p.Schedule;
                },
                dependsOn: "example.world");
        using Engine engine = _harness.Start();

        // Ready systems run by load order, then by name; alpha waits for growth.
        Assert.Equal(
            ["example.farm.input", TickStages.SchedulerSystem, "example.world.weather", "example.world.zeta", "example.farm.animals", "example.farm.growth", "example.world.alpha"],
            Systems(schedule!));

        _harness.Tick(engine);
        Assert.Equal(
            ["example.farm.input@1", "example.world.weather@1", "example.world.zeta@1", "example.farm.animals@1", "example.farm.growth@1", "example.world.alpha@1"],
            _harness.Journal);
    }

    [Fact]
    public void Resolves_the_same_order_at_every_run_and_formats_it()
    {
        string Order()
        {
            using var harness = new GameHarness();
            string? text = null;
            harness
                .Module("example.b", m => m.OnInitialize = p =>
                {
                    p.System("example.b.one", TickStages.Physics, new TickSystemOptions { After = ["example.a.two"] });
                    p.System("example.b.two", TickStages.Physics);
                })
                .Module("example.a", m =>
                {
                    m.OnInitialize = p =>
                    {
                        p.System("example.a.two", TickStages.Physics);
                        p.System("example.a.one", TickStages.Physics);
                    };
                    m.OnStart = p => text = p.Schedule.FormatOrder();
                });
            using Engine engine = harness.Start();
            return text!;
        }

        string first = Order();
        Assert.Equal(first, Order());
        Assert.Contains("5. Physics (nobara.game)", first, StringComparison.Ordinal);
        int[] positions = new[] { "example.b.two", "example.a.one", "example.a.two", "example.b.one" }.Select(n => first.IndexOf(n, StringComparison.Ordinal)).ToArray();
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void Inserts_stages_after_or_before_others_in_load_order()
    {
        ITickSchedule? schedule = null;
        _harness
            .Module("example.fluid", m => m.OnInitialize = p =>
            {
                p.Schedule.AddStage(p.Context, "example.fluid.flow", after: TickStages.World);
                p.Schedule.AddStage(p.Context, "example.fluid.settle", after: "example.fluid.flow", before: TickStages.Physics);
                p.Schedule.AddStage(p.Context, "example.fluid.prepare", before: TickStages.Physics);
            })
            .Module(
                "example.combat",
                m =>
                {
                    m.OnInitialize = p => p.Schedule.AddStage(p.Context, "example.combat.hits", after: TickStages.World);
                    m.OnStart = p => schedule = p.Schedule;
                },
                dependsOn: "example.fluid");
        using Engine engine = _harness.Start();

        Assert.Equal(
            ["Intents", "Schedule", "World", "example.fluid.flow", "example.fluid.settle", "example.combat.hits", "Entities", "example.fluid.prepare", "Physics", "Late"],
            Stages(schedule!));
        Assert.Equal("example.combat", schedule!.GetOrder()[5].ModuleId);
    }

    [Fact]
    public void A_cycle_of_systems_fails_the_start_and_names_the_systems_and_the_stage()
    {
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.System("example.farm.sow", TickStages.Entities, new TickSystemOptions { After = ["example.farm.reap"] });
            p.System("example.farm.water", TickStages.Entities, new TickSystemOptions { After = ["example.farm.sow"] });
            p.System("example.farm.reap", TickStages.Entities, new TickSystemOptions { After = ["example.farm.water"] });
            p.System("example.farm.sell", TickStages.Entities, new TickSystemOptions { After = ["example.farm.reap"] });
        });
        using Engine engine = _harness.Load();

        TickScheduleException exception = StartFails(engine);

        string problem = Assert.Single(exception.Problems);
        Assert.Equal("The tick systems of stage 'Entities' form a cycle: example.farm.reap -> example.farm.sow -> example.farm.water -> example.farm.reap.", problem);
    }

    [Fact]
    public void A_constraint_against_the_order_of_the_stages_fails_the_start()
    {
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.System("example.farm.input", TickStages.Intents, new TickSystemOptions { After = ["example.farm.physics"] });
            p.System("example.farm.physics", TickStages.Physics);
        });
        using Engine engine = _harness.Load();

        TickScheduleException exception = StartFails(engine);

        Assert.Equal(
            "System 'example.farm.input' (stage 'Intents') runs after 'example.farm.physics', which runs in stage 'Physics', later in the tick: the constraint and the stages form a cycle.",
            Assert.Single(exception.Problems));
    }

    [Fact]
    public void A_constraint_toward_a_system_of_an_earlier_stage_holds_and_one_toward_an_absent_system_is_ignored()
    {
        ITickSchedule? schedule = null;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.System("example.farm.late", TickStages.Late, new TickSystemOptions { After = ["example.farm.input", "example.weather.rain"] });
            p.System("example.farm.input", TickStages.Intents);
            schedule = p.Schedule;
        });
        using Engine engine = _harness.Start();

        Assert.Equal(["example.farm.input", TickStages.SchedulerSystem, "example.farm.late"], Systems(schedule!));
        Assert.Contains(_harness.Log.Entries, e => e.Message.Contains("'example.weather.rain', which no module registers here; the constraint is ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void Stages_that_cannot_be_placed_fail_the_start()
    {
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.Schedule.AddStage(p.Context, "example.farm.a", after: "example.farm.b");
            p.Schedule.AddStage(p.Context, "example.farm.b", after: "example.farm.a");
            p.Schedule.AddStage(p.Context, "example.farm.c", after: "example.weather.storm");
            p.Schedule.AddStage(p.Context, "example.farm.d", after: TickStages.Physics, before: TickStages.World);
            p.System("example.farm.lost", "example.farm.nowhere");
        });
        using Engine engine = _harness.Load();

        TickScheduleException exception = StartFails(engine);

        Assert.Contains("Stage 'example.farm.a' of module 'example.farm' cannot be placed: stage 'example.farm.b' cannot be placed first; the stages wait on each other.", exception.Problems);
        Assert.Contains("Stage 'example.farm.c' of module 'example.farm' cannot be placed: no module adds stage 'example.weather.storm'.", exception.Problems);
        Assert.Contains("Stage 'example.farm.d' of module 'example.farm' runs after 'Physics' and before 'World', but 'World' runs before 'Physics': the constraints form a cycle.", exception.Problems);
        Assert.Contains("System 'example.farm.lost' of module 'example.farm' runs in stage 'example.farm.nowhere', which no module adds.", exception.Problems);
    }

    [Fact]
    public void Names_are_qualified_by_their_module_and_unique()
    {
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            Assert.Throws<ArgumentException>(() => p.System("example.other.growth", TickStages.Entities));
            Assert.Throws<ArgumentException>(() => p.System("growth", TickStages.Entities));
            Assert.Throws<ArgumentException>(() => p.System("example.farm.Growth", TickStages.Entities));
            p.System("example.farm.growth", TickStages.Entities);
            Assert.Throws<ArgumentException>(() => p.System("example.farm.growth", TickStages.World));
            Assert.Throws<ArgumentException>(() => p.Schedule.AddStage(p.Context, "example.farm.growth", after: TickStages.World));
            Assert.Throws<ArgumentException>(() => p.Schedule.AddStage(p.Context, "example.farm.stage"));
        });
        using Engine engine = _harness.Start();
    }

    [Fact]
    public void The_order_is_fixed_once_the_game_module_starts()
    {
        _harness.Module("example.farm", m => m.OnStart = p =>
        {
            Assert.True(p.Schedule.IsResolved);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => p.System("example.farm.late", TickStages.Entities));
            Assert.Contains("fixed once nobara.game starts", exception.Message, StringComparison.Ordinal);
        });
        _harness.Module("example.early", m => m.OnInitialize = p =>
        {
            Assert.False(p.Schedule.IsResolved);
            Assert.Throws<InvalidOperationException>(p.Schedule.GetOrder);
        });
        using Engine engine = _harness.Start();
    }

    [Fact]
    public void A_revoked_system_runs_until_the_end_of_its_tick_then_stops()
    {
        TickSystemHandle? growth = null;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.System("example.farm.harvest", TickStages.World, action: t =>
            {
                if (t.Tick == 2)
                {
                    growth!.Dispose();
                }
            });
            growth = p.System("example.farm.growth", TickStages.Entities);
        });
        using Engine engine = _harness.Start();

        _harness.Tick(engine, 3);

        Assert.Equal(
            ["example.farm.harvest@1", "example.farm.growth@1", "example.farm.harvest@2", "example.farm.growth@2", "example.farm.harvest@3"],
            _harness.Journal);
        Assert.True(growth!.IsRevoked);
    }

    [Fact]
    public void A_system_of_the_other_side_keeps_its_place_but_does_not_run()
    {
        ITickSchedule? schedule = null;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.System("example.farm.ai", TickStages.Entities, new TickSystemOptions { Side = Side.Server });
            p.System("example.farm.animation", TickStages.Entities, new TickSystemOptions { After = ["example.farm.ai"] });
            schedule = p.Schedule;
        });
        using Engine engine = _harness.Start(Side.Client);

        _harness.Tick(engine);

        Assert.Equal(["example.farm.animation@1"], _harness.Journal);
        TickSystemInfo ai = schedule!.GetOrder().SelectMany(s => s.Systems).Single(s => s.Name == "example.farm.ai");
        Assert.Equal(TickSystemState.OtherSide, ai.State);
        Assert.Contains("example.farm.ai (example.farm) [OtherSide]", schedule.FormatOrder(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_distribution_inserts_stages_adds_constraints_and_disables_systems()
    {
        _harness.Config = """
            <Stage name="example.combat" after="Entities" before="Physics" />
            <Constraint system="example.farm.ai" after="example.farm.move" />
            <Disable system="example.farm.debug" />
            <Disable system="example.farm.move" side="server" />
            """;
        ITickSchedule? schedule = null;
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.System("example.farm.ai", TickStages.Entities);
            p.System("example.farm.move", TickStages.Entities);
            p.System("example.farm.debug", TickStages.Entities);
            p.System("example.farm.hits", "example.combat");
            schedule = p.Schedule;
        });
        using Engine engine = _harness.Start(Side.Client);

        TickStageInfo combat = schedule!.GetOrder()[4];
        Assert.Equal("example.combat", combat.Name);
        Assert.Equal("example", combat.ModuleId);
        Assert.Equal(TickSystemState.Disabled, schedule.GetOrder()[3].Systems.Single(s => s.Name == "example.farm.debug").State);

        _harness.Tick(engine);
        Assert.Equal(["example.farm.move@1", "example.farm.ai@1", "example.farm.hits@1"], _harness.Journal);
    }

    [Fact]
    public void An_invalid_configuration_fails_the_start_with_its_positions()
    {
        _harness.Config = """
            <Stage name="example.combat" />
            <Stage name="other.combat" after="Entities" />
            <Constraint system="example.farm.ai" after="example.farm.ghost" />
            <Constraint system="example.farm.ai" before="Physics" />
            <Disable system="example.farm.ai" side="moon" />
            <Disable system="example.farm.ghost" />
            <Shuffle />
            """;
        _harness.Module("example.farm", m => m.OnInitialize = p => p.System("example.farm.ai", TickStages.Entities));
        using Engine engine = _harness.Load();

        EngineStopException stop = Assert.Throws<EngineStopException>(engine.MainContext.Start);

        // The syntax is checked when nobara.game initializes; the names, when it starts.
        Assert.Equal(LifecycleStep.Initialization, stop.Step);
        var exception = Assert.IsType<TickScheduleException>(stop.InnerException);
        Assert.Equal(
            [
                "engine.nobara(2,2): <Stage> needs 'after', 'before', or both.",
                "engine.nobara(6,35): the side of <Disable> is 'client' or 'server', not 'moon'.",
                "engine.nobara(8,2): unknown element <Shuffle>; nobara.game reads <Stage>, <Constraint>, and <Disable>.",
            ],
            exception.Problems);
    }

    [Fact]
    public void Configuration_names_are_checked_against_the_registered_systems()
    {
        _harness.Config = """
            <Stage name="other.combat" after="Entities" />
            <Constraint system="example.farm.ai" after="example.farm.ghost" />
            <Constraint system="example.farm.ai" before="Physics" />
            <Disable system="example.farm.ghost" />
            """;
        _harness.Module("example.farm", m => m.OnInitialize = p => p.System("example.farm.ai", TickStages.Entities));
        using Engine engine = _harness.Load();

        TickScheduleException exception = StartFails(engine);

        Assert.Equal(
            [
                "engine.nobara(2,2): the stage name 'other.combat' must be qualified by the game: 'example.' then lowercase names separated by dots.",
                "engine.nobara(3,2): no module registers a tick system named 'example.farm.ghost' on this side.",
                "engine.nobara(4,2): 'Physics' is a stage, not a system; a constraint orders systems.",
                "engine.nobara(5,2): no module registers a tick system named 'example.farm.ghost' on this side.",
            ],
            exception.Problems);
    }

    [Fact]
    public void A_cycle_through_the_configuration_names_its_position()
    {
        _harness.Config = """<Constraint system="example.farm.a" after="example.farm.b" />""";
        _harness.Module("example.farm", m => m.OnInitialize = p =>
        {
            p.System("example.farm.a", TickStages.Entities);
            p.System("example.farm.b", TickStages.Entities, new TickSystemOptions { After = ["example.farm.a"] });
        });
        using Engine engine = _harness.Load();

        TickScheduleException exception = StartFails(engine);

        Assert.Equal(
            "The tick systems of stage 'Entities' form a cycle: example.farm.a -> example.farm.b -> example.farm.a (constraints of engine.nobara(2,2)).",
            Assert.Single(exception.Problems));
    }
}
