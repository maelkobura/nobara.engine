// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using Nobara.Core.Profiling;
using Nobara.Core.Resources;

namespace Nobara.Core;

/// <summary>Where a context is in its life.</summary>
public enum ContextState
{
    /// <summary>Created; no module code has run in it.</summary>
    Created,

    /// <summary>Running steps 2 to 4.</summary>
    Starting,

    /// <summary>Started: every active module went through steps 2 to 4.</summary>
    Running,

    /// <summary>Running steps 6 and 7.</summary>
    Stopping,

    /// <summary>Stopped: every module went through steps 6 and 7.</summary>
    Stopped,
}

/// <summary>
/// Everything that belongs to one side of the game in a process: an instance
/// of each module of that side, a service registry, and the lifecycle of
/// those modules.
/// </summary>
/// <remarks>
/// <para>
/// Contexts are isolated: a module never resolves a service of another
/// context. The assemblies are shared, and a module that runs on both sides
/// has one instance per context.
/// </para>
/// <para>
/// The core intercepts every exception that a module throws when the core
/// calls it, and applies the error policy of the module the exception comes
/// from: the module whose code is closest to the throw point in the stack
/// trace, which is the called module or a module whose service it called.
/// </para>
/// </remarks>
public sealed class EngineContext
{
    private readonly List<ModuleRuntime> _modules;
    private readonly Dictionary<string, ModuleRuntime> _byId;
    private readonly Dictionary<Assembly, ModuleRuntime> _byAssembly;
    private readonly PhaseTracker _phase = new();
    private readonly ContextLoop _loop;
    private volatile bool _stopRequested;
    private volatile ModuleRuntime? _stopInterceptor;
    private Action? _stopHandler;
    private int _stopRequestPending;
    private bool _inFrame;

    internal EngineContext(Engine engine, Side side)
    {
        Engine = engine;
        Side = side;
        Name = side == Side.Server ? "server" : "client";
        Logger = engine.CreateLogger(Name, "core", _phase);
        Services = new ServiceRegistry(engine.ModuleOfType);
        Resources = new ContextResources(engine.Resources, engine.DistributionData.ProtectedNamespaces);
        _loop = new ContextLoop(this, engine.LoopDeclaration, engine.Time, engine.MaxFrameTime);

        _modules = [];
        foreach (Engine.LoadedEntry entry in engine.Loaded.Where(e => e.Info.RunsOn(side)))
        {
            _modules.Add(new ModuleRuntime(entry.Info, entry.Module, _modules.Count));
        }

        _byId = _modules.ToDictionary(m => m.Info.Id, StringComparer.Ordinal);
        _byAssembly = [];
        foreach (IGrouping<Assembly, ModuleRuntime> group in _modules.Where(m => m.Loaded.Assembly is not null).GroupBy(m => m.Loaded.Assembly!))
        {
            // An assembly that holds several modules cannot tell them apart.
            if (group.Count() == 1)
            {
                _byAssembly.Add(group.Key, group.First());
            }
        }

        foreach (ModuleRuntime module in _modules)
        {
            module.Jobs = new JobOwner(this, module);
            module.SynchronizationMarker = engine.Profiler.CreateMarker(Name, module.Info.Id, ProfileKind.Synchronization, "Synchronization");
            module.Context = new ModuleContext(this, module, engine.CreateLogger(Name, module.Info.Id, _phase));
        }

        Modules = _modules.Select(m => m.Info).ToArray();
    }

    /// <summary>Gets the engine that hosts the context.</summary>
    public Engine Engine { get; }

    /// <summary>Gets the side of the context: <see cref="Side.Client"/> or <see cref="Side.Server"/>.</summary>
    public Side Side { get; }

    /// <summary>Gets the name of the context in logs: <c>client</c> or <c>server</c>.</summary>
    public string Name { get; }

    /// <summary>Gets where the context is in its life.</summary>
    public ContextState State { get; private set; }

    /// <summary>Gets the lifecycle step that the context is running.</summary>
    public LifecycleStep Step { get; private set; } = LifecycleStep.Loading;

    /// <summary>Gets the modules of this side, in load order, disabled ones included.</summary>
    public IReadOnlyList<ModuleInfo> Modules { get; }

    /// <summary>Gets the logger of the core in this context.</summary>
    public Logger Logger { get; }

    /// <summary>Gets the phases of the loop of this context, in the order they run.</summary>
    public IReadOnlyList<LoopPhase> Phases => _loop.Phases;

