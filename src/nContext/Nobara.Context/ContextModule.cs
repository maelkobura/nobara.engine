// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core;
using Nobara.Core.Loop;

[assembly: ModuleEntry(typeof(Nobara.Context.ContextModule))]

namespace Nobara.Context;

/// <summary>
/// The module that drives the states of the application: it publishes
/// <see cref="IStateStack"/> and <see cref="IWorldSessions"/>, applies the
/// transitions and the session requests at the start of each frame, and hands
/// the stop requests of the context to the active state.
/// </summary>
public sealed class ContextModule : IModule
{
    private StateStack? _states;
    private WorldSessions? _sessions;

    /// <inheritdoc/>
    public void Register(ModuleContext context)
    {
        WorldSessions sessions = new(context);
        StateStack states = new(context, sessions);
        _sessions = sessions;
        _states = states;
        context.Publish<IStateStack>(states);
        context.Publish<IWorldSessions>(sessions);
        context.Loop.SubscribeFrameStart(new FrameStartHandler(states));
        context.InterceptStopRequests(() => states.Raise(new StopRequested()));
    }

    /// <inheritdoc/>
    public void Start(ModuleContext context) => _states!.Start();

    /// <inheritdoc/>
    public void Stop(ModuleContext context)
    {
        _states?.Stop();
        _sessions?.Stop();
    }

    /// <summary>Delivers the global events, applies the transitions and the session requests, and updates the running states.</summary>
    private sealed class FrameStartHandler(StateStack states) : IPhaseHandler
    {
        public void Run(in PhaseTime time) => states.RunFrameStart(time);
    }
}
