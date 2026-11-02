// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Game;

namespace Nobara.Entity;

/// <summary>The entity worlds of one context, with their components and systems.</summary>
internal sealed class EntityWorlds : IEntityWorlds, IDisposable
{
    /// <summary>The name of the component of the world transform.</summary>
    public const string WorldTransformName = "nobara.entity.transform";

    /// <summary>The name of the component of the local transform.</summary>
    public const string LocalTransformName = "nobara.entity.local_transform";

    /// <summary>The name of the component of the parent link.</summary>
    public const string ParentLinkName = "nobara.entity.parent";

    /// <summary>The index of the parent link in every registry: it is registered third.</summary>
    public const int ParentIndex = 2;

    private readonly ModuleContext _module;
    private readonly List<EntitySystemRunner> _runners = [];
    private readonly HashSet<string> _countedOwners = new(StringComparer.Ordinal);
    private ITickSchedule? _schedule;
    private IGameTime? _time;
    private IWorldSessions? _sessions;
    private EntityWorld[] _sorted = new EntityWorld[4];
    private bool _disposed;

    public EntityWorlds(ModuleContext module)
    {
        _module = module;
        Registry = new ComponentRegistry(module.Jobs);
        Structure = module.Jobs.CreateData("entity structure");
        string id = module.Module.Id;
        WorldTransform = Registry.Register(id, WorldTransformName, new ComponentOptions { TrackChanges = true }, Core.Mathematics.WorldTransform.Identity);
        LocalTransform = Registry.Register(id, LocalTransformName, null, global::Nobara.Entity.LocalTransform.Identity);
        ParentLink = Registry.Register(id, ParentLinkName, null, default(ParentLink));
    }

    public IReadOnlyList<EntityWorld> Worlds => new ArraySegment<EntityWorld>(_sorted, 0, SortedCount);

    public IReadOnlyList<ComponentInfo> Components => Registry.Infos;

    public JobData Structure { get; }

    public ComponentType<WorldTransform> WorldTransform { get; }

    public ComponentType<LocalTransform> LocalTransform { get; }

    public ComponentType<ParentLink> ParentLink { get; }

    internal ComponentRegistry Registry { get; }

    /// <summary>Gets the worlds in the order of their dimensions; the first <see cref="SortedCount"/> are used.</summary>
    internal EntityWorld[] Sorted
    {
        [HotPath]
        get => _sorted;
    }

    internal int SortedCount { get; private set; }

    /// <summary>Gets or sets the number of entity systems running: worlds are not created or destroyed meanwhile.</summary>
    internal int Running { get; set; }

    /// <summary>Resolves the services of the dependencies, and follows the sessions.</summary>
    public void Initialize(ITickSchedule schedule, IGameTime time, IWorldSessions sessions)
    {
        _schedule = schedule;
        _time = time;
        _sessions = sessions;

        // A world left by a module after the end of the previous session is freed here: in
        // development mode the leak was reported, and the new session starts clean.
        sessions.SubscribeStarting(_module, _ => DisposeLeftovers());
    }

    public EntityWorld Create(ModuleContext owner, DimensionId dimension)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireReady();
        RequireNotRunning();
        if (_sessions!.State is not (SessionState.Starting or SessionState.Active))
        {
            throw new InvalidOperationException(
                $"Module '{owner.Module.Id}' creates the entity world of {dimension} outside a world session: create it in SessionStarting at the earliest.");
        }

        if (TryGet(dimension, out _))
        {
            throw new ArgumentException($"The {dimension} has an entity world already.", nameof(dimension));
        }

        if (!_time!.HasDimension(dimension))
        {
            throw new ArgumentException($"The {dimension} has no clock: add it with IGameTime.AddDimension first.", nameof(dimension));
        }

        if (_module.Side == Side.Client && SortedCount > 0)
        {
            throw new InvalidOperationException(
                $"The client has the entity world of {_sorted[0].Dimension} already: it holds the world of the dimension the player sees only. Destroy it before creating the world of {dimension}.");
        }

