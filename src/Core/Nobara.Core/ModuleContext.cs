// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Configuration;
using Nobara.Core.Jobs;
using Nobara.Core.Loading;
using Nobara.Core.Logging;
using Nobara.Core.Loop;
using Nobara.Core.Profiling;
using Nobara.Core.Resources;

namespace Nobara.Core;

/// <summary>
/// A module in one context: what the core gives the module at each step of
/// its lifecycle. There is one per module and per context.
/// </summary>
/// <remarks>
/// Call it on the loop thread of the context, except <see cref="Post(SendOrPostCallback, object?)"/>,
/// <see cref="SynchronizationContext"/>, and <see cref="RequestStop"/>. The
/// lifecycle steps limit what is allowed: services are published and phases
/// subscribed to during registration, and services are resolved from
/// initialization on.
/// </remarks>
public sealed class ModuleContext
{
    private readonly EngineContext _context;
    private readonly ModuleRuntime _runtime;

    internal ModuleContext(EngineContext context, ModuleRuntime runtime, Logger logger)
    {
        _context = context;
        _runtime = runtime;
        Logger = logger;
        Loop = new ModuleLoop(context, context.Loop, runtime);
        SynchronizationContext = new ModuleSynchronizationContext(context.Loop, runtime);
        Settings = new ModuleSettings(context.Engine.Settings, runtime, context.Side);
        Resources = new ModuleResources(context, context.Resources, runtime);
        ModuleConfig = context.Engine.DistributionData.ModuleConfigs.GetValueOrDefault(runtime.Info.Id);
        Jobs = new ModuleJobs(context.Engine.Jobs, runtime.Jobs);
        Profiler = new ModuleProfiler(context.Engine.Profiler, context.Name, runtime.Info.Id);
    }

    /// <summary>Gets the module.</summary>
    public ModuleInfo Module => _runtime.Info;

    /// <summary>Gets the side of the context: <see cref="Side.Client"/> or <see cref="Side.Server"/>.</summary>
    public Side Side => _context.Side;

    /// <summary>Gets the lifecycle step that the context is running.</summary>
    public LifecycleStep Step => _context.Step;

    /// <summary>Gets the logger of the module in this context.</summary>
    public Logger Logger { get; }

    /// <summary>Gets the data folders of the game.</summary>
    public EnginePaths Paths => _context.Engine.Paths;

    /// <summary>Gets the identity of the game.</summary>
    public DistributionInfo Distribution => _context.Engine.Distribution;

    /// <summary>Gets the loop of the context: its phases, and the subscriptions and levers of the module.</summary>
    public ModuleLoop Loop { get; }

    /// <summary>Gets the configuration keys of the module and of its declared dependencies.</summary>
    public ModuleSettings Settings { get; }

    /// <summary>Gets the resources: decoders, decoded objects, tags, and free properties.</summary>
    public ModuleResources Resources { get; }

    /// <summary>Gets the job system: the jobs of the module run on the worker threads of the process.</summary>
    public ModuleJobs Jobs { get; }

    /// <summary>Gets the profiler, to measure scopes of the module.</summary>
    public ModuleProfiler Profiler { get; }

    /// <summary>
    /// Gets the <c>&lt;ModuleConfig module="..."&gt;</c> block of
    /// <c>engine.nobara</c> that targets the module, in the generic
    /// representation, or <see langword="null"/> when the distribution gives
    /// none.
    /// </summary>
    /// <remarks>
    /// The compiler does not know the content of the block: the module checks
    /// it, usually during initialization, and names the position of an error
    /// with <see cref="GenericElement.Source"/>.
    /// </remarks>
    public GenericElement? ModuleConfig { get; }

    /// <summary>
    /// Gets the synchronization point of the context for this module, as a
    /// synchronization context: what it receives runs on the loop thread, at
    /// the start of the next frame, and an exception it throws is charged to
    /// this module.
    /// </summary>
    /// <remarks>
    /// The core makes it the current synchronization context while it calls
    /// the lifecycle methods of the module and the callbacks the module posts.
    /// An <see langword="await"/> there resumes on the loop thread, however
    /// many times the method awaits.
    /// </remarks>
    /// <example>
    /// <code>
    /// public void Initialize(ModuleContext context) => _loading = LoadAsync();
    ///
    /// private async Task LoadAsync()
    /// {
    ///     byte[] data = await File.ReadAllBytesAsync(_path); // resumes on the loop thread
    /// }
    /// </code>
    /// </example>
    public SynchronizationContext SynchronizationContext { get; }

    /// <summary>
    /// Queues a callback on the synchronization point of the context: it runs
    /// on the loop thread at the start of the next frame, before the first
    /// phase. It can be called from any thread.
    /// </summary>
    /// <param name="callback">The code to run; an exception it throws is charged to this module.</param>
    /// <param name="state">The argument of <paramref name="callback"/>.</param>
    public void Post(SendOrPostCallback callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _context.Loop.Post(_runtime, callback, state);
    }

