// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Core;

/// <summary>The steps of the lifecycle of a module.</summary>
public enum LifecycleStep
{
    /// <summary>Step 0, once per process: integrity, manifests, and checks of the composition. No module code runs.</summary>
    Discovery,

    /// <summary>Step 1, once per process: the assemblies are loaded, platform modules first.</summary>
    Loading,

    /// <summary>Step 2, per context: the module publishes its services. It must not resolve any.</summary>
    Registration,

    /// <summary>Step 3, per context: the module resolves the services of its dependencies, which are initialized, and creates its state.</summary>
    Initialization,

    /// <summary>Step 4, per context: last preparations before the first tick.</summary>
    Start,

    /// <summary>Step 5, per context: the loop runs.</summary>
    Execution,

    /// <summary>Step 6, per context, in reverse order: the module ends its activity.</summary>
    Stop,

    /// <summary>Step 7, per context, in reverse order: the module frees its memory and resources.</summary>
    Release,
}

/// <summary>
/// The code of a module, instantiated once per context. The core calls its
/// methods on the loop thread of the context: steps 2 to 4 in dependency
/// order, steps 6 and 7 in reverse order.
/// </summary>
/// <remarks>
/// <para>
/// Every method does nothing by default; a module implements the steps it
/// needs. Its class is named by <see cref="ModuleEntryAttribute"/>.
/// </para>
/// <para>
/// An exception thrown by a method follows the error policy of the module
/// that threw it (<see cref="ModuleInfo.ErrorPolicy"/>). The module must not
/// keep mutable static state: it may run in two contexts of the same process.
/// </para>
/// </remarks>
public interface IModule
{
    /// <summary>Step 2: publishes the services of the module.</summary>
    /// <param name="context">The module in its context.</param>
    /// <remarks>Resolving a service here throws: the services of other modules may not be published yet.</remarks>
    void Register(ModuleContext context)
    {
    }

    /// <summary>Step 3: resolves the services of the dependencies and creates the state of the module.</summary>
    /// <param name="context">The module in its context.</param>
    /// <remarks>Every service is published, and every dependency is initialized.</remarks>
    void Initialize(ModuleContext context)
    {
    }

    /// <summary>Step 4: last preparations before the first tick.</summary>
    /// <param name="context">The module in its context.</param>
    void Start(ModuleContext context)
    {
    }

    /// <summary>Step 6: ends the activity of the module, for example by saving.</summary>
    /// <param name="context">The module in its context.</param>
    /// <remarks>The modules that depend on this one are already stopped. Exceptions are logged.</remarks>
    void Stop(ModuleContext context)
    {
    }

    /// <summary>Step 7: frees the memory and the resources of the module.</summary>
    /// <param name="context">The module in its context.</param>
    /// <remarks>The modules that depend on this one are already released. Exceptions are logged.</remarks>
    void Release(ModuleContext context)
    {
    }

    /// <summary>
    /// Called when an optional dependency of the module is disabled after an
    /// error. From then on the module works as if the dependency were absent.
    /// </summary>
    /// <param name="context">The module in its context.</param>
    /// <param name="dependency">The disabled dependency.</param>
    /// <remarks>
    /// The services of the dependency are no longer available; drop any
    /// reference to them. A required dependency that is disabled disables this
    /// module too, without this call.
    /// </remarks>
    void OnDependencyDisabled(ModuleContext context, ModuleInfo dependency)
    {
    }
}
