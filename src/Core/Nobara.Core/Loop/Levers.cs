// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core.Loop;

/// <summary>
/// The levers on the fixed tick of a context: pause, time scale, and tick
/// rate. Only the module that reserves a fixed phase gets them, through
/// <see cref="ModuleLoop.GetTickControl"/>.
/// </summary>
/// <remarks>
/// Use the levers from the loop thread of the context. A change takes effect
/// at the next frame. The fixed phases of a context share one tick, so the
/// levers act on all of them.
/// </remarks>
public sealed class TickControl
{
    private readonly ContextLoop _loop;

    internal TickControl(ContextLoop loop)
    {
        _loop = loop;
    }

    /// <summary>
    /// Gets or sets whether the fixed phases are suspended. While they are,
    /// no tick runs and no time accumulates; the other phases still run.
    /// </summary>
    public bool Paused
    {
        get => _loop.Paused;
        set => _loop.Paused = value;
    }

    /// <summary>
    /// Gets or sets how fast time accumulates for the tick: 1 is real time, 2
    /// runs twice as many ticks per second. The duration of a tick does not
    /// change.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive finite number.</exception>
    public double TimeScale
    {
        get => _loop.TimeScale;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The time scale is a positive finite number; pause the tick to stop it.");
            }

            _loop.TimeScale = value;
        }
    }

    /// <summary>Gets or sets the number of ticks per real second at a time scale of 1. Changing it changes the duration of a tick.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive finite number.</exception>
    public double TickRate
    {
        get => _loop.TickRate;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The tick rate is a positive finite number of ticks per second.");
            }

            _loop.SetTickRate(value);
        }
    }

    /// <summary>Gets the duration of one tick.</summary>
    public TimeSpan TickDuration => _loop.TickDuration;

    /// <summary>Gets the number of the last tick that ran, or 0.</summary>
    public ulong Tick => _loop.Tick;
}

/// <summary>
/// The lever on contexts: it creates and destroys the server context of an
/// integrated server. Only the module that reserves
/// <see cref="LoopReservations.Contexts"/> gets it, through
/// <see cref="ModuleContext.GetContextControl"/>.
/// </summary>
/// <remarks>
/// This version of the core runs only the main context:
/// <see cref="CreateIntegratedServer"/> is not supported yet.
/// </remarks>
public sealed class ContextControl
{
    private readonly Engine _engine;

    internal ContextControl(Engine engine)
    {
        _engine = engine;
    }

    /// <summary>Gets the context created at start-up, which lives as long as the process.</summary>
    public EngineContext MainContext => _engine.MainContext;

    /// <summary>Gets the server context of the integrated server, or <see langword="null"/> when there is none.</summary>
    public EngineContext? IntegratedServer => null;

    /// <summary>Gets whether this core can create the server context of an integrated server.</summary>
    public bool CanCreateIntegratedServer => false;

    /// <summary>
    /// Creates the server context of an integrated server, next to the main
    /// client context, and starts it on its own loop thread: its modules go
    /// through steps 2 to 4.
    /// </summary>
    /// <returns>The server context.</returns>
    /// <exception cref="NotSupportedException">This version of the core runs only the main context.</exception>
    public EngineContext CreateIntegratedServer() =>
        throw new NotSupportedException("This version of the core runs only the main context; it cannot create an integrated server.");

    /// <summary>Stops and destroys the server context of the integrated server: its modules go through steps 6 and 7.</summary>
    /// <exception cref="InvalidOperationException">There is no integrated server.</exception>
    public void DestroyIntegratedServer() =>
        throw new InvalidOperationException("There is no integrated server to destroy.");
}

/// <summary>
/// The loop of a context, as one module sees it: its phases, the
/// subscriptions of the module, and the levers the module is granted.
/// </summary>
public sealed class ModuleLoop
{
    private readonly ContextLoop _loop;
    private readonly ModuleRuntime _module;
    private readonly EngineContext _context;