    /// <summary>
    /// Asks the context to stop after the frame that runs. For the main
    /// context, the game ends. It can be called from any thread.
    /// </summary>
    public void RequestStop()
    {
        Logger.Info($"Module '{Module.Id}' requests the {_context.Name} context to stop.");
        _context.RequestStop();
    }

    /// <summary>Gets the lever that creates and destroys contexts.</summary>
    /// <returns>The lever.</returns>
    /// <exception cref="InvalidOperationException">The module does not reserve <see cref="LoopReservations.Contexts"/>, or is disabled.</exception>
    public ContextControl GetContextControl()
    {
        if (_runtime.Disabled)
        {
            throw new InvalidOperationException($"Module '{Module.Id}' is disabled; it cannot create or destroy contexts.");
        }

        if (!Module.Reservations.Contains(LoopReservations.Contexts, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module '{Module.Id}' cannot create or destroy contexts: only the module that reserves '{LoopReservations.Contexts}' can.");
        }

        return _context.Engine.ContextControl;
    }

    /// <summary>Publishes a service of the module in this context.</summary>
    /// <typeparam name="TService">The type under which other modules resolve the service, usually an interface.</typeparam>
    /// <param name="service">The instance.</param>
    /// <exception cref="InvalidOperationException">The step is not <see cref="LifecycleStep.Registration"/>, or another module already publishes <typeparamref name="TService"/>.</exception>
    public void Publish<TService>(TService service)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(service);
        RequireStep(LifecycleStep.Registration, "Services are published during registration");
        _context.Services.Publish(_runtime, typeof(TService), service);
    }

    /// <summary>Resolves a service of the module itself or of one of its declared dependencies.</summary>
    /// <typeparam name="TService">The type the service is published under.</typeparam>
    /// <returns>The instance.</returns>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>: services may not be published yet.</exception>
    /// <exception cref="ServiceResolutionException">No active module publishes the service, or its module is not a declared dependency.</exception>
    public TService Resolve<TService>()
        where TService : class
    {
        if (TryResolve(out TService? service))
        {
            return service;
        }

        throw new ServiceResolutionException(
            typeof(TService),
            ServiceFailure.NotPublished,
            $"Module '{Module.Id}' asked for service '{typeof(TService).FullName}', which no active module publishes in the {EngineStopException.Describe(Side)} context.");
    }

    /// <summary>
    /// Resolves a service, or reports it absent: the service of an optional
    /// dependency that is inactive, for example.
    /// </summary>
    /// <typeparam name="TService">The type the service is published under.</typeparam>
    /// <param name="service">The instance, or <see langword="null"/> when the service is absent.</param>
    /// <returns><see langword="true"/> when the service is available.</returns>
    /// <exception cref="InvalidOperationException">The step is <see cref="LifecycleStep.Registration"/>: services may not be published yet.</exception>
    /// <exception cref="ServiceResolutionException">The module that publishes the service is not a declared dependency.</exception>
    public bool TryResolve<TService>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TService? service)
        where TService : class
    {
        if (_context.Step == LifecycleStep.Registration)
        {
            throw new InvalidOperationException(
                $"Module '{Module.Id}' asked for service '{typeof(TService).FullName}' during registration; services are resolved from initialization on, once every module has published its own.");
        }

        object? instance = _context.Services.Resolve(_runtime, typeof(TService));
        service = instance as TService;
        return service is not null;
    }

    /// <summary>Tells whether a module is active in this context: present, running on this side, and not disabled.</summary>
    /// <param name="moduleId">The identifier of a module.</param>
    /// <returns><see langword="true"/> when the module is active.</returns>
    /// <remarks>Test an optional dependency with this method before calling its isolation class.</remarks>
    public bool IsModuleActive(string moduleId) => _context.IsModuleActive(moduleId);

    /// <summary>
    /// Adds an emergency hook: code that runs before the context stops on an
    /// error, such as an emergency save of the world.
    /// </summary>
    /// <param name="hook">The code to run; its exceptions are logged.</param>
    /// <exception cref="InvalidOperationException">The step is not <see cref="LifecycleStep.Registration"/>.</exception>
    public void AddEmergencyHook(Action<EmergencyStop> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        RequireStep(LifecycleStep.Registration, "Emergency hooks are added during registration");
        _context.AddEmergencyHook(_runtime, hook);
    }

    /// <summary>
    /// Compares the states of the application registered in code with the
    /// states that the active modules of this context declare in their
    /// manifests. The module that owns the states calls it once they are all
    /// registered.
    /// </summary>
    /// <param name="registered">Every state registered in code in this context.</param>
    /// <exception cref="StateMismatchException">A state is registered without being declared by its module, or declared without being registered.</exception>
    public void VerifyStates(IEnumerable<RegisteredState> registered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        _context.VerifyStates(registered);
    }

    private void RequireStep(LifecycleStep step, string rule)
    {
        if (_context.Step != step)
        {
            throw new InvalidOperationException($"Module '{Module.Id}' cannot do this during {EngineStopException.Describe(_context.Step)}. {rule}.");
        }
    }
}
