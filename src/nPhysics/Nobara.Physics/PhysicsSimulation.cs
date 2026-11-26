// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
using Nobara.Blocks;
using Nobara.Context;
using Nobara.Core;
using Nobara.Core.Jobs;
using Nobara.Core.Mathematics;
using Nobara.Entity;
using Nobara.Game;
using Nobara.Voxel;

namespace Nobara.Physics;

/// <summary>
/// The physics of one context: the registry of backends, the shapes, the
/// bodies and joints and their worlds, the queue of commands, and the tick
/// system that steps the worlds and writes the transforms.
/// </summary>
internal sealed partial class PhysicsSimulation : IPhysicsSystem, IPhysicsBackends, IPhysicsShapeSource, ITickSystem, IDisposable
{
    /// <summary>The name of the tick system that steps the worlds.</summary>
    public const string StepSystem = "nobara.physics.step";

    /// <summary>The gravity of every world: 9.81 blocks per square second downward, a block being one meter.</summary>
    public static readonly Vector3 DefaultGravity = new(0f, -9.81f, 0f);

    private readonly ModuleContext _context;
    private readonly List<(ModuleContext Module, IPhysicsBackend Backend)> _backends = [];
    private readonly List<WorldEntry> _worlds = [];
    private readonly List<MediumProviderHandle> _mediums = [];
    private readonly List<PhysicsStepSubscription> _observers = [];
    private ShapeSlot[] _shapes = new ShapeSlot[16];
    private int _shapeHigh;
    private int _shapeFree = -1;
    private int _shapeCount;
    private IPhysicsBackend? _backend;
    private IEntityWorlds? _entities;
    private IVoxelWorlds? _voxels;
    private GridTransformWriter? _gridWriter;
    private TickSystemHandle? _step;
    private SessionSubscription? _sessionEnded;
    private readonly List<SessionSubscription> _counters = [];
    private readonly Action<float> _placeBodies;
    private ComponentType<WorldTransform> _transform;
    private ComponentType<ParentLink> _parent;
    private ComponentType<PhysicsBody> _bodyComponent;
    private ComponentType<BodyShape> _shapeComponent;
    private ComponentType<BodyMaterial> _materialComponent;
    private ComponentType<BodyMass> _massComponent;
    private ComponentType<BodyVelocity> _velocityComponent;
    private Vector3 _gravity = DefaultGravity;
    private bool _started;
    private bool _disposed;

    public PhysicsSimulation(ModuleContext context)
    {
        _context = context;
        _placeBodies = PlaceBodies;
    }

    public string BackendName => _backend?.Name ?? string.Empty;

    public int Substeps { get; internal set; } = 8;

    /// <summary>Gets the number of velocity iterations of each substep.</summary>
    public int VelocityIterations { get; private set; } = 1;

    public Vector3 Gravity => _gravity;

    public IReadOnlyList<PhysicsWorldInfo> Worlds
    {
        get
        {
            var worlds = new PhysicsWorldInfo[_worlds.Count];
            for (int i = 0; i < worlds.Length; i++)
            {
                WorldEntry entry = _worlds[i];
                worlds[i] = new PhysicsWorldInfo(entry.Region.Dimension, entry.Origin, entry.Bodies, entry.Joints);
            }

            return worlds;
        }
    }

    public PhysicsStatistics Statistics => new(_worlds.Count, _shapeCount, _staticBodies, _kinematicBodies, _dynamicBodies, _sleepingBodies, _jointCount, _steps, _lastStep)
    {
        AwakeBodies = AwakeCount(),
        Regions = _regions.Count,
        FrozenBodies = _frozenBodies,
        Ghosts = _ghostCount,
        Transfers = _transfers,
        Merges = _merges,
        Splits = _splits,
        Recenters = _recenters,
        VoxelChunks = _voxelChunkCount,
        VoxelWakes = _wakes,
        Characters = _characterCount,
        Queries = _lastQueries,
        ContactEvents = _begunCount + _endedCount + _impactCount,
    };

