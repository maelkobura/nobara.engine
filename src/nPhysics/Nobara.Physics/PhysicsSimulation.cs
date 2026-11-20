// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Numerics;
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
    private ComponentType<WorldTransform> _transform;
    private ComponentType<ParentLink> _parent;
    private ComponentType<PhysicsBody> _bodyComponent;
    private ComponentType<BodyShape> _shapeComponent;
    private ComponentType<BodyMaterial> _materialComponent;
    private ComponentType<BodyMass> _massComponent;
    private ComponentType<BodyVelocity> _velocityComponent;
    private bool _started;
    private bool _disposed;

    public PhysicsSimulation(ModuleContext context) => _context = context;

    public string BackendName => _backend?.Name ?? string.Empty;

    public int Substeps { get; private set; } = 8;

    /// <summary>Gets the number of velocity iterations of each substep.</summary>
    public int VelocityIterations { get; private set; } = 1;

    public Vector3 Gravity => DefaultGravity;

    public IReadOnlyList<PhysicsWorldInfo> Worlds
    {
        get
        {
            var worlds = new PhysicsWorldInfo[_worlds.Count];
            for (int i = 0; i < worlds.Length; i++)
            {
                WorldEntry entry = _worlds[i];
                worlds[i] = new PhysicsWorldInfo(entry.Dimension, entry.Origin, entry.Bodies, entry.Joints);
            }

            return worlds;
        }
    }

    public PhysicsStatistics Statistics => new(_worlds.Count, _shapeCount, _staticBodies, _kinematicBodies, _dynamicBodies, _sleepingBodies, _jointCount, _steps, _lastStep);

    /// <summary>Resolves the services of the dependencies, registers the components and the tick system.</summary>
    public void Initialize(IEntityWorlds entities, IVoxelWorlds voxels, ITickSchedule schedule, IWorldSessions sessions)
    {
        _entities = entities;
        _voxels = voxels;
        _gridWriter = voxels.ClaimGridTransforms(_context);
        _transform = entities.WorldTransform;
        _parent = entities.ParentLink;
        _bodyComponent = entities.RegisterComponent<PhysicsBody>(_context, "nobara.physics.body");
        _shapeComponent = entities.RegisterComponent<BodyShape>(_context, "nobara.physics.shape");
        _materialComponent = entities.RegisterComponent<BodyMaterial>(_context, "nobara.physics.material");
        _massComponent = entities.RegisterComponent<BodyMass>(_context, "nobara.physics.mass");
        _velocityComponent = entities.RegisterComponent<BodyVelocity>(_context, "nobara.physics.velocity");
        _step = schedule.AddSystem(_context, StepSystem, TickStages.Physics, this, new TickSystemOptions
        {
            Reads = [entities.Structure],
            Writes =
            [
                Data(_transform),
                Data(_bodyComponent),
                Data(_shapeComponent),
                Data(_materialComponent),
                Data(_massComponent),
                Data(_velocityComponent),
            ],
        });
        _sessionEnded = sessions.SubscribeEnded(_context, _ => Reset());
    }

    /// <summary>Chooses the backend from <c>physics.backend</c> and reads the settings of the step.</summary>
    public void Start()
    {
        Substeps = _context.Settings.GetInt(PhysicsSettings.Substeps);
        VelocityIterations = _context.Settings.GetInt(PhysicsSettings.VelocityIterations);
        _backend = ChooseBackend();
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
        Reset();
    }

    /// <summary>Forgets every body and joint and disposes every world, at the end of a world session: the entities are gone with it.</summary>
    private void Reset()
    {
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
            world.Backend.Dispose();
        }

        _worlds.Clear();
        for (int i = 0; i < _commandCount; i++)
        {
            if (_commands[i].Command == CommandKind.SetShape)
            {
                ReleaseShape(_commands[i].Shape);
            }
        }

        _commandCount = 0;
        _staticBodies = _kinematicBodies = _dynamicBodies = _sleepingBodies = 0;
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

    /// <summary>A physics world of the backend, and what nPhysics knows of it.</summary>
    private sealed class WorldEntry(DimensionId dimension, ChunkCoordinate origin, PhysicsBackendWorld backend)
    {
        public DimensionId Dimension { get; } = dimension;

        public ChunkCoordinate Origin { get; } = origin;

        public PhysicsBackendWorld Backend { get; } = backend;

        public int Bodies { get; set; }

        public int Joints { get; set; }
    }
}