        var world = new EntityWorld(this, dimension, owner.Module.Id);
        var states = new EntitySystemRunner.WorldState[_runners.Count];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = _runners[i].Attach(world);
        }

        world.SystemStates = states;
        Insert(world);
        CountLeaks(owner);
        return world;
    }

    public void Destroy(ModuleContext owner, DimensionId dimension)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireNotRunning();
        if (!TryGet(dimension, out EntityWorld? world))
        {
            throw new ArgumentException($"The {dimension} has no entity world.", nameof(dimension));
        }

        if (world.OwnerModuleId != owner.Module.Id)
        {
            throw new ArgumentException(
                $"Module '{owner.Module.Id}' cannot destroy the entity world of {dimension}: module '{world.OwnerModuleId}' created it.",
                nameof(owner));
        }

        Remove(world);
        world.Dispose();
    }

    public bool TryGet(DimensionId dimension, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EntityWorld? world)
    {
        for (int i = 0; i < SortedCount; i++)
        {
            if (_sorted[i].Dimension == dimension)
            {
                world = _sorted[i];
                return true;
            }
        }

        world = null;
        return false;
    }

    public ComponentType<T> RegisterComponent<T>(ModuleContext module, string name, ComponentOptions? options = null, T defaultValue = default)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(module);
        return Registry.Register(module.Module.Id, name, options, defaultValue);
    }

    public ComponentType RegisterComponent(ModuleContext module, string name, Type type, ComponentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        return Registry.Register(module.Module.Id, name, type, options);
    }

    public bool TryGetComponent(string name, out ComponentType component) => Registry.TryGet(name, out component);

    public bool TryGetComponent<T>(out ComponentType<T> component)
        where T : unmanaged =>
        Registry.TryGet(out component);

    public ComponentInfo GetComponent(ComponentType component) => Registry.Info(component);

    public EntitySystemHandle AddSystem(ModuleContext module, string name, IEntitySystem system, EntitySystemOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(system);
        RequireReady();
        options ??= new EntitySystemOptions();
        if (options.ParallelThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The parallel threshold of an entity system is 0 or more blocks.");
        }

        foreach (ComponentType component in options.Query.All.Concat(options.Query.None))
        {
            Registry.Require(component);
        }

        // Every entity system reads the structure of the worlds; the components it declares
        // become the data of the job system that nGame and the jobs of the system declare.
        var writes = new List<JobData>();
        foreach (ComponentType component in options.Writes)
        {
            writes.Add(Registry.Info(component).Data);
        }

        writes.AddRange(options.WritesData);
        var reads = new List<JobData>();
        foreach (ComponentType component in options.Reads)
        {
            JobData data = Registry.Info(component).Data;
            if (!writes.Contains(data))
            {
                reads.Add(data);
            }
        }

        reads.AddRange(options.ReadsData.Where(d => !writes.Contains(d)));
        if (options.ChangesStructure)
        {
            writes.Add(Structure);
        }
        else
        {
            reads.Add(Structure);
        }

        var runner = new EntitySystemRunner(this, _time!, module, name, system, options, [.. reads.Distinct()], [.. writes.Distinct()], _runners.Count);
        TickSystemHandle handle = _schedule!.AddSystem(module, name, options.Stage, runner, new TickSystemOptions
        {
            After = options.After,
            Before = options.Before,
            Reads = runner.Reads,
            Writes = runner.Writes,
            Side = options.Side,
        });
        _runners.Add(runner);
        return new EntitySystemHandle(handle);
    }

    /// <summary>Frees every world; the module is released.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < SortedCount; i++)
        {
            _sorted[i].Dispose();
        }

        SortedCount = 0;
        Registry.Dispose();
    }

    private void DisposeLeftovers()
    {
        while (SortedCount > 0)
        {
            EntityWorld world = _sorted[SortedCount - 1];
            _module.Logger.Warning(
                $"Module '{world.OwnerModuleId}' did not destroy the entity world of {world.Dimension} by the end of the previous world session; it is destroyed now, with its {world.EntityCount} entities.");
            Remove(world);
            world.Dispose();
        }
    }

    // In development mode nContext reads these counters after SessionEnded: worlds and
    // entities left then are a leak of the module that created the worlds.
    private void CountLeaks(ModuleContext owner)
    {
        string ownerId = owner.Module.Id;
        if (_countedOwners.Add(ownerId))
        {
            _sessions!.RegisterWorldStateCounter(owner, "entity worlds", () => CountWorlds(ownerId, entities: false));
            _sessions.RegisterWorldStateCounter(owner, "entities", () => CountWorlds(ownerId, entities: true));
        }
    }

    private long CountWorlds(string ownerId, bool entities)
    {
        long count = 0;
        for (int i = 0; i < SortedCount; i++)
        {
            if (_sorted[i].OwnerModuleId == ownerId)
            {
                count += entities ? _sorted[i].EntityCount : 1;
            }
        }

        return count;
    }

    private void Insert(EntityWorld world)
    {
        if (SortedCount == _sorted.Length)
        {
            Array.Resize(ref _sorted, SortedCount * 2);
        }

        int index = SortedCount;
        while (index > 0 && _sorted[index - 1].Dimension > world.Dimension)
        {
            _sorted[index] = _sorted[index - 1];
            index--;
        }

        _sorted[index] = world;
        SortedCount++;
    }

    private void Remove(EntityWorld world)
    {
        int index = Array.IndexOf(_sorted, world, 0, SortedCount);
        Array.Copy(_sorted, index + 1, _sorted, index, SortedCount - index - 1);
        _sorted[--SortedCount] = null!;
    }

    private void RequireReady()
    {
        if (_schedule is null)
        {
            throw new InvalidOperationException("The entity module is not initialized yet: use its service from the initialization of a module that depends on it.");
        }
    }

    private void RequireNotRunning()
    {
        if (Running > 0)
        {
            throw new InvalidOperationException("An entity system runs: worlds are created and destroyed outside entity systems.");
        }
    }
}