    /// <summary>Resolves the services of the dependencies, registers the components, the tick system, and the counters of world state, and follows the chunks and their blocks.</summary>
    public void Initialize(IEntityWorlds entities, IVoxelWorlds voxels, IChunkLoading loading, IVoxelBatches batches, IShapeRegistry shapes, IBlockRegistry blocks, ITickSchedule schedule, IWorldSessions sessions)
    {
        _entities = entities;
        _voxels = voxels;
        _loading = loading;
        _shapeRegistry = shapes;
        _blocks = blocks;
        _gridWriter = voxels.ClaimGridTransforms(_context);
        _transform = entities.WorldTransform;
        _parent = entities.ParentLink;
        _bodyComponent = entities.RegisterComponent<PhysicsBody>(_context, "nobara.physics.body");
        _shapeComponent = entities.RegisterComponent<BodyShape>(_context, "nobara.physics.shape");
        _materialComponent = entities.RegisterComponent<BodyMaterial>(_context, "nobara.physics.material");
        _massComponent = entities.RegisterComponent<BodyMass>(_context, "nobara.physics.mass");
        _velocityComponent = entities.RegisterComponent<BodyVelocity>(_context, "nobara.physics.velocity");
        RegisterCharacters(entities);
        _step = schedule.AddSystem(_context, StepSystem, TickStages.Physics, this, new TickSystemOptions
        {
            Reads = [voxels.ChunkAccess, Data(_characterComponent), Data(_intentComponent)],
            Writes =
            [
                entities.Structure,   // characters change parents and leave
                Data(_transform),
                Data(_localTransform),
                Data(_characterStateComponent),
                Data(_bodyComponent),
                Data(_shapeComponent),
                Data(_materialComponent),
                Data(_massComponent),
                Data(_velocityComponent),
            ],
        });
        _sessionEnded = sessions.SubscribeEnded(_context, _ => Reset());
        _residency = loading.SubscribeResidency(_context, OnResidency);
        _notifications = batches.Subscribe(_context, OnBlocksChanged);
        _counters.Add(sessions.RegisterWorldStateCounter(_context, "bodies", () => _staticBodies + _kinematicBodies + _dynamicBodies));
        _counters.Add(sessions.RegisterWorldStateCounter(_context, "joints", () => _jointCount));
        _counters.Add(sessions.RegisterWorldStateCounter(_context, "regions", () => _regions.Count));
        _counters.Add(sessions.RegisterWorldStateCounter(_context, "physics worlds", () => _worlds.Count + _retiring.Count));
        _counters.Add(sessions.RegisterWorldStateCounter(_context, "ghosts", () => _ghostCount));
        _counters.Add(sessions.RegisterWorldStateCounter(_context, "voxel chunks", () => _voxelChunkCount));
        _counters.Add(sessions.RegisterWorldStateCounter(_context, "characters", () => _characterCount));
    }

    /// <summary>Chooses the backend from <c>physics.backend</c> and reads the settings of the step.</summary>
    public void Start()
    {
        Substeps = _context.Settings.GetInt(PhysicsSettings.Substeps);
        VelocityIterations = _context.Settings.GetInt(PhysicsSettings.VelocityIterations);
        _backend = ChooseBackend();
        BuildVoxelShapes();
        PrepareQueries();
        _started = true;
        _context.Logger.Info($"Physics backend '{_backend.Name}' chosen; {Substeps} substeps and {VelocityIterations} velocity iterations per step.");
    }

    public void Register(ModuleContext module, IPhysicsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(backend);
        if (_started)
        {
            throw new InvalidOperationException($"Module '{module.Module.Id}' registers the physics backend '{backend.Name}' after nPhysics has started: register it while initializing.");
        }

        foreach ((ModuleContext other, IPhysicsBackend existing) in _backends)
        {
            if (string.Equals(existing.Name, backend.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Modules '{other.Module.Id}' and '{module.Module.Id}' both register a physics backend named '{backend.Name}'.");
            }
        }

        _backends.Add((module, backend));
        _context.Logger.Debug($"Physics backend '{backend.Name}' registered by module '{module.Module.Id}'.");
    }

    public MediumProviderHandle AddMediumProvider(ModuleContext module, IMediumProvider provider)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(provider);
        var handle = new MediumProviderHandle(provider, h => _mediums.Remove(h));
        _mediums.Add(handle);
        return handle;
    }

    [HotPath]
    public bool TrySampleMedium(DimensionId dimension, in WorldPosition position, out MediumSample sample)
    {
        for (int i = 0; i < _mediums.Count; i++)
        {
            if (_mediums[i].Provider.TrySample(dimension, position, out sample))
            {
                return true;
            }
        }

        sample = default;
        return false;
    }

    public PhysicsStepSubscription SubscribeStepEnd(ModuleContext module, IPhysicsStepObserver observer)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(observer);
        var subscription = new PhysicsStepSubscription(module, observer, RemoveObserver);
        _observers.Add(subscription);
        return subscription;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _step?.Dispose();
        _sessionEnded?.Dispose();
        _residency?.Dispose();
        _notifications?.Dispose();
        foreach (SessionSubscription counter in _counters)
        {
            counter.Dispose();
        }

