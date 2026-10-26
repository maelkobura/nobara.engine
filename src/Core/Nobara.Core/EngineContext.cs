// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Nobara.Core.Logging;

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

    internal EngineContext(Engine engine, Side side)
    {
        Engine = engine;
        Side = side;
        Name = side == Side.Server ? "server" : "client";
        Logger = engine.CreateLogger(Name, "core", _phase);
        Services = new ServiceRegistry(engine.ModuleOfType);

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

    internal ServiceRegistry Services { get; }

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
    public void Start()
    {
        if (State != ContextState.Created)
        {
            throw new InvalidOperationException($"The {Name} context is {State}; it can be started once.");
        }

        State = ContextState.Starting;
        Logger.Info($"Starting the {Name} context with {_modules.Count} modules.");
        try
        {
            RunStep(LifecycleStep.Registration, module =>
            {
                module.Instance = module.Loaded.CreateInstance();
                module.Instance?.Register(module.Context);
                module.Registered = true;
            });
            RunStep(LifecycleStep.Initialization, module =>
            {
                module.InitializeCalled = true;
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
        Logger.Info($"The {Name} context is running.");
    }

    /// <summary>Runs steps 6 and 7 for every active module of the context, in reverse load order.</summary>
    /// <remarks>
    /// Exceptions thrown by modules during these steps are logged and the
    /// shutdown continues. Calling it on a stopped context does nothing.
    /// </remarks>
    public void Stop()
    {
        if (State is ContextState.Stopped or ContextState.Stopping)
        {
            return;
        }

        Logger.Info($"Stopping the {Name} context.");
        Shutdown();
    }

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
        try
        {
            call(module);
            return true;
        }
        catch (Exception exception) when (exception is not EngineStopException)
        {
            HandleFault(module, step, exception);
            return false;
        }
    }

    private void HandleFault(ModuleRuntime called, LifecycleStep step, Exception exception)
    {
        ModuleRuntime culprit = Attribute(exception) ?? called;
        ModuleInfo info = culprit.Info;
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

            if (module.InitializeCalled && !module.Released)
            {
                Quietly(module, LifecycleStep.Release, m => m.Instance?.Release(m.Context));
                module.Released = true;
            }

            Services.Withdraw(module);
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

    // Calls a module during a shutdown or an emergency: exceptions are logged
    // and nothing else happens.
    private static void Quietly(ModuleRuntime module, LifecycleStep step, Action<ModuleRuntime> call)
    {
        try
        {
            call(module);
        }
        catch (Exception exception)
        {
            module.Context.Logger.Error($"Module '{module.Info.Id}' threw during {EngineStopException.Describe(step)}; the exception is ignored.", exception);
        }
    }

    // The module whose code is closest to the throw point.
    private ModuleRuntime? Attribute(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not (TargetInvocationException or TypeInitializationException or AggregateException))
            {
                foreach (StackFrame frame in new StackTrace(current, fNeedFileInfo: false).GetFrames())
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
