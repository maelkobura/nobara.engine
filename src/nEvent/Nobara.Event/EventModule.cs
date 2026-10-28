// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;

[assembly: ModuleEntry(typeof(Nobara.Event.EventModule))]

namespace Nobara.Event;

/// <summary>
/// The module that gives each context its event bus: it publishes
/// <see cref="IEventBus"/>, hands the deferred events out after the
/// simulation, and removes the handlers of the modules when the context stops.
/// </summary>
public sealed class EventModule : IModule
{
    private EventBus? _bus;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        _bus = new EventBus();
        context.Publish<IEventBus>(_bus);

        // Synchronous emissions are checked against the thread that runs the frames.
        _bus.BindToCurrentThread();
        context.Loop.SubscribeFrameStart(new BindThread(_bus));

        var phases = new List<DeferredPhase.Candidate>();
        foreach (LoopPhase phase in context.Loop.Phases)
        {
            phases.Add(new DeferredPhase.Candidate(phase.Name, phase.IsFixed, context.Loop.ReservedBy(phase) is not null));
        }

        var distribution = new DistributeDeferred(_bus);
        int chosen = DeferredPhase.Choose(phases);
        if (chosen < 0)
        {
            context.Loop.SubscribeFrameStart(distribution);
            context.Logger.Debug("Deferred events are handed out at the start of the next frame: no open phase follows the simulation.");
        }
        else
        {
            context.Loop.Subscribe(phases[chosen].Name, distribution);
            context.Logger.Debug($"Deferred events are handed out in phase '{phases[chosen].Name}'.");
        }
    }

    /// <inheritdoc/>
    public void Initialize(ModuleContext context) => _bus?.BindToCurrentThread();

    /// <inheritdoc/>
    public void Start(ModuleContext context) => _bus?.BindToCurrentThread();

    /// <inheritdoc/>
    public void Stop(ModuleContext context)
    {
        _bus?.BindToCurrentThread();

        // The modules that use the bus are stopped already: nothing is left to receive an event.
        _bus?.RevokeAll();
    }

    private sealed class BindThread(EventBus bus) : IPhaseHandler
    {
        public void Run(in PhaseTime time) => bus.BindToCurrentThread();
    }

    private sealed class DistributeDeferred(EventBus bus) : IPhaseHandler
    {
        public void Run(in PhaseTime time) => bus.DistributeDeferred();
    }
}