        Reset();
    }

    /// <summary>Forgets every body, joint, ghost, and region, and disposes every world, at the end of a world session: the entities are gone with it.</summary>
    private void Reset()
    {
        ResetCharacters();
        ResetContacts();
        for (int i = 0; i < _ghostHigh; i++)
        {
            if (_ghosts[i].Alive)
            {
                FreeGhost(i);
            }
        }

        for (int i = 0; i < _bodyHigh; i++)
        {
            ref BodyRecord body = ref _bodies[i];
            if (body.Alive)
            {
                ReleaseShape(body.Shape);
                FreeBody(i);
            }
        }

        for (int i = 0; i < _jointHigh; i++)
        {
            if (_joints[i].Alive)
            {
                FreeJoint(i);
            }
        }

        foreach (WorldEntry world in _worlds)
        {
            ForgetVoxels(world);
            world.Backend.Dispose();
        }

        foreach (WorldEntry world in _retiring)
        {
            ForgetVoxels(world);
            world.Backend.Dispose();
        }

        _cells.Clear();
        _gridFollow.Clear();
        foreach (VoxelGridCells cells in _gridCells.Values)
        {
            cells.Clear();
            RefreshVoxelRadii(cells);
        }

        _wakeQueue.Clear();
        Array.Clear(_terrains);
        _terrainCount = 0;
        _voxelChunkCount = 0;
        _voxelsRecompute = _voxelsReset = _chunksChanged = false;

        foreach (RegionEntry region in _regions)
        {
            region.Alive = false;
            region.World = null;
        }

        _worlds.Clear();
        _retiring.Clear();
        _regions.Clear();
        _previousTicketCount = -1;
        _ticketCount = 0;
        for (int i = 0; i < _commandCount; i++)
        {
            if (_commands[i].Command == CommandKind.SetShape)
            {
                ReleaseShape(_commands[i].Shape);
            }
        }

        _commandCount = 0;
        _staticBodies = _kinematicBodies = _dynamicBodies = _sleepingBodies = _frozenBodies = 0;
    }

    /// <summary>Changes the gravity of every world, from the next step on: a setting of development, from the debug window.</summary>
    internal void SetGravity(Vector3 gravity)
    {
        _gravity = gravity;
        foreach (WorldEntry world in _worlds)
        {
            world.Backend.SetGravity(gravity);
        }

        foreach (WorldEntry world in _retiring)
        {
            world.Backend.SetGravity(gravity);
        }
    }

    private int AwakeCount()
    {
        int awake = 0;
        foreach (WorldEntry world in _worlds)
        {
            awake += world.Awake;
        }

        return awake;
    }

    private JobData Data(ComponentType component) => _entities!.GetComponent(component).Data;

    private IPhysicsBackend ChooseBackend()
    {
        if (_backends.Count == 0)
        {
            throw new InvalidOperationException("No physics backend is registered: a distribution that activates nobara.physics needs a backend module of nobara.physics.");
        }

        string wanted = _context.Settings.GetString(PhysicsSettings.Backend);
        if (wanted.Length > 0 && !string.Equals(wanted, PhysicsSettings.AutomaticBackend, StringComparison.OrdinalIgnoreCase))
        {
            foreach ((_, IPhysicsBackend backend) in _backends)
            {
                if (string.Equals(backend.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return backend;
                }
            }

            _context.Logger.Warning($"'{PhysicsSettings.Backend}' names the physics backend '{wanted}', which no module registers; nPhysics uses '{_backends[0].Backend.Name}'. Registered: {string.Join(", ", _backends.Select(b => b.Backend.Name))}.");
        }

        return _backends[0].Backend;
    }

    private void RequireStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new InvalidOperationException("nPhysics has not started yet: create bodies once the world session starts.");
        }
    }

    /// <summary>A physics world of the backend, the region it simulates, and what nPhysics knows of it.</summary>
    private sealed class WorldEntry(RegionEntry region, ChunkCoordinate origin, PhysicsBackendWorld backend)
    {
        public RegionEntry Region { get; } = region;

        /// <summary>Gets the origin of the world: the origin of its region when it was created, which a recentered region does not change.</summary>
        public ChunkCoordinate Origin { get; } = origin;

        public PhysicsBackendWorld Backend { get; } = backend;

        public int Bodies { get; set; }

        public int Joints { get; set; }

        public int Ghosts { get; set; }

        /// <summary>Gets or sets the number of its kinematic and dynamic bodies that were awake after the last step.</summary>
        public int Awake { get; set; }

        /// <summary>Gets or sets the chunks of voxels the world holds, once a dynamic body needs one.</summary>
        public VoxelTable? Voxels { get; set; }
    }
}
