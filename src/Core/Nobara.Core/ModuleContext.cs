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
/// <see cref="SynchronizationContext"/>, <see cref="RequestStop"/>, and
/// <see cref="ForwardStopRequest"/>. The
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
        Settings = new ModuleSettings(context.Engine.Settings, runtime, context.Side, (callback, state) => context.Loop.Post(runtime, callback, state));
        Resources = new ModuleResources(context, context.Resources, runtime);
        ModuleConfig = context.Engine.DistributionData.ModuleConfigs.GetValueOrDefault(runtime.Info.Id);
        Jobs = new ModuleJobs(context.Engine.Jobs, runtime.Jobs);
        Profiler = new ModuleProfiler(context.Engine.Profiler, context.Name, runtime.Info.Id);
    }

    /// <summary>Gets the module.</summary>
    public ModuleInfo Module => _runtime.Info;

    /// <summary>
    /// Gets the rank of the module in the load order of the context, from 0.
    /// A module comes after its dependencies, and each module of the context
    /// has its own rank.
    /// </summary>
    /// <remarks>
    /// A module that orders the code of other modules uses it to break ties in
    /// a deterministic way. The rank is not stable across distributions.
    /// </remarks>
    public int LoadOrder => _runtime.Order;

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

    /// <summary>
    /// Gets whether the engine runs in development mode
    /// (<see cref="EngineOptions.DevelopmentMode"/>), so that a module turns on
    /// tools that cost time, such as debug drawing. The value does not change
    /// while the engine runs.
    /// </summary>
    public bool DevelopmentMode => _context.Engine.DevelopmentMode;

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
    /// Stops the context after the frame that runs. For the main context, the
    /// game ends. It can be called from any thread.
    /// </summary>
    /// <remarks>
    /// The stop is not handed to the module that intercepts stop requests
    /// (<see cref="InterceptStopRequests"/>): a module calls this method once
    /// the context has to stop.
    /// </remarks>
    public void RequestStop()
    {
        Logger.Info($"Module '{Module.Id}' requests the {_context.Name} context to stop.");
        _context.StopAfterFrame();
    }

    /// <summary>
    /// Hands the context a stop request that comes from outside the game, such
    /// as the player closing the window, as Ctrl+C does: the module that
    /// intercepts stop requests receives it (<see cref="InterceptStopRequests"/>),
    /// and without one the context stops after the frame that runs. It can be
    /// called from any thread.
    /// </summary>
    /// <remarks>
    /// A module that only relays a request of the player calls this method; a
    /// module that decides that the context stops calls <see cref="RequestStop"/>.
    /// </remarks>
    public void ForwardStopRequest()
    {
        Logger.Info($"Module '{Module.Id}' forwards a stop request to the {_context.Name} context.");
        _context.RequestStop();
    }

    /// <summary>
    /// Receives the stop requests of the context instead of letting it stop:
    /// Ctrl+C, the cancellation of <see cref="EngineContext.Run"/>,
    /// <see cref="EngineContext.RequestStop"/>, and
    /// <see cref="ForwardStopRequest"/>. The handler runs on the loop
    /// thread at the start of the next frame, and the module stops the context
    /// with <see cref="RequestStop"/> when it decides to.
    /// </summary>
    /// <param name="handler">The code to run for each request; an exception it throws is charged to this module.</param>
    /// <remarks>
    /// It is how the module that drives the states of the application lets the
    /// active state decide what a stop request does. Requests made before the
    /// handler runs are merged into one. When the module is disabled, requests
    /// stop the context again.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The step is not <see cref="LifecycleStep.Registration"/>, the module
    /// does not reserve <see cref="LoopReservations.Contexts"/>, or it already
    /// intercepts stop requests.
    /// </exception>
    public void InterceptStopRequests(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RequireStep(LifecycleStep.Registration, "Stop requests are intercepted during registration");
        if (!Module.Reservations.Contains(LoopReservations.Contexts, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module '{Module.Id}' cannot intercept stop requests: only the module that reserves '{LoopReservations.Contexts}' can.");
        }

        _context.InterceptStopRequests(_runtime, handler);
    }

    /// <summary>
    /// Applies the error policy to an exception that code of this module
    /// threw while another module called it, such as a tick system or a
    /// scheduled task: the caller catches the exception, reports it here, and
    /// goes on with the code of the other modules.
    /// </summary>
    /// <param name="exception">The exception that the code threw.</param>
    /// <remarks>
    /// <para>
    /// As at the boundaries the core watches itself, the exception is charged
    /// to the module whose code is closest to the throw point, or to this
    /// module when none is found. The method that caught the exception does
    /// not count. The policy of that module applies: <c>log</c> logs it,
    /// <c>disable</c> disables that module and the modules that require it
    /// before the method returns, and <c>stop</c> throws
    /// <see cref="EngineStopException"/>, which the caller lets through.
    /// </para>
    /// <para>Call it on the loop thread of the context.</para>
    /// </remarks>
    /// <exception cref="EngineStopException">The policy of the module charged is <c>stop</c>; the context stops.</exception>
    public void ReportFault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is EngineStopException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        }

        _context.ReportFault(_runtime, exception);
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
