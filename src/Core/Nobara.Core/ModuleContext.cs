// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Core.Loading;
using Nobara.Core.Logging;

namespace Nobara.Core;

/// <summary>
/// A module in one context: what the core gives the module at each step of
/// its lifecycle. There is one per module and per context.
/// </summary>
/// <remarks>
/// Call it on the loop thread of the context. The lifecycle steps limit what
/// is allowed: services are published during registration and resolved from
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
