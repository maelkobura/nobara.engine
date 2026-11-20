// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using BepuUtilities.Memory;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Logging;

[assembly: ModuleEntry(typeof(Nobara.Physics.Bepu.BepuModule))]

namespace Nobara.Physics.Bepu;

/// <summary>The module of the BEPUphysics backend: it registers the backend <c>bepu</c> with nPhysics.</summary>
public sealed class BepuModule : IModule
{
    /// <summary>The identifier of the module.</summary>
    public const string ModuleId = "nobara.physics.bepu";

    private BepuBackend? _backend;

    /// <summary>Gets the backend, once the module is initialized.</summary>
    internal BepuBackend? Backend => _backend;

    /// <inheritdoc/>
    public void Initialize(ModuleContext context)
    {
        _backend = new BepuBackend(context.Resolve<IPhysicsSystem>(), context.Jobs, context.Logger);
        context.Resolve<IPhysicsBackends>().Register(context, _backend);
    }

    /// <inheritdoc/>
    public void Release(ModuleContext context) => _backend?.Dispose();
}

/// <summary>
/// The BEPUphysics backend: each physics world is a simulation of its own, with
/// its own pools of unmanaged memory.
/// </summary>
/// <param name="shapes">Where the descriptions of the children of a compound are read.</param>
/// <param name="jobs">The job system that a large world spreads its step over, or <see langword="null"/> to step every world on one thread.</param>
/// <param name="logger">The logger of the module.</param>
internal sealed class BepuBackend(IPhysicsSystem shapes, ModuleJobs? jobs, Logger logger) : IPhysicsBackend, IDisposable
{
    /// <summary>
    /// The number of awake bodies from which a world stepped on the loop thread
    /// spreads its step over the job system; below it, the step runs on the
    /// loop thread alone, which costs less than waking the workers.
    /// </summary>
    public const int DefaultSpreadThreshold = 1000;

    private readonly BufferPool _pool = new();
    private readonly List<BepuWorld> _worlds = [];

    /// <summary>The name of the backend.</summary>
    public const string BackendName = "bepu";

    /// <inheritdoc/>
    public string Name => BackendName;

    /// <summary>Gets or sets the number of awake bodies from which a world spreads its step; the tests lower it.</summary>
    public int SpreadThreshold { get; set; } = DefaultSpreadThreshold;

    /// <summary>Gets or sets the number of workers of the dispatcher of each new world, calling thread included; 0 for every worker of the job system.</summary>
    public int ThreadCount { get; set; }

    /// <summary>Gets the worlds that are not disposed.</summary>
    internal IReadOnlyList<BepuWorld> Worlds => _worlds;

    /// <inheritdoc/>
    public BodyInertia ComputeInertia(ShapeDescription shape, float mass) => Inertia.ToNeutral(Inertia.Of(shape, mass, shapes, _pool));

    /// <inheritdoc/>
    public PhysicsBackendWorld CreateWorld(in PhysicsWorldOptions options)
    {
        JobThreadDispatcher? dispatcher = jobs is null ? null : new JobThreadDispatcher(jobs, logger, ThreadCount);
        var world = new BepuWorld(options, dispatcher, this);
        _worlds.Add(world);
        return world;
    }

    /// <summary>Forgets a world that is disposed.</summary>
    [HotPath]
    internal void Forget(BepuWorld world) => _worlds.Remove(world);

    /// <summary>Releases the pool of the computations of inertia.</summary>
    public void Dispose() => _pool.Clear();
}
