// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;

[assembly: ModuleEntry(typeof(Nobara.Game.GameModule))]

namespace Nobara.Game;

/// <summary>
/// The module that masters the simulation tick: it reserves the simulation
/// phase, runs its stages and tick systems, keeps game time, and runs the
/// scheduler. It publishes <see cref="ITickSchedule"/>,
/// <see cref="IGameTime"/>, and <see cref="IScheduler"/>.
/// </summary>
public sealed class GameModule : IModule
{
    /// <summary>The identifier of the event module, the optional dependency of nGame.</summary>
    internal const string EventModuleId = "nobara.event";

    private TickSchedule? _schedule;
    private GameTime? _time;
    private Scheduler? _scheduler;
    private GameConfig _config = GameConfig.Empty;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        LoopPhase simulation = context.Loop.Phases.FirstOrDefault(p => p.IsFixed && p.Reservation == LoopReservations.Phase("Simulation"))
            ?? throw new InvalidOperationException(
                $"Module '{context.Module.Id}' runs the simulation phase, and the loop has none; declare a fixed phase named 'Simulation' in <Loop>.");

        _time = new GameTime(context.Loop.GetTickControl());
        _scheduler = new Scheduler(_time);
        _schedule = new TickSchedule(context);
        _schedule.AddBuiltIn(TickStages.SchedulerSystem, TickStages.Schedule, new SchedulerSystem(_scheduler));

        context.Publish<ITickSchedule>(_schedule);
        context.Publish<IGameTime>(_time);
        context.Publish<IScheduler>(_scheduler);
        context.Loop.Subscribe(simulation.Name, new SimulationHandler(_time, _schedule));
        context.Loop.SubscribeFrameStart(new FrameStartHandler(_time, _scheduler));
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _config = GameConfig.Read(context.ModuleConfig);
        if (context.IsModuleActive(EventModuleId))
        {
            _time!.Notifications = EventNotifications.Create(context);
        }
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context)
    {
        _schedule!.Resolve(_config, context.Distribution.Id, context.Side);
        IReadOnlyList<TickStageInfo> order = _schedule.GetOrder();
        context.Logger.Info($"The simulation tick runs {order.Count} stages and {order.Sum(s => s.Systems.Count(y => y.State == TickSystemState.Runs))} systems.");
        context.Logger.Debug("Order of the simulation tick:" + Environment.NewLine + _schedule.FormatOrder());
    }

    /// <inheritdoc/>
    public void OnDependencyDisabled(ModuleContext context, ModuleInfo dependency)
    {
        if (dependency.Id == EventModuleId && _time is not null)
        {
            _time.Notifications = null;
        }
    }

    /// <summary>Runs one tick: the clocks move, then each stage runs its systems.</summary>
    private sealed class SimulationHandler(GameTime time, TickSchedule schedule) : IPhaseHandler
    {
        [HotPath]
        public void Run(in PhaseTime phase)
        {
            time.BeginTick(phase.Tick);
            schedule.RunTick(new TickTime(phase.Tick, phase.Delta));
        }
    }

    /// <summary>Counts real time, then runs the tasks of real time that are due, paused or not.</summary>
    private sealed class FrameStartHandler(GameTime time, Scheduler scheduler) : IPhaseHandler
    {
        [HotPath]
        public void Run(in PhaseTime phase)
        {
            time.AddRealTime(phase.Delta);
            scheduler.RunRealTimeTasks();
        }
    }

    /// <summary>The tick system that runs the tasks of game time that are due.</summary>
    private sealed class SchedulerSystem(Scheduler scheduler) : ITickSystem
    {
        public void Run(in TickTime time) => scheduler.RunGameTasks();
    }
}