    /// <summary>Gets the number of the last frame that ran, or 0.</summary>
    public ulong Frame => _loop.Frame;

    /// <summary>Gets the number of the last tick that ran, or 0.</summary>
    public ulong Tick => _loop.Tick;

    /// <summary>Gets whether the context stops after the frame that runs.</summary>
    public bool IsStopRequested => _stopRequested;

    internal ServiceRegistry Services { get; }

    internal ContextResources Resources { get; }

    internal ContextLoop Loop => _loop;

    /// <summary>Tells whether a module is active in this context: present, running on this side, and not disabled.</summary>
    /// <param name="moduleId">The identifier of a module.</param>
    /// <returns><see langword="true"/> when the module is active.</returns>
    public bool IsModuleActive(string moduleId) => _byId.TryGetValue(moduleId, out ModuleRuntime? module) && !module.Disabled;

    /// <summary>Runs steps 2 to 4 for every module of the context, in load order.</summary>
    /// <remarks>
    /// Each step runs for every module before the next step starts. A module
    /// whose policy is <see cref="ErrorPolicy.Disable"/> and that throws is
    /// disabled, with the modules that require it, and the start continues.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The context was already started.</exception>
    /// <exception cref="EngineStopException">
    /// A module whose policy is <see cref="ErrorPolicy.Stop"/> threw. The
    /// modules that had started are stopped and released before the exception
    /// is thrown.
    /// </exception>
    /// <exception cref="ResourceLoadException">
    /// After registration, the modules that read a free property disagree on
    /// its type, or a resource has a value of another type. The context is
    /// stopped before the exception is thrown.
    /// </exception>
    public void Start()
    {
        if (State != ContextState.Created)
        {
            throw new InvalidOperationException($"The {Name} context is {State}; it can be started once.");
        }

        State = ContextState.Starting;
        Logger.Info($"Starting the {Name} context with {_modules.Count} modules.");
        _loop.FindReservations(_modules);
        try
        {
            RunStep(LifecycleStep.Registration, module =>
            {
                module.Instance = module.Loaded.CreateInstance();
                module.Instance?.Register(module.Context);
                module.Registered = true;
            });
            CheckProperties();
            RunStep(LifecycleStep.Initialization, module =>
            {
                module.InitializeCalled = true;

                // The resources of the module are decoded before it initializes,
                // so its dependents find them decoded.
                Resources.DecodeOwned(module);
                module.Instance?.Initialize(module.Context);
            });
            RunStep(LifecycleStep.Start, module =>
            {
                module.Instance?.Start(module.Context);
                module.Started = true;
            });
        }
        catch (EngineStopException)
        {
            Shutdown();
            throw;
        }

        Step = LifecycleStep.Execution;
        _phase.Current = string.Empty;
        State = ContextState.Running;
        _loop.Begin();
        Logger.Info($"The {Name} context is running: {string.Join(", ", _loop.Phases.Select(p => p.IsFixed ? p.Name + $" ({_loop.TickRate:0.##} Hz)" : p.Name))}.");
    }

    /// <summary>
    /// Runs one frame: the synchronization point, the handlers of the start of
    /// the frame, then each phase in order. A fixed phase runs once per tick
    /// due, zero or more times.
    /// </summary>
    /// <remarks>
    /// Call it from the loop thread of the context. A module whose policy is
    /// <see cref="ErrorPolicy.Disable"/> and that throws is disabled, with the
    /// modules that require it, and the frame goes on.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The context is not running, or a frame is already running.</exception>
    /// <exception cref="EngineStopException">
    /// A module whose policy is <see cref="ErrorPolicy.Stop"/> threw. The
    /// context is stopped before the exception is thrown.
    /// </exception>
    public void RunFrame()
    {
        if (State != ContextState.Running)
        {
            throw new InvalidOperationException($"The {Name} context is {State}; frames run while it is Running.");
        }

        if (_inFrame)
        {
            throw new InvalidOperationException($"A frame of the {Name} context is already running; frames do not nest.");
        }

        _inFrame = true;
        try
        {
            _loop.RunFrame();
        }
        catch (EngineStopException)
        {
            _inFrame = false;
            Shutdown();
            throw;
        }
        finally
        {
            _inFrame = false;
        }
    }

    /// <summary>
    /// Starts the context if needed, runs frames on the calling thread until a
    /// stop is requested, then stops the context.
    /// </summary>
    /// <param name="cancellation">Requests a stop when it is canceled.</param>
    /// <exception cref="InvalidOperationException">The context is stopping or stopped.</exception>
    /// <exception cref="EngineStopException">A module whose policy is <see cref="ErrorPolicy.Stop"/> threw. The context is stopped.</exception>
    public void Run(CancellationToken cancellation = default)
    {
        if (State is ContextState.Stopping or ContextState.Stopped)
        {
            throw new InvalidOperationException($"The {Name} context is {State}; a stopped context does not run again.");
        }

        if (State == ContextState.Created)
        {
            Start();
        }

        using CancellationTokenRegistration registration = cancellation.Register(RequestStop);
        try
        {
            while (!_stopRequested && State == ContextState.Running)
            {
                RunFrame();
                if (Engine.Pacing == FramePacing.Tick && !_stopRequested)
                {
                    _loop.WaitForNextTick(cancellation);
                }
            }
        }
        finally
        {
            Stop();
        }
    }

    /// <summary>
    /// Asks <see cref="Run"/> to stop the context after the frame that runs.
    /// It can be called from any thread. Ctrl+C and the cancellation of
    /// <see cref="Run"/> call it.
    /// </summary>
    /// <remarks>
    /// When a module intercepts the stop requests of the context
    /// (<see cref="ModuleContext.InterceptStopRequests"/>), the request goes to
    /// that module instead, on the loop thread at the start of the next frame,
    /// and the module decides when the context stops. Requests made before it
    /// receives one are merged into it.
    /// </remarks>
    public void RequestStop()
    {
        ModuleRuntime? interceptor = _stopInterceptor;
        if (interceptor is null || interceptor.Disabled || State is ContextState.Stopping or ContextState.Stopped)
        {
            _stopRequested = true;
            return;
        }

        if (Interlocked.Exchange(ref _stopRequestPending, 1) == 0)
        {
            _loop.Post(interceptor, static context => ((EngineContext)context!).DeliverStopRequest(), this);
        }
    }

    /// <summary>Stops the context after the frame that runs, whoever intercepts stop requests.</summary>
    internal void StopAfterFrame() => _stopRequested = true;

    internal void InterceptStopRequests(ModuleRuntime module, Action handler)
    {
        if (_stopInterceptor is not null)
        {
            throw new InvalidOperationException($"Module '{_stopInterceptor.Info.Id}' already intercepts the stop requests of the {Name} context.");
        }

        _stopHandler = handler;
        _stopInterceptor = module;
    }

    private void DeliverStopRequest()
    {
        Volatile.Write(ref _stopRequestPending, 0);
        Logger.Info($"A stop of the {Name} context is requested; module '{_stopInterceptor!.Info.Id}' decides when it stops.");
        _stopHandler!();
    }

    /// <summary>Runs steps 6 and 7 for every active module of the context, in reverse load order.</summary>
    /// <remarks>
    /// Exceptions thrown by modules during these steps are logged and the
    /// shutdown continues. Calling it on a stopped context does nothing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A frame is running: call <see cref="RequestStop"/> instead.</exception>
    public void Stop()
    {
        if (State is ContextState.Stopped or ContextState.Stopping)
        {
            return;
        }

        if (_inFrame)
        {
            throw new InvalidOperationException($"A frame of the {Name} context is running; call RequestStop to stop the context after it.");
        }

        Logger.Info($"Stopping the {Name} context.");
        Shutdown();
    }

    internal void EnterPhase(string phase) => _phase.Current = phase;

    /// <summary>
    /// Takes the resources reloaded since the last frame, at the
    /// synchronization point, and hands each changed type to the module that
    /// defines it.
    /// </summary>
    internal void SynchronizeResources()
    {
        IReadOnlyList<ResourceGeneration> generations = Engine.ResourceGenerationsAfter(Resources.Version);
        foreach (ResourceGeneration generation in generations)
        {
            Resources.Version = generation.Version;
            List<ResourceProblem> problems = Resources.CheckProperties(generation.Catalog);
            if (problems.Count > 0)
            {
                Logger.Error($"The resources of {string.Join(", ", generation.Modules)} were not reloaded in the {Name} context: {new ResourceLoadException(problems).Message}");
                continue;
            }

            Resources.Catalog = generation.Catalog;
            if (generation.Changes.Count == 0)
            {
                Logger.Info($"Reloaded the resources of {string.Join(", ", generation.Modules)}: nothing changed.");
            }

            foreach (TypeChange change in generation.Changes)
            {
                ApplyReload(change);
            }
        }
    }

    private void ApplyReload(TypeChange change)
    {
        string summary = $"{change.Added.Count} added, {change.Changed.Count} changed, {change.Removed.Count} removed, {change.ChangedTags.Count} tags changed";
        if (Resources.ReloadHandler(change.Type) is not { } entry)
        {
            string owner = _byId.ContainsKey(change.OwnerId) ? $"module '{change.OwnerId}' does not reload them" : $"module '{change.OwnerId}' does not run in this context";
            Logger.Warning($"Resources of type '{change.Type}' changed ({summary}), but {owner}: restart the context to apply the change.");
            return;
        }

        var reload = new ResourceReload(change.Type, change.Added, change.Changed, change.Removed, change.ChangedTags);
        if (!Invoke(entry.Owner, LifecycleStep.Execution, module => entry.Handler(reload)))
        {
            return;
        }

        Logger.Info($"Reloaded resources of type '{change.Type}': {summary}.");
        foreach ((string objectId, string reason) in reload.Refusals)
        {
            string what = objectId.Length > 0 ? $"The change of '{objectId}'" : $"The change of the resources of type '{change.Type}'";
            entry.Owner.Context.Logger.Warning($"{what} is not applied while the game runs: {reason}");
        }
    }

    // Applies the policy of the module that threw in the loop. The module is
    // null for a callback posted without one.
    internal void HandleLoopFault(ModuleRuntime? called, Exception exception)
    {
        ModuleRuntime? module = called ?? Attribute(exception);
        if (module is null)
        {
            Logger.Error("A callback of the synchronization point threw; no module is charged with it.", exception);
            return;
        }

        HandleFault(module, LifecycleStep.Execution, exception);
    }

    // Applies the policy of the module charged with an exception that a
    // module caught while it called code of another module.
    internal void ReportFault(ModuleRuntime called, Exception exception) => HandleFault(called, Step, exception, reported: true);

    internal void AddEmergencyHook(ModuleRuntime module, Action<EmergencyStop> hook) => module.EmergencyHooks.Add(hook);

    internal void VerifyStates(IEnumerable<RegisteredState> registered)
    {
        var differences = new List<string>();
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ModuleRuntime module in _modules.Where(m => !m.Disabled))
        {
            foreach (StateDeclaration state in module.Info.States.Where(s => (s.Side & Side) != Side.None))
            {
                declared[state.StateId] = module.Info.Id;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RegisteredState state in registered)
        {
            seen.Add(state.StateId);
            if (!declared.TryGetValue(state.StateId, out string? owner))
            {
                differences.Add($"State '{state.StateId}' is registered by module '{state.ModuleId}', but no active module declares it on the {Name} side.");
            }
            else if (!string.Equals(owner, state.ModuleId, StringComparison.Ordinal))
            {
                differences.Add($"State '{state.StateId}' is registered by module '{state.ModuleId}', but module '{owner}' declares it.");
            }
        }

        foreach (KeyValuePair<string, string> state in declared.Where(d => !seen.Contains(d.Key)))
        {
            differences.Add($"State '{state.Key}' is declared by module '{state.Value}' and not registered on the {Name} side.");
        }

        if (differences.Count > 0)
        {
            throw new StateMismatchException(differences);
        }
    }

    // Free properties are checked once every module has declared those it reads.
    private void CheckProperties()
    {
        List<ResourceProblem> problems = Resources.CheckProperties();
        if (problems.Count == 0)
        {
            return;
        }

        var exception = new ResourceLoadException(problems);
        Logger.Critical(exception.Message);
        Shutdown();
        throw exception;
    }

    private void RunStep(LifecycleStep step, Action<ModuleRuntime> call)
    {
        Step = step;
        _phase.Current = step.ToString();
        Logger.Info($"{step}: {_modules.Count(m => !m.Disabled)} modules.");
        foreach (ModuleRuntime module in _modules)
        {
            if (!module.Disabled)
            {
                Logger.Debug($"{step}: {module.Info.Id}.");
                Invoke(module, step, call);
            }
        }
    }

    // Calls a module and applies the error policy if it throws. Returns
    // whether the call completed.
    private bool Invoke(ModuleRuntime module, LifecycleStep step, Action<ModuleRuntime> call)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(module.Context.SynchronizationContext);
            call(module);
            return true;
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            HandleFault(module, step, exception);
            return false;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private void HandleFault(ModuleRuntime called, LifecycleStep step, Exception exception, bool reported = false)
    {
        ModuleRuntime culprit = Attribute(exception, reported) ?? called;
        ModuleInfo info = culprit.Info;
        if (culprit.Disabled)
        {
            // A late failure of a module already disabled, such as one of its jobs.
            culprit.Context.Logger.Error($"Module '{info.Id}' threw during {EngineStopException.Describe(step)} after it was disabled; the exception is ignored.", exception);
            return;
        }

        string through = culprit == called ? string.Empty : $" (called by '{called.Info.Id}')";
        culprit.Context.Logger.Error(
            $"Module '{info.Id}'{through} threw during {EngineStopException.Describe(step)}; its policy is {info.ErrorPolicy}.", exception);

        if (info.ErrorPolicy == ErrorPolicy.Log)
        {
            return;
        }

        List<ModuleRuntime> cascade = info.ErrorPolicy == ErrorPolicy.Disable ? Cascade(culprit) : [];
        ModuleRuntime? stopper = cascade.FirstOrDefault(m => m.Info.ErrorPolicy == ErrorPolicy.Stop);
        if (info.ErrorPolicy == ErrorPolicy.Stop || stopper is not null)
        {
            if (stopper is not null)
            {
                Logger.Error($"Disabling '{info.Id}' would disable '{stopper.Info.Id}', whose policy is Stop.");
            }

            EmergencyStop(culprit, step, exception);
        }

        Disable(culprit, cascade);
    }

    // The module and every active module that requires it, directly or not,
    // in reverse load order.
    private List<ModuleRuntime> Cascade(ModuleRuntime root)
    {
        var set = new HashSet<ModuleRuntime> { root };
        foreach (ModuleRuntime module in _modules.Where(m => m.Order > root.Order && !m.Disabled))
        {
            if (set.Any(s => module.DependsOn(s.Info.Id, requiredOnly: true)))
            {
                set.Add(module);
            }
        }

        return set.OrderByDescending(m => m.Order).ToList();
    }

    private void Disable(ModuleRuntime culprit, List<ModuleRuntime> cascade)
    {
        foreach (ModuleRuntime module in cascade)
        {
            module.Disabled = true;
            if (module.Started)
            {
                Quietly(module, LifecycleStep.Stop, m => m.Instance?.Stop(m.Context));
            }

            // The jobs of the module are done before it frees what they use.
            WaitForJobs(module);
            if (module.InitializeCalled && !module.Released)
            {
                Quietly(module, LifecycleStep.Release, m => m.Instance?.Release(m.Context));
                module.Released = true;
            }

            Services.Withdraw(module);
            Resources.Withdraw(module);
            Engine.Settings.RemoveListeners(module);
            _loop.Changed();
            string reason = module == culprit ? "after an error" : $"because it requires '{culprit.Info.Id}'";
            module.Context.Logger.Warning($"Module '{module.Info.Id}' is disabled {reason}.");
        }

        // Optional dependents continue without the disabled modules.
        foreach (ModuleRuntime module in cascade)
        {
            foreach (ModuleRuntime dependent in _modules.Where(m => !m.Disabled && m.Registered && m.DependsOn(module.Info.Id, requiredOnly: false)))
            {
                Invoke(dependent, Step, d => d.Instance?.OnDependencyDisabled(d.Context, module.Info));
            }
        }
    }

    private void EmergencyStop(ModuleRuntime culprit, LifecycleStep step, Exception exception)
    {
        var stop = new EmergencyStop(culprit.Info.Id, step, Side, exception);
        foreach (ModuleRuntime module in _modules.Where(m => !m.Disabled))
        {
            foreach (Action<EmergencyStop> hook in module.EmergencyHooks)
            {
                Quietly(module, step, _ => hook(stop));
            }
        }

        string report = WriteCrashReport(culprit, step, exception);
        Logger.Critical($"The {Name} context stops: module '{culprit.Info.Id}' threw during {EngineStopException.Describe(step)}. Crash report: {(report.Length > 0 ? report : "not written")}.", exception);
        throw new EngineStopException(culprit.Info.Id, step, Side, report, exception);
    }

    private void Shutdown()
    {
        _loop.End();
        State = ContextState.Stopping;
        Step = LifecycleStep.Stop;
        _phase.Current = Step.ToString();
        Logger.Info($"{Step}: {_modules.Count(m => m.Started && !m.Disabled)} modules.");
        foreach (ModuleRuntime module in Enumerable.Reverse(_modules).Where(m => m.Started && !m.Disabled))
        {
            Logger.Debug($"Stop: {module.Info.Id}.");
            Quietly(module, LifecycleStep.Stop, m => m.Instance?.Stop(m.Context));
            module.Started = false;
        }

        // The settings are shared by the process: changes no longer reach the modules of this context.
        foreach (ModuleRuntime module in _modules)
        {
            Engine.Settings.RemoveListeners(module);
        }

        // Jobs are done before any module frees what they use.
        foreach (ModuleRuntime module in Enumerable.Reverse(_modules))
        {
            WaitForJobs(module);
        }

        foreach (JobFailedException failure in _loop.DiscardPosted().OfType<JobFault>().Select(f => f.ToException()))
        {
            Logger.Error($"{failure.Message} The context stops; the exception is ignored.", failure.InnerException);
        }

        Step = LifecycleStep.Release;
        _phase.Current = Step.ToString();
        Logger.Info($"{Step}: {_modules.Count(m => m.InitializeCalled && !m.Released && !m.Disabled)} modules.");
        foreach (ModuleRuntime module in Enumerable.Reverse(_modules).Where(m => m.InitializeCalled && !m.Released && !m.Disabled))
        {
            Logger.Debug($"Release: {module.Info.Id}.");
            Quietly(module, LifecycleStep.Release, m => m.Instance?.Release(m.Context));
            module.Released = true;
        }

        _phase.Current = string.Empty;
        State = ContextState.Stopped;
        Logger.Info($"The {Name} context is stopped.");
    }

    // Closes the jobs of a module: it schedules no more, and those in flight
    // are done when this returns. Their failures from now on are logged.
    private void WaitForJobs(ModuleRuntime module)
    {
        module.Jobs.Closed = true;
        Engine.Jobs.WaitFor(module.Jobs);
    }

    // Calls a module during a shutdown or an emergency: exceptions are logged
    // and nothing else happens.
    private static void Quietly(ModuleRuntime module, LifecycleStep step, Action<ModuleRuntime> call)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(module.Context.SynchronizationContext);
            call(module);
        }
        catch (Exception exception)
        {
            module.Context.Logger.Error($"Module '{module.Info.Id}' threw during {EngineStopException.Describe(step)}; the exception is ignored.", exception);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // The module whose code is closest to the throw point. For an exception
    // that a module caught and reported, the last frame is the method that
    // caught it, which is not where the error comes from.
    private ModuleRuntime? Attribute(Exception exception, bool reported = false)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not (TargetInvocationException or TypeInitializationException or AggregateException or JobFailedException))
            {
                StackFrame[] frames = new StackTrace(current, fNeedFileInfo: false).GetFrames();
                int count = reported && current == exception ? Math.Max(0, frames.Length - 1) : frames.Length;
                foreach (StackFrame frame in frames.AsSpan(0, count))
                {
                    Assembly? assembly = frame.GetMethod()?.DeclaringType?.Assembly;
                    if (assembly is not null && _byAssembly.TryGetValue(assembly, out ModuleRuntime? module) && !module.Disabled)
                    {
                        return module;
                    }
                }

                return null;
            }
        }

        return null;
    }