    internal ModuleLoop(EngineContext context, ContextLoop loop, ModuleRuntime module)
    {
        _context = context;
        _loop = loop;
        _module = module;
    }

    /// <summary>Gets the phases of this context, in the order they run. Phases of the other side are left out.</summary>
    public IReadOnlyList<LoopPhase> Phases => _loop.Phases;

    /// <summary>Gets the number of the frame that runs, or of the last one, from 1.</summary>
    public ulong Frame => _loop.Frame;

    /// <summary>Gets the number of the last tick that ran, or 0.</summary>
    public ulong Tick => _loop.Tick;

    /// <summary>Gets the duration of one tick.</summary>
    public TimeSpan TickDuration => _loop.TickDuration;

    /// <summary>Finds a phase of this context.</summary>
    /// <param name="name">The name of the phase, such as <c>Render</c>.</param>
    /// <returns>The phase, or <see langword="null"/> when this context has no such phase.</returns>
    public LoopPhase? FindPhase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _loop.Find(name);
    }

    /// <summary>Gets the module that reserves a phase, or <see langword="null"/> when the phase is open to every module.</summary>
    /// <param name="phase">A phase of this context.</param>
    /// <returns>The identifier of the module.</returns>
    public string? ReservedBy(LoopPhase phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        return _loop.ReservedBy(phase)?.Info.Id;
    }

    /// <summary>Subscribes a handler to a phase. Handlers of a phase run in the order they subscribed.</summary>
    /// <param name="phase">The name of the phase.</param>
    /// <param name="handler">The code to run.</param>
    /// <returns>The subscription; dispose it to remove the handler.</returns>
    /// <exception cref="InvalidOperationException">
    /// The step is not <see cref="LifecycleStep.Registration"/>, this context
    /// has no such phase, or another module reserves it.
    /// </exception>
    public LoopSubscription Subscribe(string phase, IPhaseHandler handler)
    {
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(handler);
        RequireRegistration("subscribe to phase '" + phase + "'");
        return _loop.Subscribe(_module, _loop.Find(_module, phase), handler);
    }

    /// <summary>
    /// Subscribes a handler to the start of each frame: after the
    /// synchronization point, before the first phase. It is where the states
    /// of the application change, between two frames.
    /// </summary>
    /// <param name="handler">The code to run. <see cref="PhaseTime.Delta"/> is the time since the previous frame.</param>
    /// <returns>The subscription; dispose it to remove the handler.</returns>
    /// <exception cref="InvalidOperationException">The step is not <see cref="LifecycleStep.Registration"/>.</exception>
    public LoopSubscription SubscribeFrameStart(IPhaseHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RequireRegistration("subscribe to the start of the frame");
        return _loop.Subscribe(_module, null, handler);
    }

    /// <summary>Gets the levers on the fixed tick.</summary>
    /// <returns>The levers, the same instance at each call.</returns>
    /// <exception cref="InvalidOperationException">The module does not reserve a fixed phase.</exception>
    public TickControl GetTickControl() => _loop.GetControl(_module);

    private void RequireRegistration(string action)
    {
        if (_context.Step != LifecycleStep.Registration)
        {
            throw new InvalidOperationException(
                $"Module '{_module.Info.Id}' cannot {action} during {EngineStopException.Describe(_context.Step)}. Modules subscribe to the loop during registration.");
        }
    }
}

/// <summary>
/// Sends the continuations of a module to the synchronization point of its
/// context, so that they run on the loop thread at the start of a frame.
/// </summary>
internal sealed class ModuleSynchronizationContext : SynchronizationContext
{
    private readonly ContextLoop _loop;
    private readonly ModuleRuntime _module;

    public ModuleSynchronizationContext(ContextLoop loop, ModuleRuntime module)
    {
        _loop = loop;
        _module = module;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _loop.Post(_module, d, state);
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("The synchronization point of a context runs callbacks at the start of a frame; post them instead.");

    public override SynchronizationContext CreateCopy() => this;
}