    private string WriteCrashReport(ModuleRuntime culprit, LifecycleStep step, Exception exception)
    {
        try
        {
            string folder = Engine.Paths.Logs;
            Directory.CreateDirectory(folder);
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, $"crash-{stamp}-{Name}.txt");
            var text = new StringBuilder()
                .AppendLine("Crash report")
                .AppendLine(CultureInfo.InvariantCulture, $"Time (UTC): {DateTime.UtcNow:O}")
                .AppendLine(CultureInfo.InvariantCulture, $"Game: {Engine.Distribution.Name} {Engine.Distribution.Version}")
                .AppendLine(CultureInfo.InvariantCulture, $"Context: {Name}")
                .AppendLine(CultureInfo.InvariantCulture, $"Module: {culprit.Info.Id} {culprit.Info.Version}")
                .AppendLine(CultureInfo.InvariantCulture, $"Step: {step}")
                .AppendLine()
                .AppendLine("Modules:");
            foreach (ModuleRuntime module in _modules)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {module.Info.Id} {module.Info.Version}{(module.Disabled ? " (disabled)" : string.Empty)}");
            }

            text.AppendLine().AppendLine("Exception:").AppendLine(exception.ToString());
            File.WriteAllText(path, text.ToString());
            return path;
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            Logger.Error("The crash report cannot be written.", writeError);
            return string.Empty;
        }
    }
}
